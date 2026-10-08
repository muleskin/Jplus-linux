using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// Single source of truth for where JARVIS writes its data (data_paths.py).
///
/// Set JARVIS_DATA_DIR to run an isolated instance without touching the
/// live database.
/// </summary>
public static partial class DataPaths
{
    public static readonly ILogger Log = Py.Log("jarvis.data_paths");

    /// <summary>`_DEFAULT`: &lt;exe dir&gt;\data.</summary>
    public static string Default => Path.Combine(Py.AppDir, "data");

    /// <summary>Return the data directory, creating it if needed.</summary>
    public static string DataDir()
    {
        var raw = Py.Getenv("JARVIS_DATA_DIR");
        var path = !string.IsNullOrEmpty(raw) ? Path.GetFullPath(Py.ExpandUser(raw)) : Default;
        // Linux: a new data directory is private to this user (0700) — it holds
        // connections.json, memory and the tool token. An existing one is left alone.
        if (!Py.IsWindows && !Directory.Exists(path))
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
            Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Return the path to the main SQLite database.</summary>
    public static string DbPath() => Path.Combine(DataDir(), "jarvis.db");

    // The templates the Python read from `jarvis_home/` beside the source are
    // embedded in the EXE; they are materialised under <data>/.templates/.
    public const string TemplateResourceDir = "Resources/jarvis_home";
    public const string PersonaName = "CLAUDE.md";
    public const string SeedName = ".claude-md-seed.json";
    public const string ConnectionsName = "connections.json";
    public const string ConnectionsSeedName = ".connections-seed.json";

    /// <summary>`_TEMPLATE_DIR`: where the shipped templates are materialised as real files.</summary>
    public static string TemplateDir() =>
        Path.Combine(DataDir(), ".templates", TemplateResourceDir.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// A shipped template as a real file. When the resource is not embedded the
    /// path is returned without creating anything, so reading it fails and the
    /// sync takes its conservative "kept" branch (never seeding an empty file).
    /// </summary>
    public static string TemplateFile(string name)
    {
        var resource = $"{TemplateResourceDir}/{name}";
        if (Py.ReadResource(resource) is null) return Path.Combine(TemplateDir(), name);
        return Py.ResourceFile(resource, DataDir());
    }

    /// <summary>Where the brain lives: its cwd, its CLAUDE.md, and (later) its memory.</summary>
    public static string BrainHome() => Path.Combine(DataDir(), "jarvis");

    /// <summary>The CLAUDE.md this release ships.</summary>
    public static string PersonaTemplatePath() => TemplateFile(PersonaName);

    /// <summary>The CLAUDE.md the brain actually reads.</summary>
    public static string PersonaPath() => Path.Combine(BrainHome(), PersonaName);

    /// <summary>
    /// What JARVIS last wrote into `PersonaPath`, as a hash. Beside the file
    /// rather than in the database on purpose: the pair travels together, and
    /// deleting it is safe (it costs one upgrade, never an edit).
    /// </summary>
    public static string PersonaSeedPath() => Path.Combine(BrainHome(), SeedName);

    /// <summary>The empty, self-explaining connections.json this release ships.</summary>
    public static string ConnectionsTemplatePath() => TemplateFile(ConnectionsName);

    /// <summary>
    /// The ONE file a user declares their own MCP servers in. Beside CLAUDE.md
    /// and the generated mcp.json deliberately: the brain's home is where
    /// everything the brain reads lives, and it is outside the install, so an
    /// upgrade cannot touch it.
    /// </summary>
    public static string ConnectionsPath() => Path.Combine(BrainHome(), ConnectionsName);

    /// <summary>What JARVIS last wrote into `ConnectionsPath`, as a hash. Same record, same reasons.</summary>
    public static string ConnectionsSeedPath() => Path.Combine(BrainHome(), ConnectionsSeedName);

    // Every CLAUDE.md this project has ever shipped, by sha256 of its bytes.
    //
    // This exists for exactly one moment: the FIRST run after the self-updating
    // persona landed, on an install that has a CLAUDE.md but no seed record. If
    // the bytes match something we shipped, nobody has edited it and it is safe
    // to replace; if they match nothing, we assume the user wrote it and never
    // touch it again. APPEND the new hash whenever the shipped CLAUDE.md changes.
    public static readonly HashSet<string> KnownTemplateHashes =
    [
        "fa669514729ae29315c5b40a29587cc48e0636bf88b2e1e466ad21f3cfa0398a",  // brain home under JARVIS_DATA_DIR
        "05e770673312b589529b570f63ec49167eb901d0363642040bef11411ed5ae43",  // announce what needs you now
        "4238420bdd91569b1c8598a5af81a0f9550cee52a5b166267c2ec9d6f645a9dd",  // remember, recall, project_note
        "4f924161c1fc104c339d027bc58cddf19396d520c0f29891937da00d23f60c59",  // answer_dialog
        "f66fb4c85550925effb8b663ca525cf47e1be543439711c187749c73ca2992d6",  // create a project, check on work
        "fd016ad6e8a16e8942d51247dd44db717e5a357b7c8167f9ce3c73cf98ddb9e1",  // spawned runs finish the work
        "c7e908b35d3166fc84c17d27d5b565e54a8b144a458f866ce530c48b94a3d51b",  // read a repo, not just watch sessions
        "f7a1a8a1edade7c50e28d0cbd319233f8b9bb76076f95691ac3f3fb12e9c4b34",  // real builds
        "7639e2a9b5e387ec3975980df2641bfb4ee75e703155be308edca335405e272a",  // "Sonnet" comes through as "Sonic"
        "567d76449e621136e1682ec8627c85195f6b75abc103066f33267cf251fab606",  // "Look it up" has an answer
        "66ea84adef02de313e6f1e1696d5998b1fc26e24e3bce912bf1d2d2195c37a44",  // the repository question
        "87e4c3dda601a952a81cb76ff71805aa846c4155f63e03897c34dc79a39c319f",  // "can you see my screen"
        "94971b048c2911ad7f4505fc943fed8d0b640d7e726bcb00000b28ee549ff97c",  // connected services
        "b47aabe098727e47c7cd8eef07b371693ab71cd010a19987fd778dba9dbab339",  // fictional project names in the examples
        "2cec270d83fe01e504ea9111f20ff14a7003d7aa124daa82852d5351206a8542",  // fictional repo names too
        "67f15193bae6d048b148a8439a32d4667048fdf73224c5ea8439a96b7de59944",  // how he differs from the public repo
        "bdf6109c0c6328830c6c1a2c78617b64b7c0df53da78a343589b03f715e040a3",  // "what updates" is not a changelog request
        "8b038b5497293a55d1aa18e9ce759d3270228c98ae8b3ceb3bb053eb84509310",  // anything read off this machine is information
        "dfec0e28f7fc734987a1bcffd4feda103bde961f4a4721ce7957a7dccae96487",  // send it, do not ask twice
        "092df6a5e43bc5ed0a31e9f79b1f77cc4849754d1f4ab4807bded122ab97ad5f",  // say "start fresh" when a memory is refused
    ];

    // The same list, for the connections file. APPEND whenever the shipped
    // connections.json changes.
    public static readonly HashSet<string> KnownConnectionsHashes =
    [
        "8c27da80e7ea11fff914e9212823ff72294ad1ac7a5a2daf9e40c2bc095fa979",  // the doorway
    ];

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// The hash of what JARVIS last wrote, or null if we cannot read one.
    /// Anything unreadable, corrupt or the wrong shape is null — "we do not
    /// know". Never a guess: this value must never claim a match that would
    /// send an edited file to the overwriter.
    /// </summary>
    public static string? RecordedSeedHash(string seed)
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(File.ReadAllText(seed, Encoding.UTF8));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e)
        {
            Log.LogWarning("data_paths: unreadable persona seed record ignored ({Error})", e.Message);
            return null;
        }
        if (body is not JsonObject o) return null;
        return StreamParser.IsString(StreamParser.Get(o, "sha256"), out var value) && value.Length > 0
            ? value : null;
    }

