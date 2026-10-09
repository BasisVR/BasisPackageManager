using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.Views;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class InstallsViewModel : ObservableObject
{
    private readonly UserSettingsService _settingsService;
    private readonly BasisInstallService _installService;
    private readonly GitService _git;
    private readonly BasisUpdateService _updates;
    private readonly MainWindowViewModel _shell;

    private InstallRow? _activeRow;
    private bool _checkingBasis;
    private bool _recheckBasis;
    private bool _isGridView;

    public ObservableCollection<InstallRow> Installs { get; } = new();

    public RelayCommand AddExistingCommand { get; }
    public RelayCommand FindProjectsCommand { get; }
    public RelayCommand NewProjectCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand UpdateAllCommand { get; }
    public RelayCommand<InstallRow> ChangeBranchCommand { get; }
    public RelayCommand<InstallRow> UpdateBasisCommand { get; }
    public RelayCommand<InstallRow> CheckUpdatesCommand { get; }
    public RelayCommand<InstallRow> OpenInUnityCommand { get; }
    public RelayCommand<InstallRow> OpenFolderCommand { get; }
    public RelayCommand<InstallRow> ManagePackagesCommand { get; }
    public RelayCommand<InstallRow> RemoveCommand { get; }
    public RelayCommand<InstallRow> SetActiveCommand { get; }
    public RelayCommand<InstallRow> BackupCommand { get; }
    public RelayCommand<InstallRow> BasisChangesCommand { get; }
    public RelayCommand ToggleLayoutCommand { get; }

    public bool IsGridView
    {
        get => _isGridView;
        set
        {
            if (!SetField(ref _isGridView, value)) return;
            RaiseLayout();
            _ = PersistGridViewAsync(value);
        }
    }

    public bool IsListView => !_isGridView;
    public string LayoutToggleLabel => _isGridView ? L.Tr("installs.button.listView") : L.Tr("installs.button.gridView");

    public InstallsViewModel(UserSettingsService settingsService, BasisInstallService installService, GitService git, BasisUpdateService updates, MainWindowViewModel shell)
    {
        _settingsService = settingsService;
        _installService = installService;
        _git = git;
        _updates = updates;
        _shell = shell;

        AddExistingCommand = new RelayCommand(AddExistingAsync);
        FindProjectsCommand = new RelayCommand(FindProjectsAsync);
        NewProjectCommand = new RelayCommand(NewProjectAsync);
        RefreshCommand = new RelayCommand(RefreshAsync);
        UpdateAllCommand = new RelayCommand(() => UpdateAllAsync(BasisPM.App.Services.Dialogs.ConfirmAsync));
        ChangeBranchCommand = new RelayCommand<InstallRow>(ChangeBranchAsync);
        UpdateBasisCommand = new RelayCommand<InstallRow>(r => UpdateBasisAsync(r, null));
        CheckUpdatesCommand = new RelayCommand<InstallRow>(r => RefreshGitInfoAsync(r, fetch: true));
        OpenInUnityCommand = new RelayCommand<InstallRow>(OpenInUnity);
        OpenFolderCommand = new RelayCommand<InstallRow>(r => { if (r is not null) BasisPM.App.Services.ExternalLink.OpenFolder(r.RepoRoot); });
        ManagePackagesCommand = new RelayCommand<InstallRow>(r => Activate(r, "packages"));
        RemoveCommand = new RelayCommand<InstallRow>(RemoveAsync);
        SetActiveCommand = new RelayCommand<InstallRow>(r => Activate(r, null));
        BackupCommand = new RelayCommand<InstallRow>(BackupAsync);
        BasisChangesCommand = new RelayCommand<InstallRow>(r => r is null ? Task.CompletedTask : _shell.OpenBasisChangesAsync(r.Install));
        ToggleLayoutCommand = new RelayCommand(() => IsGridView = !IsGridView);
    }

    public async Task LoadAsync(UserSettings settings)
    {
        if (_isGridView != settings.ProjectsGridView)
        {
            _isGridView = settings.ProjectsGridView;
            OnPropertyChanged(nameof(IsGridView));
            RaiseLayout();
        }
        var activePath = _activeRow?.RepoRoot;
        Installs.Clear();
        _activeRow = null;
        foreach (var root in settings.Installs.ToList())
        {
            if (!Directory.Exists(root)) continue;
            var install = await _installService.LoadAsync(root, settings.InstallAliases.GetValueOrDefault(root));
            if (!install.IsBasisCheckout) continue;
            AddRow(install, activate: false);
        }
        SyncPackageProjects();

        var next = Installs.FirstOrDefault(r => string.Equals(r.RepoRoot, activePath, StringComparison.OrdinalIgnoreCase)) ?? Installs.FirstOrDefault();
        if (next is not null) Activate(next, null);
        else _shell.ClearActiveInstall();
    }

    private void RaiseLayout()
    {
        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(LayoutToggleLabel));
    }

    private async Task PersistGridViewAsync(bool grid)
    {
        try { await _settingsService.UpdateAsync(s => s.ProjectsGridView = grid); }
        catch (Exception ex) { DiagnosticLog.Write("Saving the projects view preference", ex); }
    }

    private async Task RefreshAsync()
    {
        var settings = await _settingsService.LoadAsync();
        await LoadAsync(settings);
        await CheckBasisUpdatesAsync(manual: true);
    }

    // Keeps the Packages tab's project dropdown in step with the installs list (Unity projects only).
    private void SyncPackageProjects() =>
        _shell.PackagesVM.SetInstallOptions(Installs.Where(r => r.Install.HasUnityProject).Select(r => r.Install).ToList());

    private InstallRow AddRow(BasisInstall install, bool activate)
    {
        var row = new InstallRow(install);
        Installs.Add(row);
        SyncPackageProjects();
        _ = RefreshGitInfoAsync(row, fetch: false);
        _ = LoadPendingUpdateAsync(row);
        if (activate) Activate(row, null);
        return row;
    }

    private void Activate(InstallRow? row, string? navigateTo)
    {
        if (row is null) return;
        if (_activeRow is not null) _activeRow.IsActive = false;
        _activeRow = row;
        row.IsActive = true;
        _shell.SetActiveInstall(row.Install);
        if (navigateTo is not null) _shell.NavigateTo(navigateTo);
    }

    public void ActivateInstall(BasisInstall install)
    {
        var row = Installs.FirstOrDefault(r => string.Equals(r.RepoRoot, install.RepoRoot, StringComparison.OrdinalIgnoreCase));
        if (row is not null) Activate(row, null);
        else _shell.SetActiveInstall(install);
    }

    /// <summary>
    /// Renames a project's card. The name is a label kept in settings and the folder isn't touched; an empty name, or the
    /// folder's own name, goes back to showing the folder name.
    /// </summary>
    public async Task RenameAsync(InstallRow row, string? name)
    {
        var trimmed = (name ?? "").Trim();
        var alias = trimmed.Length == 0 || trimmed == row.Install.Name ? null : trimmed;
        if (alias == row.Install.Alias) return;
        row.Install.Alias = alias;
        row.RefreshName();
        SyncPackageProjects();
        if (row.IsActive) _shell.SetActiveInstall(row.Install);
        try
        {
            await PersistAsync();
            _shell.SetStatus(L.Tr("installs.status.renamed", row.Name), StatusKind.Success);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Saving the name of {row.RepoRoot}", ex);
            _shell.SetStatus(L.Tr("installs.status.renameFailed", ex.Message), StatusKind.Error, row.Name);
        }
    }

    public void MarkActive(BasisInstall install)
    {
        var row = Installs.FirstOrDefault(r => string.Equals(r.RepoRoot, install.RepoRoot, StringComparison.OrdinalIgnoreCase));
        if (row is null || ReferenceEquals(row, _activeRow)) return;
        if (_activeRow is not null) _activeRow.IsActive = false;
        _activeRow = row;
        row.IsActive = true;
    }

    private async Task RefreshGitInfoAsync(InstallRow? row, bool fetch)
    {
        if (row is null) return;
        if (!row.Install.IsGitRepo) { row.GitSummary = L.Tr("installs.git.notGitRepo"); return; }
        row.IsBusy = true;
        var activity = fetch ? _shell.BeginActivity(L.Tr("installs.git.checkingRemote"), row.Name) : Guid.Empty;
        try
        {
            if (fetch)
            {
                row.GitSummary = L.Tr("installs.git.checkingRemote");
                await _git.FetchAsync(row.RepoRoot, line => ReportActivity(activity, line));
            }
            var status = await _git.GetStatusAsync(row.RepoRoot);
            row.Branch = status.Branch;
            row.Commit = status.ShortCommit;
            row.GitSummary = DescribeStatus(status);
            row.ChangeCount = status.ChangeCount;
            row.CoreHasUpdate = status.Upstream.HasUpstream && status.Upstream.Behind > 0;
            if (fetch)
                _shell.SetStatus(L.Tr("installs.status.rowSummary", row.Name, row.GitSummary), StatusKind.Info);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Refreshing Git state for {row.RepoRoot}", ex);
            row.GitSummary = L.Tr("installs.git.error", ex.Message);
        }
        finally
        {
            row.IsBusy = false;
            if (fetch) _shell.EndActivity(activity);
        }
    }

    private static string DescribeStatus(GitStatus status)
    {
        var parts = new List<string>();
        if (status.Upstream.HasUpstream)
        {
            var remote = status.Upstream.Name;
            if (status.Upstream.Behind > 0) parts.Add(remote is null ? L.Tr("installs.git.behindUpstream", status.Upstream.Behind) : L.Tr("installs.git.behindRemote", status.Upstream.Behind, remote));
            if (status.Upstream.Ahead > 0) parts.Add(L.Tr("installs.git.ahead", status.Upstream.Ahead));
            if (status.Upstream.IsUpToDate) parts.Add(remote is null ? L.Tr("installs.git.upToDate") : L.Tr("installs.git.upToDateWith", remote));
        }
        else parts.Add(L.Tr("installs.git.noUpstream"));
        if (status.ChangeCount > 0) parts.Add(L.Tr("installs.git.localChanges", status.ChangeCount, status.ChangeCount == 1 ? "" : "s"));
        return string.Join("  ·  ", parts);
    }

    private async Task ChangeBranchAsync(InstallRow? row)
    {
        if (row is null) return;
        if (!row.Install.IsGitRepo) { _shell.SetStatus(L.Tr("installs.status.notGitRepo", row.Name), StatusKind.Error); return; }
        if (!_git.IsAvailable) { _shell.SetStatus(L.Tr("installs.status.gitNotFound"), StatusKind.Error); return; }
        if (row.BasisUpdateInProgress) { _shell.SetStatus(L.Tr("installs.status.updateInProgressNoBranch", row.Name), StatusKind.Error); return; }

        row.IsBusy = true;
        _shell.SetStatus(L.Tr("installs.status.loadingBranches", row.Name));
        var fetchActivity = _shell.BeginActivity(L.Tr("installs.status.loadingBranches", row.Name), row.Name);
        IReadOnlyList<ProjectBranch> projectBranches;
        IReadOnlyList<string> basisBranches;
        string basisBranch;
        try
        {
            await _git.FetchAsync(row.RepoRoot, line => ReportActivity(fetchActivity, line));
            projectBranches = await _updates.ListProjectBranchesAsync(row.RepoRoot);
            var heads = await _updates.GetBasisHeadsAsync(refresh: true);
            basisBranches = BasisUpdateService.OrderBranches(heads.Keys);
            basisBranch = await _updates.ResolveBasisBranchAsync(row.RepoRoot, heads);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Loading branches for {row.RepoRoot}", ex);
            _shell.SetStatus(L.Tr("installs.status.branchListFailed", ex.Message), StatusKind.Error, row.Name);
            return;
        }
        finally
        {
            row.IsBusy = false;
            _shell.EndActivity(fetchActivity);
        }

        var groups = BranchGroups(basisBranches, basisBranch, projectBranches);
        if (groups.Count == 0) { _shell.SetStatus(L.Tr("installs.status.noBranches", row.Name), StatusKind.Info); return; }
        var current = projectBranches.FirstOrDefault(b => b.IsCurrent)?.Name ?? row.Branch;
        var picked = await BasisPM.App.Services.Dialogs.PickBranchAsync(L.Tr("installs.dialog.pickBranchTitle", row.Name),
            L.Tr("dialog.branchPicker.subProject", current, basisBranch), groups);
        if (picked is null || (picked.IsCurrent && !picked.IsBasis)) return;
        if (picked.IsBasis) await UpdateBasisAsync(row, picked.Name);
        else await SwitchProjectBranchAsync(row, new ProjectBranch(picked.Name, picked.Remote, false));
    }

    public static IReadOnlyList<BranchGroup> BranchGroups(IReadOnlyList<string> basisBranches, string basisBranch, IReadOnlyList<ProjectBranch> projectBranches)
    {
        var groups = new List<BranchGroup>();
        if (basisBranches.Count > 0)
            groups.Add(new BranchGroup(L.Tr("dialog.branchPicker.basisGroup"), L.Tr("dialog.branchPicker.basisHint"),
                basisBranches.Select(b => new BranchChoice(b, b, null, true, b == basisBranch)).ToList()));
        if (projectBranches.Count > 0)
            groups.Add(new BranchGroup(L.Tr("dialog.branchPicker.projectGroup"), L.Tr("dialog.branchPicker.projectHint"),
                projectBranches.Select(b => new BranchChoice(b.Display, b.Name, b.Remote, false, b.IsCurrent)).ToList()));
        return groups;
    }

    private async Task SwitchProjectBranchAsync(InstallRow row, ProjectBranch target)
    {
        BranchSwitchPlan plan;
        row.IsBusy = true;
        try { plan = await _updates.PlanBranchSwitchAsync(row.RepoRoot, target); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Checking a switch to {target.Display} for {row.RepoRoot}", ex);
            _shell.SetStatus(L.Tr("installs.status.switchBranchError", ex.Message), StatusKind.Error, row.Name);
            return;
        }
        finally { row.IsBusy = false; }

        if (plan.InProgress) { _shell.SetStatus(L.Tr("installs.status.updateInProgressNoBranch", row.Name), StatusKind.Error); return; }
        if (plan.BlockingPaths.Count > 0)
        {
            _shell.SetStatus(L.Tr("installs.status.switchBranchFailed", row.Name,
                BasisUpdateText.DescribeFailure(BasisUpdateFailure.IgnoredFilesInTheWay, string.Join('\n', plan.BlockingPaths))), StatusKind.Error);
            return;
        }
        if (!plan.CanSwitch)
        {
            _shell.SetStatus(L.Tr("installs.status.switchBranchFailed", row.Name, BasisUpdateText.DescribeSwitchBlock(plan)), StatusKind.Error);
            return;
        }
        if (OperatingSystem.IsWindows() && UnityProjectService.IsOpenInUnity(row.UnityProjectPath))
        {
            _shell.SetStatus(L.Tr("installs.status.unityOpenSwitch", row.Name), StatusKind.Error);
            return;
        }
        if (plan.CollidingPaths.Count > 0 && !await BasisPM.App.Services.Dialogs.ConfirmAsync(
                L.Tr("installs.dialog.carryEditsTitle"), L.Tr("installs.dialog.carryEditsBody", plan.CollidingPaths.Count, target.Name)))
            return;
        if (!await PromptBackupAsync(row, L.Tr("installs.action.changeBranch")))
            return;

        var info = new OperationInfo(BasisOperation.BranchSwitch, target.Name, plan.CurrentBranch);
        var unityBefore = row.UnityVersion;
        row.IsBusy = true;
        _shell.SetStatus(L.Tr("installs.status.switchingBranch", row.Name, target.Name));
        var activity = _shell.BeginActivity(L.Tr("installs.status.switchingBranch", row.Name, target.Name), row.Name);
        BasisUpdateResult result;
        try
        {
            result = await _updates.SwitchBranchAsync(row.RepoRoot, plan, line => ReportActivity(activity, line));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Switching branches for {row.RepoRoot}", ex);
            _shell.SetStatus(L.Tr("installs.status.switchBranchError", ex.Message), StatusKind.Error, row.Name);
            return;
        }
        finally
        {
            row.IsBusy = false;
            _shell.EndActivity(activity);
        }
        await HandleBasisUpdateResultAsync(row, result, unityBefore, info);
    }

    private sealed record OperationInfo(BasisOperation Operation, string Branch, string? FromBranch)
    {
        public bool IsBasisSwitch => Operation != BasisOperation.BranchSwitch && FromBranch is not null && FromBranch != Branch;

        public static OperationInfo From(BasisUpdatePlan plan) => new(plan.Kind == BasisUpdateKind.Apply ? BasisOperation.Apply : BasisOperation.Merge, plan.BasisBranch, plan.FromBranch);

        public static OperationInfo From(BasisUpdateState state) => state.Operation == BasisOperation.BranchSwitch
            ? new(state.Operation, state.TargetBranch ?? "", state.LocalBranch)
            : new(state.Operation, state.BasisBranch, null);
    }

    public async Task CheckBasisUpdatesAsync(bool manual)
    {
        if (_checkingBasis)
        {
            _recheckBasis = true;
            return;
        }
        var rows = Installs.ToList();
        if (rows.Count == 0) return;
        _checkingBasis = true;
        _recheckBasis = false;
        var activity = manual ? _shell.BeginActivity(L.Tr("installs.status.checkingBasis")) : Guid.Empty;
        try
        {
            await _updates.GetBasisHeadsAsync(refresh: true);
            foreach (var row in rows) await RefreshBasisStateAsync(row, allowFetch: true);
            if (manual)
            {
                var available = rows.Count(r => r.BasisUpdateAvailable || r.BasisUpdateInProgress);
                _shell.SetStatus(available == 0
                    ? L.Tr("installs.status.basisAllUpToDate")
                    : L.Tr("installs.status.basisChecked", available, rows.Count), StatusKind.Info);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Checking projects for Basis updates", ex);
            if (manual) _shell.SetStatus(L.Tr("installs.status.basisCheckFailed", ex.Message), StatusKind.Error);
        }
        finally
        {
            _checkingBasis = false;
            if (manual) _shell.EndActivity(activity);
            _shell.RefreshBasisUpdateNotice();
        }
        if (_recheckBasis) await CheckBasisUpdatesAsync(manual: false);
    }

    private async Task RefreshBasisStateAsync(InstallRow row, bool allowFetch)
    {
        row.IsCheckingBasis = true;
        try
        {
            row.ApplyBasisCheck(await _updates.CheckAsync(row.RepoRoot, allowFetch));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Checking {row.RepoRoot} for a Basis update", ex);
            row.ApplyBasisCheck(new BasisUpdateCheck(BasisUpdateStatus.Error, "", null, null, false, ex.Message));
        }
        finally
        {
            row.IsCheckingBasis = false;
            _shell.RefreshBasisUpdateNotice();
        }
    }

    private async Task LoadPendingUpdateAsync(InstallRow row)
    {
        try
        {
            var state = await _updates.LoadStateAsync(row.RepoRoot);
            if (state is null || row.BasisCheck is not null) return;
            var info = OperationInfo.From(state);
            row.ApplyBasisCheck(new BasisUpdateCheck(BasisUpdateStatus.UpdateAvailable, info.Branch, state.UpstreamSha, null, true, null, state.Operation));
            _shell.RefreshBasisUpdateNotice();
        }
        catch (Exception ex) { DiagnosticLog.Write($"Reading the Basis update state for {row.RepoRoot}", ex); }
    }

    private async Task UpdateBasisAsync(InstallRow? row, string? basisBranch)
    {
        if (row is null) return;
        if (!_git.IsAvailable) { _shell.SetStatus(L.Tr("installs.status.gitNotFound"), StatusKind.Error); return; }

        var plan = await PlanBasisUpdateAsync(row, basisBranch);
        if (plan is null) return;
        if (plan.Kind == BasisUpdateKind.InProgress)
        {
            await ResolveBasisUpdateAsync(row, plan.State is { } state ? OperationInfo.From(state) : OperationInfo.From(plan));
            return;
        }

        var review = new BasisUpdateReviewViewModel(row.Name, plan, UnityProjectService.IsOpenInUnity(row.UnityProjectPath),
            branch => PlanBasisUpdateAsync(row, branch),
            () => _updates.ListBasisBranchesAsync());
        switch (await BasisPM.App.Services.Dialogs.ReviewBasisUpdateAsync(review))
        {
            case BasisUpdateDecision.Update:
                await ApplyBasisUpdateAsync(row, review);
                break;
            case BasisUpdateDecision.Follow:
                await FollowBasisBranchAsync(row, review.Plan.BasisBranch);
                break;
            case BasisUpdateDecision.SetUpGit:
                await SetUpGitAsync(row);
                break;
            default:
                if (review.Plan.Kind == BasisUpdateKind.UpToDate) await RefreshBasisStateAsync(row, allowFetch: false);
                break;
        }
    }

    /// <summary>
    /// Brings every project up to date with the Basis branch it already follows, the way each row's Update would but
    /// behind one confirmation instead of a review per project. A project that needs a decision (a predicted conflict,
    /// a move to another Basis branch, a guessed Basis version, a paused update, Unity open) is listed and left for its
    /// own Update button.
    /// </summary>
    public async Task UpdateAllAsync(Func<string, string, Task<bool>> confirm)
    {
        if (!_git.IsAvailable) { _shell.SetStatus(L.Tr("installs.status.gitNotFound"), StatusKind.Error); return; }
        var rows = Installs.ToList();
        if (rows.Count == 0) return;

        var ready = new List<(InstallRow Row, BasisUpdatePlan Plan)>();
        var waiting = new List<(InstallRow Row, string Reason)>();
        var checking = _shell.BeginActivity(L.Tr("installs.updateAll.checking"));
        try
        {
            foreach (var row in rows)
            {
                if (row.IsBusy) { waiting.Add((row, L.Tr("installs.updateAll.reason.busy"))); continue; }
                row.IsBusy = true;
                try
                {
                    var plan = await _updates.PlanAsync(row.RepoRoot, null, line => ReportActivity(checking, line, row.Name));
                    if (plan.Kind == BasisUpdateKind.UpToDate) continue;
                    if (WhyNotAutomatic(row, plan) is { } reason) waiting.Add((row, reason));
                    else ready.Add((row, plan));
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"Checking {row.RepoRoot} against the latest Basis", ex);
                    waiting.Add((row, ex.Message));
                }
                finally { row.IsBusy = false; }
            }
        }
        finally { _shell.EndActivity(checking); }

        if (ready.Count == 0)
        {
            ReportUpdateAll(Array.Empty<string>(), waiting, Array.Empty<string>());
            return;
        }
        var lines = new List<string> { L.Tr("installs.updateAll.confirmIntro"), "" };
        lines.AddRange(ready.Select(r => L.Tr("installs.updateAll.confirmItem", r.Row.Name, r.Plan.IncomingCount, r.Plan.BasisBranch)));
        if (waiting.Count > 0)
        {
            lines.Add("");
            lines.Add(L.Tr("installs.updateAll.confirmWaiting"));
            lines.AddRange(waiting.Select(w => L.Tr("installs.updateAll.waitingItem", w.Row.Name, w.Reason)));
        }
        if (!await confirm(L.Tr("installs.updateAll.confirmTitle"), string.Join("\n", lines))) return;

        var updated = new List<string>();
        var notes = new List<string>();
        var applying = _shell.BeginActivity(L.Tr("installs.updateAll.confirmTitle"));
        try
        {
            for (var i = 0; i < ready.Count; i++)
            {
                var (row, plan) = ready[i];
                if (OperatingSystem.IsWindows() && UnityProjectService.IsOpenInUnity(row.UnityProjectPath))
                {
                    waiting.Add((row, L.Tr("installs.updateAll.reason.unityOpen")));
                    continue;
                }
                var unityBefore = row.UnityVersion;
                row.IsBusy = true;
                _shell.SetStatus(L.Tr("installs.updateAll.updating", row.Name, i + 1, ready.Count), StatusKind.Info, row.Name);
                BasisUpdateResult result;
                try { result = await _updates.ApplyAsync(row.RepoRoot, plan, line => ReportActivity(applying, line, row.Name)); }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"Updating {row.RepoRoot} to the latest Basis", ex);
                    waiting.Add((row, ex.Message));
                    continue;
                }
                finally { row.IsBusy = false; }

                if (result.Kind == BasisUpdateResultKind.Conflicts)
                {
                    // Left paused like a single update closed mid-conflict; the row's Finish update picks it up.
                    var info = OperationInfo.From(plan);
                    row.ApplyBasisCheck(new BasisUpdateCheck(BasisUpdateStatus.UpdateAvailable, info.Branch, null, null, true, null, info.Operation));
                    waiting.Add((row, L.Tr("installs.updateAll.reason.stopped")));
                    continue;
                }
                if (result.Kind == BasisUpdateResultKind.Updated)
                {
                    updated.Add(row.Name);
                    await ReloadRowAsync(row);
                    if (row.HasUnityProject && !string.Equals(unityBefore, row.UnityVersion, StringComparison.Ordinal))
                        notes.Add(L.Tr("installs.updateAll.unityChanged", row.Name, row.UnityVersion));
                }
                else waiting.Add((row, BasisUpdateText.DescribeFailure(result.Failure, result.Detail)));
                await RefreshBasisStateAsync(row, allowFetch: false);
            }
        }
        finally
        {
            _shell.EndActivity(applying);
            _shell.RefreshBasisUpdateNotice();
        }
        ReportUpdateAll(updated, waiting, notes);
    }

    private static string? WhyNotAutomatic(InstallRow row, BasisUpdatePlan plan) => plan.Kind switch
    {
        BasisUpdateKind.InProgress => L.Tr("installs.updateAll.reason.paused"),
        BasisUpdateKind.NotGitRepo => L.Tr("installs.updateAll.reason.notGit"),
        BasisUpdateKind.Blocked when BasisUpdateText.DescribeBlock(plan) is { Length: > 0 } block => block,
        _ when !plan.CanApply => L.Tr("installs.updateAll.reason.review"),
        _ when plan.IsSwitch => L.Tr("installs.updateAll.reason.switch", plan.FromBranch ?? "", plan.BasisBranch),
        _ when plan.BaseSource == BasisBaseSource.Similarity => L.Tr("installs.updateAll.reason.firstUpdate"),
        _ when plan.PredictedConflicts is { Count: > 0 } => L.Tr("installs.updateAll.reason.conflicts"),
        _ when OperatingSystem.IsWindows() && UnityProjectService.IsOpenInUnity(row.UnityProjectPath) => L.Tr("installs.updateAll.reason.unityOpen"),
        _ => null,
    };

    private void ReportUpdateAll(IReadOnlyList<string> updated, IReadOnlyList<(InstallRow Row, string Reason)> waiting, IReadOnlyList<string> notes)
    {
        var needsYou = string.Join("; ", waiting.Select(w => L.Tr("installs.updateAll.waitingShort", w.Row.Name, w.Reason)));
        var message = (updated.Count, waiting.Count) switch
        {
            (0, 0) => L.Tr("installs.status.basisAllUpToDate"),
            (0, _) => L.Tr("installs.updateAll.nothing", needsYou),
            (_, 0) => L.Tr("installs.updateAll.done", string.Join(", ", updated)),
            _ => L.Tr("installs.updateAll.doneWaiting", string.Join(", ", updated), needsYou),
        };
        _shell.SetStatus(string.Join(" ", notes.Prepend(message)), waiting.Count == 0 ? StatusKind.Success : StatusKind.Info);
    }

    private async Task FollowBasisBranchAsync(InstallRow row, string basisBranch)
    {
        var set = await _updates.SetBasisBranchAsync(row.RepoRoot, basisBranch);
        _shell.SetStatus(set.Ok ? L.Tr("installs.status.basisFollowed", row.Name, basisBranch) : L.Tr("installs.status.basisFollowFailed", row.Name, basisBranch),
            set.Ok ? StatusKind.Success : StatusKind.Error);
        await RefreshBasisStateAsync(row, allowFetch: true);
    }

    private async Task<BasisUpdatePlan?> PlanBasisUpdateAsync(InstallRow row, string? basisBranch)
    {
        row.IsBusy = true;
        var activity = _shell.BeginActivity(L.Tr("installs.status.planningUpdate", row.Name), row.Name);
        try
        {
            return await _updates.PlanAsync(row.RepoRoot, basisBranch, line => ReportActivity(activity, line));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Checking {row.RepoRoot} against the latest Basis", ex);
            _shell.SetStatus(L.Tr("installs.status.basisUpdateFailed", row.Name, ex.Message), StatusKind.Error);
            return null;
        }
        finally
        {
            row.IsBusy = false;
            _shell.EndActivity(activity);
        }
    }

    private async Task ApplyBasisUpdateAsync(InstallRow row, BasisUpdateReviewViewModel review)
    {
        var plan = review.Plan;
        if (OperatingSystem.IsWindows() && UnityProjectService.IsOpenInUnity(row.UnityProjectPath))
        {
            _shell.SetStatus(L.Tr("installs.status.unityOpen", row.Name), StatusKind.Error);
            return;
        }
        if (review.Backup && !await BackupAsync(row)) return;

        var unityBefore = row.UnityVersion;
        row.IsBusy = true;
        _shell.SetStatus(L.Tr("installs.status.updatingBasis", row.Name));
        var activity = _shell.BeginActivity(L.Tr("installs.status.updatingBasis", row.Name), row.Name);
        BasisUpdateResult result;
        try
        {
            result = await _updates.ApplyAsync(row.RepoRoot, plan, line => ReportActivity(activity, line));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Updating {row.RepoRoot} to the latest Basis", ex);
            _shell.SetStatus(L.Tr("installs.status.basisUpdateFailed", row.Name, ex.Message), StatusKind.Error);
            return;
        }
        finally
        {
            row.IsBusy = false;
            _shell.EndActivity(activity);
        }
        await HandleBasisUpdateResultAsync(row, result, unityBefore, OperationInfo.From(plan));
    }

    private async Task ResolveBasisUpdateAsync(InstallRow row, OperationInfo info, string? unityVersionBefore = null)
    {
        var root = await _updates.ResolveRepositoryRootAsync(row.RepoRoot) ?? row.RepoRoot;
        var unityBefore = unityVersionBefore ?? row.UnityVersion;
        var model = new MergeConflictsViewModel(row.Name, root,
            () => _updates.LoadStateAsync(row.RepoRoot),
            () => _updates.GetConflictsAsync(row.RepoRoot),
            (path, choice) => _updates.ResolveAsync(row.RepoRoot, path, choice),
            () => _updates.ContinueAsync(row.RepoRoot),
            () => _updates.AbortAsync(row.RepoRoot),
            () => OperatingSystem.IsWindows() && UnityProjectService.IsOpenInUnity(row.UnityProjectPath));
        await model.LoadAsync();
        var outcome = await BasisPM.App.Services.Dialogs.ResolveBasisConflictsAsync(model);
        if (outcome is null)
        {
            await RefreshBasisStateAsync(row, allowFetch: false);
            _shell.SetStatus(info.Operation == BasisOperation.BranchSwitch
                ? L.Tr("installs.status.switchPaused", row.Name, info.Branch)
                : L.Tr("installs.status.basisUpdatePaused", row.Name), StatusKind.Info);
            return;
        }
        await HandleBasisUpdateResultAsync(row, outcome, unityBefore, info);
    }

    private async Task HandleBasisUpdateResultAsync(InstallRow row, BasisUpdateResult result, string unityBefore, OperationInfo info)
    {
        var switching = info.Operation == BasisOperation.BranchSwitch;
        switch (result.Kind)
        {
            case BasisUpdateResultKind.Updated:
                await ReloadRowAsync(row);
                if (switching) SyncPackageProjects();
                var unityChanged = row.HasUnityProject && !string.Equals(unityBefore, row.UnityVersion, StringComparison.Ordinal);
                _shell.SetStatus(switching ? L.Tr("installs.status.switchedBranch", row.Name, info.Branch)
                    : unityChanged ? L.Tr("installs.status.basisUpdatedUnity", row.Name, info.Branch, row.UnityVersion, unityBefore)
                    : info.IsBasisSwitch ? L.Tr("installs.status.basisSwitched", row.Name, info.FromBranch ?? "", info.Branch)
                    : L.Tr("installs.status.basisUpdated", row.Name, info.Branch, ShortSha(result.NewHead)), StatusKind.Success);
                break;
            case BasisUpdateResultKind.Aborted:
                await ReloadRowAsync(row);
                _shell.SetStatus(switching ? L.Tr("installs.status.switchAborted", row.Name, info.FromBranch ?? "")
                    : L.Tr("installs.status.basisUpdateAborted", row.Name), StatusKind.Info);
                break;
            case BasisUpdateResultKind.Conflicts:
                row.ApplyBasisCheck(new BasisUpdateCheck(BasisUpdateStatus.UpdateAvailable, info.Branch, null, null, true, null, info.Operation));
                _shell.RefreshBasisUpdateNotice();
                await ResolveBasisUpdateAsync(row, info, unityBefore);
                return;
            default:
                _shell.SetStatus(switching
                    ? L.Tr("installs.status.switchBranchFailed", row.Name, BasisUpdateText.DescribeFailure(result.Failure, result.Detail))
                    : L.Tr("installs.status.basisUpdateFailed", row.Name, BasisUpdateText.DescribeFailure(result.Failure, result.Detail)), StatusKind.Error);
                break;
        }
        await RefreshBasisStateAsync(row, allowFetch: switching);
    }

    private async Task SetUpGitAsync(InstallRow row)
    {
        row.IsBusy = true;
        _shell.SetStatus(L.Tr("installs.status.gitSetup", row.Name));
        var activity = _shell.BeginActivity(L.Tr("installs.status.gitSetup", row.Name), row.Name);
        BasisStepResult init;
        try { init = await _updates.InitializeRepositoryAsync(row.RepoRoot, row.UnityProjectPath, line => ReportActivity(activity, line)); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Recording {row.RepoRoot} in a new git repository", ex);
            init = BasisStepResult.Fail(BasisUpdateFailure.InitFailed, ex.Message);
        }
        finally
        {
            row.IsBusy = false;
            _shell.EndActivity(activity);
        }
        if (!init.Ok)
        {
            _shell.SetStatus(L.Tr("installs.status.gitSetupFailed", row.Name, BasisUpdateText.DescribeFailure(init.Failure, init.Detail)), StatusKind.Error);
            return;
        }
        _shell.SetStatus(DescribeGitSetup(row, init), init.LargeFiles.Count > 0 ? StatusKind.Info : StatusKind.Success);
        await ReloadRowAsync(row);
        await UpdateBasisAsync(row, null);
    }

    private static string DescribeGitSetup(InstallRow row, BasisStepResult init)
    {
        var parts = new List<string> { L.Tr("installs.status.gitSetupDone", row.Name) };
        if (init.AddedFiles.Count > 0) parts.Add(L.Tr("installs.status.gitSetupAdded", string.Join(", ", init.AddedFiles)));
        if (init.Unhidden.Count > 0) parts.Add(L.Tr("installs.status.gitSetupUnhidden", init.Unhidden.Count));
        if (init.LeftOut.Count > 0) parts.Add(L.Tr("installs.status.gitSetupLeftOut", string.Join(", ", init.LeftOut)));
        if (init.LargeFiles.Count > 0) parts.Add(L.Tr("installs.status.gitSetupLargeFiles", init.LargeFiles.Count));
        return string.Join(" ", parts);
    }

    private async Task ReloadRowAsync(InstallRow row)
    {
        var reloaded = await _installService.LoadAsync(row.RepoRoot, row.Install.Alias);
        row.UpdateInstall(reloaded);
        if (row.IsActive) _shell.SetActiveInstall(reloaded);
        await RefreshGitInfoAsync(row, fetch: false);
    }

    private static string ShortSha(string? sha) => string.IsNullOrEmpty(sha) ? "" : sha.Length > 9 ? sha[..9] : sha;

    private async Task<bool> BackupAsync(InstallRow? row)
    {
        if (row is null) return false;
        if (!BackupService.LooksLikeUnityProject(row.UnityProjectPath))
        {
            _shell.SetStatus(L.Tr("installs.status.noUnityToBackup", row.Name), StatusKind.Error);
            return false;
        }
        row.IsBusy = true;
        _shell.SetStatus(L.Tr("installs.status.backingUp", row.Name));
        var activity = _shell.BeginActivity(L.Tr("installs.status.backingUp", row.Name), row.Name);
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var zip = await BackupService.CreateBackupAsync(row.UnityProjectPath, DefaultBackupDir(row), stamp,
                msg => Dispatcher.UIThread.Post(() =>
                {
                    _shell.SetStatus(msg, StatusKind.Info, row.Name);
                    _shell.ReportActivity(activity, msg);
                }));
            _shell.SetStatus(L.Tr("installs.status.backedUp", row.Name, zip), StatusKind.Success);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Backing up Basis installation {row.RepoRoot}", ex);
            _shell.SetStatus(L.Tr("installs.status.backupFailed", ex.Message), StatusKind.Error, row.Name);
            return false;
        }
        finally
        {
            row.IsBusy = false;
            _shell.EndActivity(activity);
        }
    }

    /// <summary>
    /// Offers a backup before a project-mutating action. Returns false only if the user cancels;
    /// true means proceed (having optionally taken a backup first).
    /// </summary>
    private async Task<bool> PromptBackupAsync(InstallRow row, string action)
    {
        if (!row.HasUnityProject) return true; // nothing to back up
        var window = GetMainWindow();
        if (window is null) return true;

        var message = L.Tr("installs.dialog.backupPrompt", action, row.Name);
        var choice = await new BackupPromptDialog(message).ShowDialog<BackupChoice>(window);
        if (choice == BackupChoice.Cancel) return false;
        if (choice == BackupChoice.Backup) await BackupAsync(row);
        return true;
    }

    private static string DefaultBackupDir(InstallRow row) =>
        Path.Combine(Path.GetDirectoryName(row.RepoRoot) ?? row.RepoRoot, "BasisBackups");

    private async Task AddExistingAsync()
    {
        var window = GetMainWindow();
        if (window is null) return;
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.Tr("installs.picker.selectExistingClone"),
            AllowMultiple = false,
        });
        var picked = folders?.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(picked)) return;

        if (Installs.Any(r => string.Equals(r.RepoRoot, picked, StringComparison.OrdinalIgnoreCase)))
        {
            _shell.SetStatus(L.Tr("installs.status.alreadyInList"), StatusKind.Info);
            return;
        }

        var install = await _installService.LoadAsync(picked);
        if (!install.IsBasisCheckout)
        {
            _shell.SetStatus(L.Tr("installs.status.notBasisCheckout"), StatusKind.Error);
            return;
        }
        var alias = await BasisPM.App.Services.Dialogs.PromptAliasAsync(L.Tr("installs.dialog.nameThisInstall"), picked, install.Name);
        install.Alias = string.IsNullOrWhiteSpace(alias) ? null : alias;
        var row = AddRow(install, activate: true);
        await PersistAsync();
        _ = RefreshBasisStateAsync(row, allowFetch: true);
        var note = install.HasUnityProject ? "" : L.Tr("installs.status.noUnityDetectedNote");
        _shell.SetStatus(L.Tr("installs.status.added", install.Name, note), install.HasUnityProject ? StatusKind.Success : StatusKind.Info);
    }

    private async Task FindProjectsAsync()
    {
        var window = GetMainWindow();
        if (window is null) return;
        var startIn = Installs.Select(r => Path.GetDirectoryName(r.RepoRoot)).FirstOrDefault(Directory.Exists);
        if (await FindProjectsWindow.PickFolderAsync(window, startIn) is not { Length: > 0 } folder) return;
        var search = new FindProjectsViewModel(_installService, Installs.Select(r => r.Install).ToList());
        _ = search.SearchAsync(folder);
        var picked = await new FindProjectsWindow { DataContext = search }.ShowDialog<IReadOnlyList<FoundProject>?>(window);
        if (picked is { Count: > 0 }) await AddFoundAsync(picked);
    }

    public async Task<int> AddFoundAsync(IReadOnlyList<FoundProject> projects)
    {
        var added = new List<InstallRow>();
        foreach (var project in projects)
        {
            if (Installs.Any(r => Platform.PathsEqual(r.RepoRoot, project.RepoRoot) || Platform.PathsEqual(r.Install.UnityProjectPath, project.UnityProjectPath))) continue;
            var alias = string.Equals(project.Name, Path.GetFileName(project.RepoRoot), StringComparison.Ordinal) ? null : project.Name;
            var install = await _installService.LoadAsync(project.RepoRoot, alias);
            if (install.IsBasisCheckout) added.Add(AddRow(install, activate: _activeRow is null));
        }
        if (added.Count == 0) return 0;
        await PersistAsync();
        _ = RefreshBasisStatesAsync(added);
        _shell.SetStatus(added.Count == 1 ? L.Tr("installs.status.foundAddedOne", added[0].Name) : L.Tr("installs.status.foundAdded", added.Count), StatusKind.Success);
        return added.Count;
    }

    private async Task RefreshBasisStatesAsync(IReadOnlyList<InstallRow> rows)
    {
        foreach (var row in rows) await RefreshBasisStateAsync(row, allowFetch: true);
    }

    private async Task NewProjectAsync()
    {
        if (!_git.IsAvailable)
        {
            _shell.SetStatus(L.Tr("installs.status.gitNotFound"), StatusKind.Error);
            return;
        }

        var window = GetMainWindow();
        if (window is null) return;
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.Tr("installs.picker.newProjectFolder"),
            AllowMultiple = false,
        });
        var picked = folders?.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(picked)) return;

        if (Installs.Any(r => string.Equals(r.RepoRoot, picked, StringComparison.OrdinalIgnoreCase)))
        {
            _shell.SetStatus(L.Tr("installs.status.alreadyInList"), StatusKind.Info);
            return;
        }
        if (Directory.EnumerateFileSystemEntries(picked).Any())
        {
            _shell.SetStatus(L.Tr("installs.status.cloneFolderNotEmpty"), StatusKind.Error);
            return;
        }

        try
        {
            var cloningText = L.Tr("installs.status.cloningBasis");
            _shell.SetStatus(cloningText, StatusKind.Info, Path.GetFileName(picked));
            var activity = _shell.BeginActivity(cloningText, Path.GetFileName(picked));
            GitResult result;
            try
            {
                result = await _git.CloneAsync(
                    BasisInstallService.BasisRepoUrl,
                    picked,
                    BasisInstallService.DefaultBranch,
                    line => ReportActivity(activity, line));
            }
            finally
            {
                _shell.EndActivity(activity);
            }
            if (!result.Ok)
            {
                _shell.SetStatus(L.Tr("installs.status.cloneFailed", Tail(result.Output)), StatusKind.Error, Path.GetFileName(picked));
                return;
            }

            var install = await _installService.LoadAsync(picked);
            if (!install.IsBasisCheckout)
            {
                _shell.SetStatus(L.Tr("installs.status.cloneMissingProject"), StatusKind.Error, Path.GetFileName(picked));
                return;
            }

            var alias = await BasisPM.App.Services.Dialogs.PromptAliasAsync(L.Tr("installs.dialog.nameThisInstall"), picked, install.Name);
            install.Alias = string.IsNullOrWhiteSpace(alias) ? null : alias;
            AddRow(install, activate: true);
            await PersistAsync();
            _shell.SetStatus(L.Tr("installs.status.basisCloned", install.DisplayName), StatusKind.Success);
            _shell.NavigateTo("packages");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Cloning a Basis installation", ex);
            _shell.SetStatus(L.Tr("installs.status.cloneError", ex.Message), StatusKind.Error, Path.GetFileName(picked));
        }
    }

    // Launches the install's Unity project — the resolved Basis/Basis subfolder (install.UnityProjectPath) —
    // in the editor matching its ProjectVersion, falling back to Unity Hub when that version isn't installed.
    // The shell method reports its own status and handles the "no Unity project" case.
    private void OpenInUnity(InstallRow? row)
    {
        if (row is null) return;
        _ = _shell.OpenProjectInUnityAsync(row.Install);
    }

    private async Task RemoveAsync(InstallRow? row)
    {
        if (row is null) return;
        var window = GetMainWindow();
        if (window is null) return;

        // Confirm first, and let the user choose between forgetting the project (files kept) and
        // permanently deleting the folder from disk.
        var choice = await new RemovePromptDialog(row.Name, row.RepoRoot).ShowDialog<RemoveChoice>(window);
        if (choice == RemoveChoice.Cancel) return;

        if (choice == RemoveChoice.DeleteFromDisk)
        {
            var confirmed = await BasisPM.App.Services.Dialogs.ConfirmAsync(
                L.Tr("installs.dialog.deleteConfirmTitle"),
                L.Tr("installs.dialog.deleteConfirmBody", row.Name, row.RepoRoot));
            if (!confirmed) return;

            row.IsBusy = true;
            _shell.SetStatus(L.Tr("installs.status.deleting", row.Name));
            try
            {
                await BasisInstallService.DeleteFolderAsync(row.RepoRoot);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"Deleting Basis installation {row.RepoRoot}", ex);
                row.IsBusy = false;
                // Leave the row in place so the user can retry (e.g. after closing Unity, which locks files).
                _shell.SetStatus(L.Tr("installs.status.deleteFailed", row.Name, ex.Message), StatusKind.Error);
                return;
            }
        }

        Installs.Remove(row);
        SyncPackageProjects();
        if (_activeRow == row)
        {
            _activeRow = null;
            var next = Installs.FirstOrDefault();
            if (next is not null) Activate(next, null);
            else _shell.ClearActiveInstall();
        }
        await PersistAsync();
        _shell.SetStatus(
            choice == RemoveChoice.DeleteFromDisk
                ? L.Tr("installs.status.deleted", row.Name)
                : L.Tr("installs.status.removed", row.Name),
            StatusKind.Info);
    }

    private async Task PersistAsync()
    {
        var settings = await _settingsService.LoadAsync();
        settings.Installs = Installs.Select(r => r.RepoRoot).ToList();
        settings.InstallAliases = Installs
            .Where(r => !string.IsNullOrWhiteSpace(r.Install.Alias))
            .ToDictionary(r => r.RepoRoot, r => r.Install.Alias!, StringComparer.OrdinalIgnoreCase);
        await _settingsService.SaveAsync(settings);
    }

    private static string Tail(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "" : lines[^1].Trim();
    }

    private void ReportActivity(Guid activity, string message, string? project = null) =>
        Dispatcher.UIThread.Post(() =>
        {
            _shell.SetStatus(message, StatusKind.Info, project ?? _shell.ProjectOf(activity));
            _shell.ReportActivity(activity, message);
        });

    private static Window? GetMainWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null;
}

