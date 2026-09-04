using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MediaTool.Core.Native;

/// <summary>One directory entry as returned by the kernel, before any policy is applied.</summary>
internal struct RawDirEntry
{
    public string Name;
    public long Size;
    public long CreationTime;   // FILETIME ticks (UTC)
    public long LastWriteTime;  // FILETIME ticks (UTC)
    public uint Attributes;
    public uint ReparseTag;     // 0 when unknown / not a reparse point
    public bool HasFileId;
    public ulong FileIdLow;     // low 64 bits of FILE_ID_128
    public ulong FileIdHigh;    // high 64 bits, 0 on volumes with 64-bit ids

    public readonly bool IsDirectory => (Attributes & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0;
    public readonly bool IsReparsePoint => (Attributes & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    public readonly bool IsCloudPlaceholder => (Attributes & Win32.CLOUD_PLACEHOLDER_MASK) != 0;
}

/// <summary>
/// Directory enumeration via GetFileInformationByHandleEx.
///
/// Why not FindFirstFile / Directory.EnumerateFiles: neither returns the NTFS file id, so
/// establishing stable file identity would cost one CreateFile per file — the single most
/// expensive thing you can do to a scan of several million files. FILE_ID_EXTD_DIR_INFO
/// hands back attributes, size, timestamps, reparse tag AND the 128-bit file id in the same
/// buffer, one syscall per ~1000 entries.
///
/// Falls back to FILE_FULL_DIR_INFO on volumes that do not support the extended class
/// (exFAT/FAT32 removable drives), where identity degrades to path-based.
/// </summary>
internal sealed class DirectoryReader : IDisposable
{
    // FILE_ID_EXTD_DIR_INFO field offsets (header is 88 bytes, FileName follows)
    private const int ExtdOffNextEntry = 0;
    private const int ExtdOffCreationTime = 8;
    private const int ExtdOffLastWriteTime = 24;
    private const int ExtdOffEndOfFile = 40;
    private const int ExtdOffAttributes = 56;
    private const int ExtdOffFileNameLength = 60;
    private const int ExtdOffReparseTag = 68;
    private const int ExtdOffFileId = 72;
    private const int ExtdOffFileName = 88;

    // FILE_FULL_DIR_INFO field offsets (header is 68 bytes)
    private const int FullOffNextEntry = 0;
    private const int FullOffCreationTime = 8;
    private const int FullOffLastWriteTime = 24;
    private const int FullOffEndOfFile = 40;
    private const int FullOffAttributes = 56;
    private const int FullOffFileNameLength = 60;
    private const int FullOffFileName = 68;

    private const int BufferSize = 256 * 1024;

    private readonly SafeFileHandle _handle;
    private readonly IntPtr _buffer;
    private bool _useExtended;
    private bool _first = true;
    private bool _exhausted;

    /// <summary>False when the volume forced the no-file-id fallback.</summary>
    public bool HasFileIds => _useExtended;

    private DirectoryReader(SafeFileHandle handle)
    {
        _handle = handle;
        _buffer = Marshal.AllocHGlobal(BufferSize);
        _useExtended = true;
    }

    /// <summary>
    /// Opens a directory for enumeration. Returns null and sets <paramref name="error"/>
    /// on failure — access-denied on a system folder is normal and must not abort a scan.
    /// </summary>
    public static DirectoryReader? Open(string extendedPath, out int error)
    {
        var handle = Win32.CreateFile(
            extendedPath,
            Win32.FILE_LIST_DIRECTORY | Win32.FILE_READ_ATTRIBUTES | Win32.SYNCHRONIZE,
            Win32.FILE_SHARE_ALL,
            IntPtr.Zero,
            Win32.OPEN_EXISTING,
            // BACKUP_SEMANTICS is required to get a handle to a directory at all.
            // OPEN_REPARSE_POINT is deliberately NOT set: junctions are filtered during
            // traversal, but a user may legitimately point the scan root at one.
            Win32.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return null;
        }

        error = 0;
        return new DirectoryReader(handle);
    }

    /// <summary>
    /// Fills <paramref name="into"/> with the next batch of entries. Returns false once the
    /// directory is fully enumerated. "." and ".." are filtered out here.
    /// </summary>
    public bool ReadBatch(List<RawDirEntry> into)
    {
        into.Clear();
        if (_exhausted) return false;

        int infoClass = _useExtended
            ? (_first ? Win32.FileIdExtdDirectoryRestartInfo : Win32.FileIdExtdDirectoryInfo)
            : (_first ? Win32.FileFullDirectoryRestartInfo : Win32.FileFullDirectoryInfo);

        if (!Win32.GetFileInformationByHandleEx(_handle, infoClass, _buffer, BufferSize))
        {
            int err = Marshal.GetLastWin32Error();

            // The volume does not implement the extended class. Restart with the universal one.
            if (_useExtended && _first &&
                (err == Win32.ERROR_INVALID_PARAMETER || err == Win32.ERROR_NOT_SUPPORTED ||
                 err == Win32.ERROR_INVALID_FUNCTION))
            {
                _useExtended = false;
                return ReadBatch(into);
            }

            _exhausted = true;
            if (err is Win32.ERROR_NO_MORE_FILES or Win32.ERROR_SUCCESS) return false;

            // A directory deleted mid-scan is not an error worth aborting for.
            if (err is Win32.ERROR_FILE_NOT_FOUND or Win32.ERROR_PATH_NOT_FOUND) return false;

            throw new Win32Exception(err);
        }

        _first = false;
        Parse(into);
        // A batch can legitimately be empty (only "." and ".."); the caller loops until
        // the kernel reports ERROR_NO_MORE_FILES above.
        return true;
    }

    private unsafe void Parse(List<RawDirEntry> into)
    {
        byte* p = (byte*)_buffer;
        int offNext = _useExtended ? ExtdOffNextEntry : FullOffNextEntry;
        int offName = _useExtended ? ExtdOffFileName : FullOffFileName;
        int offNameLen = _useExtended ? ExtdOffFileNameLength : FullOffFileNameLength;
        int offAttrs = _useExtended ? ExtdOffAttributes : FullOffAttributes;
        int offSize = _useExtended ? ExtdOffEndOfFile : FullOffEndOfFile;
        int offCreate = _useExtended ? ExtdOffCreationTime : FullOffCreationTime;
        int offWrite = _useExtended ? ExtdOffLastWriteTime : FullOffLastWriteTime;

        while (true)
        {
            uint next = *(uint*)(p + offNext);
            uint nameLenBytes = *(uint*)(p + offNameLen);

            var name = new string((char*)(p + offName), 0, (int)(nameLenBytes / 2));

            if (name is not ("." or ".."))
            {
                var e = new RawDirEntry
                {
                    Name = name,
                    Size = *(long*)(p + offSize),
                    CreationTime = *(long*)(p + offCreate),
                    LastWriteTime = *(long*)(p + offWrite),
                    Attributes = *(uint*)(p + offAttrs),
                };

                if (_useExtended)
                {
                    e.ReparseTag = *(uint*)(p + ExtdOffReparseTag);
                    e.FileIdLow = *(ulong*)(p + ExtdOffFileId);
                    e.FileIdHigh = *(ulong*)(p + ExtdOffFileId + 8);
                    // A zero id means the filesystem did not supply one; do not trust it as identity.
                    e.HasFileId = e.FileIdLow != 0 || e.FileIdHigh != 0;
                }

                into.Add(e);
            }

            if (next == 0) break;
            p += next;
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
    }
}
