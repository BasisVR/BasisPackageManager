using System.Globalization;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed partial class PackagesViewModel
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);

    private PackageUpdateService? _updates;
    private ProjectTargetService? _targets;
    private PackageShareService? _shares;
    private readonly Dictionary<string, PackageUpdateInfo> _updateInfos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<PackageDependency>> _dependencyGaps = new(StringComparer.OrdinalIgnoreCase);
    private ProjectTarget _projectTarget = ProjectTarget.Unknown;
    private int _updateScan;
    private (string? Token, DateTime At) _checkToken;
    private RelayCommand<PackageRow>? _updatePackageCommand;
    private RelayCommand<PackageRow>? _installMissingCommand;
    private RelayCommand<PackageRow>? _shareCommand;
    private RelayCommand? _restoreAllCommand;

    internal PackageUpdateService Updates => _updates ??= new PackageUpdateService(_gitService, _mountService, _mountRegistry, _versionService, _ghApi, _githubService);
    private ProjectTargetService Targets => _targets ??= new ProjectTargetService(_gitService, _basisUpdates, _ghApi);
    private PackageShareService Shares => _shares ??= new PackageShareService(_gitService, _ghApi, _mountService, _mountRegistry);

    public int PackageUpdateCount => _updateInfos.Values.Count(i => i.Available && !IsIncluded(i.PackageId));
    public bool HasPackageUpdates => PackageUpdateCount > 0;
    public string PackageUpdateCountText => PackageUpdateCount.ToString(CultureInfo.InvariantCulture);
    public string UpdateAllLabel => L.Tr("packages.button.updateAllCount", PackageUpdateCount);

    public RelayCommand<PackageRow> UpdatePackageCommand => _updatePackageCommand ??= new RelayCommand<PackageRow>(UpdatePackageAsync);
    public RelayCommand<PackageRow> InstallMissingCommand => _installMissingCommand ??= new RelayCommand<PackageRow>(InstallMissingAsync);
    public RelayCommand<PackageRow> ShareCommand => _shareCommand ??= new RelayCommand<PackageRow>(ShareAsync);
    public RelayCommand RestoreAllCommand => _restoreAllCommand ??= new RelayCommand(RestoreAllAsync);

    public int RestorableCount => _basisDevReport.Packages.Count(p =>
        (p.Issues.Contains(BasisDevIssueKind.MissingClone) && p.Actions.Contains(BasisDevAction.Reclone)) || p.Actions.Contains(BasisDevAction.Switch));
    public bool CanRestoreAll => RestorableCount > 0;
    public string RestoreAllLabel => L.Tr("basisdev.button.restoreAll", RestorableCount);

    private void ForgetUpdateChecks()
    {
        _updates?.ClearCache();
        _targets?.Invalidate();
        _checkToken = default;
    }

    private async Task<string?> CheckTokenAsync()
    {
        if (DateTime.UtcNow - _checkToken.At < TokenLifetime) return _checkToken.Token;
        string? token = null;
        try { token = await _ghAuth.GetTokenAsync(); }
        catch (Exception ex) { DiagnosticLog.Write("Reading the GitHub token for update checks", ex); }
        _checkToken = (token, DateTime.UtcNow);
        return token;
    }

    private IEnumerable<string> UpdateCandidates(BasisInstall install)
    {
        foreach (var id in _mounts.Keys)
            if (!IsIncluded(id)) yield return id;
        foreach (var (id, value) in install.Manifest.Dependencies)
            if (!_mounts.ContainsKey(id) && UpmGitUrl.Parse(value) is not null && !IsIncluded(id)) yield return id;
    }

    private async Task CheckUpdatesAsync()
    {
        if (_install is not { HasUnityProject: true } install || !_gitService.IsAvailable)
        {
            _updateInfos.Clear();
            _dependencyGaps.Clear();
            ApplyUpdateState();
            return;
        }
        var scan = ++_updateScan;
        var ids = UpdateCandidates(install).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var catalog = _catalog;
        try
        {
            var token = await CheckTokenAsync();
            var target = await Targets.ResolveAsync(install, token);
            var infos = ids.Count == 0 ? new Dictionary<string, PackageUpdateInfo>() : await Updates.CheckAllAsync(install, ids, target, catalog, token);
            var gaps = await Task.Run(() => FindDependencyGaps(install, ids, catalog));
            if (scan != _updateScan || !ReferenceEquals(install, _install)) return;
            _projectTarget = target;
            _updateInfos.Clear();
            foreach (var (id, info) in infos) _updateInfos[id] = info;
            _dependencyGaps.Clear();
            foreach (var (id, gap) in gaps) _dependencyGaps[id] = gap;
            ApplyUpdateState();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { DiagnosticLog.Write("Checking the project's packages for updates", ex); }
    }

    private Dictionary<string, IReadOnlyList<PackageDependency>> FindDependencyGaps(BasisInstall install, IReadOnlyList<string> ids, Catalog catalog)
    {
        var gaps = new Dictionary<string, IReadOnlyList<PackageDependency>>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return gaps;
        var present = PackageDependencies.PresentIds(install.UnityProjectPath);
        var scopes = PackageDependencies.RegistryScopes(install.Manifest);
        var roots = UnityPackageMap.List(install.UnityProjectPath, includeAssets: false).ToDictionary(p => p.Id, p => p.Root, StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (!roots.TryGetValue(id, out var root) || PackageJsonFile.Read(root) is not { } package) continue;
            var missing = PackageDependencies.Analyze(package, present, scopes, catalog).Where(d => d.State is not (DependencyState.Present or DependencyState.UnityResolves)).ToList();
            if (missing.Count > 0) gaps[id] = missing;
        }
        return gaps;
    }

    private void ApplyUpdateState()
    {
        foreach (var row in Available)
        {
            row.SetUpdate(_updateInfos.GetValueOrDefault(row.Name));
            row.SetDependencyGaps(_dependencyGaps.GetValueOrDefault(row.Name));
            row.SetCompatibility(row.IsAvailableToInstall && _projectTarget.IsKnown ? PackageCompatibility.Check(row.Entry, _projectTarget) : null);
        }
        OnPropertyChanged(nameof(PackageUpdateCount));
        OnPropertyChanged(nameof(HasPackageUpdates));
        OnPropertyChanged(nameof(PackageUpdateCountText));
        OnPropertyChanged(nameof(UpdateAllLabel));
        OnPropertyChanged(nameof(CanUpdateAll));
        OnPropertyChanged(nameof(CanRestoreAll));
        OnPropertyChanged(nameof(RestorableCount));
        OnPropertyChanged(nameof(RestoreAllLabel));
    }

    private void UpdateAllAvailable()
    {
        if (_install is not { HasUnityProject: true } target) return;
        var infos = _updateInfos.Values.Where(i => i.Available && !IsIncluded(i.PackageId)).ToList();
        if (infos.Count == 0) return;
        foreach (var info in infos) _ = UpdateToAsync(target, EntryFor(info.PackageId), info, askOnConflict: false);
        SetStatus(L.Tr("packages.status.updateAllQueued", infos.Count), StatusKind.Info);
    }

    private CatalogPackageVersion EntryFor(string id) =>
        Available.FirstOrDefault(r => string.Equals(r.Name, id, StringComparison.OrdinalIgnoreCase))?.Entry
        ?? CatalogEntryFor(id)
        ?? new CatalogPackageVersion { Name = id, DisplayName = id };

    private async Task UpdatePackageAsync(PackageRow? row)
    {
        if (row?.UpdateInfo is not { Available: true } info || _install is not { HasUnityProject: true } target) return;
        var hasLocalWork = row.MountFolder is { } folder && await _mountService.HasLocalWorkAsync(folder);
        var model = new PackageUpdateViewModel(row.DisplayName, info, hasLocalWork, async ct => await Updates.GetChangesAsync(target, row.Name, info.TargetRef, info.TargetCommit, await CheckTokenAsync(), ct));
        if (!await Dialogs.ReviewPackageUpdateAsync(model)) return;
        await UpdateToAsync(target, row.Entry, info, askOnConflict: true);
    }

    private Task UpdateToAsync(BasisInstall target, CatalogPackageVersion entry, PackageUpdateInfo info, bool askOnConflict) =>
        PackageWorkAsync(target, entry.Name, L.Tr("packages.status.updating", entry.DisplayName), async () =>
        {
            var key = QueueKey(target, entry.Name);
            Task Retry() => UpdateToAsync(target, entry, info, askOnConflict);
            if (RefuseAdvised(target, entry.Name, entry.Url, entry.DisplayName)) return;
            IsBusy = true;
            try
            {
                if (WorkingCloneMount(target, entry.Name) is null)
                {
                    Enqueue(entry, target);
                    return;
                }
                var request = new PackageUpdateRequest(info.TargetRef, Commit: info.Kind == PackageUpdateKind.Commits ? info.TargetCommit : null);
                var result = await Updates.UpdateCloneAsync(target, entry.Name, request, UpdateReview(target, entry), Progress(target));
                if (result.Outcome == PackageUpdateOutcome.Conflict && askOnConflict
                    && await Dialogs.ConfirmAsync(L.Tr("packages.dialog.updateConflictTitle"), L.Tr("packages.dialog.updateConflictBody", entry.DisplayName, LabelOf(info), string.Join(", ", result.ConflictedFiles.Take(5)))))
                {
                    var url = UpmGitUrl.Parse(info.Url)?.ToManifestUrl(info.TargetRef, info.SubPath) ?? info.Url;
                    var (_, installed) = await ReplaceCloneAsync(target, entry.Name, url);
                    await FinishUpdateAsync(target, entry, installed, L.Tr("packages.status.updatedFresh", entry.DisplayName, LabelOf(info)), Retry);
                    return;
                }
                switch (result.Outcome)
                {
                    case PackageUpdateOutcome.Updated or PackageUpdateOutcome.UpToDate:
                        await FinishUpdateAsync(target, entry, result.ManifestValue ?? info.Url,
                            result.KeptLocalChanges ? L.Tr("packages.status.updatedKept", entry.DisplayName, LabelOf(info)) : L.Tr("packages.status.updatedTo", entry.DisplayName, LabelOf(info)), Retry);
                        break;
                    case PackageUpdateOutcome.Declined:
                        SetStatus(L.Tr("packages.status.reviewDeclined", entry.DisplayName), StatusKind.Info, target.DisplayName, key: key);
                        break;
                    case PackageUpdateOutcome.Conflict:
                        SetStatus(L.Tr("packages.status.updateConflict", entry.DisplayName, result.ConflictedFiles.Count), StatusKind.Error, target.DisplayName, key: key);
                        break;
                    default:
                        SetStatus(L.Tr("packages.status.updateFailed", entry.DisplayName, result.Message), StatusKind.Error, target.DisplayName, result.Outcome == PackageUpdateOutcome.NotAPackage ? null : Retry, key);
                        break;
                }
                RefreshInstalled();
            }
            catch (ReviewDeclinedException)
            {
                SetStatus(L.Tr("packages.status.reviewDeclined", entry.DisplayName), StatusKind.Info, target.DisplayName, key: key);
                RefreshInstalled();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"Updating {entry.Name}", ex);
                SetStatus(L.Tr("packages.status.updateFailed", entry.DisplayName, ex.Message), StatusKind.Error, target.DisplayName, Retry, key);
            }
            finally { IsBusy = false; }
        });

    private async Task FinishUpdateAsync(BasisInstall target, CatalogPackageVersion entry, string installedUrl, string message, Func<Task> retry)
    {
        _mountEditedIds.Remove(entry.Name);
        _mountEditSummaries.Remove(entry.Name);
        ReportWithServerSide(message, entry, target, await SyncServerSideAsync(target, entry, installedUrl), retry);
        await ReloadManifestAsync(target);
        NoteInstalled(target, entry);
        StartVerifyingInstalls();
        RefreshInstalled();
        _ = OfferDependenciesAsync(target, entry.Name);
    }

    private static string LabelOf(PackageUpdateInfo info) =>
        info.TargetRef is { Length: > 0 } target && info.Kind == PackageUpdateKind.Release ? target
        : info.TargetRef is { Length: > 0 } branch ? $"{branch} @ {PackageUpdateInfo.ShortCommit(info.TargetCommit)}"
        : PackageUpdateInfo.ShortCommit(info.TargetCommit);

    private Action<string> Progress(BasisInstall target) => line => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetStatus(line, StatusKind.Info, target.DisplayName));

    private Func<PackageUpdateTarget, CancellationToken, Task<bool>> UpdateReview(BasisInstall target, CatalogPackageVersion entry)
    {
        var listed = CatalogEntryFor(entry.Name);
        return Updates.ReviewBeforeUpdate(ReviewGate(target, listed is null ? InstallOrigin.GitUrl : OriginOf(listed), listed ?? entry));
    }

    private async Task<(bool Handled, string Url)> TryUpdateInPlaceAsync(BasisInstall target, string packageId, string gitUrl, bool orDefaultBranch, Func<PackageReview, Task<bool>>? review)
    {
        if (WorkingCloneMount(target, packageId) is null || UpmGitUrl.Parse(gitUrl) is not { } parsed) return (false, gitUrl);
        var approve = review is null ? null : Updates.ReviewBeforeUpdate(review);
        var request = new PackageUpdateRequest(parsed.Ref, parsed.CloneUrl);
        var result = await Updates.UpdateCloneAsync(target, packageId, request, approve, Progress(target));
        if (result.Outcome == PackageUpdateOutcome.NotAPackage && orDefaultBranch && parsed.Ref is not null)
            result = await Updates.UpdateCloneAsync(target, packageId, request with { Ref = null }, approve, Progress(target));
        switch (result.Outcome)
        {
            case PackageUpdateOutcome.Updated or PackageUpdateOutcome.UpToDate:
                _mountEditedIds.Remove(packageId);
                _mountEditSummaries.Remove(packageId);
                return (true, result.ManifestValue ?? gitUrl);
            case PackageUpdateOutcome.NotAClone:
                return (false, gitUrl);
            case PackageUpdateOutcome.Declined:
                throw new ReviewDeclinedException();
            case PackageUpdateOutcome.NotAPackage:
                throw new NotAPackageException(result.Message);
            case PackageUpdateOutcome.Conflict:
                if (await Dialogs.ConfirmAsync(L.Tr("packages.dialog.updateConflictTitle"), L.Tr("packages.dialog.updateConflictBody", packageId, parsed.Ref ?? L.Tr("packages.status.defaultBranch"), string.Join(", ", result.ConflictedFiles.Take(5)))))
                    return (false, gitUrl);
                throw new InvalidOperationException(result.Message);
            default:
                throw new InvalidOperationException(result.Message);
        }
    }

    private async Task<(bool Mounted, string Url)> ReplaceCloneAsync(BasisInstall target, string packageId, string gitUrl)
    {
        if (WorkingCloneMount(target, packageId) is not null)
        {
            var swap = await _mountService.UnmountAsync(target, packageId, restoreManifest: true, restoreLine: gitUrl);
            if (!swap.Ok) throw new InvalidOperationException(swap.Error ?? L.Tr("develop.status.swapBackFailed"));
        }
        var entry = CatalogEntryFor(packageId);
        var origin = entry is null ? InstallOrigin.GitUrl : OriginOf(entry);
        var result = await _mountService.MountAsync(target, packageId, gitUrl, line => Avalonia.Threading.Dispatcher.UIThread.Post(() => ReportCloneProgress(line)),
            review: ReviewGate(target, origin, entry ?? new CatalogPackageVersion { Name = packageId, DisplayName = packageId, Url = gitUrl }));
        if (result.Declined) throw new ReviewDeclinedException();
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? L.Tr("develop.status.mountFailed"));
        return (true, result.ManifestValue ?? gitUrl);
    }

    private async Task<string?> ResolveInstallUrlAsync(BasisInstall target, CatalogPackageVersion entry, string gitUrl)
    {
        var loc = UpmGitUrl.Parse(gitUrl);
        if (loc is null || loc.Ref is not null) return null;
        var token = await CheckTokenAsync();
        var versions = await _versionService.GetVersionsAsync(gitUrl, token);
        var stable = versions.Options.Where(o => o.Kind != VersionKind.Branch && !o.IsPrerelease && SemVer.TryParseTag(o.Ref, out _)).ToList();
        if (stable.Count == 0) return versions.LatestStable?.Ref is { } tag ? loc.ToManifestUrl(tag, loc.Path) : null;
        var projectTarget = await Targets.ResolveAsync(target, token);
        var source = new InstalledPackageSource(entry.Name, null, loc.CloneUrl, null, loc.Path, null, null);
        CompatibilityResult? newestProblem = null;
        foreach (var option in stable.Take(6))
        {
            var problem = await Updates.CompatibilityOfAsync(source, option.Ref!, projectTarget, _catalog);
            if (problem.Ok)
            {
                if (newestProblem is not null)
                    SetStatus(L.Tr("packages.status.installedOlderCompatible", entry.DisplayName, option.Ref!, stable[0].Ref!, DescribeProblem(newestProblem)), StatusKind.Info, target.DisplayName);
                return loc.ToManifestUrl(option.Ref, loc.Path);
            }
            newestProblem ??= problem;
        }
        if (newestProblem is not null && !await Dialogs.ConfirmAsync(L.Tr("packages.dialog.incompatibleTitle"), L.Tr("packages.dialog.incompatibleBody", entry.DisplayName, stable[0].Ref!, DescribeProblem(newestProblem))))
            throw new ReviewDeclinedException();
        return loc.ToManifestUrl(stable[0].Ref, loc.Path);
    }

    internal static string DescribeProblem(CompatibilityResult problem) => problem.Problem switch
    {
        CompatibilityProblem.NeedsNewerUnity => L.Tr("packages.compat.needsUnity", problem.Required ?? ""),
        CompatibilityProblem.NeedsNewerBasis => L.Tr("packages.compat.needsNewerBasis", problem.Required ?? ""),
        CompatibilityProblem.NeedsOlderBasis => L.Tr("packages.compat.needsOlderBasis", problem.Required ?? ""),
        CompatibilityProblem.NeedsOtherBasisChannel => L.Tr("packages.compat.needsChannel", problem.Required ?? ""),
        _ => "",
    };

    private async Task OfferDependenciesAsync(BasisInstall target, string packageId)
    {
        try
        {
            var root = MountedPackageRoot(target, packageId)
                ?? UnityPackageMap.List(target.UnityProjectPath, includeAssets: false).FirstOrDefault(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase))?.Root;
            if (root is null) return;
            var catalog = _catalog;
            var gaps = await Task.Run(() => PackageDependencies.AnalyzeInstalled(target.UnityProjectPath, root, target.Manifest, catalog));
            var installable = gaps.Where(d => d.CanInstall && !IsIncluded(d.Id)).ToList();
            var unknown = gaps.Where(d => d.State == DependencyState.Missing).Select(d => d.Id).ToList();
            if (unknown.Count > 0)
                SetStatus(L.Tr("packages.status.dependencyUnknown", packageId, string.Join(", ", unknown)), StatusKind.Error, target.DisplayName);
            if (installable.Count == 0) return;
            var list = string.Join("\n", installable.Select(d => d.State == DependencyState.InCatalog ? L.Tr("packages.dependencies.fromRegistry", d.Id) : L.Tr("packages.dependencies.fromGit", d.Id, d.GitUrl ?? "")));
            if (!await Dialogs.ConfirmAsync(L.Tr("packages.dialog.dependenciesTitle"), L.Tr("packages.dialog.dependenciesBody", packageId, list))) return;
            await InstallDependenciesAsync(target, installable);
        }
        catch (Exception ex) { DiagnosticLog.Write($"Looking for the dependencies of {packageId}", ex); }
    }

    private async Task InstallDependenciesAsync(BasisInstall target, IReadOnlyList<PackageDependency> dependencies)
    {
        foreach (var dependency in dependencies)
        {
            if (dependency.State == DependencyState.InCatalog && dependency.CatalogEntry is { } entry)
            {
                Enqueue(CatalogEntryFor(entry.Name) ?? entry, target);
                continue;
            }
            if (dependency.State != DependencyState.FromGit || dependency.GitUrl is not { } url) continue;
            var listed = CatalogEntryFor(dependency.Id) is { Url.Length: > 0 } candidate && AdvisoryService.SameRepository(candidate.Url, url) ? candidate : null;
            await PackageWorkAsync(target, dependency.Id, L.Tr("packages.status.installingNow", dependency.Id), async () =>
            {
                if (await InstallFromUrlNowAsync(target, dependency.Id, dependency.Id, url, listed is null ? InstallOrigin.GitUrl : OriginOf(listed), listed))
                {
                    SetStatus(L.Tr("packages.status.installedCloned", dependency.Id, target.DisplayName), StatusKind.Success, target.DisplayName);
                    await ReloadManifestAsync(target);
                    NoteInstalled(target, listed ?? new CatalogPackageVersion { Name = dependency.Id, DisplayName = dependency.Id });
                    StartVerifyingInstalls();
                }
                RefreshInstalled();
            });
        }
    }

    private async Task InstallMissingAsync(PackageRow? row)
    {
        if (row is null || _install is not { HasUnityProject: true } target) return;
        var gaps = _dependencyGaps.GetValueOrDefault(row.Name)?.Where(d => d.CanInstall && !IsIncluded(d.Id)).ToList();
        if (gaps is not { Count: > 0 }) return;
        await InstallDependenciesAsync(target, gaps);
    }

    private async Task<bool> ConfirmDependentsAsync(BasisInstall target, string packageId, string displayName)
    {
        var catalog = _catalog;
        IReadOnlyList<PackageDependent> dependents;
        try { dependents = await Task.Run(() => PackageDependencies.FindDependents(target.UnityProjectPath, packageId, catalog)); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Looking for packages that need {packageId}", ex);
            return true;
        }
        if (dependents.Count == 0) return true;
        return await Dialogs.ConfirmAsync(L.Tr("packages.dialog.dependentsTitle", displayName),
            L.Tr("packages.dialog.dependentsBody", displayName, string.Join("\n", dependents.Select(d => d.DisplayName == d.Id ? d.Id : $"{d.DisplayName} ({d.Id})"))));
    }

    private async Task ShareAsync(PackageRow? row)
    {
        if (row is null || _install is not { HasUnityProject: true } target || row.MountFolder is not { } folder) return;
        var (original, canShare, reason) = await Shares.DescribeAsync(target, row.Name);
        if (!canShare || original is null)
        {
            SetStatus(L.Tr("packages.status.shareUnavailable", row.DisplayName, reason ?? ""), StatusKind.Info, target.DisplayName);
            return;
        }
        var status = await _gitService.GetStatusAsync(folder);
        var host = new SubmitPrHost(_gitService, _ghAuth, _ghApi, folder);
        var model = new SharePackageViewModel(host, row.DisplayName, original.Slug, status.ChangeCount > 0, PackageShareService.SuggestBranch(target));
        if (await Dialogs.SharePackageAsync(model) is not { } draft) return;
        await PackageWorkAsync(target, row.Name, L.Tr("packages.status.sharing", row.DisplayName), async () =>
        {
            var key = QueueKey(target, row.Name);
            try
            {
                var result = await Shares.ShareAsync(target, row.Name, draft.Token, draft.User, draft.Message, draft.Branch, Progress(target));
                if (result.Ok) SetStatus(L.Tr("packages.status.shared", row.DisplayName, result.Branch ?? "", result.Url ?? ""), StatusKind.Success, target.DisplayName, key: key);
                else SetStatus(L.Tr("packages.status.shareFailed", row.DisplayName, result.Message), StatusKind.Error, target.DisplayName, () => ShareAsync(row), key);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"Sharing {row.Name}", ex);
                SetStatus(L.Tr("packages.status.shareFailed", row.DisplayName, ex.Message), StatusKind.Error, target.DisplayName, () => ShareAsync(row), key);
            }
            RefreshInstalled();
        });
    }

    private async Task RestoreAllAsync()
    {
        if (_install is not { HasUnityProject: true } install) return;
        await ProjectWorkAsync(install, L.Tr("basisdev.status.restoringAll"), async () =>
        {
            var key = "restore|" + install.UnityProjectPath;
            IsBusy = true;
            try
            {
                BasisDev.ApproveUpdate = ListedReview(install);
                var result = await BasisDev.RestoreAllAsync(install, switchListed: true, Progress(install));
                if (result.Ok) SetStatus(L.Tr("basisdev.status.restoredAll", result.Message), StatusKind.Success, install.DisplayName, key: key);
                else SetStatus(L.Tr("basisdev.status.restoreAllFailed", result.Message), StatusKind.Error, install.DisplayName, RestoreAllAsync, key);
                await ReloadInstalledAsync();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("Restoring the listed packages", ex);
                SetStatus(L.Tr("basisdev.status.restoreAllFailed", ex.Message), StatusKind.Error, install.DisplayName, RestoreAllAsync, key);
            }
            finally
            {
                BasisDev.ApproveUpdate = null;
                IsBusy = false;
            }
        });
    }

    public async Task<ProjectSyncResult> SyncProjectAsync(BasisInstall install, Action<string>? onProgress = null)
    {
        BasisDev.ApproveUpdate = ListedReview(install);
        try { return await new ProjectSyncService(_gitService, BasisDev, _projectService).SyncAsync(install, onProgress); }
        finally
        {
            BasisDev.ApproveUpdate = null;
            if (_install is not null && Platform.PathsEqual(_install.UnityProjectPath, install.UnityProjectPath))
            {
                ForgetUpdateChecks();
                await ReloadInstalledAsync();
            }
        }
    }

    internal Func<PackageUpdateTarget, CancellationToken, Task<bool>> ListedReview(BasisInstall install) => async (target, ct) =>
    {
        var listed = CatalogEntryFor(target.PackageId) is { Url.Length: > 0 } candidate && AdvisoryService.SameRepository(candidate.Url, target.Source.CloneUrl) ? candidate : null;
        var review = ReviewGate(install, listed is null ? InstallOrigin.PackageList : OriginOf(listed), listed ?? new CatalogPackageVersion { Name = target.PackageId, DisplayName = target.PackageId, Url = target.Source.CloneUrl });
        return await Updates.ReviewBeforeUpdate(review)(target, ct);
    };
}

