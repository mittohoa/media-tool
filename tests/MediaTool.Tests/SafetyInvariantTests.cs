using System.Text.RegularExpressions;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// The promises this tool makes about never destroying a photo, enforced against the source
/// itself rather than against its behaviour.
///
/// Behavioural tests prove the code did the right thing on the inputs they tried. These
/// prove something narrower but far stronger: the ability to destroy a file exists in
/// exactly the places listed below and nowhere else. Adding a new one is not forbidden —
/// merging EXIF needed a write, purging needed a delete — but it cannot be done quietly.
/// The declared counts are the review gate: a new call site fails the build until someone
/// writes down what it is and why it is safe.
/// </summary>
public class SafetyInvariantTests
{
    private static readonly string SourceRoot = FindSourceRoot();

    /// <summary>
    /// Permanent deletion. Two sites, both operating on files the tool itself put there:
    /// the purge, and the merger discarding its own rejected temporary file.
    /// </summary>
    private static readonly Dictionary<string, int> AllowedDeletes = new()
    {
        ["QuarantinePurger.cs"] = 1,
        ["MetadataMerger.cs"] = 2,
        // Removing a hardlink, once its identity proves the bytes belong to the kept copy:
        // one when a link fails to verify, one when undoing a batch.
        ["HardlinkExecutor.cs"] = 2,
        // Removing the .lnk shortcuts this tool created, during uninstall. It touches
        // nothing in the library and cannot be pointed at anything else — the paths are
        // computed, never supplied.
        ["ShellIntegration.cs"] = 1,
    };

    /// <summary>
    /// Moves. Every one is reversible, and each has a matching undo path.
    /// </summary>
    private static readonly Dictionary<string, int> AllowedMoves = new()
    {
        // into quarantine, back out via the catalog, back out via the manifest
        ["PlanExecutor.cs"] = 3,
        // the original out to quarantine, the enriched copy into its place
        ["MetadataMerger.cs"] = 2,
        // out to quarantine, and three routes back: link creation failed, link failed to
        // verify, and the explicit undo
        ["HardlinkExecutor.cs"] = 4,
        // the catalog itself being set aside, not a photograph: the database file and its
        // write-ahead log. Renamed rather than deleted so a reset can be walked back.
        ["CatalogReset.cs"] = 1,
    };

    /// <summary>
    /// Writing new bytes into the library. One site, and it only ever creates a file at a
    /// name nothing occupies — never replacing anything.
    /// </summary>
    private static readonly Dictionary<string, int> AllowedWrites = new()
    {
        ["MetadataMerger.cs"] = 1,
    };

    /// <summary>Calls that would let code replace a file in place, which nothing here may do.</summary>
    private static readonly string[] ForbiddenApis =
    [
        @"Directory\.Delete",
        @"File\.WriteAllLines",
        @"File\.AppendAll",
        @"File\.Copy",
        @"File\.Replace",
        @"FileMode\.Create",
        @"FileMode\.Truncate",
        @"FileMode\.Append",
        @"FileAccess\.Write",
        @"FileAccess\.ReadWrite",
        @"\.SetLength",
    ];

    [Fact]
    public void NothingCanReplaceAFileInPlace()
    {
        var offenders = new List<string>();

        foreach (string file in EnumerateSourceFiles("MediaTool.Core"))
        {
            string text = File.ReadAllText(file);

            foreach (string api in ForbiddenApis)
                foreach (Match match in Regex.Matches(text, api))
                {
                    // Writing the catalog, a report or a manifest is not touching a photo.
                    if (IsToolOwnedFile(text, match.Index)) continue;
                    offenders.Add($"{Path.GetFileName(file)}: {api}");
                }
        }

        Assert.True(offenders.Count == 0,
            "Nothing may overwrite a file in place. Found: " + string.Join(", ", offenders));
    }

    [Fact]
    public void DeletionHappensOnlyWhereItIsDeclared()
        => AssertCallSites(@"File\.Delete", AllowedDeletes, "delete a file");

    [Fact]
    public void MovingUserFilesHappensOnlyWhereItIsDeclared()
        => AssertCallSites(@"File\.Move", AllowedMoves, "move a user's file");

    [Fact]
    public void WritingIntoTheLibraryHappensOnlyWhereItIsDeclared()
        => AssertCallSites(@"File\.WriteAllBytes", AllowedWrites, "write bytes into the library");

    [Fact]
    public void PurgingIsGatedOnRetentionThenContainment()
    {
        string purger = ReadCoreFile("Actions", "QuarantinePurger.cs");

        int retention = purger.IndexOf("if (!batch.IsRipe(retention))", StringComparison.Ordinal);
        int containment = purger.IndexOf("if (!IsInside(root, destination))", StringComparison.Ordinal);
        int dryRun = purger.IndexOf("if (dryRun)", StringComparison.Ordinal);
        int delete = purger.IndexOf("File.Delete", StringComparison.Ordinal);

        Assert.True(retention > 0, "purge must enforce a retention period");
        Assert.True(containment > 0, "purge must confirm the path is inside the quarantine folder");
        Assert.True(retention < containment, "retention is checked before any file is considered");
        Assert.True(containment < delete, "containment is checked before anything is deleted");
        Assert.True(dryRun < delete, "the dry-run branch comes before the delete");
    }

