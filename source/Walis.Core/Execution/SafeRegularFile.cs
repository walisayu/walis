using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace Walis.Core.Execution;

/// <summary>
///     Opens a regular file without following a symbolic link in the last segment. A build script may swap the pathname between the
///     check and the open, so the <see cref="FileInfo.LinkTarget" /> pre-check alone is not enough.
/// </summary>
public static class SafeRegularFile
{
    private const int LinuxOpenNonBlocking = 0x800;
    private const int LinuxOpenNoFollow = 0x20000;
    private const int LinuxOpenCloseOnExec = 0x80000;
    private const int MacOpenNonBlocking = 0x4;
    private const int MacOpenNoFollow = 0x100;
    private const int MacOpenCloseOnExec = 0x1000000;
    private const uint WindowsFileTypeDisk = 0x0001;
    private const int LinuxAtEmptyPath = 0x1000;
    private const uint LinuxStatxType = 0x0001;
    private const int LinuxStatxBufferSize = 256;
    private const int LinuxStatxModeOffset = 28;
    private const int MacStatBufferSize = 256;
    private const int MacStatModeOffset = 4;
    private const ushort UnixFileTypeMask = 0xF000;
    private const ushort UnixRegularFile = 0x8000;

    public static bool TryOpenRead(string path, out FileStream stream, int bufferSize = 64 * 1024)
    {
        stream = null!;

        try
        {
            var info = new FileInfo(path);
            info.Refresh();

            if (!info.Exists ||
                info.LinkTarget is not null ||
                (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) !=
                0)
                return false;

            SafeFileHandle handle;

            if (OperatingSystem.IsWindows())
            {
                handle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    FileOptions.SequentialScan);
            }
            else
            {
                var flags = OperatingSystem.IsMacOS()
                    ? MacOpenNonBlocking | MacOpenNoFollow | MacOpenCloseOnExec
                    : LinuxOpenNonBlocking | LinuxOpenNoFollow | LinuxOpenCloseOnExec;
                var descriptor = OpenUnix(path, flags, 0);

                if (descriptor < 0) return false;

                handle = new SafeFileHandle(descriptor, true);
            }

            // FileAttributes.Device on Unix is not guaranteed to cover FIFO/device/socket; always inspect the opened
            // handle's type so a special file cannot block or stream until the byte budget is exhausted.
            if (!IsRegularFileHandle(handle))
            {
                handle.Dispose();
                return false;
            }

            stream = new FileStream(handle, FileAccess.Read, bufferSize, false);

            if (!stream.CanSeek)
            {
                stream.Dispose();
                stream = null!;
                return false;
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
                SecurityException)
        {
            stream?.Dispose();
            stream = null!;
            return false;
        }
    }

    private static bool IsRegularFileHandle(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows()) return GetWindowsFileType(handle) == WindowsFileTypeDisk;

        var descriptor = handle.DangerousGetHandle().ToInt32();
        var bufferSize = OperatingSystem.IsLinux() ? LinuxStatxBufferSize : MacStatBufferSize;
        var buffer = Marshal.AllocHGlobal(bufferSize);

        try
        {
            ushort mode;

            if (OperatingSystem.IsLinux())
            {
                if (StatxUnix(
                        descriptor,
                        string.Empty,
                        LinuxAtEmptyPath,
                        LinuxStatxType,
                        buffer) != 0)
                    return false;

                mode = unchecked((ushort)Marshal.ReadInt16(buffer, LinuxStatxModeOffset));
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (FStatUnix(descriptor, buffer) != 0) return false;

                mode = unchecked((ushort)Marshal.ReadInt16(buffer, MacStatModeOffset));
            }
            else
            {
                return false;
            }

            return (mode & UnixFileTypeMask) == UnixRegularFile;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix(string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatxUnix(int directory, string path, int flags, uint mask, IntPtr buffer);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStatUnix(int descriptor, IntPtr buffer);

    [DllImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
    private static extern uint GetWindowsFileType(SafeFileHandle handle);
}