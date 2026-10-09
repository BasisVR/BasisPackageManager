using BasisPM.App.Localization;
using BasisPM.Core.Models;

namespace BasisPM.App.ViewModels;

public sealed record BasisDevChoice(BasisDevItem Item, BasisDevAction Action, string Label, string Tooltip, bool IsPrimary, bool IsDestructive);

public sealed class BasisDevItem
{
    private readonly bool _basisRepo;

    public BasisDevItem(BasisDevPackage package, BasisInstall install)
    {
        Package = package;
        _basisRepo = install.IsBasisCheckout;
        Folder = package.EmbeddedFolder is null ? null : Relative(install.UnityProjectPath, package.EmbeddedFolder);
        Clone = package.CloneFolder is null ? null : Relative(install.UnityProjectPath, package.CloneFolder);
        SourceText = DescribeSource(package, install, Folder, Clone);
        CloneText = Clone is null ? null : L.Tr(CloneStateKey(package, _basisRepo), Clone);
        UpstreamText = package.Upstream is { } upstream && Clone is not null ? DescribeUpstream(upstream, package.Sidecar is null) : null;
        ComparisonText = package.Comparison is { } comparison ? DescribeComparison(comparison) : null;
        IssueLabel = package.Issue is { } issue ? L.Tr(IssueLabelKey(issue)) : null;
        IssueText = package.Issues.Count == 0 ? null : string.Join("\n", package.Issues.Order().Select(DescribeIssue));
        var primary = package.Actions.FirstOrDefault();
        Choices = package.Actions.Select(a => new BasisDevChoice(this, a, L.Tr(ActionLabelKey(a)), ActionTooltip(a), a == primary && a is not (BasisDevAction.Release or BasisDevAction.Unignore), a == BasisDevAction.Release)).ToList();
    }

    public BasisDevPackage Package { get; }
    public string Id => Package.Id;
    public string? Folder { get; }
    public string? Clone { get; }
    public string SourceText { get; }
    public string? CloneText { get; }
    public string? UpstreamText { get; }
    public string? ComparisonText { get; }
    public string? IssueLabel { get; }
    public string? IssueText { get; }
    public IReadOnlyList<BasisDevChoice> Choices { get; }
    public bool HasIssue => IssueLabel is not null;
    public bool HasClone => CloneText is not null;
    public bool HasUpstream => UpstreamText is not null;
    public bool HasComparison => ComparisonText is not null;
    public bool HasChoices => Choices.Count > 0;
    public bool CanOpenFolder => Directory.Exists(Package.CloneFolder) || Directory.Exists(Package.EmbeddedFolder);

    private string? RestoreLine =>
        Package.OriginalManifestValue is { Length: > 0 } original && !original.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? original
            : Package.Upstream?.ManifestUrl;

    private static string DescribeSource(BasisDevPackage package, BasisInstall install, string? folder, string? clone) => package.Source switch
    {
        PackageSourceKind.ProjectRepo => L.Tr(install.IsBasisCheckout ? "basisdev.source.basisRepo" : "basisdev.source.projectRepo", folder ?? ""),
        PackageSourceKind.LocalFolder => L.Tr("basisdev.source.localFolder", folder ?? ""),
        PackageSourceKind.DevClone => L.Tr("basisdev.source.devClone", clone ?? folder ?? ""),
        PackageSourceKind.LocalPath => L.Tr("basisdev.source.localPath", package.ManifestValue ?? ""),
        PackageSourceKind.Tarball => L.Tr("basisdev.source.tarball", package.ManifestValue ?? ""),
        PackageSourceKind.Git => L.Tr("basisdev.source.git", package.ManifestValue ?? ""),
        PackageSourceKind.Registry => L.Tr("basisdev.source.registry", package.ManifestValue ?? ""),
        _ => L.Tr("basisdev.source.none"),
    };

    private static string CloneStateKey(BasisDevPackage package, bool basisRepo) =>
        package.HandledByProject ? basisRepo ? "basisdev.clone.handledByBasis" : "basisdev.clone.handledByProject"
        : package.CloneExists ? package.CloneActive ? "basisdev.clone.inUse" : "basisdev.clone.notInUse"
        : Directory.Exists(package.CloneFolder) ? "basisdev.clone.notAClone" : "basisdev.clone.missing";

