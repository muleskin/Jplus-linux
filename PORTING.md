# Porting conventions: jarvis (Python) → Jplus (C#)

Source: `C:\Users\Jedd\Desktop\Developing\Private\jarvis` (read-only — never edit it).
Target: `C:\Users\Jedd\Desktop\Developing\Private\Jplus` — .NET 10, ASP.NET Core,
`net10.0-windows`, one self-contained single-file EXE that can run as a Windows service.

Several people port different Python modules **at the same time** and cannot see each
other's C# until the end. These rules exist so the pieces fit together by construction.
Follow them mechanically; when in doubt, choose the most literal mapping.

## Files and names

- Python module `foo_bar.py` → `src/FooBar.cs`, `public static partial class FooBar`
  in `namespace Jplus;` (file-scoped). Exception: `server.py` → `public static partial class Server`
  split across `src/Server.Part1.cs`, `Server.Part2.cs`, `Server.Part3.cs`.
  `migrations/001_dispatches_to_runs.py` → `src/Migrations.cs`, `Migrations.Migrate001()`.
- Module-level function `do_thing` → `public static ... DoThing(...)` on that class.
  Private `_do_thing` → `public static ... DoThing(...)` too (make everything public —
  other modules and server.py do call "private" helpers). If `_x` and `x` both exist,
  the private one becomes `XPrivate`.
- Module constants / globals (`TAIL_BYTES`, `_DEFAULT_ROOTS`, `log`) → PascalCase
  static members: `TailBytes`, `DefaultRoots`. Mutable module globals → `public static` fields.
- **Keep the async-ness of the Python**: `async def f` → `public static async Task<T> F(...)`
  (or `Task` for None). NO `Async` suffix. Plain `def` stays synchronous.
  Something Python ran via `asyncio.to_thread(f)` → `await Task.Run(() => F())`.
- **Every Python class is nested inside its module's static class** and referenced as
  `Module.ClassName` (PascalCase, leading `_` dropped): `run_store.RunStatus` → `RunStore.RunStatus`,
  `session_watch.SessionState` → `SessionWatch.SessionState`, `speech.SpeechScheduler` →
  `Speech.SpeechScheduler`, `repo_read.Window` → `RepoRead.Window`, `screen.Window` → `Screen.Window`,
  `web_auth.OriginGuard` → `WebAuth.OriginGuard`. Nested classes are `public` (non-static) classes.
  The one module whose class has its own name: `brain.py` → static class **`BrainMod`**, so the
  class is `BrainMod.Brain`, `BrainMod.BrainConfig`, `BrainMod.TurnResult`.
  Exceptions (`class Refused(Exception)`) → nested `public class Refused : Exception`.
  `NamedTuple` → nested `public sealed record`.
- Dataclass fields → PascalCase auto-properties (`session_id` → `SessionId`).
  JSON serialization uses `Py.Json` (snake_case naming policy), so they come out as
  `session_id` on the wire automatically. Dataclass methods / @property → methods/properties
  PascalCase. `@property foo` → property `Foo`.
- Keyword args keep their names in camelCase as optional C# parameters
  (`def f(x, *, limit=5)` → `F(object x, int limit = 5)`).
- Enum-ish string constants (`RunStatus.RUNNING = "running"`) → `public const string Running = "running";`
  The **values** stay exactly as in Python (they are on the wire and in the DB).

## Types

| Python | C# |
|---|---|
| `str` / `Optional[str]` | `string` / `string?` |
| `int` | `int` (use `long` for token counts, byte offsets, sizes, epoch ms) |
| `float` (incl. epoch seconds from `time.time()`) | `double` |
| `bool` | `bool` |
| `Path` | `string` (full path). `Path.home()` → `Py.Home`; `expanduser` → `Py.ExpandUser` |
| `list[T]` | `List<T>` |
| `dict` used as a record/row returned by a function, or sent as JSON | `Dictionary<string, object?>` with the **exact same snake_case keys** |
| `dict` parsed from JSON (stream-json lines, transcripts, roster files, tool args) | `JsonObject` / `JsonNode` (System.Text.Json.Nodes) |
| `set[str]` / `frozenset` | `HashSet<string>` |
| tuple return | C# tuple `(T1, T2)` with field names matching the Python variable names |
| `bytes` | `byte[]` |
| `None` return | `null` |

