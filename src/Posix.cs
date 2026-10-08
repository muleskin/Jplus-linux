using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jplus;

/// <summary>
/// The handful of POSIX calls the Linux build needs and .NET does not expose:
/// `statx` (st_dev / st_ino / st_uid / st_mode — file identity and ownership),
/// `geteuid`, `open(O_NOFOLLOW)` and `kill`. glibc (2.28+) on x64 and arm64.
/// Only ever called when `!Py.IsWindows`.
/// </summary>
public static class Posix
{
    public const int AtFdCwd = -100;
    public const int AtSymlinkNoFollow = 0x100;
    public const int AtEmptyPath = 0x1000;
    public const uint StatxBasicStats = 0x7ff;

    public const int ORdWr = 0x2;
    public const int OCloExec = 0x80000;
    // O_NOFOLLOW differs by architecture (asm-generic vs x86).
    public static int ONoFollow => RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm
        ? 0x8000 : 0x20000;

    public const uint SIfMt = 0xF000, SIfReg = 0x8000, SIfDir = 0x4000, SIfLnk = 0xA000;

    public const int SigKill = 9, SigTerm = 15;

    [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
    private static extern int StatxNative(int dirfd, string path, int flags, uint mask, byte[] buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int OpenNative(string path, int flags, uint mode);

    public const int EExist = 17;

    [DllImport("libc", SetLastError = true, EntryPoint = "mkdir")]
    public static extern int Mkdir(string path, uint mode);

    [DllImport("libc", EntryPoint = "geteuid")]
    public static extern uint Geteuid();

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    public static extern int Kill(int pid, int sig);

    /// <summary>What `os.stat` / `os.lstat` / `os.fstat` gave the Python.</summary>
    public readonly record struct Stat(ulong Dev, ulong Ino, uint Uid, uint Mode, long Size, double Mtime)
    {
        public bool IsRegular => (Mode & SIfMt) == SIfReg;
        public bool IsDir => (Mode & SIfMt) == SIfDir;
        public bool IsLink => (Mode & SIfMt) == SIfLnk;
    }

    private static Stat? Statx(int dirfd, string path, int flags)
    {
        var buf = new byte[256];
        try
        {
            if (StatxNative(dirfd, path, flags, StatxBasicStats, buf) != 0) return null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        var s = buf.AsSpan();
        uint uid = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]);
        uint mode = BinaryPrimitives.ReadUInt16LittleEndian(s[28..]);
        ulong ino = BinaryPrimitives.ReadUInt64LittleEndian(s[32..]);
        long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(s[40..]);
        long mSec = BinaryPrimitives.ReadInt64LittleEndian(s[112..]);
        uint mNsec = BinaryPrimitives.ReadUInt32LittleEndian(s[120..]);
        ulong devMajor = BinaryPrimitives.ReadUInt32LittleEndian(s[136..]);
        ulong devMinor = BinaryPrimitives.ReadUInt32LittleEndian(s[140..]);
        return new Stat((devMajor << 32) | devMinor, ino, uid, mode, size, mSec + mNsec / 1e9);
    }

    /// <summary>`os.stat` (follows links); null if it cannot be stat'd.</summary>
    public static Stat? StatPath(string path) => Statx(AtFdCwd, path, 0);

    /// <summary>`os.lstat` (does not follow a final link).</summary>
    public static Stat? LStat(string path) => Statx(AtFdCwd, path, AtSymlinkNoFollow);

    /// <summary>`os.fstat` on an open handle.</summary>
    public static Stat? FStat(SafeFileHandle h)
    {
        bool added = false;
        try
        {
            h.DangerousAddRef(ref added);
            return Statx((int)h.DangerousGetHandle(), "", AtEmptyPath);
        }
        finally
        {
            if (added) h.DangerousRelease();
        }
    }

    /// <summary>
    /// `os.open(path, O_RDWR | O_NOFOLLOW)`: refuses (throws IOException) when
    /// the final component is a symlink, so a link planted at the path cannot
    /// redirect us. The handle owns the fd.
    /// </summary>
    public static SafeFileHandle OpenNoFollow(string path)
    {
        int fd = OpenNative(path, ORdWr | ONoFollow | OCloExec, 0);
        if (fd < 0)
            throw new IOException($"{path}: open failed (errno {Marshal.GetLastPInvokeError()})");
        return new SafeFileHandle(fd, ownsHandle: true);
    }

    /// <summary>chmod 0600, best effort (a file on a filesystem without modes just keeps its own).</summary>
    public static void Chmod600(string path)
    {
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }
}
