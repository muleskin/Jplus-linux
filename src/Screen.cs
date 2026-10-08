using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Jplus;

/// <summary>
/// JARVIS's eyes on the PC itself: the window list, and one deliberate picture.
///
/// Two capabilities, priced very differently, exactly as `read_page` and
/// `look_at_page` are for the web:
///
/// 1. `ListWindows()` — which app is in front and what its windows are called.
///    No pixels. "What am I looking at" is usually answerable from this alone.
/// 2. `CaptureScreen()` — a PNG of the screen, shrunk, which the brain SEES. It
///    reaches the brain as an MCP `image` content block (`Server.ToolImage`); this
///    module is only a source of pixels for it. Nothing here leaves the machine.
///
/// **This is a camera pointed at the user's life.** A screenshot can hold a
/// password, a private message, a client's data. So nothing here runs on a timer,
/// speculatively, or as ambient context: the tools are gated to a user-origin turn
/// in `Server.ActingTools`, and this module is called from nowhere else.
///
/// Windows port: `screencapture` + `sips` → System.Drawing `CopyFromScreen` and an
/// in-memory resize (the capture never touches disk at all, which is stricter than
/// the Python's milliseconds-long temp directory); the System Events window script
/// → Win32 `EnumWindows`. A service in session 0 has no desktop, so both refuse
/// there with `Py.NoDesktopReason`. Linux: see Screen.Linux.cs.
/// </summary>
public static partial class Screen
{
    public static readonly ILogger Log = Py.Log("jarvis.screen");

    // Each of these must finish WELL inside `JarvisMcp.TimeoutSec` (20s), and the
    // caller puts its own hard deadline on top.
    public const double CaptureTimeoutSec = 8.0;
    public const double ResizeTimeoutSec = 6.0;
    public const double WindowsTimeoutSec = 5.0;

    // The longest edge the brain is shown. Images are charged by AREA — roughly
    // width*height/750 tokens: 1920x1080 ≈ 2,765 tokens, 1280x720 ≈ 1,229. 1280 is
    // the same width `Browser.LookViewport` uses, and the point below which
    // terminal text and menu bars stop being legible.
    public const int ShotMaxEdge = 1280;

    // A PNG bigger than this is not going through the tool channel; say so rather
    // than sending something the CLI will choke on. Same bound as Browser.
    public const int MaxShotBytes = 4_000_000;

    // The window list is a tool result like any other, cut at `Server.ToolResultCap`.
    // Bound the list here so the cut never lands mid-way through the untrusted block.
    public const int MaxWindows = 12;

    // The blank-frame check downsamples to this edge before looking at pixels.
    public const int BlankSampleEdge = 32;

    // Per-channel mean absolute deviation, in 0-255 levels, below which a frame is
    // "one colour". A real screenshot's channels sit around 20; a solid fill is 0.
    public const double BlankMad = 1.5;

    /// <summary>Something JARVIS could not see. The message is meant to be spoken.</summary>
    public class ScreenError : Exception
    {
        public ScreenError(string message) : base(message) { }
    }

    public class Shot
    {
        public Shot() { }
        public Shot(byte[] png, int width, int height) { Png = png; Width = width; Height = height; }
        public byte[] Png { get; set; } = [];
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public class Window
    {
        public Window() { }
        public Window(string app, string title, bool frontmost) { App = app; Title = title; Frontmost = frontmost; }
        public string App { get; set; } = "";
        public string Title { get; set; } = "";
        public bool Frontmost { get; set; }
    }

    /// <summary>The service refusal, without the trailing full stop: callers append their own.</summary>
    private static string NoDesktop => Py.NoDesktopReason.TrimEnd('.');

    // ── permission ─────────────────────────────────────────────────────────────

