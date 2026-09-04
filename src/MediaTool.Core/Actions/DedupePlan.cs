using MediaTool.Core.Dedupe;

namespace MediaTool.Core.Actions;

/// <summary>Which relation put a group together — and therefore how it must be verified.</summary>
public enum GroupKind
{
    /// <summary>Byte-identical files. Verification is a byte comparison.</summary>
    ExactBytes,

    /// <summary>Identical picture, different container bytes. Verification is a re-decode.</summary>
    IdenticalPicture,

    /// <summary>Near duplicates. There is no exact check to make; a human has to look.</summary>
    NearDuplicate,

    /// <summary>
    /// A near-duplicate a person has actually looked at and decided about.
    ///
    /// The machine cannot prove two near-duplicates are the same photo — that is why the
    /// review app exists. But "a human judged it" does not mean "skip verification": what
    /// gets verified changes rather than disappearing. At apply time the file must still
    /// decode to the same picture it did when it was shown, so the thing being moved is
    /// provably the thing that was reviewed.
    /// </summary>
    ReviewedByHuman,
}

public enum PlannedAction
{
    Keep,
    Quarantine,
    /// <summary>Left alone because the plan could not justify a decision.</summary>
    Skip,
}

public sealed class PlanRow
{
    public required int Group { get; init; }
    public required GroupKind Kind { get; init; }
    public required PlannedAction Action { get; init; }
    public required KeeperCandidate File { get; init; }
    public required int Score { get; init; }
    public required string Reason { get; init; }

    /// <summary>File key of the copy being kept. Null on the keeper row itself.</summary>
    public long? KeptFileKey { get; init; }
}

public sealed class PlanSummary
{
    public int Groups;
    public int Keep;
    public int Quarantine;
    public int Skipped;
    public long ReclaimableBytes;
    public int GroupsWithOfflineCopies;
    public int GroupsMissingMetadataOnKeeper;
}
