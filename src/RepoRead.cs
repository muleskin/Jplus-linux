using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Jplus;

/// <summary>
/// Reading a repository, cheaply.
///
/// JARVIS could see what SESSIONS were doing and knew nothing about the CODE.
/// These are primitives, not intelligence: no model, no `claude` subprocess, no
/// network — three bounded filesystem reads the brain composes.
///
/// Everything here is SYNCHRONOUS and blocking on purpose; Server calls it via
/// Task.Run. The one asynchronous entry point is `Search`, which prefers ripgrep
/// when installed and otherwise falls back to the pure walk below.
///
/// Two rules govern every function:
/// 1. **Bounded.** Every walk has a depth, a file count, a byte budget and a
///    wall-clock deadline, and stops at whichever it hits first.
/// 2. **Nothing sensitive, ever.** The user's home directory is itself a
///    "project", so containment is the floor, not the ceiling:
///    `SensitiveReason` refuses credentials, keys, dotfile directories and
///    `.env` outright, and the walk never lists or greps them either.
///
/// Linux notes: containment (`IsRelativeTo`) is case-sensitive; the private-data
/// test stays case-insensitive (it fails closed); `Identity` is statx dev/ino.
///
/// Windows notes: every path comparison is case-insensitive; `RealPath` stands
/// in for `os.path.realpath` (junctions and symlinks resolved via
/// FileSystemInfo.ResolveLinkTarget, 8.3 short names expanded so `CREDEN~1.JSO`
/// cannot walk past a name rule); alternate data streams (`file::$DATA`) and
/// DOS device names are refused in `ResolveWithin`.
/// </summary>
public static partial class RepoRead
{
    // --- bounds ---------------------------------------------------------------
    //
    // Perhaps ten times what a real project needs; they exist only so that a
    // pathological tree cannot hold the voice loop open.

    public const int MaxDepth = 8;               // directories below the project root
    public const int MaxDirs = 4_000;            // directories entered in one walk
    public const int MaxFiles = 20_000;          // files considered in one walk
    public const double MaxWalkSeconds = 2.0;    // wall clock for the walk itself

    public const long SearchMaxFileBytes = 512_000;     // a file bigger than this is not searched
    public const long SearchByteBudget = 32L * 1024 * 1024;
    public const double SearchSeconds = 3.0;
    public const int SearchMaxHits = 8;                 // hits actually reported
    public const int SearchHitCountCap = 500;           // hits counted before we stop counting
    public const int SearchLineChars = 100;

    public const int ReadMaxLines = 60;
    public const int ReadLeadLines = 8;          // lines shown BEFORE the line asked about
    public const int ReadMaxChars = 1_100;       // sits under Server.WrapContentCap (1200)
    public const long ReadMaxFileBytes = 4L * 1024 * 1024;

    public const int ReadmeChars = 340;

    // Build output, dependencies or version-control plumbing. Skipping them is
    // what makes the walk fast AND the answer useful.
    public static readonly HashSet<string> IgnoredDirs = new(StringComparer.Ordinal)
    {
        ".git", ".hg", ".svn", "node_modules", "bower_components", "vendor",
        "dist", "build", "out", "target", "coverage", "htmlcov",
        ".venv", "venv", "env", "__pycache__", ".mypy_cache", ".pytest_cache",
        ".ruff_cache", ".tox", ".nox", ".gradle", ".idea", "pods",
        ".next", ".nuxt", ".svelte-kit", ".parcel-cache", ".turbo", ".cache",
        "site-packages", ".terraform", ".eggs", "obj",
    };