    private static string DescribeUpstream(BasisDevUpstream upstream, bool unrecorded)
    {
        var text = string.IsNullOrWhiteSpace(upstream.Url) ? L.Tr("basisdev.upstream.noOrigin") : upstream.Url;
        if (!string.IsNullOrWhiteSpace(upstream.Path)) text += " ?path=" + upstream.Path;
        if (!string.IsNullOrWhiteSpace(upstream.Ref)) text += " #" + upstream.Ref;
        if (!string.IsNullOrWhiteSpace(upstream.Commit)) text += " @ " + Short(upstream.Commit);
        return unrecorded ? L.Tr("basisdev.upstream.unrecorded", text) : text;
    }

    private static string DescribeComparison(BasisDevComparison comparison)
    {
        if (comparison.Identical) return L.Tr("basisdev.compare.identical", Short(comparison.MatchingCommit));
        var differs = L.Tr("basisdev.compare.differs", comparison.Changed, comparison.Added, comparison.Removed, comparison.Modified);
        return differs + " " + (comparison.MatchingCommit is { } match ? L.Tr("basisdev.compare.matches", Short(match)) : L.Tr("basisdev.compare.noMatch"));
    }

    private string DescribeIssue(BasisDevIssueKind issue) => issue switch
    {
        BasisDevIssueKind.MissingClone => L.Tr("basisdev.issue.missingClone.description", Clone ?? ""),
        BasisDevIssueKind.Detached => Package.ManifestValue is { } line
            ? L.Tr("basisdev.issue.detached.description", line, Clone ?? "")
            : L.Tr("basisdev.issue.unused.description", Id, Clone ?? ""),
        BasisDevIssueKind.Shadowed => L.Tr("basisdev.issue.shadowed.description", Folder ?? "", Clone ?? ""),
        BasisDevIssueKind.StaleRecord => L.Tr("basisdev.issue.staleRecord.description", Clone ?? ""),
        _ => L.Tr("basisdev.issue.unrecorded.description", Clone ?? ""),
    };

    private static string IssueLabelKey(BasisDevIssueKind issue) => issue switch
    {
        BasisDevIssueKind.MissingClone => "basisdev.issue.missingClone",
        BasisDevIssueKind.Detached or BasisDevIssueKind.Shadowed => "basisdev.issue.notInUse",
        BasisDevIssueKind.StaleRecord => "basisdev.issue.staleRecord",
        _ => "basisdev.issue.unrecorded",
    };

    private string ActionLabelKey(BasisDevAction action) => action switch
    {
        BasisDevAction.Ignore => _basisRepo ? "basisdev.action.ignoreBasis" : "basisdev.action.ignore",
        BasisDevAction.Unignore => "basisdev.action.unignore",
        BasisDevAction.Record => "basisdev.action.record",
        BasisDevAction.Prune => "basisdev.action.forget",
        BasisDevAction.UseClone => "basisdev.action.useClone",
        BasisDevAction.Release => "basisdev.action.release",
        BasisDevAction.Restore => "basisdev.action.restore",
        _ => "basisdev.action.reclone",
    };

    private string ActionTooltip(BasisDevAction action) => action switch
    {
        BasisDevAction.Record => L.Tr("basisdev.tooltip.record", Id),
        BasisDevAction.Prune => L.Tr("basisdev.tooltip.forget", Clone ?? ""),
        BasisDevAction.UseClone => L.Tr("basisdev.tooltip.useClone", Clone ?? ""),
        BasisDevAction.Release => L.Tr("basisdev.tooltip.release", Clone ?? ""),
        BasisDevAction.Restore => RestoreLine is { } line ? L.Tr("basisdev.tooltip.restore", line) : L.Tr("basisdev.tooltip.restoreRemove", Id),
        BasisDevAction.Ignore => L.Tr(_basisRepo ? "basisdev.tooltip.ignoreBasis" : "basisdev.tooltip.ignore", Clone ?? "", Id),
        BasisDevAction.Unignore => L.Tr("basisdev.tooltip.unignore", Clone ?? "", Id),
        _ => L.Tr("basisdev.tooltip.reclone", Package.Upstream?.Url ?? "", Clone ?? ""),
    };

    private static string Relative(string project, string folder) =>
        Path.GetRelativePath(project, folder).Replace('\\', '/');

    private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "" : sha.Length > 9 ? sha[..9] : sha;
}
