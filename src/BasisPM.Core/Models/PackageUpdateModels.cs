using BasisPM.Core.Services;

namespace BasisPM.Core.Models;

public enum PackageUpdateOutcome { Updated, UpToDate, Conflict, Blocked, NotAClone, NotAPackage, Declined, Failed }

public sealed record PackageUpdateResult(PackageUpdateOutcome Outcome, string Message)
{
    public string? FromCommit { get; init; }
    public string? ToCommit { get; init; }
    public string? Ref { get; init; }
    public string? ManifestValue { get; init; }
    public bool KeptLocalChanges { get; init; }
    public bool Merged { get; init; }
    public IReadOnlyList<string> ConflictedFiles { get; init; } = Array.Empty<string>();
    public bool Ok => Outcome is PackageUpdateOutcome.Updated or PackageUpdateOutcome.UpToDate;

    public static PackageUpdateResult Fail(PackageUpdateOutcome outcome, string message) => new(outcome, message);
}

public sealed record PackageUpdateRequest(string? Ref, string? Url = null, string? Commit = null, string? ForkOf = null, bool ReplaceForkOf = false);

public sealed record PackageUpdateTarget(string PackageId, string Folder, string? PackageRoot, UpmGitUrl Source, string Commit, string? Ref);

public enum PackageUpdateKind { Release, Commits }

public sealed record PackageUpdateInfo(string PackageId, string Url, string? SubPath, PackageUpdateKind Kind, string? InstalledRef, string? InstalledCommit, string? TargetRef, string? TargetCommit, bool Available)
{
    public string? SkippedRef { get; init; }
    public CompatibilityResult? SkippedProblem { get; init; }
    public string? InstalledVersion => SemVer.TryParseTag(InstalledRef, out var version) ? version.ToString() : null;
    public string? TargetVersion => SemVer.TryParseTag(TargetRef, out var version) ? version.ToString() : null;

    public static string ShortCommit(string? commit) => commit is { Length: > 7 } ? commit[..7] : commit ?? "";
}

public sealed record PackageReleaseNote(string Tag, string? Name, string? Body, DateTimeOffset? Published, string? Url, bool Prerelease);

public sealed record PackageChanges(IReadOnlyList<PackageReleaseNote> Releases, IReadOnlyList<GitCommitInfo> Commits, bool MoreCommits, string? CompareUrl)
{
    public static PackageChanges Empty { get; } = new(Array.Empty<PackageReleaseNote>(), Array.Empty<GitCommitInfo>(), false, null);
    public bool IsEmpty => Releases.Count == 0 && Commits.Count == 0;
}
