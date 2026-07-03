namespace BasisPM.Core.Models;

public enum GitChangeKind { Modified, Added, Deleted, Renamed, Untracked, Conflicted, Other }

public sealed record GitFileChange(string Code, string Path, GitChangeKind Kind, bool Staged)
{
    public string KindLabel => Kind switch
    {
        GitChangeKind.Modified => "modified",
        GitChangeKind.Added => "added",
        GitChangeKind.Deleted => "deleted",
        GitChangeKind.Renamed => "renamed",
        GitChangeKind.Untracked => "new",
        GitChangeKind.Conflicted => "conflict",
        _ => "changed",
    };
}

public sealed record GitStatus(string Branch, string ShortCommit, IReadOnlyList<GitFileChange> Changes, AheadBehind Upstream)
{
    public bool IsClean => Changes.Count == 0;
    public int ChangeCount => Changes.Count;
}

public sealed record AheadBehind(bool HasUpstream, int Ahead, int Behind)
{
    public static readonly AheadBehind None = new(false, 0, 0);
    public bool IsUpToDate => HasUpstream && Ahead == 0 && Behind == 0;
}

public sealed record GitResult(bool Ok, int Code, string Output);
