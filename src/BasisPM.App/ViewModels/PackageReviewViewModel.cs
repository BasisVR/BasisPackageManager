using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public enum InstallOrigin { Registry, Unofficial, GitUrl, Link, PackageList }

public sealed record ReviewCapabilityItem(string Title, string Why, bool IsHigh, IReadOnlyList<string> Places, string More)
{
    public bool HasMore => More.Length > 0;
}

public sealed record ReviewIssueItem(string Message, string Location, string Fix, IssueSeverity Severity)
{
    public bool HasLocation => Location.Length > 0;
    public bool HasFix => Fix.Length > 0;
    public bool IsError => Severity == IssueSeverity.Error;
    public bool IsWarning => Severity == IssueSeverity.Warning;
    public bool IsInfo => Severity == IssueSeverity.Info;
    public string SeverityLabel => L.Tr("review.compat." + Severity.ToString().ToLowerInvariant());
}

public sealed class PackageReviewViewModel
{
    private const int PlacesShown = 4;

    public PackageReviewViewModel(PackageReview review, InstallOrigin origin, bool trusted, string? reviewedCommit, PackageScan scan,
        PackageAdvisory? advisory, IReadOnlyList<PackageIssue> issues, string? claimedName = null)
    {
        Review = review;
        Origin = origin;
        ShowSafety = !trusted || advisory is not null;
        Scan = scan;
        Advisory = advisory;
        Reason = ReasonFor(origin, trusted, reviewedCommit, review.Commit, issues.Count > 0);
        NameWarning = NameWarningFor(review, claimedName);
        Capabilities = ShowSafety
            ? scan.Capabilities.Select(capability =>
            {
                var found = scan.Findings.Where(f => f.Capability == capability).ToList();
                var places = found.Take(PlacesShown).Select(f => $"{f.Location}  ·  {f.Evidence}").ToList();
                var more = found.Count > PlacesShown ? L.Tr("review.more", found.Count - PlacesShown) : "";
                return new ReviewCapabilityItem(L.Tr($"review.cap.{capability}.title"), L.Tr($"review.cap.{capability}.why"), scan.RiskOf(capability) == PackageRisk.High, places, more);
            }).ToList()
            : Array.Empty<ReviewCapabilityItem>();
        Issues = issues
            .OrderByDescending(i => i.Severity)
            .Select(i => new ReviewIssueItem(i.Message, i.Location, i.Fix ?? "", i.Severity))
            .ToList();
    }

    public PackageReview Review { get; }
    public InstallOrigin Origin { get; }
    public PackageScan Scan { get; }
    public PackageAdvisory? Advisory { get; }
    public bool ShowSafety { get; }

    public string Title => L.Tr("review.title", Review.DisplayName);
    public string Reason { get; }
    public bool HasReason => Reason.Length > 0;
    public string? NameWarning { get; }
    public bool HasNameWarning => !string.IsNullOrEmpty(NameWarning);

    public string SourceText => Review.Source.Host + "/" + Review.Source.Slug + (string.IsNullOrEmpty(Review.Source.Path) ? "" : "  ·  " + Review.Source.Path);
    public string VersionText => string.Join("  ·  ", new[]
    {
        Review.Package?.Version is { Length: > 0 } version ? version : null,
        Review.Source.Ref,
        Review.Commit is { Length: > 0 } commit ? PackageScanService.ShortCommit(commit) : null,
    }.Where(p => !string.IsNullOrEmpty(p)).Distinct());
    public bool HasVersion => VersionText.Length > 0;

    public IReadOnlyList<ReviewCapabilityItem> Capabilities { get; }
    public bool HasCapabilities => Capabilities.Count > 0;
    public bool NothingFound => ShowSafety && Capabilities.Count == 0;
    public string NothingFoundText => L.Tr("review.safety.none", Scan.Files);
    public string ScanSummary => L.Tr("review.safety.summary", Scan.Scripts, Scan.Binaries) + (Scan.Incomplete ? " " + L.Tr("review.safety.incomplete") : "");

    public bool HasAdvisory => Advisory is not null;
    public bool AdvisoryBlocks => Advisory?.Blocks == true;
    public string AdvisoryHeading => AdvisoryBlocks ? L.Tr("review.advisory.pulled") : L.Tr("review.advisory.warning");
    public string AdvisoryTitle => Advisory?.Title ?? "";
    public string AdvisoryDetails => Advisory?.Details ?? "";
    public bool HasAdvisoryDetails => !string.IsNullOrWhiteSpace(Advisory?.Details);

    public IReadOnlyList<ReviewIssueItem> Issues { get; }
    public bool HasIssues => Issues.Count > 0;
    public bool HasIssueErrors => Issues.Any(i => i.IsError);

    public bool IsRisky => HasAdvisory || HasIssueErrors || ShowSafety && (Scan.HasHighRisk || Origin == InstallOrigin.Link);
    public bool CanInstall => !AdvisoryBlocks;
    public string InstallLabel => IsRisky ? L.Tr("review.button.installAnyway") : L.Tr("review.button.install");
    public bool InstallIsPrimary => !IsRisky;
    public bool CancelIsPrimary => IsRisky;
    public string Footer => ShowSafety ? L.Tr("review.footer") : "";
    public bool HasFooter => Footer.Length > 0;

    public void OpenFolder() => ExternalLink.OpenFolder(Review.PackageRoot);

    private static string ReasonFor(InstallOrigin origin, bool trusted, string? reviewedCommit, string? commit, bool hasIssues)
    {
        if (trusted) return hasIssues ? L.Tr("review.reason.compatibility") : "";
        return origin switch
        {
            InstallOrigin.Registry => L.Tr("review.reason.unreviewed", PackageScanService.ShortCommit(reviewedCommit), PackageScanService.ShortCommit(commit)),
            InstallOrigin.Unofficial => L.Tr("review.reason.unofficial"),
            InstallOrigin.Link => L.Tr("review.reason.link"),
            InstallOrigin.PackageList => L.Tr("review.reason.packageList"),
            _ => L.Tr("review.reason.gitUrl"),
        };
    }

    private static string? NameWarningFor(PackageReview review, string? claimedName)
    {
        var declared = review.Package?.Name;
        if (!string.IsNullOrWhiteSpace(declared) && !string.Equals(declared, review.PackageId, StringComparison.OrdinalIgnoreCase))
            return L.Tr("review.idMismatch", declared, review.PackageId);
        if (!string.IsNullOrWhiteSpace(claimedName)
            && !string.Equals(claimedName.Trim(), review.DisplayName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(claimedName.Trim(), review.PackageId, StringComparison.OrdinalIgnoreCase))
            return L.Tr("review.claimedName", claimedName.Trim(), review.DisplayName);
        return null;
    }
}
