using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MediaTool.Core.Native;

/// <summary>
/// P/Invoke surface. Kept deliberately small: directory enumeration, volume identity,
/// and storage device type. Everything else in the crawler is managed code.
/// </summary>
internal static class Win32
{
    // ---- error codes ----------------------------------------------------
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INVALID_FUNCTION = 1;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_NOT_READY = 21;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_NO_MORE_ITEMS = 259;

    // ---- CreateFile -----------------------------------------------------
    public const uint FILE_LIST_DIRECTORY = 0x0001;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint GENERIC_READ = 0x80000000;

    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint FILE_SHARE_DELETE = 0x4;
    public const uint FILE_SHARE_ALL = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

    public const uint OPEN_EXISTING = 3;

    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

    // ---- file attributes ------------------------------------------------
    public const uint FILE_ATTRIBUTE_READONLY = 0x00000001;
    public const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
    public const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    public const uint FILE_ATTRIBUTE_OFFLINE = 0x00001000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

    /// <summary>
    /// Attributes that mean "the bytes are not on this machine". Touching such a file
    /// makes the sync provider (OneDrive / Dropbox / Google Drive) download it. On a
    /// multi-TB catalog that silently turns a scan into a terabyte download.
    /// </summary>
    public const uint CLOUD_PLACEHOLDER_MASK =
        FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS;

    // ---- reparse tags ---------------------------------------------------
    public const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    public const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;
    public const uint IO_REPARSE_TAG_CLOUD = 0x9000001A;

    /// <summary>
    /// The cloud-sync family runs IO_REPARSE_TAG_CLOUD (0x9000001A) through
    /// IO_REPARSE_TAG_CLOUD_F (0x9000F01A) — one tag per registered sync root.
    /// </summary>
    public static bool IsCloudReparseTag(uint tag) => (tag & 0xFFFF0FFF) == 0x9000001A;

    // ---- FILE_INFO_BY_HANDLE_CLASS --------------------------------------
    public const int FileIdBothDirectoryInfo = 10;
    public const int FileIdBothDirectoryRestartInfo = 11;
    public const int FileFullDirectoryInfo = 14;
    public const int FileFullDirectoryRestartInfo = 15;
    public const int FileIdInfo = 18;
    public const int FileIdExtdDirectoryInfo = 19;
    public const int FileIdExtdDirectoryRestartInfo = 20;

    // ---- SetErrorMode ---------------------------------------------------
    public const uint SEM_FAILCRITICALERRORS = 0x0001;
    public const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    // ---- storage query --------------------------------------------------
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const int StorageDeviceSeekPenaltyProperty = 7;
    public const int PropertyStandardQuery = 0;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile, int fileInformationClass, IntPtr lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll")]
    public static extern uint SetErrorMode(uint uMode);

    // ---- volume enumeration --------------------------------------------
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindFirstVolumeW(StringBuilder lpszVolumeName, int cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextVolumeW(IntPtr hFindVolume, StringBuilder lpszVolumeName, int cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindVolumeClose(IntPtr hFindVolume);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumePathNamesForVolumeNameW(
        string lpszVolumeName, char[] lpszVolumePathNames, uint cchBufferLength, out uint lpcchReturnLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer, int nVolumeNameSize,
        out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer, int nFileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumePathNameW(string lpszFileName, StringBuilder lpszVolumePathName, uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeNameForVolumeMountPointW(
        string lpszVolumeMountPoint, StringBuilder lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName, out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateHardLink(
        string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetDriveTypeW([MarshalAs(UnmanagedType.LPWStr)] string lpRootPathName);
}