public sealed class InstallRow : ObservableObject
{
    private string _branch = "…";
    private string _commit = "";
    private string _gitSummary = L.Tr("installs.git.checking");
    private int _changeCount;
    private bool _isBusy;
    private bool _isActive;
    private bool _coreHasUpdate;

    public BasisInstall Install { get; private set; }

    public InstallRow(BasisInstall install) => Install = install;

    public string Name => Install.DisplayName;
    public string FolderName => Install.Name;
    public string RepoRoot => Install.RepoRoot;
    public string UnityProjectPath => Install.UnityProjectPath;
    public string UnityVersion => Install.UnityVersion;
    public bool HasUnityProject => Install.HasUnityProject;
    public bool IsGitRepo => Install.IsGitRepo;
    public string UnityVersionLabel => Install.HasUnityProject ? L.Tr("installs.row.unityVersion", Install.UnityVersion) : L.Tr("installs.row.noUnityProject");

    public string Branch { get => _branch; set => SetField(ref _branch, value); }
    public string Commit { get => _commit; set { if (SetField(ref _commit, value)) OnPropertyChanged(nameof(BranchCommit)); } }
    public string BranchCommit => string.IsNullOrEmpty(_commit) ? _branch : $"{_branch} · {_commit}";
    public string GitSummary { get => _gitSummary; set => SetField(ref _gitSummary, value); }
    public int ChangeCount { get => _changeCount; set { if (SetField(ref _changeCount, value)) OnPropertyChanged(nameof(HasLocalChanges)); } }
    public bool HasLocalChanges => _changeCount > 0;
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }
    public bool IsActive { get => _isActive; set => SetField(ref _isActive, value); }
    // True only when the checked-out branch is behind its upstream — i.e. an update is actually
    // available. Drives the "Update Core" button's brand-red styling (neutral when up to date).
    public bool CoreHasUpdate { get => _coreHasUpdate; set => SetField(ref _coreHasUpdate, value); }

    private BasisUpdateCheck? _basisCheck;
    private bool _isCheckingBasis;

    public BasisUpdateCheck? BasisCheck => _basisCheck;
    public bool IsCheckingBasis { get => _isCheckingBasis; set { if (SetField(ref _isCheckingBasis, value)) RaiseBasisState(); } }
    public bool BasisUpdateInProgress => _basisCheck?.InProgress == true;
    public bool BranchSwitchInProgress => BasisUpdateInProgress && _basisCheck!.Operation == BasisOperation.BranchSwitch;
    public bool BasisUpdateAvailable => _basisCheck is { Status: BasisUpdateStatus.UpdateAvailable, InProgress: false };
    public bool HighlightBasisUpdate => BasisUpdateAvailable || BasisUpdateInProgress;
    public bool BasisPillMuted => !HighlightBasisUpdate && (_isCheckingBasis || _basisCheck?.Status is BasisUpdateStatus.UpToDate or BasisUpdateStatus.Error);
    public string? BasisRemoteSha => _basisCheck?.RemoteSha;
    public int? BasisBehind => _basisCheck?.Behind;

    public string BasisPillText
    {
        get
        {
            if (BranchSwitchInProgress) return L.Tr("installs.basis.switchInProgress");
            if (BasisUpdateInProgress) return L.Tr("installs.basis.inProgress");
            if (BasisUpdateAvailable)
                return _basisCheck!.Behind switch
                {
                    1 => L.Tr("installs.basis.updateCountOne"),
                    int n and > 1 => L.Tr("installs.basis.updateCount", n),
                    _ => L.Tr("installs.basis.updateAvailable"),
                };
            if (_isCheckingBasis) return L.Tr("installs.basis.checking");
            return _basisCheck?.Status switch
            {
                BasisUpdateStatus.UpToDate => L.Tr("installs.basis.upToDate", _basisCheck.BasisBranch),
                BasisUpdateStatus.Error => L.Tr("installs.basis.checkFailed"),
                _ => "",
            };
        }
    }

    public string BasisPillTooltip => _basisCheck?.Detail ?? L.Tr("installs.basis.tooltip", _basisCheck?.BasisBranch ?? BasisInstallService.DefaultBranch);
    public string UpdateButtonLabel => BranchSwitchInProgress ? L.Tr("installs.button.finishSwitch")
        : BasisUpdateInProgress ? L.Tr("installs.button.finishUpdate") : L.Tr("installs.button.updateBasis");
    public string UpdateButtonTooltip => BranchSwitchInProgress ? L.Tr("installs.tooltip.finishSwitch")
        : BasisUpdateInProgress ? L.Tr("installs.tooltip.finishUpdate") : L.Tr("installs.tooltip.updateBasis");

    public void ApplyBasisCheck(BasisUpdateCheck? check)
    {
        _basisCheck = check;
        RaiseBasisState();
    }

    private void RaiseBasisState()
    {
        OnPropertyChanged(nameof(BasisCheck));
        OnPropertyChanged(nameof(BasisUpdateInProgress));
        OnPropertyChanged(nameof(BranchSwitchInProgress));
        OnPropertyChanged(nameof(BasisUpdateAvailable));
        OnPropertyChanged(nameof(HighlightBasisUpdate));
        OnPropertyChanged(nameof(BasisPillMuted));
        OnPropertyChanged(nameof(BasisRemoteSha));
        OnPropertyChanged(nameof(BasisBehind));
        OnPropertyChanged(nameof(BasisPillText));
        OnPropertyChanged(nameof(BasisPillTooltip));
        OnPropertyChanged(nameof(UpdateButtonLabel));
        OnPropertyChanged(nameof(UpdateButtonTooltip));
    }

    public void RefreshName() => OnPropertyChanged(nameof(Name));

    public void UpdateInstall(BasisInstall install)
    {
        Install = install;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(UnityVersion));
        OnPropertyChanged(nameof(UnityVersionLabel));
        OnPropertyChanged(nameof(HasUnityProject));
        OnPropertyChanged(nameof(IsGitRepo));
    }
}
