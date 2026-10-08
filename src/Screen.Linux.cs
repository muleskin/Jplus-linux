using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jplus;

/// <summary>
/// Screen on Linux. System.Drawing does not run here, so:
///
/// * The picture comes from whichever screenshot tool the desktop has —
///   `grim` (wlroots Wayland), `gnome-screenshot`, `spectacle` (KDE), `maim`,
///   `scrot` or ImageMagick `import` (X11) — written into a private 0700
///   temp directory that is deleted straight after, as the Python's was.
///   `JARVIS_SCREENSHOT_CMD` overrides the choice: a command line that writes a
///   PNG to the path given by `{out}`.
/// * Cropping to one monitor, shrinking to ShotMaxEdge and the blank-frame
///   check run in-process on a minimal PNG codec (zlib from the BCL), so no
///   image library or ImageMagick is required.
/// * The window list comes from `xprop` (EWMH) on X11, `swaymsg` on sway and
///   `hyprctl` on Hyprland. Other Wayland compositors (GNOME, KDE) expose no
///   window list to clients: that is reported, never answered with a partial
///   XWayland-only list, which would be a lie about what is open.
/// </summary>
public static partial class Screen
{
    // ── capture ────────────────────────────────────────────────────────────────

    private sealed record ShotTool(string Exe, Func<string, string[]> Args, bool Wayland, bool X11);

    private static readonly ShotTool[] ShotTools =
    [
        new("grim", o => [o], Wayland: true, X11: false),
        new("gnome-screenshot", o => ["-f", o], Wayland: true, X11: true),
        new("spectacle", o => ["-b", "-n", "-f", "-o", o], Wayland: true, X11: true),
        new("maim", o => [o], Wayland: false, X11: true),
        new("scrot", o => ["-o", o], Wayland: false, X11: true),
        new("import", o => ["-window", "root", o], Wayland: false, X11: true),
    ];

    private static async Task<Shot> LinuxCapture(int? display)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "jarvis-shot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var outFile = Path.Combine(tmp, "screen.png");
            var (exe, args) = ChooseShotCommand(outFile)
                ?? throw new ScreenError("I have no screenshot tool to use, sir — install grim, gnome-screenshot, " +
                                         "spectacle, maim or scrot");
            var r = await Py.Run(exe, args, timeoutSeconds: CaptureTimeoutSec);
            if (r.TimedOut || r.ReturnCode != 0 || !File.Exists(outFile))
            {
                Log.LogWarning("screen capture with {Tool} failed ({Code}): {Err}", Path.GetFileName(exe), r.ReturnCode,
                    r.Stderr.Length > 200 ? r.Stderr[..200] : r.Stderr.Trim());
                throw new ScreenError("I couldn't get a picture of your screen, sir");
            }
            var raw = await File.ReadAllBytesAsync(outFile);
            var monitors = await LinuxMonitors();
            return await Task.Run(() => ProcessShot(raw, display, monitors))
                .WaitAsync(TimeSpan.FromSeconds(ResizeTimeoutSec));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    /// <summary>The screenshot tool a capture would use right now, by name (for preflight), or null.</summary>
    public static string? LinuxShotToolName() =>
        ChooseShotCommand(Path.Combine(Path.GetTempPath(), "probe.png")) is { } c ? Path.GetFileName(c.exe) : null;

