# Jplus — Linux edition

JARVIS (the voice assistant for Claude Code, originally Python + FastAPI for macOS)
ported to C# / .NET 10: one self-contained `Jplus` binary that runs in a terminal or
as a systemd service. The Vite frontend (orb + `/dashboard`) is embedded in the
binary and unchanged; the HTTP/WebSocket API is the same as the Python server's.

This tree is the Linux conversion of the Windows port (`../Jplus`). The source is
cross-platform: every Windows code path is still there behind `OperatingSystem.IsWindows()`,
and the same project builds `win-x64` too. Source of the original port: `../jarvis`;
conventions: [PORTING.md](PORTING.md).

## Build

The only prerequisite is the **.NET 10 SDK** (plus Node/npm the first time, to build the
frontend). If you don't have it, the bundled `dotnet-install.sh` installs it to
`~/.dotnet` without root:

```bash
./dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"     # add to ~/.bashrc to persist
```

A `Makefile` wraps the common tasks (`make help` lists them; the RID follows the host
architecture, override with `RID=linux-arm64`):

```bash
make publish      # self-contained single-file binary -> dist/<rid>/Jplus
make package      # + dist/jplus-<rid>.tar.gz (binary, README, env.example)
make install      # per-user: ~/.local/lib/jplus/Jplus, on PATH as `jplus`; .env seeded from the template
make service      # install, then register and start the systemd user unit
make uninstall    # remove the unit and the binary (data/ and .env are kept)
```

Without make:

```bash
dotnet publish -c Release -r linux-x64 -o publish/linux-x64
```

(`-r linux-arm64` for a Raspberry Pi / ARM box.) Produces `publish/linux-x64/Jplus`
(~54 MB, no other files, no .NET runtime or libicu needed on the target). If
`frontend/dist` is missing the build runs `npm ci && npm run build` in `frontend/` first.
It builds from Windows or Linux alike.

## Run

```bash
./Jplus --port 8340
```

Then open Chrome at `https://localhost:8340` (voice) or `/dashboard`. Options:
`--host` (default `127.0.0.1`), `--port` (default 8340), `--no-ssl`.

Beside the binary it uses:
- `.env` — settings (`FISH_API_KEY`, `USER_NAME`, …, same names as the Python `.env`).
- `cert.pem` / `key.pem` — generated (self-signed, localhost) on first start if absent; the key is 0600.
- `data/` — SQLite runs DB, memory Markdown, brain home, `jplus.log`. Created 0700.
  Override with `JARVIS_DATA_DIR`.

Requires Claude Code installed and logged in (`claude` on PATH or `JARVIS_CLAUDE_PATH`).

## systemd service

```bash
./Jplus install            # per-user unit: ~/.config/systemd/user/jplus.service
./Jplus start              # = systemctl --user start jplus.service
journalctl --user -u jplus.service -f
```

The unit runs as **you** — the account logged in to Claude Code, whose `~/.claude`
holds the login and the session roster. It records the installing shell's `PATH`,
so a `claude` in `~/.local/bin`, npm-global or nvm is found.

- Run from a desktop terminal, `install` ties the unit to `graphical-session.target`
  (`--desktop`), so JARVIS starts with your desktop and can open windows, take
  screenshots and post notifications. GNOME and KDE export `DISPLAY` /
  `WAYLAND_DISPLAY` to the user manager automatically. On other desktops, add
  `systemctl --user import-environment DISPLAY WAYLAND_DISPLAY XAUTHORITY` to your
  session startup.
- `--headless` starts it with the user manager instead. Add
  `loginctl enable-linger $USER` to have it up at boot without logging in.
- `sudo ./Jplus install --system --user NAME` writes a system unit running as NAME
  (always headless).
- `uninstall | start | stop` take `--system` for the system unit.
  `/api/restart` works under systemd: the process exits non-zero and
  `Restart=on-failure` brings it back.

Without a desktop session (no `DISPLAY` / `WAYLAND_DISPLAY`), opening a
terminal/browser/editor, screenshots, the window list and notifications refuse
with an explanation. Voice, the brain, runs, the dashboard, session watching,
steering, memory, specs/builds, usage and answering prompts in tmux sessions all work.

## Optional desktop tools

| Feature | Uses (first one found) |
|---|---|
| open a terminal | `$JARVIS_TERMINAL`, else gnome-terminal, ptyxis, konsole, xfce4-terminal, kitty, alacritty, wezterm, foot, tilix, terminator, x-terminal-emulator, xterm |
| open a browser | google-chrome / chromium (Chrome), firefox |
| open in editor | `code` (VS Code), else `xdg-open` |
| `read_page` / `look_at_page` | headless google-chrome / chromium / microsoft-edge |
| `look_at_screen` | `$JARVIS_SCREENSHOT_CMD` (with `{out}`), else grim (sway/Hyprland), gnome-screenshot, spectacle (KDE), maim, scrot, ImageMagick `import` |
| window list | xprop (X11), swaymsg (sway), hyprctl (Hyprland). GNOME/KDE Wayland expose none: refused rather than answered with a partial list |
| `answer_dialog` keystroke | tmux or WezTerm (the pane that owns the session's pty) |
| notifications | `notify-send` |
| fast `search_code` | `rg` (falls back to a built-in walk) |

`answer_dialog` presses a key only in the tmux / WezTerm pane whose pty is the
session's controlling terminal (`/proc/<pid>/stat`). It never focuses a window, and it
never falls back to "the front window". Run Claude Code inside tmux if you want JARVIS
to answer its permission prompts. A session in a plain GNOME Terminal tab reports
`not_found`.

## Roles of the one binary

| Command | What it is |
|---|---|
| `Jplus [--port N]` | the server (`server.py`) |
| `Jplus mcp` | the brain's stdio MCP child (`jarvis_mcp.py`); written into `mcp.json`, spawned by Claude Code |
| `Jplus install/uninstall/start/stop` | systemd unit management (Windows: service management) |

## Platform mapping

| Python (macOS) | Windows | Linux |
|---|---|---|
| AppleScript Terminal | Windows Terminal (`wt`), else cmd | the terminal table above, `cd` + command via `$SHELL -c` |
| AppleScript Chrome / `open` | Chrome / default browser | Chrome/Chromium / `xdg-open` |
| Playwright | headless Chrome/Edge | headless Chrome/Chromium/Edge |
| `screencapture` / window list | GDI / `EnumWindows` | screenshot tool + built-in PNG codec / xprop, swaymsg, hyprctl |
| keystroke into a Terminal.app tab | `WriteConsoleInput` | `tmux send-keys` / `wezterm cli send-text` |
| session inbox Unix socket | named pipe | Unix socket (as the original) |
| macOS notification | toast | `notify-send` |
| POSIX 0600 / O_NOFOLLOW token checks | ACLs | restored (statx, `open(O_NOFOLLOW)`, fchmod) |