    [Fact]
    public void MergingNeverOverwritesAndAlwaysVerifiesFirst()
    {
        string merger = ReadCoreFile("Actions", "MetadataMerger.cs");

        int tempGuard = merger.IndexOf("if (File.Exists(tempPath))", StringComparison.Ordinal);
        int write = merger.IndexOf("File.WriteAllBytes", StringComparison.Ordinal);
        int verify = merger.IndexOf("VerifyMerged(", StringComparison.Ordinal);
        int backup = merger.IndexOf("File.Move(LongPath.Prefix(keeperPath)", StringComparison.Ordinal);

        Assert.True(tempGuard > 0, "the merger must refuse to reuse an existing temporary file");
        Assert.True(tempGuard < write, "the temp-file guard comes before the write");
        Assert.True(write < verify, "the merged file is verified after it is written");
        Assert.True(verify < backup, "verification happens before the original is moved aside");
    }

    [Fact]
    public void LinkingIsGatedOnIdentityAndRevertsOnFailure()
    {
        string executor = ReadCoreFile("Actions", "HardlinkExecutor.cs");

        int exactOnly = executor.IndexOf("row.Kind != GroupKind.ExactBytes", StringComparison.Ordinal);
        int bytesEqual = executor.IndexOf("ContentHasher.ContentsEqual", StringComparison.Ordinal);
        int dryRun = executor.IndexOf("if (dryRun)", StringComparison.Ordinal);
        int backup = executor.IndexOf("File.Move(LongPath.Prefix(duplicate)", StringComparison.Ordinal);
        int link = executor.IndexOf("Win32.CreateHardLink", StringComparison.Ordinal);

        Assert.True(exactOnly > 0, "only byte-identical groups may be collapsed into one file");
        Assert.True(bytesEqual > 0 && bytesEqual < dryRun, "the bytes are compared before anything happens");
        Assert.True(dryRun < backup, "the dry-run branch comes before the file is moved");
        Assert.True(backup < link, "the original is safely in quarantine before its path is reused");
    }

    [Fact]
    public void ExecutingAPlanIsOptInAndVerified()
    {
        string executor = ReadCoreFile("Actions", "PlanExecutor.cs");

        int verify = executor.IndexOf("var (verified, why) = Verify(", StringComparison.Ordinal);
        int dryRun = executor.IndexOf("if (dryRun)", StringComparison.Ordinal);
        int move = executor.IndexOf("File.Move", StringComparison.Ordinal);

        Assert.True(verify > 0 && verify < move, "verification comes before the move");
        Assert.True(dryRun > 0 && dryRun < move, "the dry-run branch comes before the move");
    }

    // ---- helpers -----------------------------------------------------------

    private static void AssertCallSites(string pattern, Dictionary<string, int> allowed, string what)
    {
        var found = new Dictionary<string, int>();

        foreach (string file in EnumerateSourceFiles("MediaTool.Core"))
        {
            int count = Regex.Matches(File.ReadAllText(file), pattern).Count;
            if (count > 0) found[Path.GetFileName(file)] = count;
        }

        foreach (var (file, count) in found)
        {
            Assert.True(allowed.ContainsKey(file),
                $"{file} can now {what}, which was not declared. If that is intended, add it to " +
                "the allow-list in SafetyInvariantTests with a note saying why it is safe.");

            Assert.True(allowed[file] == count,
                $"{file} has {count} call(s) that {what}; {allowed[file]} were declared. " +
                "A new one needs its own review.");
        }

        foreach (string file in allowed.Keys)
            Assert.True(found.ContainsKey(file),
                $"{file} no longer contains any call that can {what}; the allow-list is stale.");
    }

    /// <summary>
    /// True when the write is aimed at a file the tool created — the catalog, a CSV report,
    /// a manifest — rather than at something in the user's library.
    /// </summary>
    private static bool IsToolOwnedFile(string text, int index)
    {
        int lineStart = text.LastIndexOf('\n', Math.Min(index, text.Length - 1)) + 1;
        int lineEnd = text.IndexOf('\n', index);
        string line = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)];

        return line.Contains("csv", StringComparison.OrdinalIgnoreCase)
            || line.Contains("manifest", StringComparison.OrdinalIgnoreCase)
            || line.Contains("StreamWriter", StringComparison.Ordinal);
    }

    private static string ReadCoreFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([SourceRoot, "src", "MediaTool.Core", .. parts]));

    private static IEnumerable<string> EnumerateSourceFiles(string project) =>
        Directory.EnumerateFiles(Path.Combine(SourceRoot, "src", project), "*.cs", SearchOption.AllDirectories)
                 .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                          && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaTool.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the solution root.");
    }
}
