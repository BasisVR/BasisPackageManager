using System.Collections.ObjectModel;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed record PulledPackageItem(string Id, string DisplayName, PackageAdvisory Advisory)
{
    public bool Blocks => Advisory.Blocks;
    public string Label => Blocks ? L.Tr("advisory.badge.pulled") : L.Tr("advisory.badge.warning");
    public string Title => Advisory.Title;
    public string Details => Advisory.Details ?? "";
    public bool HasDetails => !string.IsNullOrWhiteSpace(Advisory.Details);
}

public sealed partial class PackagesViewModel
{
    private sealed class ReviewDeclinedException() : InvalidOperationException("The package wasn't installed.");

    private AdvisoryService? _advisoryService;
    private IReadOnlyList<PackageAdvisory> _advisories = Array.Empty<PackageAdvisory>();
    private readonly Dictionary<string, PackageAdvisory> _installedAdvisories = new(StringComparer.OrdinalIgnoreCase);
    private RelayCommand<PulledPackageItem>? _removePulledCommand;

    public ObservableCollection<PulledPackageItem> PulledPackages { get; } = new();
    public bool HasPulledPackages => PulledPackages.Count > 0;
    public bool HasBlockingPulledPackages => PulledPackages.Any(p => p.Blocks);
    public string PulledTitle => PulledPackages.Count == 1 ? L.Tr(HasBlockingPulledPackages ? "advisory.banner.title" : "advisory.banner.warningTitle") : L.Tr("advisory.banner.titleMany", PulledPackages.Count);
    public string PulledBody => L.Tr(HasBlockingPulledPackages ? "advisory.banner.body" : "advisory.banner.warningBody");
    public RelayCommand<PulledPackageItem> RemovePulledCommand => _removePulledCommand ??= new RelayCommand<PulledPackageItem>(item =>
    {
        if (item is not null && _install is { HasUnityProject: true } target) _ = RemoveByNameAsync(target, item.Id, item.DisplayName);
    });

    public IReadOnlyList<PackageAdvisory> Advisories => _advisories;

    private readonly Dictionary<string, ApiUpgradeRecord> _apiUpgrades = new(StringComparer.OrdinalIgnoreCase);

    private void SetApiUpgrades(IReadOnlyDictionary<string, ApiUpgradeRecord?> upgrades)
    {
        _apiUpgrades.Clear();
        foreach (var (id, record) in upgrades)
            if (record is not null) _apiUpgrades[id] = record;
        foreach (var row in Available) row.ApiUpgrade = row.IsMounted ? _apiUpgrades.GetValueOrDefault(row.Name) : null;
    }

    public void UseAdvisories(AdvisoryService service) => _advisoryService = service;

    public void SetAdvisories(IReadOnlyList<PackageAdvisory> advisories)
    {
        _advisories = advisories;
        RecomputeInstalledAdvisories();
        ApplyRowAdvisories();
    }

    private async Task LoadAdvisoriesAsync(string catalogUrl)
    {
        if (_advisoryService is null) return;
        try
        {
            SetAdvisories(await _advisoryService.LoadAsync(catalogUrl));
            ReportPulledProjects();
        }
        catch (Exception ex) { DiagnosticLog.Write("Loading package advisories", ex); }
    }