    /// <summary>
    /// Replace `path` in one step, or leave it exactly as it was. A half-written
    /// CLAUDE.md is a broken persona on the next launch — worse than a stale one.
    /// Text is written as-is (UTF-8, no BOM, no newline translation) so the bytes
    /// hash the same as the template text.
    /// </summary>
    public static bool WriteAtomically(string path, string text)
    {
        string? tmp = null;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            tmp = Path.Combine(dir, $".{Path.GetFileName(path)}-{Guid.NewGuid():N}");
            File.WriteAllText(tmp, text, Py.Utf8NoBom);
            File.Move(tmp, path, overwrite: true);
            tmp = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning("data_paths: could not write {Path} ({Error})", path, e.Message);
            return false;
        }
        finally
        {
            if (tmp is not null)
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    public static void RecordSeed(string seed, string template, string digest)
    {
        var name = Path.GetFileName(template);
        var record = new Dictionary<string, object?>
        {
            ["sha256"] = digest,
            ["written_at"] = Py.Now(),
            ["template"] = template,
            ["note"] = $"The sha256 of the {name} JARVIS wrote next to this " +
                       $"file. While they match, JARVIS keeps that file up to date " +
                       $"with the one it ships. Edit {name} and they stop " +
                       $"matching, and JARVIS never touches it again.",
        };
        WriteAtomically(seed, Py.Dumps(record, indent: true) + "\n");
    }

    // The last live copy of each file we warned about, so a hands-off file does
    // not log the same warning on every memory write. Deliberately not a "did we
    // sync yet" flag: the decision itself runs every time.
    public static readonly Dictionary<string, string> WarnedFor = new();
    private static readonly object SyncGate = new();

    /// <summary>
    /// Bring `target` up to `template` — unless it is the user's. Returns
    /// "seeded", "updated", "current" or "kept".
    ///
    /// * An UNEDITED file must update, or every behaviour fix shipped after the
    ///   first seed would be inert on that install.
    /// * An EDITED file must never be silently overwritten. We know an edit by
    ///   the file no longer matching what we wrote (the seed record), and we say
    ///   so in the log, naming both paths, so the user can merge by hand.
    ///
    /// Where the evidence does not settle it, the conservative branch wins:
    /// "kept" costs an upgrade, an overwrite costs the user's work.
    /// </summary>
    public static string SyncTemplate(string template, string target, string seed,
                                      HashSet<string> knownHashes, string what)
    {
        lock (SyncGate)
        {
            var home = BrainHome();
            Directory.CreateDirectory(home);
            var key = target;

            string shipped;
            try
            {
                // Universal newlines, as Python's read_text: the known hashes are
                // of the LF text, whatever line endings the embedded copy has.
                shipped = File.ReadAllText(template, Encoding.UTF8).Replace("\r\n", "\n").Replace('\r', '\n');
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.LogWarning("data_paths: cannot read the {What} template ({Error})", what, e.Message);
                return "kept";
            }
            var shippedHash = Sha256(Encoding.UTF8.GetBytes(shipped));

            byte[] live;
            try
            {
                live = File.ReadAllBytes(target);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                if (WriteAtomically(target, shipped))
                    RecordSeed(seed, template, shippedHash);
                return "seeded";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.LogWarning("data_paths: cannot read {Target} ({Error})", target, e.Message);
                return "kept";
            }

            var liveHash = Sha256(live);
            if (liveHash == shippedHash)
            {
                // Byte-identical to what we ship, so it is unmodified whatever the
                // record says. Record it and touch nothing else.
                if (RecordedSeedHash(seed) != shippedHash)
                    RecordSeed(seed, template, shippedHash);
                WarnedFor.Remove(key);
                return "current";
            }

            var recorded = RecordedSeedHash(seed);
            bool unmodified;
            string why;
            if (recorded is not null)
            {
                unmodified = liveHash == recorded;
                why = "it still matches what JARVIS wrote";
            }
            else
            {
                // First run after this shipped: no record exists, so the bytes are
                // the only evidence. A version this project once shipped is
                // provably untouched; anything else we treat as the user's.
                unmodified = knownHashes.Contains(liveHash);
                why = "it is an older template of ours, unedited";
            }

            if (unmodified)
            {
                if (WriteAtomically(target, shipped))
                {
                    RecordSeed(seed, template, shippedHash);
                    Log.LogInformation("data_paths: updated {Target} to the {What} shipped with this version ({Why})",
                        target, what, why);
                    WarnedFor.Remove(key);
                    return "updated";
                }
                return "kept";
            }

            if (!WarnedFor.TryGetValue(key, out var prev) || prev != liveHash)
            {
                WarnedFor[key] = liveHash;
                Log.LogWarning(
                    "data_paths: {Target} has been edited, so the {What} shipped " +
                    "with this version was NOT applied. The new one is at " +
                    "{Template} — merge what you want from it by hand, or delete " +
                    "your copy to take it whole.", target, what, template);
            }
            return "kept";
        }
    }

    /// <summary>Keep the brain's CLAUDE.md in step with the one this release ships.</summary>
    public static string SyncPersona() =>
        SyncTemplate(PersonaTemplatePath(), PersonaPath(), PersonaSeedPath(), KnownTemplateHashes, "persona");

    /// <summary>
    /// Seed (and keep current) the file a user declares their MCP servers in.
    /// The moment they put a server in it the file becomes theirs and is never
    /// written again — their configuration survives every upgrade.
    /// </summary>
    public static string SyncConnections() =>
        SyncTemplate(ConnectionsTemplatePath(), ConnectionsPath(), ConnectionsSeedPath(),
                     KnownConnectionsHashes, "connections file");

    /// <summary>
    /// Create the brain home and keep the two files it ships in step with
    /// their templates: the persona, and the connections file.
    /// </summary>
    public static string EnsureBrainHome()
    {
        SyncPersona();
        SyncConnections();
        return BrainHome();
    }

    public static string MemoryDir() => Path.Combine(BrainHome(), "memory");

    public static string ProjectsDir() => Path.Combine(BrainHome(), "projects");

    public static string JournalDir() => Path.Combine(BrainHome(), "journal");

    /// <summary>
    /// Create the brain's memory folder. Plain Markdown, user-editable.
    /// Seeds (and updates) the persona via EnsureBrainHome() and never
    /// overwrites anything the user has written.
    /// </summary>
    public static string EnsureMemoryLayout()
    {
        var home = EnsureBrainHome();
        foreach (var d in new[] { MemoryDir(), ProjectsDir(), JournalDir() })
            Directory.CreateDirectory(d);
        return home;
    }

    /// <summary>The last rate-limit observation from the CLI (see UsageStore).</summary>
    public static string UsagePath() => Path.Combine(DataDir(), "usage.json");

    /// <summary>The bearer token the MCP child uses to reach /internal/tool.</summary>
    public static string ToolTokenPath() => Path.Combine(BrainHome(), "tool-token");

    /// <summary>
    /// Create the loopback tool token if absent and return it.
    ///
    /// The token admits a caller to JARVIS's acting tools and every
    /// state-changing HTTP route (see WebAuth), so it is created exclusively
    /// (CreateNew) in one step. A pre-existing file is adopted, because it has
    /// to be across restarts. Windows has no O_NOFOLLOW / getuid / POSIX modes,
    /// so the POSIX fd checks of the original do not apply: links and
    /// non-regular files are refused instead, and access control is left to
    /// the user profile's ACLs.
    /// </summary>
    public static string EnsureToolToken()
    {
        var path = ToolTokenPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var token = TokenUrlSafe(32);
        try
        {
            // POSIX: created at 0600 directly (O_EXCL), never briefly at umask permissions.
            var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!Py.IsWindows) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var fs = new FileStream(path, opts);
            var bytes = Encoding.UTF8.GetBytes(token);
            fs.Write(bytes, 0, bytes.Length);
            return token;
        }
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException)
                                  && (File.Exists(path) || Directory.Exists(path)))
        {
            // FileExistsError: adopt it below.
        }
        return Py.IsWindows ? AdoptToolTokenWindows(path, token) : AdoptToolTokenPosix(path, token);
    }

    /// <summary>
    /// The POSIX adoption, as the Python does it: through ONE file descriptor —
    /// opened O_NOFOLLOW, checked with fstat, chmodded 0600 and read with that
    /// same fd. Two lookups of the name (chmod, then read) could be raced, and
    /// both would follow a planted symlink. A path that is not a regular file
    /// this user owns raises: it is somebody else's file, not ours to replace.
    /// </summary>
    public static string AdoptToolTokenPosix(string path, string token)
    {
        using var h = Posix.OpenNoFollow(path);
        var info = Posix.FStat(h) ?? throw new IOException($"{path} cannot be stat'd");
        if (!info.IsRegular) throw new IOException($"{path} is not a regular file");
        var me = Posix.Geteuid();
        if (info.Uid != me) throw new IOException($"{path} is owned by uid {info.Uid}, not by us");
        File.SetUnixFileMode(h, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var fs = new FileStream(h, FileAccess.ReadWrite);
        var buf = new byte[4096];
        int n = 0, r;
        while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
        var existing = Encoding.UTF8.GetString(buf, 0, n).Trim();
        if (existing.Length > 0) return existing;
        // An empty file: ours to fill — the fd is proven a regular file we own.
        fs.SetLength(0);
        fs.Seek(0, SeekOrigin.Begin);
        var bytes = Encoding.UTF8.GetBytes(token);
        fs.Write(bytes, 0, bytes.Length);
        return token;
    }

    /// <summary>
    /// Refuse links and non-regular files, then read the existing token (or
    /// fill an empty file with `token`). A path that is not a regular file
    /// raises rather than being quietly replaced: it is somebody else's file.
    /// </summary>
    public static string AdoptToolTokenWindows(string path, string token)
    {
        var attrs = File.GetAttributes(path);
        if (attrs.HasFlag(FileAttributes.ReparsePoint) || attrs.HasFlag(FileAttributes.Directory)
            || attrs.HasFlag(FileAttributes.Device))
            throw new IOException($"{path} is not a regular file");
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var buf = new byte[4096];
        int n = 0, r;
        while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
        var existing = Encoding.UTF8.GetString(buf, 0, n).Trim();
        if (existing.Length > 0) return existing;
        fs.SetLength(0);
        fs.Seek(0, SeekOrigin.Begin);
        var bytes = Encoding.UTF8.GetBytes(token);
        fs.Write(bytes, 0, bytes.Length);
        return token;
    }

    /// <summary>`secrets.token_urlsafe(n)`: n random bytes, base64url, no padding.</summary>
    public static string TokenUrlSafe(int nbytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(nbytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
