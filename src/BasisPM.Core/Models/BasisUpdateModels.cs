using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

public enum BasisUpdateKind { UpToDate, FastForward, Merge, Unrelated, NotGitRepo, InProgress, Blocked, Apply }

public enum BasisUpdateBlock { None, GitMissing, GitTooOld, DetachedHead, OperationInProgress, BranchNotFound, FetchFailed, ShallowClone, BasisFolderMissing }

public enum BasisUpdateStatus { Unknown, UpToDate, UpdateAvailable, NotGitRepo, Error }

[JsonConverter(typeof(JsonStringEnumConverter<BasisUpdatePhase>))]
public enum BasisUpdatePhase { Merging, RestoringChanges }

[JsonConverter(typeof(JsonStringEnumConverter<BasisOperation>))]
public enum BasisOperation { Merge, Apply, BranchSwitch }

public enum BasisBaseSource { History, Recorded, Similarity }

public enum BasisBranchSource { Saved, Recorded, Tracking, History, Estimated, SameName, Default }

public sealed class BasisBaseEstimate
{
    [JsonPropertyName("localBranch")]
    public string LocalBranch { get; set; } = "";

    [JsonPropertyName("basisCommit")]
    public string BasisCommit { get; set; } = "";

    [JsonPropertyName("basisBranch")]
    public string BasisBranch { get; set; } = "";

    [JsonPropertyName("layout")]
    public string Layout { get; set; } = "";
}

public sealed record BasisLayout(string BasisFolder, string ProjectFolder)
{
    public static BasisLayout Whole { get; } = new("", "");

    public bool IsWhole => BasisFolder.Length == 0 && ProjectFolder.Length == 0;

    public string ToProjectPath(string basisPath) => ProjectFolder.Length == 0 ? basisPath : ProjectFolder + "/" + basisPath;

    public override string ToString() => $"{(BasisFolder.Length == 0 ? "." : BasisFolder)} -> {(ProjectFolder.Length == 0 ? "." : ProjectFolder)}";

    public static BasisLayout? Parse(string? text)
    {
        var parts = (text ?? "").Split(" -> ", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return null;
        var basis = Folder(parts[0]);
        var project = Folder(parts[1]);
        return basis is null || project is null ? null : new BasisLayout(basis, project);
    }

    private static string? Folder(string text)
    {
        var folder = text.Replace('\\', '/').Trim('/');
        if (folder is "." or "") return "";
        return folder.Split('/').All(s => s.Length > 0 && s is not ("." or "..") && !s.Any(char.IsControl)) ? folder : null;
    }
}

public sealed record BasisRecord(string Commit, string BasisSha, string? Branch, BasisLayout Layout);

public sealed record BasisBaseInfo(string Sha, string Branch, BasisBaseSource Source, bool Connected, BasisLayout Layout, string? RecordCommit = null);

public sealed record BasisPackages(IReadOnlySet<string> Dependencies, IReadOnlySet<string>? EmbeddedFolders);

public sealed record BasisBaseCandidate(string Branch, BasisLayout Layout, BasisBaseMatch Match);

public sealed record ProjectBranch(string Name, string? Remote, bool IsCurrent)
{
    public string Display => Remote is null ? Name : $"{Remote}/{Name}";
}

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
    SwitchFailed,
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

public sealed record BasisUpdateCheck(BasisUpdateStatus Status, string BasisBranch, string? RemoteSha, int? Behind, bool InProgress, string? Detail = null,
    BasisOperation Operation = BasisOperation.Merge);

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

    [JsonPropertyName("operation")]
    public BasisOperation Operation { get; set; }

    [JsonPropertyName("baseSha")]
    public string? BaseSha { get; set; }

    [JsonPropertyName("basisParent")]
    public string? BasisParent { get; set; }

    [JsonPropertyName("commitMessage")]
    public string? CommitMessage { get; set; }

    [JsonPropertyName("targetBranch")]
    public string? TargetBranch { get; set; }

    // Staged edits the update doesn't touch. They leave the index while it runs and go back exactly as they were.
    [JsonPropertyName("stagedEntries")]
    public List<BasisStagedEntry> StagedEntries { get; set; } = new();

    [JsonPropertyName("discardedMeta")]
    public List<string> DiscardedMeta { get; set; } = new();
}

public sealed record BasisStagedEntry(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("path")] string Path);

public sealed class BasisUpdatePlan
{
    public required BasisUpdateKind Kind { get; init; }
    public BasisUpdateBlock Block { get; init; }
    public string? Detail { get; init; }
    public string BasisBranch { get; init; } = "";
    public string? FromBranch { get; init; }
    public string? LocalBranch { get; init; }
    public string? HeadSha { get; init; }
    public string? UpstreamSha { get; init; }
    public string? MergeBase { get; init; }
    public string? ApplyCommit { get; init; }
    public bool Connected { get; init; }
    public BasisLayout Layout { get; init; } = BasisLayout.Whole;
    public BasisBaseSource BaseSource { get; init; }
    public int IncomingCount { get; init; }
    public IReadOnlyList<GitCommitInfo> IncomingCommits { get; init; } = Array.Empty<GitCommitInfo>();
    public int OutgoingCount { get; init; }
    public int LocalCommitCount { get; init; }
    public int UncommittedCount { get; init; }
    public IReadOnlyList<string> CollidingPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockingPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ReplacedMetaPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string>? PredictedConflicts { get; init; }
    public BasisBaseMatch? SuggestedBase { get; init; }
    public BasisUpdateState? State { get; init; }

    public bool IsSwitch => FromBranch is not null && !string.Equals(FromBranch, BasisBranch, StringComparison.Ordinal);

    public bool CanApply => (Kind is BasisUpdateKind.FastForward or BasisUpdateKind.Merge or BasisUpdateKind.Apply) && BlockingPaths.Count == 0;
}

public sealed class BranchSwitchPlan
{
    public required ProjectBranch Target { get; init; }
    public BasisUpdateBlock Block { get; init; }
    public string? Detail { get; init; }
    public string? CurrentBranch { get; init; }
    public string? HeadSha { get; init; }
    public string? TargetSha { get; init; }
    public bool CreatesBranch { get; init; }
    public int UncommittedCount { get; init; }
    public IReadOnlyList<string> CollidingPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockingPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ReplacedMetaPaths { get; init; } = Array.Empty<string>();
    public bool InProgress { get; init; }

    public bool CanSwitch => Block == BasisUpdateBlock.None && !InProgress && TargetSha is not null && BlockingPaths.Count == 0;
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

    // What setting up git did along the way, for the caller to mention: git files it added or completed, package folders a
    // .gitignore was hiding that it brought back, folders that are git clones of their own and were left out, and files too
    // big for GitHub.
    public IReadOnlyList<string> AddedFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Unhidden { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LeftOut { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LargeFiles { get; init; } = Array.Empty<string>();
}
