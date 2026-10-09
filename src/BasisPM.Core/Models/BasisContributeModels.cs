namespace BasisPM.Core.Models;

public enum BasisChangeKind { Added, Modified, Deleted }

public enum BasisChangeArea { Package, Project, Repository }

public enum BasisContributeBlock
{
    None,
    GitMissing,
    GitTooOld,
    NotGitRepo,
    NoCommits,
    UpdateInProgress,
    OperationInProgress,
    BaseUnknown,
    BaseMissing,
    BasisFolderMissing,
    ReadFailed,
}

public sealed record BasisChange(string Path, string ProjectPath, BasisChangeKind Kind, string? Mode, string? Sha)
{
    public bool IsMeta => Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
}

public sealed record BasisChangeGroup(BasisChangeArea Area, string Name, string Folder, bool InBasis, IReadOnlyList<BasisChange> Changes)
{
    public string RelativePath(BasisChange change) =>
        Folder.Length > 0 && change.Path.StartsWith(Folder + "/", StringComparison.Ordinal) ? change.Path[(Folder.Length + 1)..] : change.Path;
}

public sealed record BasisContributeScan
{
    public BasisContributeBlock Block { get; init; }
    public string? Detail { get; init; }
    public string RepoRoot { get; init; } = "";
    public BasisBaseInfo? Base { get; init; }
    public GitCommitInfo? BaseCommit { get; init; }
    public string? UnityFolder { get; init; }
    public string? BaseTree { get; init; }
    public string? ProjectTree { get; init; }
    public IReadOnlyList<BasisChangeGroup> Groups { get; init; } = Array.Empty<BasisChangeGroup>();
    public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();

    public bool IsBlocked => Block != BasisContributeBlock.None;
    public IEnumerable<BasisChange> Changes => Groups.SelectMany(g => g.Changes);
    public int ChangeCount => Groups.Sum(g => g.Changes.Count);

    public BasisChangeGroup? FindPackage(string id) =>
        Groups.FirstOrDefault(g => g.Area == BasisChangeArea.Package && string.Equals(g.Name, id, StringComparison.OrdinalIgnoreCase));
}

public enum BasisDiffLineKind { Header, Hunk, Added, Removed, Context, Note }

public sealed record BasisDiffLine(BasisDiffLineKind Kind, string Text);

public sealed record BasisPullRequestDraft(string Title, string? Body, string Branch, string TargetBranch);

public sealed record BasisContributeCommit(bool Ok, string? Commit, int FileCount, string? Error)
{
    public static BasisContributeCommit Fail(string error) => new(false, null, 0, error);
}

public sealed record BasisContributeResult(bool Ok, string? Url, string? Commit, bool Forked, bool Updated, string? Error, string? CompareUrl = null)
{
    public static BasisContributeResult Success(string url, string commit, bool forked, bool updated) => new(true, url, commit, forked, updated, null);
    public static BasisContributeResult Fail(string error, string? commit = null, string? compareUrl = null) => new(false, null, commit, false, false, error, compareUrl);
}
