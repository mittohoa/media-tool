using MediaTool.Core.Actions;
using MediaTool.Core.Dedupe;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// The plan file.
///
/// This is the one input a person is invited to edit by hand, which makes it the one place
/// where malformed data is expected rather than exceptional. A misparsed column here would
/// pair the wrong two files together — so the format has to survive real paths, and anything
/// it cannot understand has to be dropped rather than guessed at.
/// </summary>
public class PlanCsvTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediatool-csv", Guid.NewGuid().ToString("N")[..12]);

    public PlanCsvTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Path_(string name) => System.IO.Path.Combine(_dir, name);

    private static PlanRow Row(string path, PlannedAction action, long fileKey, long? keptKey = null) => new()
    {
        Group = 1,
        Kind = GroupKind.ExactBytes,
        Action = action,
        Score = 0,
        Reason = "test",
        KeptFileKey = keptKey,
        File = new KeeperCandidate
        {
            FileKey = fileKey,
            VolumeGuid = "{vol}",
            VolumeName = @"E:\",
            RelativePath = path,
            Size = 1234,
            MTime = 0,
            Width = 4000,
            Height = 3000,
            ExifTags = 40,
            JpegQuality = 92,
            PixelHash = "ABCDEF0123456789ABCDEF0123456789",
        },
    };

    [Fact]
    public void APlanSurvivesARoundTrip()
    {
        string file = Path_("plan.csv");
        var written = new List<PlanRow>
        {
            Row(@"Photos\keep.jpg", PlannedAction.Keep, 1),
            Row(@"Backup\drop.jpg", PlannedAction.Quarantine, 2, keptKey: 1),
        };

        PlanCsv.Write(file, written);
        var read = PlanCsv.Read(file);

        Assert.Equal(2, read.Count);
        Assert.Equal(PlannedAction.Keep, read[0].Action);
        Assert.Equal(@"Photos\keep.jpg", read[0].File.RelativePath);
        Assert.Equal(2, read[1].File.FileKey);
        Assert.Equal(1, read[1].KeptFileKey);
    }

    [Fact]
    public void ThePixelHashSurvivesSoAReviewedPlanCanStillBeVerifiedLater()
    {
        // Without this the ReviewedByHuman check has nothing to compare against, and a file
        // edited between review and apply would be moved anyway.
        string file = Path_("plan.csv");
        PlanCsv.Write(file, [Row(@"a.jpg", PlannedAction.Quarantine, 1, keptKey: 2)]);

        Assert.Equal("ABCDEF0123456789ABCDEF0123456789", PlanCsv.Read(file)[0].File.PixelHash);
    }

    [Fact]
    public void PathsWithCommasAndQuotesRoundTripIntact()
    {
        // Real libraries contain both; a naive split on commas would shift every later column.
        string awkward = @"Photos\Hue, Da Nang 2014\anh ""dep"" nhat.jpg";
        string file = Path_("plan.csv");

        PlanCsv.Write(file, [Row(awkward, PlannedAction.Quarantine, 1, keptKey: 2)]);

        Assert.Equal(awkward, PlanCsv.Read(file)[0].File.RelativePath);
    }

    [Fact]
    public void EditingTheActionColumnIsHonoured()
    {
        // The whole reason plan and apply are separate steps.
        string file = Path_("plan.csv");
        PlanCsv.Write(file, [Row(@"a.jpg", PlannedAction.Quarantine, 1, keptKey: 2)]);

        var lines = File.ReadAllLines(file);
        lines[1] = lines[1].Replace(",Quarantine,", ",Skip,");
        File.WriteAllLines(file, lines);

        Assert.Equal(PlannedAction.Skip, PlanCsv.Read(file)[0].Action);
    }

    [Fact]
    public void AnUnrecognisedActionIsDroppedRatherThanGuessedAt()
    {
        // A typo must not be interpreted as an instruction to move someone's photo.
        string file = Path_("plan.csv");
        PlanCsv.Write(file, [Row(@"a.jpg", PlannedAction.Quarantine, 1, keptKey: 2)]);

        var lines = File.ReadAllLines(file);
        lines[1] = lines[1].Replace(",Quarantine,", ",Quarintine,");
        File.WriteAllLines(file, lines);

        Assert.Empty(PlanCsv.Read(file));
    }

    [Fact]
    public void TruncatedAndBlankLinesAreIgnored()
    {
        string file = Path_("plan.csv");
        PlanCsv.Write(file, [Row(@"a.jpg", PlannedAction.Quarantine, 1, keptKey: 2)]);

        File.AppendAllLines(file, ["", "1,ExactBytes,Quarantine", "   "]);

        Assert.Single(PlanCsv.Read(file));
    }
}