public sealed partial class PackageRow
{
    private PackageUpdateInfo? _update;
    private IReadOnlyList<PackageDependency> _dependencyGaps = Array.Empty<PackageDependency>();

    public PackageUpdateInfo? UpdateInfo => _update;
    public bool UpdateAvailable => _update is { Available: true } && !IsReadOnly && (IsInstalled || IsMounted);
    public string UpdateTargetLabel => _update is null ? "" : _update.Kind == PackageUpdateKind.Release ? _update.TargetRef ?? "" : PackageUpdateInfo.ShortCommit(_update.TargetCommit);
    public string UpdateBadge => _update?.Kind == PackageUpdateKind.Commits ? L.Tr("packages.update.badgeCommits") : L.Tr("packages.update.badge", UpdateTargetLabel);
    public string UpdateTooltip => _update is null ? ""
        : _update.Kind == PackageUpdateKind.Commits ? L.Tr("packages.tooltip.updateCommits", _update.TargetRef ?? L.Tr("packages.version.defaultBranch"))
        : L.Tr("packages.tooltip.updateRelease", _update.InstalledRef ?? "", UpdateTargetLabel);
    public string UpdateButtonLabel => L.Tr("packages.button.updateTo", UpdateTargetLabel);
    public bool HasCompatibilityNote => _update is { SkippedRef: not null, SkippedProblem: not null } && !IsReadOnly;
    public string CompatibilityNote => _update?.SkippedProblem is { } problem ? L.Tr("packages.update.skipped", _update.SkippedRef ?? "", PackagesViewModel.DescribeProblem(problem)) : "";
    public bool HasDependencyGaps => _dependencyGaps.Count > 0 && !IsReadOnly;
    public bool CanInstallMissing => _dependencyGaps.Any(d => d.CanInstall) && !IsReadOnly;
    public string DependencyGapLabel => L.Tr("packages.dependencies.missingBadge", _dependencyGaps.Count);
    public string DependencyGapText => L.Tr("packages.dependencies.missing", string.Join(", ", _dependencyGaps.Select(d => d.Id)));
    public bool CanShare => IsMounted && HasMountFolder && !IsReadOnly;
    private CompatibilityResult? _compatibility;
    public bool HasInstallWarning => _compatibility is { Ok: false } && IsAvailableToInstall;
    public string InstallWarningTooltip => _compatibility is { Ok: false } problem ? L.Tr("packages.compat.tooltip", PackagesViewModel.DescribeProblem(problem)) : "";

