using System.Globalization;
using System.Text;
using MediaTool.Core.Dedupe;

namespace MediaTool.Core.Actions;

/// <summary>
/// The plan file format, shared by the CLI and the review app.
///
/// Deliberately a plain CSV a person can open and edit: the <c>action</c> column is the
/// override, and honouring an edited plan is the whole reason the plan and the execution are
/// separate steps.
/// </summary>
public static class PlanCsv
{
    private const string Header =
        "group,kind,action,file_key,kept_file_key,score,width,height,size," +
        "has_exif,exif_tags,date_taken,jpeg_quality,volume_guid,volume,path,pixel_hash,reason";

    public static void Write(string path, IReadOnlyList<PlanRow> rows)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(Header);

        foreach (var r in rows)
            writer.WriteLine(string.Join(',',
                r.Group,
                r.Kind,
                r.Action,
                r.File.FileKey,
                r.KeptFileKey?.ToString() ?? "",
                r.Score,
                r.File.Width,
                r.File.Height,
                r.File.Size,
                r.File.HasExif ? 1 : 0,
                r.File.ExifTags,
                r.File.DateTaken?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                r.File.JpegQuality?.ToString() ?? "",
                Quote(r.File.VolumeGuid),
                Quote(r.File.VolumeName),
                Quote(r.File.RelativePath),
                r.File.PixelHash ?? "",
                Quote(r.Reason)));
    }

    public static List<PlanRow> Read(string path)
    {
        var rows = new List<PlanRow>();

        foreach (string line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var f = Split(line);
            if (f.Count < 18) continue;

            // An edited plan is expected input, not a fault: an unrecognised action is
            // skipped rather than guessed at, since guessing here moves someone's photos.
            if (!Enum.TryParse<PlannedAction>(f[2], ignoreCase: true, out var action)) continue;
            if (!Enum.TryParse<GroupKind>(f[1], ignoreCase: true, out var kind)) continue;

            rows.Add(new PlanRow
            {
                Group = ParseInt(f[0]),
                Kind = kind,
                Action = action,
                Score = ParseInt(f[5]),
                Reason = f[17],
                KeptFileKey = string.IsNullOrEmpty(f[4]) ? null : ParseLong(f[4]),
                File = new KeeperCandidate
                {
                    FileKey = ParseLong(f[3]),
                    VolumeGuid = f[13],
                    VolumeName = f[14],
                    RelativePath = f[15],
                    Size = ParseLong(f[8]),
                    MTime = 0,
                    Width = ParseInt(f[6]),
                    Height = ParseInt(f[7]),
                    HasExif = f[9] == "1",
                    ExifTags = ParseInt(f[10]),
                    JpegQuality = string.IsNullOrEmpty(f[12]) ? null : ParseInt(f[12]),
                    // Carried through the file so a plan applied later can prove the file
                    // it is about to move is still the one that was reviewed.
                    PixelHash = string.IsNullOrEmpty(f[16]) ? null : f[16],
                },
            });
        }

        return rows;
    }

    private static int ParseInt(string s) => int.TryParse(s, out int v) ? v : 0;
    private static long ParseLong(string s) => long.TryParse(s, out long v) ? v : 0;

    private static string Quote(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    private static List<string> Split(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }
}