    private IEnumerable<(string Id, string DisplayName, PackageAdvisory Advisory)> FindPulled(BasisInstall install)
    {
        if (_advisories.Count == 0 || !install.HasUnityProject) yield break;
        var embedded = _projectService.ListEmbeddedPackages(install.UnityProjectPath);
        foreach (var id in _advisories.Select(a => a.Package.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = install.Manifest.Dependencies.Keys.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase));
            var manifestValue = key is null ? null : install.Manifest.Dependencies[key];
            var mount = _mountService.FindMount(install.UnityProjectPath, id);
            var local = embedded.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (manifestValue is null && mount is null && local is null) continue;
            var (repository, commit) = AdvisoryService.InstalledSource(install.UnityProjectPath, id, mount?.OriginalManifestValue ?? manifestValue, mount?.FolderPath);
            if (AdvisoryService.Match(_advisories, id, repository, commit) is not { } advisory) continue;
            var name = key ?? mount?.PackageId ?? local?.Id ?? id;
            yield return (name, DisplayNameOf(name, local?.DisplayName), advisory);
        }
    }

    private void ReportPulledProjects()
    {
        foreach (var install in InstallOptions.ToList())
        {
            try
            {
                foreach (var (id, name, advisory) in FindPulled(install).Where(p => p.Advisory.Blocks))
                    _shell.SetStatus(L.Tr("advisory.status.found", name, install.DisplayName, advisory.Title), StatusKind.Error, install.DisplayName, key: "advisory|" + install.UnityProjectPath + "|" + id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Checking {install.DisplayName} for pulled packages", ex); }
        }
    }

    private void RecomputeInstalledAdvisories()
    {
        _installedAdvisories.Clear();
        PulledPackages.Clear();
        if (_install is { } install)
        {
            try
            {
                foreach (var (id, name, advisory) in FindPulled(install))
                {
                    _installedAdvisories[id] = advisory;
                    PulledPackages.Add(new PulledPackageItem(id, name, advisory));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Checking {install.DisplayName} for pulled packages", ex); }
        }
        OnPropertyChanged(nameof(HasPulledPackages));
        OnPropertyChanged(nameof(HasBlockingPulledPackages));
        OnPropertyChanged(nameof(PulledTitle));
        OnPropertyChanged(nameof(PulledBody));
    }

    private void ApplyRowAdvisories()
    {
        foreach (var row in Available) ApplyRowAdvisory(row);
    }

    private void ApplyRowAdvisory(PackageRow row) =>
        row.Advisory = _installedAdvisories.GetValueOrDefault(row.Name)
            ?? (row.IsInstalled || row.IsMounted ? null : AdvisoryService.Match(_advisories, row.Name, row.Entry.Url, null, unknownCommitMatches: false));

    private string DisplayNameOf(string id, string? fallback = null) =>
        CatalogEntryFor(id)?.DisplayName is { Length: > 0 } name ? name : fallback is { Length: > 0 } ? fallback : id;

    public CatalogPackageVersion? CatalogEntryFor(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _catalogService.AllLatest(_catalog).FirstOrDefault(v => string.Equals(v.Name, id.Trim(), StringComparison.OrdinalIgnoreCase));

    public InstallOrigin OriginOf(CatalogPackageVersion entry) => _unofficialIds.Contains(entry.Name) ? InstallOrigin.Unofficial : InstallOrigin.Registry;

    public string DeepLinkLabel(string? id, string? name) =>
        CatalogEntryFor(id) is { } entry ? entry.DisplayName : L.Tr("shell.deeplink.unlisted", string.IsNullOrWhiteSpace(name) ? id ?? "" : name);

    private bool RefuseAdvised(BasisInstall target, string id, string? url, string displayName)
    {
        if (AdvisoryService.Match(_advisories, id, url, null, unknownCommitMatches: false) is not { Blocks: true } advisory) return false;
        SetStatus(L.Tr("advisory.status.blocked", displayName, advisory.Title), StatusKind.Error, target.DisplayName, key: QueueKey(target, id));
        return true;
    }

    public Func<PackageReview, Task<bool>> ReviewGate(BasisInstall target, InstallOrigin origin, CatalogPackageVersion? entry = null, string? claimedName = null)
    {
        var reviewed = origin == InstallOrigin.Registry ? PackageTrust.ReviewedCommits(_catalog, entry?.Name, entry) : Array.Empty<string>();
        return review => ReviewAsync(target, origin, reviewed, entry, claimedName, review);
    }

    private async Task<bool> ReviewAsync(BasisInstall target, InstallOrigin origin, IReadOnlyList<string> reviewed, CatalogPackageVersion? entry, string? claimedName, PackageReview review)
    {
        var advisory = AdvisoryService.Match(_advisories, review.PackageId, review.Source.CloneUrl, review.Commit);
        var trusted = PackageTrust.IsTrusted(origin == InstallOrigin.Registry, reviewed, review.Commit);
        var pinned = reviewed.FirstOrDefault(c => string.Equals(c, entry?.ReviewedCommit?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? reviewed.FirstOrDefault();
        var issues = await CheckCompatibilityAsync(target, review);
        if (trusted && advisory is null && !issues.Any(i => i.Severity >= IssueSeverity.Warning)) return true;
        var scan = trusted && advisory is null ? PackageScan.Empty : await PackageScanService.ScanAsync(review.PackageRoot);
        Task<bool> Show() => Dialogs.ReviewPackageAsync(new PackageReviewViewModel(review, origin, trusted, pinned, scan, advisory, issues, claimedName));
        return Dispatcher.UIThread.CheckAccess() ? await Show() : await Dispatcher.UIThread.InvokeAsync(Show);
    }

    private Task<IReadOnlyList<PackageIssue>> CheckCompatibilityAsync(BasisInstall target, PackageReview review) =>
        AnalyzeCandidateAsync(target, review.PackageRoot, review.PackageId);

    private async Task<bool> ConfirmUncheckedAsync(string label)
    {
        Task<bool> Ask() => Dialogs.ConfirmAsync(L.Tr("review.unchecked.title"), L.Tr("review.unchecked.body", label));
        return Dispatcher.UIThread.CheckAccess() ? await Ask() : await Dispatcher.UIThread.InvokeAsync(Ask);
    }

    private Task InstallFromUrlAsync(BasisInstall target, string id, string label, string gitUrl, InstallOrigin origin, CatalogPackageVersion? entry = null, string? claimedName = null) =>
        PackageWorkAsync(target, id, L.Tr("packages.status.installingNow", label), async () =>
        {
            if (await InstallFromUrlNowAsync(target, id, label, gitUrl, origin, entry, claimedName))
                SetStatus(L.Tr("packages.status.installedCloned", label, target.DisplayName), StatusKind.Success, target.DisplayName, key: QueueKey(target, id));
        }, alwaysQueue: true);

    private async Task<bool> InstallFromUrlNowAsync(BasisInstall target, string id, string label, string gitUrl, InstallOrigin origin, CatalogPackageVersion? entry = null, string? claimedName = null)
    {
        var key = QueueKey(target, id);
        Task Retry() => InstallFromUrlAsync(target, id, label, gitUrl, origin, entry, claimedName);
        try
        {
            if (!BasisDevStore.IsValidId(id) || !GitUrlPolicy.IsSafeDependencyUrl(gitUrl))
            {
                SetStatus(L.Tr("packages.status.refusedUnsafeUrl", label), StatusKind.Error, target.DisplayName, key: key);
                return false;
            }
            if (IsIncludedIn(target, id))
            {
                SetStatus(L.Tr("packages.status.embeddedConflict", label), StatusKind.Error, target.DisplayName, key: key);
                return false;
            }
            if (RefuseAdvised(target, id, gitUrl, label)) return false;
            if (!await ConfirmDiscardEditsAsync(target, id, label)) return false;
            await ReloadManifestAsync(target);
            if (origin != InstallOrigin.Registry && target.Manifest.Dependencies.ContainsKey(id) && WorkingCloneMount(target, id) is null
                && !await Dialogs.ConfirmAsync(L.Tr("packages.dialog.replaceTitle", label), L.Tr("packages.dialog.replaceBody", id, target.DisplayName)))
                return false;
            IsBusy = true;
            await CloneInstallAsync(target, id, gitUrl.Trim(), review: ReviewGate(target, origin, entry, claimedName), reviewRequired: origin != InstallOrigin.Registry);
            RefreshInstalled();
            return true;
        }
        catch (ReviewDeclinedException)
        {
            SetStatus(L.Tr("packages.status.reviewDeclined", label), StatusKind.Info, target.DisplayName, key: key);
            RefreshInstalled();
            return false;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Installing {id} from {gitUrl}", ex);
            SetStatus(L.Tr("packages.status.installError", ex.Message), StatusKind.Error, target.DisplayName, ex is NotAPackageException ? null : Retry, key);
            return false;
        }
        finally { IsBusy = false; }
    }

    private bool IsIncludedIn(BasisInstall target, string id) =>
        ReferenceEquals(target, _install) && IsIncluded(id) || _projectService.ListEmbeddedPackages(target.UnityProjectPath).Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed partial class PackageRow
{
    private PackageAdvisory? _advisory;

    public PackageAdvisory? Advisory
    {
        get => _advisory;
        set
        {
            if (!SetField(ref _advisory, value)) return;
            OnPropertyChanged(nameof(HasAdvisory));
            OnPropertyChanged(nameof(IsPulled));
            OnPropertyChanged(nameof(IsAdvisoryWarning));
            OnPropertyChanged(nameof(AdvisoryLabel));
            OnPropertyChanged(nameof(AdvisoryText));
        }
    }

    private ApiUpgradeRecord? _apiUpgrade;

    public ApiUpgradeRecord? ApiUpgrade
    {
        get => _apiUpgrade;
        set
        {
            if (!SetField(ref _apiUpgrade, value)) return;
            OnPropertyChanged(nameof(HasApiUpgrade));
            OnPropertyChanged(nameof(ApiUpgradeText));
            OnPropertyChanged(nameof(ApiUpgradeDetails));
        }
    }

    public bool HasApiUpgrade => _apiUpgrade is { Changes.Count: > 0 };
    public string ApiUpgradeText => _apiUpgrade is null ? "" : string.IsNullOrWhiteSpace(_apiUpgrade.Unity)
        ? L.Tr("packages.apiUpgrade.applied")
        : L.Tr("packages.apiUpgrade.appliedFor", _apiUpgrade.Unity);
    public string ApiUpgradeDetails => _apiUpgrade is null ? "" : string.Join(Environment.NewLine, _apiUpgrade.Changes.Select(c => L.Tr("packages.apiUpgrade.change", c.From, c.To, $"{c.File}:{c.Line}")));

    public bool HasAdvisory => _advisory is not null;
    public bool IsPulled => _advisory?.Blocks == true;
    public bool IsAdvisoryWarning => _advisory is { Blocks: false };
    public string AdvisoryLabel => IsPulled ? L.Tr("advisory.badge.pulled") : L.Tr("advisory.badge.warning");
    public string AdvisoryText => _advisory is null ? "" : string.IsNullOrWhiteSpace(_advisory.Details) ? _advisory.Title : _advisory.Title + " " + _advisory.Details.Trim();
}