Tool handlers in server.py receive `args: dict` → `JsonObject args`.
Helpers: `Py.Str(node, "key")`, `Py.Num(node, "key")`, `Py.ToClr(node)`, `Py.Loads`, `Py.TryLoads`,
`Py.Dumps(obj)`.

## Runtime helpers (src/Py.cs — already written, read it)

`Py.Now()` (time.time), `Py.Monotonic()`, `Py.Getenv`, `Py.EnvFlag`, `Py.Truthy`, `Py.Home`,
`Py.ExpandUser`, `Py.AppDir` (stand-in for `Path(__file__).parent`), `Py.ExePath`,
`Py.Which` (shutil.which), `Py.Run(exe, args, timeoutSeconds, cwd, env, stdin)` (async
subprocess → `ProcResult(ReturnCode, Stdout, Stderr, TimedOut)`; throws FileNotFoundException
if the exe can't start), `Py.PidAlive`, `Py.ShellOpen(url/path)`, `Py.Log("jarvis.x")`
(`logging.getLogger`, returns `ILogger` — use `log.LogInformation(...)` etc.),
`Py.ReadResource("Resources/...")`, `Py.ResourceFile(resourcePath, dataDir)`,
`Py.WriteAtomic(path, text)`, `Py.Spawn(() => work(), "what")` (create_task fire-and-forget),
`Py.IsServiceSession` + `Py.NoDesktopReason`.

Regex: `System.Text.RegularExpressions.Regex`. Python `re` differences to watch: `\Z` → `\z`,
`(?P<name>...)` → `(?<name>...)`, `re.match` anchors at start (`^` or `\G`), `fullmatch` → `^...$`
with `\z`. Use `RegexOptions.CultureInvariant`. `str.split()` with no args splits on any whitespace
and drops empties. `str.title()`, `.capitalize()` — write small helpers locally if needed.

SQLite: `Microsoft.Data.Sqlite` (`SqliteConnection`). Keep the schema, table and column names
**identical** — the C# app must open a database the Python app wrote, and vice versa.
HTTP client: one shared `static readonly HttpClient`. JSON: `System.Text.Json`.

## Embedded files

The EXE embeds `Resources/jarvis_home/CLAUDE.md`, `Resources/jarvis_home/connections.json`,
`Resources/env.example` and the built frontend (`frontend/dist/**`). Where Python pointed at
a file beside the source (`_TEMPLATE_DIR / "CLAUDE.md"`, `.env.example`), use
`Py.ResourceFile("Resources/jarvis_home/CLAUDE.md", DataPaths.DataDir())` to get a real path,
or `Py.ReadResource(...)` for the text.

Default data dir = `Path.Combine(Py.AppDir, "data")`; `.env` = `Path.Combine(Py.AppDir, ".env")`.

## Windows platform mapping (the original is macOS-only)

The Python shells out to `osascript`, `screencapture`, `sips`, `ps`, `pgrep`, `open`, and uses
Playwright. Map them to Windows, keeping the **same function signatures, return shapes and
spoken/refusal wording style**:

- `open <url>` / opening Chrome → `Py.ShellOpen(url)`; a specific browser → find `chrome.exe` /
  `msedge.exe` under Program Files / LocalAppData and start it with the URL.
- Opening Terminal at a directory → `wt.exe -d <dir>` if `Py.Which("wt")`, else
  `cmd.exe /K cd /d <dir>` in a new window (UseShellExecute=true).
- `osascript` window lists → Win32 `EnumWindows` / `GetWindowText` / `IsWindowVisible` P/Invoke.
- `screencapture` + `sips` → `System.Drawing` (`Graphics.CopyFromScreen`, resize, encode PNG/JPEG).
- macOS notifications → a Windows toast via `powershell.exe -NoProfile -Command` using
  `Windows.UI.Notifications` (best effort).
- `ps -o tty`, `pgrep` → `System.Diagnostics.Process`.
- Unix sockets (session inbox) → `System.Net.Sockets` with `UnixDomainSocketEndPoint`
  (supported on Windows 10+); keep the wire format.
- Playwright (`browser.py`) → headless Microsoft Edge / Chrome command line
  (`--headless=new --dump-dom <url>` for HTML; `--screenshot=<file> --window-size=W,H` for an image).
  No Playwright dependency.
- **Every desktop-facing action** (open a window, screenshot, keystrokes, toast) must first check
  `Py.IsServiceSession`; when true, return the module's normal failure/refusal result using
  `Py.NoDesktopReason` as the explanation. A service in session 0 cannot touch the user's desktop.
- `claude` CLI: found via `Py.Which("claude")` (on Windows it's `claude.exe` or `claude.cmd`);
  honour `JARVIS_CLAUDE_PATH` exactly as the Python does. `.cmd` shims must be launched as
  `cmd.exe /c <path> args...` — `ClaudeEnv.SplitCommand` handles that (see ClaudeEnv).
- `~/.claude` → `Path.Combine(Py.Home, ".claude")`. Paths in Claude Code transcripts on Windows
  use backslashes and drive letters; `encode_cwd` must map `:` `\` `/` `.` etc. the way Claude
  Code does (every non-alphanumeric char → `-`).
- POSIX permission bits / O_NOFOLLOW / getuid / fchmod → skip on Windows (the Python already has
  a Windows branch in places — follow it).

## Web layer (server.py, web_auth.py)

- FastAPI route → ASP.NET minimal API inside `public static void MapRoutes{1,2,3}(WebApplication app)`
  in the matching Server part. Same path, verb, query parameter names, defaults and JSON body
  field names. Return `Results.Json(obj, Py.Json)` or a `Dictionary<string, object?>`.
  `HTTPException(status_code=404, detail="x")` → `Results.Json(new Dictionary<string,object?>{["detail"]="x"}, Py.Json, statusCode: 404)`.
  Pydantic request models → small classes with `[JsonPropertyName("snake_name")]` or relying on the
  snake_case policy; validate the same way and return 422 with `{"detail": ...}` on bad input.
- WebSocket endpoint → `app.Map("/ws/x", async (HttpContext ctx) => { if (!ctx.WebSockets.IsWebSocketRequest) ...; using var ws = await ctx.WebSockets.AcceptWebSocketAsync(); ... })`.
  Text frames carry JSON; binary frames carry audio (mp3) exactly like the Python.
- `web_auth.OriginGuard` (ASGI middleware) → `public sealed class OriginGuard` nested in
  `WebAuth`, an ASP.NET middleware with ctor `(RequestDelegate next)` and `Task InvokeAsync(HttpContext)`.
  `Program.cs` calls `app.UseMiddleware<WebAuth.OriginGuard>()`.
- Program.cs (already written — read it) calls: `Server.BootLoadEnv()` (the module-level `.env`
  loader at the top of server.py), `Server.LifespanStart()` / `Server.LifespanStop()` (the two halves
  of `lifespan`), `Server.MapRoutes1/2/3(app)`, `JarvisMcp.Main(string[] args)` (returns `Task<int>`),
  `WebAuth.JarvisDefaultHost`, `DataPaths.DataDir()`. It serves `/`, `/dashboard`, `/assets/**` from
  the embedded frontend — don't port the StaticFiles block. `/api/restart` calls `Program.Restart()`
  after its 2-second delay instead of `os.execv`.
- The brain's MCP child: Python wrote an mcp.json whose command was `python jarvis_mcp.py`.
  In C# the command is `Py.ExePath` with args `["mcp"]` — the same EXE in its `mcp` role.

## Quality bar

- Port **all** behaviour in your files, including comments that explain non-obvious *why*s
  (shorten long essays to the essential sentence or two). Do not stub logic out unless this
  document says the feature is unavailable on Windows.
- Spoken strings, refusal texts, prompts sent to Claude, JSON keys, DB schema, status values:
  **byte-identical** to the Python unless a macOS-ism must change (e.g. "Terminal.app" → "Windows Terminal").
- Don't port the tests. Don't add NuGet packages beyond those in Jplus.csproj
  (Microsoft.Data.Sqlite, Microsoft.Extensions.FileProviders.Embedded,
  Microsoft.Extensions.Hosting.WindowsServices, System.Drawing.Common).
- Your files must be syntactically valid C#. You cannot run a full build until everyone is done
  (other modules don't exist yet) — that integration step happens afterwards. When you call into
  a module someone else is porting, use the name these rules produce and move on.
- At the end, reply with: the files you wrote, every public signature other modules are likely
  to call (one line each), and anything you had to change or could not port.
