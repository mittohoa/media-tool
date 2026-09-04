namespace MediaTool.Core.Util;

/// <summary>
/// Win32 path helpers. Photo libraries organised by event/date routinely blow past
/// MAX_PATH (260), and a crawler that quietly drops those directories loses whole
/// branches of the collection without reporting anything.
/// </summary>
public static class LongPath
{
    private const string DevicePrefix = @"\\?\";
    private const string UncDevicePrefix = @"\\?\UNC\";

    /// <summary>
    /// Converts a fully-qualified path to its \\?\ form, which lifts the MAX_PATH limit.
    /// The prefix also disables path normalisation, so the input must already be absolute
    /// and free of "." / ".." segments.
    /// </summary>
    public static string Prefix(string fullPath)
    {
        if (fullPath.StartsWith(DevicePrefix, StringComparison.Ordinal)) return fullPath;

        // The device prefix turns off path normalisation, which includes the usual courtesy
        // of accepting forward slashes. Paths built from the catalog already use backslashes,
        // but one typed at the command line may not, and it would fail as "file not found"
        // rather than as anything that points at the cause.
        fullPath = fullPath.Replace('/', '\\');

        // \\server\share -> \\?\UNC\server\share
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return UncDevicePrefix + fullPath[2..];

        return DevicePrefix + fullPath;
    }

    /// <summary>Appends a child name to a directory path without Path.Combine's drive-relative trap.</summary>
    public static string Join(string directory, string name) =>
        directory.EndsWith('\\') ? directory + name : directory + '\\' + name;
}