    // Extensions never worth reading aloud or grepping.
    public static readonly HashSet<string> BinaryExts = new(StringComparer.Ordinal)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".icns", ".bmp", ".tiff",
        ".mp3", ".mp4", ".wav", ".aiff", ".mov", ".avi", ".m4a", ".ogg", ".webm",
        ".pdf", ".zip", ".gz", ".tgz", ".bz2", ".xz", ".7z", ".rar", ".jar",
        ".so", ".dylib", ".dll", ".a", ".o", ".pyc", ".pyo", ".class", ".wasm",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".db", ".sqlite", ".sqlite3", ".bin", ".dat", ".pack", ".idx",
        ".exe", ".img", ".dmg", ".iso",
    };

    public static readonly HashSet<string> Lockfiles = new(StringComparer.Ordinal)
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "poetry.lock",
        "Cargo.lock", "Gemfile.lock", "composer.lock", "bun.lockb", "uv.lock",
    };

    // --- the sensitive-file wall ---------------------------------------------
    //
    // Checked against the path RELATIVE to the project root, never the absolute
    // one: a project under `.claude/worktrees/` must not be refused whole.

    public static readonly HashSet<string> SensitiveDirs = new(StringComparer.Ordinal)
    {
        ".ssh", ".aws", ".gnupg", ".gpg", ".kube", ".docker", ".gcloud", ".azure",
        ".config", ".password-store", ".netrc", ".npm", ".yarn", ".cargo",
        ".local", ".mozilla", ".keychain", ".keys", "secrets", ".secrets",
        "library", ".authinfo", ".subversion", ".gem", ".m2",
        // Windows: `AppData` is what macOS's `Library` is — browser profiles,
        // credential stores and token caches, and tens of thousands of files.
        "appdata",
    };

    // A filename containing any of these is refused. Deliberately narrow —
    // "token" is not here, because `tokenizer.py` is ordinary code.
    public static readonly string[] SensitiveNameParts =
    [
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
        "credential", "secrets.", ".secret", "passwd", "shadow", "htpasswd",
        "private_key", "privatekey", "apikey", "api_key", "access_key",
    ];

    public static readonly HashSet<string> SensitiveSuffixes = new(StringComparer.Ordinal)
    {
        ".pem", ".key", ".p12", ".pfx", ".keystore", ".jks", ".ppk", ".kdbx",
    };

    public static readonly HashSet<string> SensitiveExact = new(StringComparer.Ordinal)
    {
        ".netrc", ".pypirc", ".git-credentials", ".npmrc", ".htpasswd",
        "credentials", "credentials.json", ".claude.json", ".dockercfg",
        "known_hosts", "authorized_keys", ".bash_history", ".zsh_history",
        ".python_history", ".sqlite_history", ".viminfo",
        // JARVIS's own loopback bearer token — the single thing between a local
        // process and every acting tool. An exact name, not a "token" fragment,
        // because `tokenizer.py` must stay readable.
        "tool-token",
    };

    // A `.env` is a secret; a `.env.example` is documentation.
    public static readonly HashSet<string> SafeEnvNames = new(StringComparer.Ordinal)
    {
        ".env.example", ".env.sample", ".env.template", ".env.dist",
        "env.example", ".env.defaults",
    };

    // Dot-prefixed names that are ordinary project furniture. Any other dotfile
    // is refused unread: where home is a project, that is where credentials live.
    public static readonly HashSet<string> SafeDotNames = new(StringComparer.Ordinal)
    {
        ".github", ".gitlab", ".gitignore", ".gitattributes", ".gitmodules",
        ".vscode", ".editorconfig", ".dockerignore", ".nvmrc", ".node-version",
        ".python-version", ".ruby-version", ".tool-versions", ".prettierignore",
        ".eslintignore", ".flake8", ".isort.cfg", ".coveragerc", ".babelrc",
        ".claude", ".agents", ".superpowers", ".husky", ".changeset", ".circleci",
        ".well-known", ".storybook", ".devcontainer", ".readthedocs.yaml",
    };

    public static readonly string[] SafeDotPrefixes =
        [".eslintrc", ".prettierrc", ".stylelintrc", ".babelrc", ".markdownlint", ".yamllint"];

    public static readonly Dictionary<string, string> Languages = new(StringComparer.Ordinal)
    {
        [".py"] = "Python", [".pyi"] = "Python",
        [".ts"] = "TypeScript", [".tsx"] = "TypeScript",
        [".js"] = "JavaScript", [".jsx"] = "JavaScript", [".mjs"] = "JavaScript",
        [".rs"] = "Rust", [".go"] = "Go", [".java"] = "Java", [".kt"] = "Kotlin",
        [".rb"] = "Ruby", [".php"] = "PHP", [".swift"] = "Swift", [".m"] = "Objective-C",
        [".c"] = "C", [".h"] = "C", [".cc"] = "C++", [".cpp"] = "C++", [".hpp"] = "C++",
        [".cs"] = "C#", [".scala"] = "Scala", [".ex"] = "Elixir", [".exs"] = "Elixir",
        [".sh"] = "Shell", [".zsh"] = "Shell", [".bash"] = "Shell",
        [".sql"] = "SQL", [".vue"] = "Vue", [".svelte"] = "Svelte", [".dart"] = "Dart",
        [".html"] = "HTML", [".css"] = "CSS", [".scss"] = "CSS", [".less"] = "CSS",
        [".md"] = "Markdown", [".rst"] = "reStructuredText",
    };

    // Counted, but never the headline.
    public static readonly HashSet<string> NotAHeadline = new(StringComparer.Ordinal) { "Markdown", "reStructuredText" };

    public static readonly string[] ReadmeNames = ["readme.md", "readme.rst", "readme.txt", "readme", "readme.markdown"];

    /// <summary>A path JARVIS will not touch. The message is what he says aloud.</summary>
    public class Refused : Exception
    {
        public Refused(string message) : base(message) { }
    }

    // --- paths ----------------------------------------------------------------

    private static string[] RelParts(string relative) =>
        relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != ".").ToArray();

    /// <summary>
    /// Why this path (relative to the project root) must not be read, or null.
    /// The returned fragment is for the log; the spoken refusal is fixed,
    /// because telling a caller which rule it tripped is a probing oracle.
    /// </summary>
    public static string? SensitiveReason(string relative)
    {
        var parts = RelParts(relative ?? "");
        for (int i = 0; i < parts.Length; i++)
        {
            var raw = parts[i];
            var part = raw.ToLowerInvariant();
            var isLast = i == parts.Length - 1;

            if (SensitiveDirs.Contains(part) || SensitiveExact.Contains(part))
                return $"{raw} is a sensitive name";
            if (SensitiveNameParts.Any(fragment => part.Contains(fragment, StringComparison.Ordinal)))
                return $"{raw} looks like a credential";
            if (isLast && SensitiveSuffixes.Contains(Builds.Suffix(part)))
                return $"{raw} looks like a key";
            if (part.StartsWith(".env", StringComparison.Ordinal))
            {
                if (!SafeEnvNames.Contains(part))
                    return $"{raw} is an environment file";
                continue;
            }
            if (part.StartsWith('.'))
            {
                if (SafeDotNames.Contains(part) || SafeDotPrefixes.Any(p => part.StartsWith(p, StringComparison.Ordinal)))
                    continue;
                return $"{raw} is a dotfile JARVIS does not know";
            }
        }
        return null;
    }

    // --- Windows path primitives ------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetLongPathNameW")]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[]? buffer, uint bufferLength);

    /// <summary>`(st_dev, st_ino)` — what "the same directory" means to the filesystem.</summary>
    public readonly record struct FileId(ulong Volume, ulong Index);

    /// <summary>
    /// The file's identity, or null if it cannot be stat'd. Every other answer —
    /// a string, a case-folded string, a resolved string — is a guess about
    /// spelling, and the guesses are what let `DATA\` walk past the wall.
    /// Follows links, like `os.stat`.
    /// </summary>
    public static FileId? Identity(string path)
    {
        if (!OperatingSystem.IsWindows())
            return Posix.StatPath(path) is { } st ? new FileId(st.Dev, st.Ino) : null;
        try
        {
            // access 0, share read|write|delete, OPEN_EXISTING, BACKUP_SEMANTICS (opens directories).
            using var h = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (h.IsInvalid) return null;
            if (!GetFileInformationByHandle(h, out var info)) return null;
            return new FileId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        }
        catch
        {
            return null;
        }
    }

    private static string LongName(string path)
    {
        // 8.3 short names always contain '~'; only those need expanding.
        if (!OperatingSystem.IsWindows() || !path.Contains('~')) return path;
        try
        {
            var n = GetLongPathNameW(path, null, 0);
            if (n == 0) return path;
            var buf = new char[n];
            var m = GetLongPathNameW(path, buf, n);
            if (m == 0 || m >= n) return path;
            return new string(buf, 0, (int)m);
        }
        catch
        {
            return path;
        }
    }

    private static string StripDevicePrefix(string p)
    {
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + p[8..];
        if (p.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + p[8..];
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) && p.Length > 5 && p[5] == ':') return p[4..];
        if (p.StartsWith(@"\??\", StringComparison.Ordinal) && p.Length > 5 && p[5] == ':') return p[4..];
        return p;
    }

    /// <summary>
    /// `os.path.realpath`: absolute, `..` collapsed, every symlink and junction
    /// on the way resolved (ResolveLinkTarget), 8.3 short names expanded. A tail
    /// that does not exist is kept as spelled, as non-strict realpath does.
    /// </summary>
    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(string.IsNullOrEmpty(path) ? "." : path);
        for (int hops = 0; hops < 64; hops++)
        {
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return full;
            var parts = full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            var cur = root;
            string? restart = null;
            int i = 0;
            for (; i < parts.Length; i++)
            {
                var next = Path.Combine(cur, parts[i]);
                FileAttributes attrs;
                try
                {
                    attrs = File.GetAttributes(next);
                }
                catch
                {
                    break; // does not exist (or cannot be seen): keep the rest as spelled
                }
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    FileSystemInfo fi = (attrs & FileAttributes.Directory) != 0
                        ? new DirectoryInfo(next) : new FileInfo(next);
                    string? linkTarget = null;
                    try { linkTarget = fi.LinkTarget; } catch { }
                    if (linkTarget is not null)
                    {
                        string target;
                        try
                        {
                            target = fi.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                                     ?? Path.GetFullPath(Path.Combine(cur, StripDevicePrefix(linkTarget)));
                        }
                        catch
                        {
                            target = Path.GetFullPath(Path.Combine(cur, StripDevicePrefix(linkTarget)));
                        }
                        target = StripDevicePrefix(target);
                        var rest = parts[(i + 1)..];
                        restart = Path.GetFullPath(rest.Length == 0 ? target : Path.Combine(rest.Prepend(target).ToArray()));
                        break;
                    }
                }
                cur = next;
            }
            if (restart is not null)
            {
                full = restart;
                continue;
            }
            var existing = LongName(cur);
            var tail = parts[i..];
            return tail.Length == 0 ? existing : Path.Combine(tail.Prepend(existing).ToArray());
        }
        return full; // a link loop: give back what we had, which then fails containment
    }

    private static string TrimSep(string p)
    {
        var root = Path.GetPathRoot(p) ?? "";
        var t = p.TrimEnd('\\', '/');
        return t.Length < root.Length ? root : t;
    }

    /// <summary>
    /// Is `path` equal to `root` or inside it? Both already resolved. Case-insensitive
    /// on Windows only: on Linux `Proj` and `proj` are different directories, and
    /// treating them as one would let a sibling pass for the project.
    /// </summary>
    public static bool IsRelativeTo(string path, string root)
    {
        var p = TrimSep(path);
        var r = TrimSep(root);
        if (p.Equals(r, Py.PathComparison)) return true;
        var withSep = r.EndsWith('\\') || r.EndsWith('/') ? r : r + Path.DirectorySeparatorChar;
        return p.StartsWith(withSep, Py.PathComparison);
    }

    /// <summary>`Path.parts` on Windows: the root ("C:\") and then each component.</summary>
    public static string[] PathParts(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var rest = path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return root.Length > 0 ? [root, .. rest] : rest;
    }

    // --- JARVIS's own data is not part of anybody's project -------------------
    //
    // `data_dir()` defaults to `<exe dir>\data`, and "yourself" resolves to that
    // tree — so `read_file` on JARVIS's own install reached his whole data
    // directory: mcp.json, the tool token, connections.json (where the user is
    // told to paste credentials), memory and journal (which CLAUDE.md imports as
    // trusted text). By ABSOLUTE PATH, not by name: a project may hold its own
    // `mcp.json`, and the whole data directory goes in one line so a file added
    // later is covered without anyone remembering to come back here.

    private sealed record PrivateRootsCache(bool Set, string? Key, string[] Roots, FileId?[] Ids);

    private static PrivateRootsCache _privateRootsCache = new(false, null, [], []);

    /// <summary>
    /// Absolute directories JARVIS will not read out of ANY project. Cached
    /// against JARVIS_DATA_DIR: consulted inside walks of up to 20,000 entries,
    /// and `DataDir()` creates the directory as a side effect.
    /// </summary>
    public static string[] JarvisPrivateRoots() => PrivateRoots().Roots;

    /// <summary>The roots, and their identities in the same order (an identity may be null).</summary>
    public static (string[] Roots, FileId?[] Ids) PrivateRoots()
    {
        var key = Py.Getenv("JARVIS_DATA_DIR");
        var c = _privateRootsCache;
        if (c.Set && c.Roots.Length > 0 && c.Key == key)
            return (c.Roots, c.Ids);
        string[] roots;
        try
        {
            roots = [RealPath(DataPaths.DataDir())];
        }
        catch
        {
            roots = [];
        }
        var ids = roots.Select(r => Identity(r)).ToArray();
        _privateRootsCache = new PrivateRootsCache(true, key, roots, ids);
        return (roots, ids);
    }

    // The ancestor identity check is the most expensive of the tests, so its
    // answer is remembered for the run of a walk. Bounded.
    private static readonly Dictionary<string, FileId?> AncestorIdCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object AncestorIdLock = new();
    public const int AncestorIdCacheMax = 4096;

    public static FileId? CachedIdentity(string path)
    {
        lock (AncestorIdLock)
        {
            if (AncestorIdCache.TryGetValue(path, out var got)) return got;
        }
        var ident = Identity(path);
        lock (AncestorIdLock)
        {
            if (AncestorIdCache.Count >= AncestorIdCacheMax) AncestorIdCache.Clear();
            AncestorIdCache[path] = ident;
        }
        return ident;
    }

    /// <summary>
    /// Is `absolute` the private root, or inside it? Cheapest test first:
    /// 1-2. the components match (case-insensitively — fail closed);
    /// 3. the ancestor at the root's own depth IS the root, by identity (catches
    ///    spellings no string test predicted: junctions, subst, 8.3 names);
    /// 4. some ancestor at a DIFFERENT depth is the root by identity (a linked
    ///    ancestor changes the component count). Only ancestors sharing the
    ///    root's basename are stat'd, so this almost never costs a syscall.
    /// </summary>
    public static bool Under(string absolute, string root, FileId? rootId)
    {
        var parts = PathParts(absolute);
        var rparts = PathParts(root);
        if (parts.Length >= rparts.Length)
        {
            var prefix = parts[..rparts.Length];
            bool same = true;
            for (int i = 0; i < rparts.Length; i++)
            {
                if (!TrimSep(prefix[i]).Equals(TrimSep(rparts[i]), StringComparison.OrdinalIgnoreCase))
                {
                    same = false;
                    break;
                }
            }
            if (same) return true;
            if (rootId is not null && prefix.Length > 0 && CachedIdentity(Path.Combine(prefix)) == rootId)
                return true;
        }
        if (rootId is null || rparts.Length == 0) return false;
        var leaf = rparts[^1];
        for (int i = parts.Length; i > 0; i--)
        {
            if (parts[i - 1].Equals(leaf, StringComparison.OrdinalIgnoreCase)
                && CachedIdentity(Path.Combine(parts[..i])) == rootId)
                return true;
        }
        return false;
    }

    private static string NameOf(string path)
    {
        var t = path.TrimEnd('\\', '/');
        var n = Path.GetFileName(t);
        return string.IsNullOrEmpty(n) ? t : n;
    }

    /// <summary>
    /// Why this absolute path is JARVIS's own private data, or null. The caller
    /// resolves links first where that matters — `ResolveWithin` does.
    /// </summary>
    public static string? PrivateReason(string absolute)
    {
        var (roots, ids) = PrivateRoots();
        for (int i = 0; i < roots.Length; i++)
        {
            if (Under(absolute, roots[i], ids[i]))
                return $"{NameOf(absolute)} is JARVIS's own data";
        }
        return null;
    }

    // DOS device names. "nul", "con.txt" and friends name a device, not a file,
    // in any directory on older Windows — never something inside a project.
    public static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "conin$", "conout$",
        "com0", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "com¹", "com²", "com³",
        "lpt0", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
        "lpt¹", "lpt²", "lpt³",
    };

    public static bool IsReservedDeviceName(string component)
    {
        var baseName = component.Split('.')[0].TrimEnd(' ');
        return ReservedDeviceNames.Contains(baseName);
    }

    /// <summary>
    /// The real path `target` names inside `root`, or throw `Refused`.
    /// Both sides are resolved before they are compared, so a link out of the
    /// project, a `..` and an absolute path elsewhere all fail the same way —
    /// a string prefix test has never been enough.
    /// </summary>
    public static string ResolveWithin(string root, string? target)
    {
        string realRoot, real;
        try
        {
            realRoot = RealPath(root);
            var raw = Py.ExpandUser(target ?? "");
            var candidate = Path.IsPathFullyQualified(raw) ? raw : Path.Combine(realRoot, raw);
            real = RealPath(candidate);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A NUL or another character no path can hold: nothing it could name is inside.
            throw new Refused("outside");
        }

        if (!IsRelativeTo(real, realRoot))
            throw new Refused("outside");

        var relative = TrimSep(real).Equals(TrimSep(realRoot), Py.PathComparison)
            ? "."
            : Path.GetRelativePath(realRoot, real);
        // `file::$DATA` / `file:stream` reach data no name rule can see, and a
        // device name is not a file. Both refused like any other sensitive path.
        // (Windows-only concepts; on Linux ':' and `con` are ordinary names.)
        if (Py.IsWindows && (relative.Contains(':') || RelParts(relative).Any(IsReservedDeviceName)))
            throw new Refused("sensitive");
        if (SensitiveReason(relative) is not null || PrivateReason(real) is not null)
            throw new Refused("sensitive");
        return real;
    }

    public static bool SkipDir(string name)
    {
        var lowered = name.ToLowerInvariant();
        if (IgnoredDirs.Contains(lowered)) return true;
        // The sensitive wall applies to the WALK, not only to a named path: with
        // home as the project, `Library/` (here `AppData\`) was descended into.
        if (SensitiveDirs.Contains(lowered)) return true;
        if (name.StartsWith('.') && !SafeDotNames.Contains(lowered)) return true;
        return false;
    }

    public static bool SkipFile(string name)
    {
        var lowered = name.ToLowerInvariant();
        if (Lockfiles.Contains(lowered)) return true;
        if (BinaryExts.Contains(Builds.Suffix(lowered))) return true;
        return SensitiveReason(name) is not null;
    }

    // --- the one bounded walk everything shares -------------------------------

    /// <summary>What one bounded traversal found, and whether it ran out of budget.</summary>
    public class Walk
    {
        public List<(string Rel, long Size)> Files { get; } = [];   // (path relative to root, bytes)
        public int Dirs { get; set; }
        public bool Complete { get; set; } = true;
    }

    private static readonly EnumerationOptions ScanOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>A symlink or junction (not merely a reparse point, e.g. a cloud placeholder).</summary>
    private static bool IsLink(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0) return false;
        try { return info.LinkTarget is not null; } catch { return true; }
    }

    /// <summary>
    /// Breadth-first, skipping the ignore list, bounded on all four axes.
    /// Breadth-first so that when the budget runs out, what has been seen is the
    /// top of the tree — the part that answers "what is this project".
    /// </summary>
    public static Walk WalkTree(string root, double? deadline = null)
    {
        var output = new Walk();
        var stopAt = deadline ?? Py.Monotonic() + MaxWalkSeconds;
        // Resolved, so containment cannot depend on how the caller spelled the root.
        root = RealPath(root);
        var queue = new Queue<(string Dir, string Prefix, int Depth)>();
        queue.Enqueue((root, "", 0));
        // Read once per walk. A grep never names the file it reads, so the
        // private wall has to be applied here, by identity, not only in
        // `PrivateReason`.
        var (priv, privIds) = PrivateRoots();

        bool IsPrivate(string path)
        {
            string p;
            try { p = Path.GetFullPath(path); } catch { return true; }
            for (int i = 0; i < priv.Length; i++)
                if (Under(p, priv[i], privIds[i])) return true;
            return false;
        }

        while (queue.Count > 0)
        {
            var (directory, prefix, depth) = queue.Dequeue();
            if (output.Dirs >= MaxDirs || output.Files.Count >= MaxFiles)
            {
                output.Complete = false;
                break;
            }
            if (Py.Monotonic() > stopAt)
            {
                output.Complete = false;
                break;
            }
            output.Dirs += 1;
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", ScanOptions))
                {
                    try
                    {
                        if (IsLink(entry)) continue;   // never followed, like follow_symlinks=False
                        if (entry is DirectoryInfo)
                        {
                            if (depth + 1 > MaxDepth)
                            {
                                output.Complete = false;
                                continue;
                            }
                            if (SkipDir(entry.Name) || IsPrivate(entry.FullName)) continue;
                            queue.Enqueue((entry.FullName, $"{prefix}{entry.Name}/", depth + 1));
                        }
                        else if (entry is FileInfo fi)
                        {
                            if (SkipFile(entry.Name) || IsPrivate(entry.FullName)) continue;
                            // Checked HERE as well: one directory holding a million
                            // files would otherwise be scanned in full first.
                            if (output.Files.Count >= MaxFiles)
                            {
                                output.Complete = false;
                                break;
                            }
                            output.Files.Add(($"{prefix}{entry.Name}", fi.Length));
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
        return output;
    }

    // --- git ------------------------------------------------------------------

    /// <summary>
    /// The checked-out branch, read straight off `.git` — no subprocess. Handles
    /// a worktree, where `.git` is a FILE holding `gitdir: …`.
    /// </summary>
    public static string? GitBranch(string root)
    {
        var dot = Path.Combine(root, ".git");
        string text;
        try
        {
            string head;
            if (File.Exists(dot))
            {
                var pointer = Builds.ReadTextUtf8(dot).Trim();
                if (!pointer.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
                var gitdir = pointer[(pointer.IndexOf(':') + 1)..].Trim();
                // git writes `C:/...` (absolute) here; a relative one is relative to the worktree.
                head = Path.Combine(Path.GetFullPath(gitdir, root), "HEAD");
            }
            else if (Directory.Exists(dot))
            {
                head = Path.Combine(dot, "HEAD");
            }
            else
            {
                return null;
            }
            text = Builds.ReadTextUtf8(head).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            return null;
        }
        const string refPrefix = "ref: refs/heads/";
        if (text.StartsWith(refPrefix, StringComparison.Ordinal))
        {
            var b = text[refPrefix.Length..].Trim();
            return b.Length > 0 ? b : null;
        }
        return text.Length > 0 ? "a detached head" : null;
    }

    // --- the README, said out loud --------------------------------------------

    public static readonly Regex Badge = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.CultureInvariant);
    public static readonly Regex Link = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant);
    public static readonly Regex Html = new(@"<!--.*?-->|<[^>]+>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// The first real prose in the README, trimmed hard. Titles, badges and HTML
    /// stripped; cut at a sentence boundary near the limit where there is one.
    /// </summary>
    public static string ReadmeOpening(string root, int limit = ReadmeChars)
    {
        string? path = null;
        Dictionary<string, string> names;
        try
        {
            names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", ScanOptions))
                names[f.Name.ToLowerInvariant()] = f.Name;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "";
        }
        foreach (var candidate in ReadmeNames)
        {
            if (names.TryGetValue(candidate, out var actual))
            {
                path = Path.Combine(root, actual);
                break;
            }
        }
        if (path is null) return "";
        string raw;
        try
        {
            raw = Builds.ReadTextUtf8(path);
            if (raw.Length > 20_000) raw = raw[..20_000];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }

        var text = Html.Replace(Link.Replace(Badge.Replace(raw, ""), "$1"), " ");
        var lines = new List<string>();
        foreach (var line in Builds.SplitLines(text))
        {
            var stripped = line.Trim();
            if (stripped.Length == 0)
            {
                // A blank line is a paragraph break, not the end of the prose:
                // breaking here read only a tagline. Headings and rules end it.
                continue;
            }
            if (new[] { "#", ">", "---", "===", "|", "```", "***" }.Any(p => stripped.StartsWith(p, StringComparison.Ordinal)))
            {
                if (lines.Count > 0) break;
                continue;
            }
            lines.Add(stripped.Replace("**", "").Replace("`", ""));
            if (lines.Sum(x => x.Length) > limit) break;
        }
        var prose = string.Join(" ", lines).Trim();
        if (prose.Length <= limit) return prose;
        var cut = SafeCut(prose, limit);
        var stop = Math.Max(cut.LastIndexOf(". ", StringComparison.Ordinal),
            Math.Max(cut.LastIndexOf("! ", StringComparison.Ordinal), cut.LastIndexOf("? ", StringComparison.Ordinal)));
        if (stop > limit / 2) return cut[..(stop + 1)];
        return cut.TrimEnd() + "…";
    }

    /// <summary>`text[:n]` without splitting a surrogate pair.</summary>
    private static string SafeCut(string text, int n)
    {
        if (text.Length <= n) return text;
        if (n > 0 && char.IsHighSurrogate(text[n - 1])) n--;
        return text[..n];
    }

    // --- 1. repo_overview -----------------------------------------------------

    public static string Join(List<string> items)
    {
        if (items.Count == 0) return "";
        if (items.Count == 1) return items[0];
        return $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";
    }

    /// <summary>
    /// (headline, untrusted body) — the "what is this?" answer. The headline is
    /// facts JARVIS computed; the body is repository CONTENT (the README's words,
    /// its file names), which the caller wraps as untrusted.
    /// </summary>
    public static (string Headline, string Body) Overview(string root, string name)
    {
        // Resolved here as well as inside the walk: the listing and README use it directly.
        root = RealPath(root);
        var found = WalkTree(root);
        var files = found.Files;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (rel, _) in files)
        {
            if (Languages.TryGetValue(Builds.Suffix(rel).ToLowerInvariant(), out var language))
                counts[language] = counts.GetValueOrDefault(language) + 1;
        }

        var ranked = counts.OrderBy(kv => -kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var headlineLangs = ranked.Where(l => !NotAHeadline.Contains(l.Key)).ToList();
        if (headlineLangs.Count == 0) headlineLangs = ranked;
        var spoken = Join(headlineLangs.Take(3)
            .Select((kv, i) => i == 0 ? $"{kv.Key} ({kv.Value} files)" : $"{kv.Key} ({kv.Value})").ToList());

        var total = files.Count;
        var about = !found.Complete ? "at least " : "";
        string first;
        if (spoken.Length > 0)
            first = $"{name} — mostly {spoken}; {about}{total} files in all.";
        else if (total > 0)
            first = $"{name} — {about}{total} files, no code I recognise.";
        else
            first = $"{name} — empty, as far as I can see.";

        var branch = GitBranch(root);
        if (!string.IsNullOrEmpty(branch))
            first += $" A git repository on {branch}.";

        var bodyParts = new List<string>();
        // The README of the brain home is JARVIS's own, not a project's.
        var opening = PrivateReason(root) is not null ? null : ReadmeOpening(root);
        if (!string.IsNullOrEmpty(opening))
            bodyParts.Add($"README: {opening}");

        // The NAME rules are not the private-data wall; the listing consults both,
        // or `project="jarv"` would list `connections.json, mcp.json` out of the
        // brain home that the walk itself had refused.
        List<string> topDirs, topFiles;
        try
        {
            var entries = new DirectoryInfo(root).EnumerateFileSystemInfos("*", ScanOptions).ToList();
            topDirs = entries.Where(e => e is DirectoryInfo && !IsLink(e) && !SkipDir(e.Name)
                                         && PrivateReason(e.FullName) is null)
                .Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            topFiles = entries.Where(e => e is FileInfo && !IsLink(e) && !SkipFile(e.Name)
                                          && PrivateReason(e.FullName) is null)
                .Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            topDirs = [];
            topFiles = [];
        }

        var structure = new List<string>();
        if (topDirs.Count > 0)
        {
            var shown = topDirs.Take(8).ToList();
            var more = topDirs.Count > shown.Count ? $" (+{topDirs.Count - shown.Count} more)" : "";
            structure.Add("folders " + string.Join(", ", shown.Select(d => $"{d}/")) + more);
        }
        if (topFiles.Count > 0)
        {
            var shown = topFiles.Take(8).ToList();
            var more = topFiles.Count > shown.Count ? $" (+{topFiles.Count - shown.Count} more)" : "";
            structure.Add("files " + string.Join(", ", shown) + more);
        }
        if (structure.Count > 0)
            bodyParts.Add("Top level: " + string.Join("; ", structure) + ".");

        return (first, string.Join("\n", bodyParts));
    }

    // --- 2. search_repo -------------------------------------------------------

    public class Hits
    {
        public List<string> Lines { get; } = [];   // "path:line: text"
        public int Found { get; set; }
        public bool Capped { get; set; }           // more than we bothered to count
        public string Tool { get; set; } = "walk";
    }

    /// <summary>Where ripgrep is, or null. Its own function so the pure walk can be forced.</summary>
    public static string? RgPath() => Py.Which("rg");

    public static string Clip(string text)
    {
        text = text.Trim();
        if (text.Length > SearchLineChars)
            return SafeCut(text, SearchLineChars).TrimEnd() + "…";
        return text;
    }

    /// <summary>ripgrep, when it is installed. Null if it could not be used at all.</summary>
    public static async Task<Hits?> SearchRg(string root, string query, string binary)
    {
        var args = new List<string>
        {
            "--fixed-strings", "--ignore-case", "--line-number",
            "--no-heading", "--color", "never", "--no-messages",
            "--max-filesize", SearchMaxFileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-count", "3", "--threads", "4",
            // Windows paths carry a drive colon, so `path:line:text` cannot be
            // split on ':' — a NUL after the path makes the split unambiguous.
            "--null",
        };
        foreach (var ignored in IgnoredDirs.OrderBy(x => x, StringComparer.Ordinal))
        {
            args.Add("--glob");
            args.Add($"!{ignored}/");
        }
        args.Add("--");
        args.Add(query);
        args.Add(root);

        Py.ProcResult proc;
        try
        {
            proc = await Py.Run(binary, args, SearchSeconds, cwd: root);
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or ArgumentException
                                      or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        if (proc.TimedOut) return null;
        if (proc.ReturnCode is not (0 or 1)) return null;

        var hits = new Hits { Tool = "rg" };
        string realRoot = RealPath(root);
        foreach (var raw in Builds.SplitLines(proc.Stdout))
        {
            var nul = raw.IndexOf('\0');
            if (nul < 0) continue;
            var pathText = raw[..nul];
            var parts = raw[(nul + 1)..].Split(':', 2);
            if (parts.Length < 2) continue;
            var (number, body) = (parts[0], parts[1]);
            string real, relative;
            try
            {
                real = RealPath(Path.GetFullPath(pathText, root));
                if (!IsRelativeTo(real, realRoot)) continue;
                relative = Path.GetRelativePath(realRoot, real);
            }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
            {
                continue;
            }
            if (SensitiveReason(relative) is not null || PrivateReason(real) is not null) continue;
            hits.Found += 1;
            if (hits.Found > SearchHitCountCap)
            {
                hits.Capped = true;
                break;
            }
            if (hits.Lines.Count < SearchMaxHits)
                hits.Lines.Add($"{relative.Replace('\\', '/')}:{number}: {Clip(body)}");
        }
        return hits;
    }

    private static void AsciiLowerInPlace(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b >= (byte)'A' && b <= (byte)'Z') data[i] = (byte)(b + 32);
        }
    }

    /// <summary>`bytes.splitlines()`: \n, \r and \r\n only.</summary>
    private static IEnumerable<(int Start, int Length)> ByteLines(byte[] data)
    {
        int start = 0, i = 0;
        while (i < data.Length)
        {
            var b = data[i];
            if (b == (byte)'\n' || b == (byte)'\r')
            {
                yield return (start, i - start);
                if (b == (byte)'\r' && i + 1 < data.Length && data[i + 1] == (byte)'\n') i++;
                i++;
                start = i;
            }
            else i++;
        }
        if (start < data.Length) yield return (start, data.Length - start);
    }

    /// <summary>
    /// The fallback, and on a machine with no ripgrep the only path.
    /// Case-insensitive LITERAL matching, never a regex: the query came out of a
    /// microphone via a model, and one pathological pattern would hang the walk
    /// the voice loop is waiting on. Matching is on bytes, so a file is only
    /// decoded when it actually contains the needle.
    /// </summary>
    public static Hits SearchByWalk(string root, string query)
    {
        var hits = new Hits();
        var needle = Encoding.UTF8.GetBytes(query ?? "");
        AsciiLowerInPlace(needle);
        if (needle.Length == 0) return hits;

        var deadline = Py.Monotonic() + SearchSeconds;
        long budget = SearchByteBudget;
        foreach (var (relative, size) in WalkTree(root, deadline).Files)
        {
            if (Py.Monotonic() > deadline || budget <= 0)
            {
                hits.Capped = true;
                break;
            }
            if (size > SearchMaxFileBytes || size == 0) continue;
            byte[] data;
            try
            {
                data = File.ReadAllBytes(Path.Combine(root, relative));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException)
            {
                continue;
            }
            budget -= data.Length;
            if (data.AsSpan(0, Math.Min(4096, data.Length)).IndexOf((byte)0) >= 0)   // binary, whatever its extension
                continue;
            var lower = (byte[])data.Clone();
            AsciiLowerInPlace(lower);
            if (lower.AsSpan().IndexOf(needle) < 0) continue;
            int perFile = 0, number = 0;
            foreach (var (start, length) in ByteLines(data))
            {
                number++;
                if (lower.AsSpan(start, length).IndexOf(needle) < 0) continue;
                hits.Found += 1;
                perFile += 1;
                if (hits.Lines.Count < SearchMaxHits)
                    hits.Lines.Add($"{relative}:{number}: {Clip(Py.Utf8NoBom.GetString(data, start, length))}");
                if (perFile >= 3) break;
            }
            if (hits.Found > SearchHitCountCap)
            {
                hits.Capped = true;
                break;
            }
        }
        return hits;
    }

    /// <summary>ripgrep if it is on PATH, the bounded walk otherwise.</summary>
    public static async Task<Hits> Search(string root, string query)
    {
        var binary = RgPath();
        if (binary is not null)
        {
            var result = await SearchRg(root, query, binary);
            if (result is not null) return result;
        }
        return await Task.Run(() => SearchByWalk(root, query));
    }

    // --- 3. read_file ---------------------------------------------------------

    public class Window
    {
        public string Text { get; set; } = "";
        public int First { get; set; }
        public int Last { get; set; }
        public int Total { get; set; }
        public bool Truncated { get; set; }
        public string Note { get; set; } = "";
    }

    /// <summary>Python's `around`: an int, a string, or nothing (a bool counts as nothing).</summary>
    private static object? NormaliseAround(object? around)
    {
        switch (around)
        {
            case null:
            case bool:
                return null;
            case int or long or short or byte or sbyte or uint or ulong or ushort:
                return Convert.ToInt64(around);
            case string s:
                return s;
            case JsonValue jv:
                if (jv.TryGetValue<bool>(out _)) return null;
                if (jv.TryGetValue<long>(out var l)) return l;
                if (jv.TryGetValue<string>(out var str)) return str;
                return null;
            case System.Text.Json.JsonElement je:
                return je.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Number when je.TryGetInt64(out var n) => n,
                    System.Text.Json.JsonValueKind.String => je.GetString(),
                    _ => null,
                };
            default:
                return null;   // a float, a list: neither a line nor a needle
        }
    }

    /// <summary>
    /// A bounded window on one file — never the whole of a large one. `around`
    /// is a line number or a string to find; either way the answer says which
    /// lines came back out of how many, and says so when it is not the whole file.
    /// </summary>
    public static Window ReadWindow(string path, object? around = null)
    {
        var size = new FileInfo(path).Length;   // throws FileNotFoundException like os.stat
        if (size > ReadMaxFileBytes)
            throw new Refused("huge");
        var data = File.ReadAllBytes(path);
        if (data.AsSpan(0, Math.Min(4096, data.Length)).IndexOf((byte)0) >= 0)
            throw new Refused("binary");
        var lines = Builds.SplitLines(Py.Utf8NoBom.GetString(data));

        var output = new Window { Total = lines.Count };
        if (lines.Count == 0)
        {
            output.Text = "";
            return output;
        }

        long? centre = null;
        var a = NormaliseAround(around);
        if (a is long n)
        {
            centre = n;
        }
        else if (a is string s && s.Trim().Length > 0)
        {
            var needle = s.Trim().ToLowerInvariant();
            if (needle.All(char.IsDigit))
            {
                centre = long.TryParse(needle, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : long.MaxValue;
            }
            else
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].ToLowerInvariant().Contains(needle, StringComparison.Ordinal))
                    {
                        centre = i + 1;
                        break;
                    }
                }
                if (centre is null)
                    output.Note = $"nothing matching {s.Trim()} in it";
            }
        }

        int start;
        int c = 0;
        if (centre is null)
        {
            start = 0;
        }
        else
        {
            c = (int)Math.Max(1, Math.Min(centre.Value, output.Total));
            // A small lead, not half the window: a 30-line lead plus the
            // character cap once returned a window that did not contain the
            // line asked about. The context wanted is mostly what FOLLOWS.
            start = Math.Max(0, c - 1 - ReadLeadLines);
        }

        var (text, first, last, clipped) = ClipWindow(lines, start);
        if (centre is not null && last < c)
        {
            // Long lines ate the lead as well. Put the hit itself on the first line.
            (text, first, last, clipped) = ClipWindow(lines, c - 1);
        }

        output.First = first;
        output.Last = last;
        output.Truncated = clipped || first > 1 || last < output.Total;
        output.Text = text;
        return output;
    }

    /// <summary>(text, first line, last line, was it cut) for a window at `start`.</summary>
    public static (string Text, int First, int Last, bool Clipped) ClipWindow(List<string> lines, int start)
    {
        var chunk = lines.Skip(start).Take(ReadMaxLines).ToList();
        var text = string.Join("\n", chunk);
        var clipped = false;
        var last = start + chunk.Count;
        if (text.Length > ReadMaxChars)
        {
            text = SafeCut(text, ReadMaxChars).TrimEnd();
            last = start + text.Count(ch => ch == '\n') + 1;
            clipped = true;
        }
        return (text, start + 1, last, clipped);
    }
}
