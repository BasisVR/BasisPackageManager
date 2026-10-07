using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

public enum BasisUpdateKind { UpToDate, FastForward, Merge, Unrelated, NotGitRepo, InProgress, Blocked }

public enum BasisUpdateBlock { None, GitMissing, GitTooOld, DetachedHead, OperationInProgress, BranchNotFound, FetchFailed, ShallowClone }

public enum BasisUpdateStatus { Unknown, UpToDate, UpdateAvailable, NotGitRepo, Error }

[JsonConverter(typeof(JsonStringEnumConverter<BasisUpdatePhase>))]
public enum BasisUpdatePhase { Merging, RestoringChanges }

public enum BasisUpdateResultKind { Updated, Conflicts, Failed, Aborted }

public enum BasisUpdateFailure
{
    None,
    NotApplicable,
    AlreadyInProgress,
    NoUpdateInProgress,
    HeadMoved,
    IgnoredFilesInTheWay,
    SetAsideFailed,
    MergeFailed,
    CommitFailed,
    RestoreFailed,
    AbortFailed,
    ConflictsRemain,
    MarkersRemain,
    ResolveFailed,
    LibraryNotIgnored,
    InitFailed,
    LinkFailed,
}

public enum ConflictKind { BothChanged, BothAdded, DeletedByYou, DeletedByBasis, BothDeleted }

public enum ConflictChoice { Mine, Basis, Resolved }

public sealed record GitCommitInfo(string Sha, string ShortSha, string Author, DateTimeOffset Date, string Subject);

public sealed record GitWorkingEntry(string Code, string Path, string? OriginalPath)
{
    public bool IsUntracked => Code == "??";
    public bool IsStaged => Code.Length > 0 && Code[0] is not ' ' and not '?';
}

public sealed record GitConflict(string Path, bool HasBase, bool HasOurs, bool HasTheirs);

public sealed record TreeDiffStats(int Added, int Deleted, int Modified);

public sealed record BasisBaseMatch(string Sha, string ShortSha, DateTimeOffset Date, string Subject, int MatchingFiles, int BasisFiles)
{
    public double MatchRatio => BasisFiles == 0 ? 0 : (double)MatchingFiles / BasisFiles;
}

public sealed record BasisConflict(string Path, ConflictKind Kind, bool IsUnityAsset);

public sealed record BasisUpdateCheck(BasisUpdateStatus Status, string BasisBranch, string? RemoteSha, int? Behind, bool InProgress, string? Detail = null);

public sealed class BasisUpdateState
{
    [JsonPropertyName("phase")]
    public BasisUpdatePhase Phase { get; set; }

    [JsonPropertyName("basisBranch")]
    public string BasisBranch { get; set; } = "";

    [JsonPropertyName("upstreamSha")]
    public string UpstreamSha { get; set; } = "";

    [JsonPropertyName("localBranch")]
    public string LocalBranch { get; set; } = "";

    [JsonPropertyName("preUpdateHead")]
    public string PreUpdateHead { get; set; } = "";

    [JsonPropertyName("stashSha")]
    public string? StashSha { get; set; }

    [JsonPropertyName("stashApplied")]
    public bool StashApplied { get; set; }

    [JsonPropertyName("setAsidePaths")]
    public List<string> SetAsidePaths { get; set; } = new();

    [JsonPropertyName("startedUtc")]
    public DateTimeOffset StartedUtc { get; set; }
}

public sealed class BasisUpdatePlan
{
    public required BasisUpdateKind Kind { get; init; }
    public BasisUpdateBlock Block { get; init; }
    public string? Detail { get; init; }
    public string BasisBranch { get; init; } = "";
    public string? LocalBranch { get; init; }
    public string? HeadSha { get; init; }
    public string? UpstreamSha { get; init; }
    public string? MergeBase { get; init; }
    public int IncomingCount { get; init; }
    public IReadOnlyList<GitCommitInfo> IncomingCommits { get; init; } = Array.Empty<GitCommitInfo>();
    public int LocalCommitCount { get; init; }
    public int UncommittedCount { get; init; }
    public IReadOnlyList<string> CollidingPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockingPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string>? PredictedConflicts { get; init; }
    public BasisBaseMatch? SuggestedBase { get; init; }
    public BasisUpdateState? State { get; init; }

    public bool CanApply => (Kind is BasisUpdateKind.FastForward or BasisUpdateKind.Merge) && BlockingPaths.Count == 0;
}

public sealed record BasisUpdateResult(
    BasisUpdateResultKind Kind,
    BasisUpdateFailure Failure = BasisUpdateFailure.None,
    BasisUpdatePhase? Phase = null,
    IReadOnlyList<BasisConflict>? Conflicts = null,
    string? NewHead = null,
    string? Detail = null)
{
    public static BasisUpdateResult Updated(string? head) => new(BasisUpdateResultKind.Updated, NewHead: head);
    public static BasisUpdateResult Fail(BasisUpdateFailure failure, string? detail = null) => new(BasisUpdateResultKind.Failed, failure, Detail: detail);
    public static BasisUpdateResult NeedsResolving(BasisUpdatePhase phase, IReadOnlyList<BasisConflict> conflicts) =>
        new(BasisUpdateResultKind.Conflicts, Phase: phase, Conflicts: conflicts);
    public static BasisUpdateResult Aborted(string? head) => new(BasisUpdateResultKind.Aborted, NewHead: head);
}

public sealed record BasisStepResult(bool Ok, BasisUpdateFailure Failure = BasisUpdateFailure.None, string? Detail = null)
{
    public static BasisStepResult Success { get; } = new(true);
    public static BasisStepResult Fail(BasisUpdateFailure failure, string? detail = null) => new(false, failure, detail);
}
