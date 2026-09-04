using System.Runtime.InteropServices;
using System.Text;
using MediaTool.Core.Native;

namespace MediaTool.Core.Volumes;

/// <summary>Discovers the volumes currently attached and classifies their storage.</summary>
public static class VolumeScanner
{
    private const int GuidBufferChars = 64;   // \\?\Volume{...}\ is 49 chars
    private const uint FILE_SUPPORTS_OBJECT_IDS = 0x00010000;
    private const uint DRIVE_REMOTE = 4;
    private const uint DRIVE_CDROM = 5;
    private const uint DRIVE_NO_ROOT_DIR = 1;

    /// <summary>
    /// Enumerates every mounted volume. Suppresses the "There is no disk in the drive"
    /// modal that empty card readers otherwise raise on each probe.
    /// </summary>
    public static List<VolumeInfo> EnumerateVolumes()
    {
        uint prevErrorMode = Win32.SetErrorMode(Win32.SEM_FAILCRITICALERRORS | Win32.SEM_NOOPENFILEERRORBOX);
        try
        {
            var result = new List<VolumeInfo>();
            var buffer = new StringBuilder(GuidBufferChars);

            IntPtr find = Win32.FindFirstVolumeW(buffer, GuidBufferChars);
            if (find == IntPtr.Zero || find == new IntPtr(-1)) return result;

            try
            {
                do
                {
                    var info = TryDescribe(buffer.ToString());
                    if (info is not null) result.Add(info);
                    buffer.Clear();
                    buffer.EnsureCapacity(GuidBufferChars);
                }
                while (Win32.FindNextVolumeW(find, buffer, GuidBufferChars));
            }
            finally
            {
                Win32.FindVolumeClose(find);
            }

            return result;
        }
        finally
        {
            Win32.SetErrorMode(prevErrorMode);
        }
    }

    /// <summary>Resolves the volume that owns an arbitrary path, following mounted folders.</summary>
    public static VolumeInfo? ForPath(string path, IEnumerable<VolumeInfo>? known = null)
    {
        string? mount = GetMountPointForPath(path);
        if (mount is null) return null;

        var guidBuf = new StringBuilder(GuidBufferChars);
        if (!Win32.GetVolumeNameForVolumeMountPointW(mount, guidBuf, GuidBufferChars)) return null;
        string guid = guidBuf.ToString();

        if (known is not null)
        {
            var hit = known.FirstOrDefault(v => v.VolumeGuid.Equals(guid, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }

        return TryDescribe(guid);
    }

    /// <summary>
    /// The deepest mount point containing <paramref name="path"/> — "E:\" for E:\Photos,
    /// but "C:\Mount\Disk2\" if a second disk is mounted into a folder there. Prefixing
    /// the catalog's relative paths with the wrong one silently mixes two disks together.
    /// </summary>
    public static string? GetMountPointForPath(string path)
    {
        var buf = new StringBuilder(4096);
        return Win32.GetVolumePathNameW(Path.GetFullPath(path), buf, (uint)buf.Capacity)
            ? buf.ToString()
            : null;
    }

    private static VolumeInfo? TryDescribe(string volumeGuid)
    {
        var mountPoints = GetMountPoints(volumeGuid);

        // GetVolumeInformation needs a path that resolves; the GUID path works even for
        // volumes with no drive letter (recovery partitions, disks mounted into folders).
        string probe = mountPoints.Count > 0 ? mountPoints[0] : volumeGuid;

        var labelBuf = new StringBuilder(256);
        var fsBuf = new StringBuilder(64);
        if (!Win32.GetVolumeInformationW(probe, labelBuf, labelBuf.Capacity,
                out uint serial, out _, out uint fsFlags, fsBuf, fsBuf.Capacity))
        {
            int err = Marshal.GetLastWin32Error();
            // Empty card reader / unformatted partition — real, just nothing to scan.
            if (err is Win32.ERROR_NOT_READY or Win32.ERROR_ACCESS_DENIED or Win32.ERROR_PATH_NOT_FOUND)
                return null;
            return null;
        }

        string fs = fsBuf.ToString();
        ulong total = 0, free = 0;
        Win32.GetDiskFreeSpaceExW(probe, out _, out total, out free);

        uint driveType = mountPoints.Count > 0 ? Win32.GetDriveTypeW(mountPoints[0]) : 0;
        if (driveType == DRIVE_CDROM) return null;

        return new VolumeInfo
        {
            VolumeGuid = volumeGuid,
            SerialNumber = serial,
            Label = labelBuf.ToString(),
            FileSystem = fs,
            TotalBytes = total,
            FreeBytes = free,
            MountPoints = mountPoints,
            StorageKind = driveType == DRIVE_REMOTE ? StorageKind.Remote : DetectStorageKind(volumeGuid),
            // Only NTFS/ReFS carry the file ids the incremental rescan relies on.
            SupportsFileId = fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
                          || fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static List<string> GetMountPoints(string volumeGuid)
    {
        var list = new List<string>();
        var buf = new char[512];

        if (!Win32.GetVolumePathNamesForVolumeNameW(volumeGuid, buf, (uint)buf.Length, out uint needed))
        {
            if (Marshal.GetLastWin32Error() != Win32.ERROR_MORE_DATA) return list;
            buf = new char[needed];
            if (!Win32.GetVolumePathNamesForVolumeNameW(volumeGuid, buf, (uint)buf.Length, out _)) return list;
        }

        // Result is a double-null-terminated sequence of null-terminated strings.
        int start = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            if (buf[i] != '\0') continue;
            if (i == start) break;              // empty string => end of list
            list.Add(new string(buf, start, i - start));
            start = i + 1;
        }
        return list;
    }

    /// <summary>
    /// Asks the storage stack whether the device has a seek penalty. This is the difference
    /// between "open 16 reader threads" and "open 1" — getting it wrong on an HDD costs
    /// more wall-clock than every other optimisation in the crawler combined.
    /// </summary>
    private static StorageKind DetectStorageKind(string volumeGuid)
    {
        // CreateFile on a volume device wants the path WITHOUT the trailing backslash.
        string devicePath = volumeGuid.TrimEnd('\\');

        using var handle = Win32.CreateFile(
            devicePath,
            0,                                  // no access rights needed for a property query
            Win32.FILE_SHARE_ALL,
            IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid) return StorageKind.Unknown;

        // STORAGE_PROPERTY_QUERY { PropertyId, QueryType, AdditionalParameters[1] }
        const int querySize = 12;
        // DEVICE_SEEK_PENALTY_DESCRIPTOR { Version, Size, IncursSeekPenalty }
        const int descriptorSize = 12;

        IntPtr inBuf = Marshal.AllocHGlobal(querySize);
        IntPtr outBuf = Marshal.AllocHGlobal(descriptorSize);
        try
        {
            Marshal.WriteInt32(inBuf, 0, Win32.StorageDeviceSeekPenaltyProperty);
            Marshal.WriteInt32(inBuf, 4, Win32.PropertyStandardQuery);
            Marshal.WriteInt32(inBuf, 8, 0);
            for (int i = 0; i < descriptorSize; i++) Marshal.WriteByte(outBuf, i, 0);

            bool ok = Win32.DeviceIoControl(
                handle, Win32.IOCTL_STORAGE_QUERY_PROPERTY,
                inBuf, querySize, outBuf, descriptorSize, out uint returned, IntPtr.Zero);

            if (!ok || returned < descriptorSize) return StorageKind.Unknown;

            bool incursSeekPenalty = Marshal.ReadByte(outBuf, 8) != 0;
            return incursSeekPenalty ? StorageKind.Hdd : StorageKind.Ssd;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }
}