    private static (string exe, List<string> args)? ChooseShotCommand(string outFile)
    {
        var custom = Py.Getenv("JARVIS_SCREENSHOT_CMD");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var argv = ClaudeEnv.ShellSplit(custom).Select(a => a.Replace("{out}", outFile)).ToList();
            if (argv.Count > 0 && Py.Which(argv[0]) is { } p) return (p, argv.Skip(1).ToList());
        }
        foreach (var t in ShotTools)
        {
            if (Py.IsWayland ? !t.Wayland : !t.X11) continue;
            if (Py.Which(t.Exe) is { } exe) return (exe, t.Args(outFile).ToList());
        }
        return null;
    }

    /// <summary>Monitor rectangles (primary first, then left-to-right) from `xrandr --listmonitors`; [] if unknown.</summary>
    private static async Task<List<(int X, int Y, int W, int H)>> LinuxMonitors()
    {
        if (Py.Which("xrandr") is not { } xrandr) return [];
        try
        {
            var r = await Py.Run(xrandr, ["--listmonitors"], timeoutSeconds: 3);
            if (r.ReturnCode != 0) return [];
            // " 0: +*eDP-1 1920/344x1080/194+0+0  eDP-1"
            var re = new Regex(@"^\s*\d+:\s+\+(\*?)\S+\s+(\d+)/\d+x(\d+)/\d+\+(-?\d+)\+(-?\d+)", RegexOptions.CultureInvariant);
            var found = new List<(bool primary, (int, int, int, int) r)>();
            foreach (var line in r.Stdout.Split('\n'))
            {
                var m = re.Match(line);
                if (!m.Success) continue;
                found.Add((m.Groups[1].Value == "*",
                    (int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))));
            }
            return found.OrderByDescending(f => f.primary).ThenBy(f => f.r.Item1).ThenBy(f => f.r.Item2).Select(f => f.r).ToList();
        }
        catch { return []; }
    }

    private static Shot ProcessShot(byte[] raw, int? display, List<(int X, int Y, int W, int H)> monitors)
    {
        Rgb img;
        try { img = PngCodec.Decode(raw); }
        catch (Exception e)
        {
            Log.LogWarning("could not decode the capture: {Error}", e.Message);
            throw new ScreenError("I couldn't get a picture of your screen, sir");
        }

        if (display is int d && d > 0)
        {
            if (monitors.Count > 0 && d > monitors.Count)
            {
                Log.LogWarning("screen capture: no display {Display} ({Count} attached)", d, monitors.Count);
                throw new ScreenError("I couldn't get a picture of your screen, sir");
            }
            if (monitors.Count >= d) img = img.CropOrSelf(monitors[d - 1]);
            else if (d > 1) throw new ScreenError("I couldn't get a picture of your screen, sir");
        }
        else if (monitors.Count > 1)
        {
            // The main display, as on the other platforms: a whole multi-monitor
            // desktop downscales to an unreadable strip.
            img = img.CropOrSelf(monitors[0]);
        }

        if (Math.Max(img.Width, img.Height) > ShotMaxEdge)
        {
            var (w, h) = FitWithin(img.Width, img.Height, ShotMaxEdge);
            img = img.Shrink(w, h);
        }
        var png = PngCodec.Encode(img);
        var size = PngSize(png) ?? throw new ScreenError("I couldn't get a picture of your screen, sir");
        if (png.Length > MaxShotBytes)
            throw new ScreenError("that picture came out far too large to send, sir");

        var (sw, sh) = FitWithin(img.Width, img.Height, BlankSampleEdge);
        var sample = img.Shrink(sw, sh);
        var pixels = new List<(int, int, int)>(sw * sh);
        for (int i = 0; i < sw * sh; i++)
            pixels.Add((sample.Data[i * 3 + 2], sample.Data[i * 3 + 1], sample.Data[i * 3]));
        if (IsBlank(pixels))
            throw new ScreenError(
                "your screen came back blank, sir — which usually means the screen is locked " +
                "or the compositor refused the capture rather than that there's nothing there");
        return new Shot(png, size.width, size.height);
    }

    /// <summary>An 8-bit RGB image, row-major, 3 bytes per pixel.</summary>
    public sealed class Rgb(int width, int height, byte[] data)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte[] Data { get; } = data;

        public Rgb CropOrSelf((int X, int Y, int W, int H) r)
        {
            if (r.X < 0 || r.Y < 0 || r.W <= 0 || r.H <= 0 || r.X + r.W > Width || r.Y + r.H > Height) return this;
            var o = new byte[r.W * r.H * 3];
            for (int y = 0; y < r.H; y++)
                Buffer.BlockCopy(Data, ((r.Y + y) * Width + r.X) * 3, o, y * r.W * 3, r.W * 3);
            return new Rgb(r.W, r.H, o);
        }

        /// <summary>Area-average downscale (each output pixel is the mean of the source box it covers).</summary>
        public Rgb Shrink(int w, int h)
        {
            if (w >= Width && h >= Height) return this;
            var o = new byte[w * h * 3];
            for (int oy = 0; oy < h; oy++)
            {
                int y0 = (int)((long)oy * Height / h), y1 = Math.Max(y0 + 1, (int)((long)(oy + 1) * Height / h));
                for (int ox = 0; ox < w; ox++)
                {
                    int x0 = (int)((long)ox * Width / w), x1 = Math.Max(x0 + 1, (int)((long)(ox + 1) * Width / w));
                    long r = 0, g = 0, b = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int p = (y * Width + x0) * 3;
                        for (int x = x0; x < x1; x++, p += 3) { r += Data[p]; g += Data[p + 1]; b += Data[p + 2]; }
                    }
                    long n = (long)(y1 - y0) * (x1 - x0);
                    int q = (oy * w + ox) * 3;
                    o[q] = (byte)(r / n); o[q + 1] = (byte)(g / n); o[q + 2] = (byte)(b / n);
                }
            }
            return new Rgb(w, h, o);
        }
    }

    /// <summary>Just enough PNG: decode non-interlaced 8/16-bit gray/RGB/palette/alpha; encode 8-bit RGB.</summary>
    public static class PngCodec
    {
        public static Rgb Decode(byte[] png)
        {
            if (PngSize(png) is null) throw new InvalidDataException("not a PNG");
            int pos = 8, width = 0, height = 0, depth = 0, color = 0, interlace = 0;
            byte[]? palette = null;
            using var idat = new MemoryStream();
            while (pos + 8 <= png.Length)
            {
                int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
                var type = Encoding.ASCII.GetString(png, pos + 4, 4);
                if (len < 0 || pos + 12 + len > png.Length) throw new InvalidDataException("truncated chunk");
                var body = png.AsSpan(pos + 8, len);
                switch (type)
                {
                    case "IHDR":
                        width = BinaryPrimitives.ReadInt32BigEndian(body);
                        height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                        depth = body[8]; color = body[9]; interlace = body[12];
                        break;
                    case "PLTE": palette = body.ToArray(); break;
                    case "IDAT": idat.Write(body); break;
                }
                pos += 12 + len;
                if (type == "IEND") break;
            }
            if (interlace != 0) throw new InvalidDataException("interlaced PNG");
            int channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException("color type") };
            if (!(depth == 8 || (depth == 16 && color != 3))) throw new InvalidDataException($"bit depth {depth}");
            if ((long)width * height > 100_000_000) throw new InvalidDataException("image too large");
            int bps = depth / 8, bpp = channels * bps, stride = width * bpp;

            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            var raw = new byte[(long)(stride + 1) * height];
            int read = 0, n;
            while (read < raw.Length && (n = z.Read(raw, read, raw.Length - read)) > 0) read += n;
            if (read < raw.Length) throw new InvalidDataException("short image data");

            var rgb = new byte[width * height * 3];
            var prev = new byte[stride];
            var cur = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                int f = raw[y * (stride + 1)];
                Buffer.BlockCopy(raw, y * (stride + 1) + 1, cur, 0, stride);
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    cur[i] = (byte)(cur[i] + f switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) >> 1,
                        4 => Paeth(a, b, c),
                        _ => throw new InvalidDataException("filter"),
                    });
                }
                for (int x = 0; x < width; x++)
                {
                    int s = x * bpp, d = (y * width + x) * 3;
                    switch (color)
                    {
                        case 0: case 4: rgb[d] = rgb[d + 1] = rgb[d + 2] = cur[s]; break;
                        case 2: case 6: rgb[d] = cur[s]; rgb[d + 1] = cur[s + bps]; rgb[d + 2] = cur[s + 2 * bps]; break;
                        case 3:
                            int idx = cur[s] * 3;
                            if (palette is null || idx + 2 >= palette.Length) throw new InvalidDataException("palette");
                            rgb[d] = palette[idx]; rgb[d + 1] = palette[idx + 1]; rgb[d + 2] = palette[idx + 2];
                            break;
                    }
                }
                (prev, cur) = (cur, prev);
            }
            return new Rgb(width, height, rgb);
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        public static byte[] Encode(Rgb img)
        {
            int stride = img.Width * 3;
            using var zbuf = new MemoryStream();
            using (var z = new ZLibStream(zbuf, CompressionLevel.Optimal, leaveOpen: true))
            {
                var row = new byte[stride + 1];
                for (int y = 0; y < img.Height; y++)
                {
                    row[0] = 1;   // Sub filter: cheap and good on screen content
                    int o = y * stride;
                    for (int i = 0; i < stride; i++)
                        row[i + 1] = (byte)(img.Data[o + i] - (i >= 3 ? img.Data[o + i - 3] : 0));
                    z.Write(row);
                }
            }
            using var ms = new MemoryStream();
            ms.Write(PngMagic);
            var ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr, img.Width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), img.Height);
            ihdr[8] = 8; ihdr[9] = 2;
            Chunk(ms, "IHDR", ihdr);
            Chunk(ms, "IDAT", zbuf.ToArray());
            Chunk(ms, "IEND", []);
            return ms.ToArray();
        }

        private static void Chunk(Stream s, string type, byte[] body)
        {
            Span<byte> b4 = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b4, body.Length);
            s.Write(b4);
            var t = Encoding.ASCII.GetBytes(type);
            s.Write(t);
            s.Write(body);
            uint crc = Crc32(Crc32(0xFFFFFFFFu, t), body) ^ 0xFFFFFFFFu;
            BinaryPrimitives.WriteUInt32BigEndian(b4, crc);
            s.Write(b4);
        }

        private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        private static uint Crc32(uint crc, byte[] data)
        {
            foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }

    // ── window list ────────────────────────────────────────────────────────────

    private static async Task<List<Window>> LinuxListWindows()
    {
        if (Py.IsWayland)
        {
            if (!string.IsNullOrEmpty(Py.Getenv("SWAYSOCK")) && Py.Which("swaymsg") is { } sway) return await SwayWindows(sway);
            if (!string.IsNullOrEmpty(Py.Getenv("HYPRLAND_INSTANCE_SIGNATURE")) && Py.Which("hyprctl") is { } hypr) return await HyprWindows(hypr);
            throw new ScreenError("this Wayland desktop doesn't let me read its window list, sir");
        }
        if (Py.Which("xprop") is not { } xprop)
            throw new ScreenError("I need xprop to read what's open, sir");
        return await X11Windows(xprop);
    }

    private static readonly Regex XIdRe = new(@"0x[0-9a-fA-F]+", RegexOptions.CultureInvariant);

    private static async Task<List<Window>> X11Windows(string xprop)
    {
        var root = await Py.Run(xprop, ["-root", "_NET_CLIENT_LIST_STACKING", "_NET_ACTIVE_WINDOW"], timeoutSeconds: WindowsTimeoutSec);
        if (root.ReturnCode != 0) throw new ScreenError("I couldn't read what's open, sir");
        List<string> stacking = [];
        string? active = null;
        foreach (var line in root.Stdout.Split('\n'))
        {
            if (line.StartsWith("_NET_CLIENT_LIST_STACKING", StringComparison.Ordinal))
                stacking = XIdRe.Matches(line).Select(m => m.Value).ToList();
            else if (line.StartsWith("_NET_ACTIVE_WINDOW", StringComparison.Ordinal))
                active = XIdRe.Match(line) is { Success: true } m ? m.Value : null;
        }
        if (stacking.Count == 0 && !root.Stdout.Contains("_NET_CLIENT_LIST_STACKING"))
            throw new ScreenError("your window manager doesn't publish a window list, sir");
        stacking.Reverse();   // EWMH stacking is bottom-to-top; front first, like EnumWindows

        long activeId = active is null ? -1 : Convert.ToInt64(active, 16);
        var windows = new List<Window>();
        foreach (var id in stacking)
        {
            if (windows.Count >= MaxWindows) break;
            var r = await Py.Run(xprop, ["-id", id, "_NET_WM_NAME", "WM_NAME", "_NET_WM_PID", "WM_CLASS", "_NET_WM_STATE", "_NET_WM_WINDOW_TYPE"],
                timeoutSeconds: WindowsTimeoutSec);
            if (r.ReturnCode != 0) continue;
            string? title = null, wmName = null, cls = null;
            int pid = 0;
            bool hidden = false, normal = true;
            foreach (var line in r.Stdout.Split('\n'))
            {
                if (line.StartsWith("_NET_WM_NAME", StringComparison.Ordinal)) title = XString(line);
                else if (line.StartsWith("WM_NAME", StringComparison.Ordinal)) wmName = XString(line);
                else if (line.StartsWith("_NET_WM_PID", StringComparison.Ordinal) && line.Split('=') is [_, var v]) int.TryParse(v.Trim(), out pid);
                else if (line.StartsWith("WM_CLASS", StringComparison.Ordinal))
                    cls = Regex.Matches(line, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).LastOrDefault();
                else if (line.StartsWith("_NET_WM_STATE", StringComparison.Ordinal)) hidden = line.Contains("_NET_WM_STATE_HIDDEN");
                else if (line.StartsWith("_NET_WM_WINDOW_TYPE", StringComparison.Ordinal))
                    normal = line.Contains("_NET_WM_WINDOW_TYPE_NORMAL") || line.Contains("_NET_WM_WINDOW_TYPE_DIALOG");
            }
            if (hidden || !normal) continue;
            title = (title ?? wmName ?? "").Trim();
            if (title.Length == 0) continue;
            var app = (pid > 0 ? ProcName(pid) : null) ?? cls ?? "";
            if (app.Trim().Length == 0) continue;
            windows.Add(new Window(app.Trim(), title, Convert.ToInt64(id, 16) == activeId));
        }
        return windows;
    }

    /// <summary>The quoted value of an xprop STRING / UTF8_STRING line, unescaped.</summary>
    private static string? XString(string line)
    {
        int q = line.IndexOf('"');
        if (q < 0 || !line.TrimEnd().EndsWith('"')) return null;
        var inner = line.TrimEnd()[(q + 1)..^1];
        return Regex.Replace(inner, @"\\(.)", "$1");
    }

    private static string? ProcName(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/comm").Trim(); } catch { return null; }
    }

    private static async Task<List<Window>> SwayWindows(string swaymsg)
    {
        var r = await Py.Run(swaymsg, ["-t", "get_tree", "-r"], timeoutSeconds: WindowsTimeoutSec);
        if (r.ReturnCode != 0 || Py.TryLoads(r.Stdout) is not JsonObject tree) throw new ScreenError("I couldn't read what's open, sir");
        var found = new List<Window>();
        void Walk(JsonNode? n)
        {
            if (n is not JsonObject o) return;
            var type = Py.Str(o, "type");
            if ((type == "con" || type == "floating_con") && o["pid"] is not null && Py.Str(o, "name") is { Length: > 0 } name)
            {
                var app = Py.Str(o, "app_id") ?? (Py.Num(o, "pid") is double p ? ProcName((int)p) : null) ?? "";
                bool visible = o["visible"]?.GetValue<bool>() ?? true;
                if (app.Length > 0 && visible) found.Add(new Window(app, name.Trim(), o["focused"]?.GetValue<bool>() == true));
            }
            foreach (var key in new[] { "nodes", "floating_nodes" })
                if (o[key] is JsonArray kids) foreach (var k in kids) Walk(k);
        }
        Walk(tree);
        return found.OrderByDescending(w => w.Frontmost).Take(MaxWindows).ToList();
    }

    private static async Task<List<Window>> HyprWindows(string hyprctl)
    {
        var r = await Py.Run(hyprctl, ["clients", "-j"], timeoutSeconds: WindowsTimeoutSec);
        if (r.ReturnCode != 0 || Py.TryLoads(r.Stdout) is not JsonArray clients) throw new ScreenError("I couldn't read what's open, sir");
        var act = await Py.Run(hyprctl, ["activewindow", "-j"], timeoutSeconds: WindowsTimeoutSec);
        var activeAddr = Py.Str(Py.TryLoads(act.Stdout), "address");
        var found = new List<(Window w, double focus)>();
        foreach (var c in clients)
        {
            var title = (Py.Str(c, "title") ?? "").Trim();
            var app = Py.Str(c, "class") ?? "";
            if (title.Length == 0 || app.Length == 0 || c?["mapped"]?.GetValue<bool>() == false || c?["hidden"]?.GetValue<bool>() == true) continue;
            found.Add((new Window(app, title, Py.Str(c, "address") == activeAddr), Py.Num(c, "focusHistoryID") ?? 999));
        }
        return found.OrderBy(f => f.focus).Select(f => f.w).Take(MaxWindows).ToList();
    }
}
