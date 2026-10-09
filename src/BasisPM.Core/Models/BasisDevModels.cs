using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

public sealed class BasisDevSidecar
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public string Package { get; set; } = "";
    public string Folder { get; set; } = "";
    public BasisDevUpstream Upstream { get; set; } = new();
    public BasisDevManifestLine Manifest { get; set; } = new();
    public bool? HandledByProject { get; set; }
    public DateTimeOffset? Recorded { get; set; }
    public string? Tool { get; set; }
}

public sealed class BasisDevUpstream
{
    public string Url { get; set; } = "";
    public string? Ref { get; set; }
    public string? Path { get; set; }
    public string? Commit { get; set; }

    [JsonIgnore] public string? ManifestUrl => UpmGitUrl.Parse(Url) is { } parsed ? parsed.ToManifestUrl(Ref, Path) : null;
}

public sealed class BasisDevManifestLine
{
    public string? Original { get; set; }
    public string? Mounted { get; set; }
}

public enum PackageSourceKind
{
    None,
    ProjectRepo,
    LocalFolder,
    DevClone,
    LocalPath,
    Tarball,
    Git,
    Registry,
}

public enum BasisDevIssueKind
{
    MissingClone,
    Detached,
    Shadowed,
    StaleRecord,
    Unrecorded,
}

public enum BasisDevAction
{
    Record,
    Prune,
    UseClone,
    Release,
    Restore,
    Reclone,
    Ignore,
    Unignore,
}

public sealed record BasisDevComparison(bool Identical, int Added, int Removed, int Modified, string? MatchingCommit)
{
    public int Changed => Added + Removed + Modified;
}

public sealed record BasisDevPackage
{
    public required string Id { get; init; }
    public PackageSourceKind Source { get; init; }
    public string? ManifestValue { get; init; }
    public string? EmbeddedFolder { get; init; }
    public bool EmbeddedTracked { get; init; }
    public BasisDevSidecar? Sidecar { get; init; }
    public BasisDevSidecar? Proposed { get; init; }
    public string? RecordFolder { get; init; }
    public string? RecordOriginal { get; init; }
    public string? CloneFolder { get; init; }
    public bool CloneExists { get; init; }
    public bool CloneActive { get; init; }
    public string? PackageRoot { get; init; }
    public IReadOnlyList<BasisDevIssueKind> Issues { get; init; } = Array.Empty<BasisDevIssueKind>();
    public BasisDevComparison? Comparison { get; init; }

    public bool HasRecord => RecordFolder is not null;
    public bool IsTracked => Sidecar is not null || HasRecord;
    public bool HandledByProject => Sidecar?.HandledByProject == true && CloneExists && !CloneActive;
    public BasisDevIssueKind? Issue => Issues.Count == 0 ? null : Issues.Min();
    public BasisDevUpstream? Upstream => Sidecar?.Upstream ?? Proposed?.Upstream;
    public string? OriginalManifestValue => Sidecar?.Manifest.Original ?? RecordOriginal;

    public IReadOnlyList<BasisDevAction> Actions
    {
        get
        {
            var actions = new List<BasisDevAction>();
            if (HandledByProject) actions.AddRange(new[] { BasisDevAction.Unignore, BasisDevAction.Release });
            foreach (var issue in Issues.Order())
            {
                switch (issue)
                {
                    case BasisDevIssueKind.MissingClone:
                        actions.Add(BasisDevAction.Restore);
                        if (Upstream?.Url is { Length: > 0 } || UpmGitUrl.Parse(OriginalManifestValue) is not null) actions.Add(BasisDevAction.Reclone);
                        break;
                    case BasisDevIssueKind.Detached:
                        if (PackageRoot is not null) actions.Add(BasisDevAction.UseClone);
                        actions.Add(BasisDevAction.Release);
                        actions.Add(BasisDevAction.Ignore);
                        break;
                    case BasisDevIssueKind.Shadowed:
                        actions.Add(BasisDevAction.Release);
                        actions.Add(BasisDevAction.Ignore);
                        break;
                    case BasisDevIssueKind.StaleRecord:
                        actions.Add(BasisDevAction.Prune);
                        break;
                    case BasisDevIssueKind.Unrecorded:
                        if (Proposed is not null) actions.Add(BasisDevAction.Record);
                        break;
                }
            }
            return actions.Distinct().ToList();
        }
    }
}

public sealed record BasisDevReport(string UnityProjectPath, IReadOnlyList<BasisDevPackage> Packages)
{
    public static readonly BasisDevReport Empty = new("", Array.Empty<BasisDevPackage>());

    public BasisDevPackage? Find(string id) => Packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<BasisDevPackage> WithIssues => Packages.Where(p => p.Issues.Count > 0).ToList();
    public IReadOnlyList<BasisDevPackage> SafeFixes => Packages.Where(p => p.Actions.Any(IsSafe)).ToList();

    public static bool IsSafe(BasisDevAction action) => action is BasisDevAction.Record or BasisDevAction.Prune;
}

public sealed record BasisDevResult(bool Ok, string Message, bool NeedsForce = false)
{
    public static BasisDevResult Success(string message) => new(true, message);
    public static BasisDevResult Fail(string message, bool needsForce = false) => new(false, message, needsForce);
}