    public void SetCompatibility(CompatibilityResult? result)
    {
        if (Equals(_compatibility, result)) return;
        _compatibility = result;
        OnPropertyChanged(nameof(HasInstallWarning));
        OnPropertyChanged(nameof(InstallWarningTooltip));
    }

    public void SetUpdate(PackageUpdateInfo? info)
    {
        if (ReferenceEquals(_update, info)) return;
        _update = info;
        OnPropertyChanged(nameof(UpdateInfo));
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(UpdateTargetLabel));
        OnPropertyChanged(nameof(UpdateBadge));
        OnPropertyChanged(nameof(UpdateTooltip));
        OnPropertyChanged(nameof(UpdateButtonLabel));
        OnPropertyChanged(nameof(HasCompatibilityNote));
        OnPropertyChanged(nameof(CompatibilityNote));
        OnPropertyChanged(nameof(CanUpdate));
    }

    public void SetDependencyGaps(IReadOnlyList<PackageDependency>? gaps)
    {
        gaps ??= Array.Empty<PackageDependency>();
        if (ReferenceEquals(_dependencyGaps, gaps)) return;
        _dependencyGaps = gaps;
        OnPropertyChanged(nameof(HasDependencyGaps));
        OnPropertyChanged(nameof(CanInstallMissing));
        OnPropertyChanged(nameof(DependencyGapLabel));
        OnPropertyChanged(nameof(DependencyGapText));
    }
}
