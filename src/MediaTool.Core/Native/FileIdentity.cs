using System.Runtime.InteropServices;
using MediaTool.Core.Util;

namespace MediaTool.Core.Native;

/// <summary>
/// Which physical file a path refers to.
///
/// Two paths that share a volume serial and a file id are not copies of each other — they
/// are one file with two names. That distinction is what makes hardlinking checkable: after
/// creating a link, the proof that it worked is that both paths now report the same id.
/// </summary>
public readonly record struct FileIdentity(ulong VolumeSerial, ulong Low, ulong High)
{
    public bool IsKnown => VolumeSerial != 0 || Low != 0 || High != 0;
}

internal static class FileIdentityReader
{
    /// <summary>Reads the file's identity, or an unset value when it cannot be determined.</summary>
    public static FileIdentity Read(string fullPath)
    {
        using var handle = Win32.CreateFile(
            LongPath.Prefix(fullPath),
            Win32.FILE_READ_ATTRIBUTES,
            Win32.FILE_SHARE_ALL,
            IntPtr.Zero,
            Win32.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid) return default;

        // FILE_ID_INFO { ULONGLONG VolumeSerialNumber; FILE_ID_128 FileId; }
        const int size = 24;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!Win32.GetFileInformationByHandleEx(handle, Win32.FileIdInfo, buffer, size))
                return default;

            return new FileIdentity(
                (ulong)Marshal.ReadInt64(buffer, 0),
                (ulong)Marshal.ReadInt64(buffer, 8),
                (ulong)Marshal.ReadInt64(buffer, 16));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