    /// <summary>
    /// True, False, or null when the probe itself could not be run.
    ///
    /// macOS asks `CGPreflightScreenCaptureAccess`. Windows has no Screen Recording
    /// permission: any process in the interactive session may capture the desktop,
    /// and a service in session 0 has none to capture. So this is simply "are we on
    /// a desktop". Never prompts, never captures.
    /// </summary>
    public static bool? ScreenRecordingGranted()
    {
        try
        {
            return !Py.IsServiceSession;
        }
        catch (Exception e)
        {
            Log.LogWarning("screen recording probe failed: {Error}", e.Message);
            return null;
        }
    }

    public const string NoPermission =
        "I've not been granted Screen Recording, sir, so I can't see your screen";

    // ── PNG and BMP, read by hand ───────────────────────────────────────────────

    public static readonly byte[] PngMagic = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// (width, height) from a PNG's IHDR, or null if that is not a PNG. Doubles as
    /// the answer to "did the encoder actually produce a picture?".
    /// </summary>
    public static (int width, int height)? PngSize(byte[] png)
    {
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(PngMagic)
            || Encoding.ASCII.GetString(png, 12, 4) != "IHDR")
            return null;
        long width = ((long)png[16] << 24) | ((long)png[17] << 16) | ((long)png[18] << 8) | png[19];
        long height = ((long)png[20] << 24) | ((long)png[21] << 16) | ((long)png[22] << 8) | png[23];
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue) return null;
        return ((int)width, (int)height);
    }

    /// <summary>
    /// The (b, g, r) triples out of an uncompressed 24/32-bit BMP, or [] if
    /// unreadable. (The Python read what `sips` wrote; kept for parity — the
    /// Windows path samples the bitmap directly, see FrameIsBlank.)
    /// </summary>
    public static List<(int, int, int)> BmpPixels(byte[] raw)
    {
        var result = new List<(int, int, int)>();
        if (raw.Length < 32 || raw[0] != (byte)'B' || raw[1] != (byte)'M') return result;
        int offset = BitConverter.ToInt32(raw, 10);
        int depth = BitConverter.ToUInt16(raw, 28);
        if ((depth != 24 && depth != 32) || offset < 0 || offset >= raw.Length) return result;
        int step = depth / 8;
        for (int i = offset; i + step <= raw.Length; i += step)
            result.Add((raw[i], raw[i + 1], raw[i + 2]));
        return result;
    }

    /// <summary>
    /// Is this frame one flat colour? Judged PER CHANNEL: a solid dark-green
    /// desktop is (44, 62, 24) everywhere, and spread measured across all bytes at
    /// once would call that busy. A little noise around black still counts as blank.
    /// </summary>
    public static bool IsBlank(List<(int, int, int)> pixels)
    {
        if (pixels.Count == 0) return false;          // nothing to judge: do not accuse
        for (int channel = 0; channel < 3; channel++)
        {
            double sum = 0;
            foreach (var p in pixels) sum += Channel(p, channel);
            double mean = sum / pixels.Count;
            double dev = 0;
            foreach (var p in pixels) dev += Math.Abs(Channel(p, channel) - mean);
            if (dev / pixels.Count >= BlankMad) return false;
        }
        return true;
    }

    private static int Channel((int, int, int) p, int c) => c switch { 0 => p.Item1, 1 => p.Item2, _ => p.Item3 };

    /// <summary>
    /// Whether the capture came back all one colour. A capture of a locked
    /// workstation or a secure desktop can come back as a flat frame, and a tool
    /// that returns a black rectangle and lets JARVIS confidently describe nothing
    /// is the worst failure this project has. If sampling itself fails this says
    /// false: refusing a good capture over a tooling failure is worse.
    /// </summary>
    public static bool FrameIsBlank(Bitmap frame)
    {
        try
        {
            var (w, h) = FitWithin(frame.Width, frame.Height, BlankSampleEdge);
            using var sample = Resize(frame, w, h);
            var pixels = new List<(int, int, int)>(w * h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = sample.GetPixel(x, y);
                    pixels.Add((c.B, c.G, c.R));
                }
            return IsBlank(pixels);
        }
        catch (Exception e)
        {
            Log.LogWarning("blank-frame check could not run: {Error}", e.Message);
            return false;
        }
    }

    /// <summary>`sips -Z edge`: scale so the longest side is `edge`, keeping aspect.</summary>
    public static (int w, int h) FitWithin(int width, int height, int edge)
    {
        int longest = Math.Max(width, height);
        if (longest <= 0) return (1, 1);
        double scale = (double)edge / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static Bitmap Resize(Bitmap src, int w, int h)
    {
        var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY);   // no dark fringe at the edges
        g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        return dst;
    }

    private static byte[] EncodePng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    // ── Win32 ──────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;
    private const uint MonitorInfoFPrimary = 1;
    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80, WsExAppWindow = 0x40000;
    private const int DwmwaCloaked = 14;

    /// <summary>
    /// Physical-pixel bounds of every monitor, the primary first and then left to
    /// right, top to bottom — so display 1 is the main one, as `screencapture -D 1` is.
    /// Must be called on a thread that is per-monitor DPI aware.
    /// </summary>
    private static List<Rectangle> Monitors()
    {
        var found = new List<(Rectangle r, bool primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT rc, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            var r = GetMonitorInfo(h, ref info) ? info.rcMonitor : rc;
            found.Add((Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), (info.dwFlags & MonitorInfoFPrimary) != 0));
            return true;
        }, IntPtr.Zero);
        return found.OrderByDescending(m => m.primary).ThenBy(m => m.r.Left).ThenBy(m => m.r.Top)
                    .Select(m => m.r).ToList();
    }

    // ── the picture ────────────────────────────────────────────────────────────

    /// <summary>
    /// A PNG of the screen, shrunk to `ShotMaxEdge`.
    ///
    /// `display` is a 1-based monitor index (1 = the primary); null captures the
    /// whole virtual screen — every monitor at once.
    ///
    /// Throws ScreenError — with a sentence fit to be spoken — rather than ever
    /// handing back something the brain would describe wrongly.
    ///
    /// Call this ONLY on a turn the user drove.
    /// </summary>
    public static async Task<Shot> CaptureScreen(int? display = null)
    {
        if (Py.IsServiceSession) throw new ScreenError(NoDesktop);
        if (ScreenRecordingGranted() == false) throw new ScreenError(NoPermission);

        try
        {
            if (!Py.IsWindows) return await LinuxCapture(display);
            return await Task.Run(() => CaptureScreenSync(display))
                .WaitAsync(TimeSpan.FromSeconds(CaptureTimeoutSec + ResizeTimeoutSec));
        }
        catch (ScreenError) { throw; }
        catch (TimeoutException)
        {
            Log.LogWarning("screen capture timed out");
            throw new ScreenError("I couldn't get a picture of your screen, sir");
        }
        catch (Exception e)
        {
            Log.LogWarning("screen capture failed: {Error}", e.Message.Length > 200 ? e.Message[..200] : e.Message);
            throw new ScreenError("I couldn't get a picture of your screen, sir");
        }
    }

    private static Shot CaptureScreenSync(int? display)
    {
        // Per-monitor DPI aware for this thread only, so the bounds and the copy are
        // in physical pixels rather than a virtualised, blurry 96-DPI view.
        IntPtr previous = IntPtr.Zero;
        try { previous = SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2); } catch (EntryPointNotFoundException) { }
        try
        {
            Rectangle bounds;
            if (display is int d && d > 0)
            {
                var monitors = Monitors();
                if (d > monitors.Count)
                {
                    Log.LogWarning("screen capture: no display {Display} ({Count} attached)", d, monitors.Count);
                    throw new ScreenError("I couldn't get a picture of your screen, sir");
                }
                bounds = monitors[d - 1];
            }
            else
            {
                // The main display, as `screencapture` captured by default. The whole
                // virtual desktop of a multi-monitor setup downscales to an unreadable strip.
                var monitors = Monitors();
                bounds = monitors.Count > 0
                    ? monitors[0]
                    : new Rectangle(GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
                                    GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));
            }
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new ScreenError("I couldn't get a picture of your screen, sir");

            using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            Bitmap frame = full;
            Bitmap? small = null;
            try
            {
                if (Math.Max(full.Width, full.Height) > ShotMaxEdge)
                {
                    try
                    {
                        var (w, h) = FitWithin(full.Width, full.Height, ShotMaxEdge);
                        small = Resize(full, w, h);
                        frame = small;
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning("could not resize the capture: {Error}", e.Message);
                        // Deliberately NOT sending the full-size one instead: a
                        // 3840-wide capture is thousands of tokens off one turn.
                        throw new ScreenError("I couldn't get your screen down to a sensible size, sir");
                    }
                }

                var png = EncodePng(frame);
                var size = PngSize(png) ?? throw new ScreenError("I couldn't get a picture of your screen, sir");

                if (png.Length > MaxShotBytes)
                    throw new ScreenError("that picture came out far too large to send, sir");

                if (FrameIsBlank(frame))
                    throw new ScreenError(
                        "your screen came back blank, sir — which usually means the screen is locked " +
                        "or a secure desktop is showing rather than that there's nothing there");

                return new Shot(png, size.width, size.height);
            }
            finally
            {
                small?.Dispose();
            }
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                try { SetThreadDpiAwarenessContext(previous); } catch { }
            }
        }
    }

    // ── the cheap path ─────────────────────────────────────────────────────────

    /// <summary>
    /// Open windows: app (owning process name), window title, and whether that
    /// app is the one in front. Read-only: it enumerates windows that already exist
    /// and opens nothing.
    ///
    /// Throws ScreenError rather than returning an empty list when the list cannot
    /// be read — an empty list would have JARVIS say "nothing is open", a lie.
    /// </summary>
    public static async Task<List<Window>> ListWindows()
    {
        if (Py.IsServiceSession) throw new ScreenError(NoDesktop);
        try
        {
            if (!Py.IsWindows) return await LinuxListWindows().WaitAsync(TimeSpan.FromSeconds(WindowsTimeoutSec * 3));
            return await Task.Run(ListWindowsSync).WaitAsync(TimeSpan.FromSeconds(WindowsTimeoutSec));
        }
        catch (ScreenError) { throw; }
        catch (Exception e)
        {
            Log.LogWarning("list_windows failed: {Error}", e.Message.Length > 200 ? e.Message[..200] : e.Message);
            throw new ScreenError("I couldn't read what's open, sir");
        }
    }

    private static List<Window> ListWindowsSync()
    {
        var windows = new List<Window>();
        var names = new Dictionary<uint, string>();
        var shell = GetShellWindow();
        var fg = GetForegroundWindow();
        uint fgPid = 0;
        if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out fgPid);

        string AppName(uint pid)
        {
            if (names.TryGetValue(pid, out var n)) return n;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                n = p.ProcessName;
            }
            catch { n = ""; }
            names[pid] = n;
            return n;
        }

        // EnumWindows walks top-level windows in z-order, front first.
        bool ok = EnumWindows((hWnd, _) =>
        {
            if (hWnd == shell || !IsWindowVisible(hWnd)) return true;
            // Cloaked: UWP frames that are not actually shown, windows on another
            // virtual desktop. Visible by the flag, invisible to the user.
            if (DwmGetWindowAttribute(hWnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            long ex = GetWindowLongPtr(hWnd, GwlExStyle).ToInt64();
            if ((ex & WsExToolWindow) != 0 && (ex & WsExAppWindow) == 0) return true;
            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            var app = AppName(pid).Trim();
            if (app.Length == 0) return true;
            windows.Add(new Window(app, title, pid != 0 && pid == fgPid));
            return windows.Count < MaxWindows;
        }, IntPtr.Zero);

        if (!ok && windows.Count == 0 && Marshal.GetLastWin32Error() != 0)
            throw new ScreenError("I couldn't read what's open, sir");
        return windows;
    }
}
