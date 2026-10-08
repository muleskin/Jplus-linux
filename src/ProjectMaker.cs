using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// Creating a brand-new project directory from a name someone said out loud.
///
/// The name arrives by speech, through an LLM: it may hold path separators,
/// `..`, an absolute path, a leading dot, or whatever a microphone made of
/// "cost flex". So it is validated against an allowlist first, and then — belt
/// and braces — the final path is resolved and its parent compared with the
/// resolved root. String checks alone have never been enough.
///
/// Windows adds its own dangerous shapes, refused the same way: characters no
/// Windows filename may hold (`&lt;&gt;:"|?*` and control characters), DOS
/// device names (`con`, `nul`, `com1`, `lpt1`, … with or without an extension)
/// and a trailing dot, which Windows silently strips.
///
/// Two things this module will never do:
/// 1. **Reuse or overwrite an existing directory.** Creation is atomic and fails
///    if the name exists (CreateDirectoryW, the `mkdir(exist_ok=False)` of
///    Win32); an existing name is reported back, not adopted.
/// 2. **Delete anything.** There is no removal path here at all.
/// </summary>
public static partial class ProjectMaker
{
    public static readonly ILogger Log = Py.Log("jarvis.project_maker");

    // Where new projects go. `~/Projects` by default; JARVIS_PROJECTS_DIR moves it.
    public const string DefaultRoot = "~/Projects";

    public const int MaxNameChars = 64;

    // An allowlist, not a denylist. Starting with a letter or digit rejects
    // `.hidden`, `..`, `../evil` and `/etc/passwd` before any path is built.
    // Used as a full match (\A...\z): `$` would accept "chitauri\n".
    public static readonly Regex SafeName = new(@"\A[a-z0-9][a-z0-9._-]*\z", RegexOptions.CultureInvariant);

    // Characters that can only ever be an attempt to leave the projects root,
    // or to name something the shell and the filesystem disagree about. REFUSED,
    // never slugified away: turning `../evil` into `evil` would create a project
    // under a name the user never said.
    public static readonly char[] ForbiddenChars = ['/', '\\', '\0', '~', ':'];

    // Windows: characters no filename may contain (beyond the separators and
    // ':' above). `*` and `?` are wildcards; `<>|"` are shell syntax.
    public static readonly char[] WindowsInvalidChars = ['<', '>', '"', '|', '?', '*'];

    // Every apostrophe a microphone and an LLM can produce. Dropped rather than
    // dashed, so "Tony Stark's website" is `tony-starks-website`.
    public const string Apostrophes = "'‘’ʼ´`";

    public const double GitTimeoutSec = 10.0;

    /// <summary>The name cannot be used for a directory. Nothing was created.</summary>
    public class BadName : ArgumentException
    {
        public BadName(string message) : base(message) { }
    }

    public static string ProjectsRoot()
    {
        var raw = Py.Getenv("JARVIS_PROJECTS_DIR");
        if (string.IsNullOrEmpty(raw)) raw = DefaultRoot;
        return Path.GetFullPath(Py.ExpandUser(raw));
    }

    /// <summary>
    /// "Tony Stark's website" → "tony-starks-website". Only the HUMAN parts of a
    /// name are transformed; everything dangerous was refused by `SanitiseName`
    /// first. Not a sanitiser and never to be used as one.
    /// </summary>
    public static string Slugify(string name)
    {
        var slug = name.ToLowerInvariant();
        slug = new string(slug.Where(ch => !Apostrophes.Contains(ch)).ToArray());
        slug = Regex.Replace(slug, "[^a-z0-9._-]+", "-", RegexOptions.CultureInvariant);
        slug = Regex.Replace(slug, "-{2,}", "-", RegexOptions.CultureInvariant).Trim('-');
        return slug;
    }

    /// <summary>Is this (slug) a DOS device name, with or without an extension?</summary>
    public static bool IsReservedName(string name) => RepoRead.IsReservedDeviceName(name);

    /// <summary>
    /// The spoken name as a safe single directory name, or BadName. Refuse the
    /// dangerous shapes OUTRIGHT, then slugify what is left — people name things
    /// in English, and refusing "My App" is friction for no safety gained.
    /// </summary>
    public static string SanitiseName(string? raw)
    {
        var name = (raw ?? "").Trim();
        if (name.Length == 0)
            throw new BadName("empty");
        if (name.Length > MaxNameChars)
            throw new BadName("too long");
        if (name.IndexOfAny(ForbiddenChars) >= 0)
            throw new BadName("path separator");
        if (name.Contains(".."))
            throw new BadName("traversal");
        if (name.StartsWith('.'))
            throw new BadName("leading dot");
        if (name.IndexOfAny(WindowsInvalidChars) >= 0 || name.Any(char.IsControl))
            throw new BadName("invalid characters");

        var slug = Slugify(name);
        if (slug.Length == 0)
            throw new BadName("nothing usable");
        // Belt and braces: whatever the slugifier produced must still satisfy
        // the allowlist the rest of this module was written against.
        if (slug.Contains("..") || !SafeName.IsMatch(slug))
            throw new BadName("unsafe characters");
        if (slug.Length > MaxNameChars)
            throw new BadName("too long");
        // Windows strips a trailing dot, so `app.` would become `app` on disk.
        if (slug.EndsWith('.'))
            throw new BadName("trailing dot");
        if (IsReservedName(slug))
            throw new BadName("reserved name");
        return slug;
    }

