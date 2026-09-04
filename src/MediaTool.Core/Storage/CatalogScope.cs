using System.Text;

namespace MediaTool.Core.Storage;

/// <summary>
/// Narrows which catalogued files a query considers.
///
/// A library scanned whole usually mixes things that want different treatment — personal
/// photos alongside a web project's image assets, say. Rescanning a subset would throw away
/// the hashing and decode work already done for those exact files, which is the expensive
/// part; filtering the existing catalog gives the same answer immediately.
/// </summary>
public sealed class CatalogScope
{
    /// <summary>Only files whose path starts with one of these. Empty means every file.</summary>
    public List<string> Under { get; } = [];

    /// <summary>Files whose path contains any of these are dropped, even if <see cref="Under"/> matched.</summary>
    public List<string> Exclude { get; } = [];

    public bool IsEmpty => Under.Count == 0 && Exclude.Count == 0;

    /// <summary>
    /// Builds a SQL fragment for the WHERE clause, always starting with " AND " so it can be
    /// appended to an existing condition, or empty when nothing is scoped.
    ///
    /// Values are inlined rather than parameterised because the fragment has to compose into
    /// queries that already carry their own parameters; every value is escaped for LIKE and
    /// single-quoted here.
    /// </summary>
    public string ToSqlPredicate(string alias = "f")
    {
        if (IsEmpty) return "";

        var sql = new StringBuilder();

        if (Under.Count > 0)
        {
            sql.Append(" AND (");
            for (int i = 0; i < Under.Count; i++)
            {
                if (i > 0) sql.Append(" OR ");
                sql.Append(UnderPredicate(alias, Under[i]));
            }
            sql.Append(')');
        }

        foreach (string fragment in Exclude)
            sql.Append($" AND {alias}.rel_path NOT LIKE '%{EscapeLike(Split(fragment).Relative)}%' ESCAPE '\\'");

        return sql.ToString();
    }

    /// <summary>
    /// One subtree condition.
    ///
    /// The catalog stores a path relative to its volume, so a drive letter has to be matched
    /// against the volume rather than thrown away. Throwing it away was a real bug with two
    /// silent failures: "F:" narrowed to the empty string and so matched the whole catalog,
    /// and "F:\Photos" matched "E:\Photos" as well. Both widened the scope instead of
    /// narrowing it, which for a tool that moves files is the wrong direction to be wrong in.
    /// </summary>
    private static string UnderPredicate(string alias, string fragment)
    {
        var (drive, relative) = Split(fragment);

        string onVolume = drive is null
            ? ""
            : $"{alias}.volume_id IN (SELECT volume_id FROM volumes "
              + $"WHERE UPPER(last_mount_point) LIKE '{EscapeLike(drive)}:%' ESCAPE '\\')";

        // Anchored at the start: "under" means a subtree, not a substring anywhere.
        string onPath = relative.Length == 0
            ? ""
            : $"{alias}.rel_path LIKE '{EscapeLike(relative)}%' ESCAPE '\\'";

        if (onVolume.Length == 0) return onPath.Length == 0 ? "1=1" : onPath;
        if (onPath.Length == 0) return onVolume;

        return $"({onVolume} AND {onPath})";
    }

    /// <summary>
    /// Separates a drive letter from the rest, since a user thinks in terms of "E:\Photos"
    /// while the catalog keeps the volume and "Photos" apart.
    /// </summary>
    private static (string? Drive, string Relative) Split(string fragment)
    {
        string value = fragment.Trim().Trim('"');

        string? drive = null;
        if (value.Length >= 2 && value[1] == ':' && char.IsLetter(value[0]))
        {
            drive = char.ToUpperInvariant(value[0]).ToString();
            value = value[2..];
        }

        return (drive, value.Trim('\\'));
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_")
        .Replace("'", "''");

    public override string ToString()
    {
        if (IsEmpty) return "whole catalog";
        var parts = new List<string>();
        if (Under.Count > 0) parts.Add("under " + string.Join(", ", Under));
        if (Exclude.Count > 0) parts.Add("excluding " + string.Join(", ", Exclude));
        return string.Join("; ", parts);
    }
}