    /// <summary>
    /// The directory `name` would occupy under `root`, proven contained: resolve
    /// both sides and require that the target's parent IS the root. A link in
    /// the root's own path is resolved on both sides, so it cannot make the
    /// comparison lie. Case-insensitive, as the filesystem is.
    /// </summary>
    public static string TargetFor(string name, string root)
    {
        var rootReal = RepoRead.RealPath(root);
        var target = Path.Combine(rootReal, name);
        var resolved = RepoRead.RealPath(target);
        var parent = Path.GetDirectoryName(resolved.TrimEnd('\\', '/')) ?? "";
        if (!parent.TrimEnd('\\', '/').Equals(rootReal.TrimEnd('\\', '/'), Py.PathComparison)
            || !Path.GetFileName(resolved.TrimEnd('\\', '/')).Equals(name, Py.PathComparison))
            throw new BadName("escapes the projects root");
        return target;
    }

    public static string Readme(string name, string description)
    {
        var body = description.Trim();
        if (body.Length == 0) body = $"{name} — created by JARVIS.";
        return $"# {name}\n\n{body}\n";
    }

    /// <summary>
    /// `git init` in the new directory. False if git is absent or unhappy. Never
    /// throws: a project without a repository is still a project.
    /// </summary>
    public static async Task<bool> GitInit(string path)
    {
        var git = Py.Which("git");
        if (git is null) return false;
        Py.ProcResult proc;
        try
        {
            proc = await Py.Run(git, ["init", "--quiet"], GitTimeoutSec, cwd: path);
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception)
        {
            Log.LogWarning("git init could not start in {Path}: {Error}", path, e.Message);
            return false;
        }
        if (proc.TimedOut)
        {
            Log.LogWarning("git init timed out in {Path}", path);
            return false;
        }
        return proc.ReturnCode == 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    private const int ErrorAlreadyExists = 183;
    private const int ErrorFileExists = 80;

    /// <summary>
    /// `mkdir(exist_ok=False)`: atomic, and the only guard that cannot be raced.
    /// False when the name already exists (as a directory or a file).
    /// </summary>
    public static bool MakeDirExclusive(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (CreateDirectoryW(path, IntPtr.Zero)) return true;
            var err = Marshal.GetLastWin32Error();
            if (err is ErrorAlreadyExists or ErrorFileExists) return false;
            throw new IOException($"could not create {path}", new System.ComponentModel.Win32Exception(err));
        }
        // POSIX mkdir(2): EEXIST is the atomic "taken" answer, as in the Python.
        if (Posix.Mkdir(path, 0x1ED /* 0755 */) == 0) return true;
        var errno = Marshal.GetLastPInvokeError();
        if (errno == Posix.EExist) return false;
        throw new IOException($"could not create {path} (errno {errno})");
    }

    /// <summary>
    /// Make one new project. Throws BadName; otherwise always returns a dict.
    /// {"created": false, "reason": "exists", ...} means the name is taken and
    /// the existing directory was left completely untouched.
    /// </summary>
    public static async Task<Dictionary<string, object?>> Create(string rawName, string description = "",
        string? root = null)
    {
        var name = SanitiseName(rawName);
        root ??= ProjectsRoot();
        Directory.CreateDirectory(root);
        var target = TargetFor(name, root);
        var rootReal = RepoRead.RealPath(root);
        var rootName = Path.GetFileName(rootReal.TrimEnd('\\', '/'));

        // Exclusive, deliberately: an existing directory is never adopted.
        if (!MakeDirExclusive(target))
        {
            return new Dictionary<string, object?>
            {
                ["created"] = false, ["reason"] = "exists", ["name"] = name,
                ["path"] = target, ["root"] = rootReal, ["root_name"] = rootName,
            };
        }

        File.WriteAllText(Path.Combine(target, "README.md"), Readme(name, description ?? ""), Py.Utf8NoBom);
        var gitOk = await GitInit(target);
        Log.LogInformation("created project {Name} at {Path} (git={Git})", name, target, gitOk);
        return new Dictionary<string, object?>
        {
            ["created"] = true, ["name"] = name, ["path"] = target,
            ["root"] = rootReal, ["root_name"] = rootName,
            ["git"] = gitOk,
        };
    }
}
