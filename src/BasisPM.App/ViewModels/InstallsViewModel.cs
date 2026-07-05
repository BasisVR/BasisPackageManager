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
    private readonly MainWindowViewModel _shell;

    private string _clonePath = "";
    private string _folderName = BasisInstallService.DefaultFolderName;
    private BranchOption? _selectedBranch;
    private string _cloneProgress = "";
    private bool _isCloning;
    private InstallRow? _activeRow;
    private bool _showCloneForm;
    private string? _cloneSpaceWarning;
    private bool _cloneSpaceCritical;

    // Basis + its Unity Library can grow past 20 GB. Warn well before the drive is full, and make
    // the clone an explicit choice once it's critically low (the clone would likely fail partway).
    private const long WarnBelowBytes = 10L * 1024 * 1024 * 1024;      // 10 GB
    private const long CriticalBelowBytes = 3L * 1024 * 1024 * 1024;   //  3 GB

    public ObservableCollection<InstallRow> Installs { get; } = new();
    public ObservableCollection<BranchOption> Branches { get; } = new();

    public string ClonePath { get => _clonePath; set { if (SetField(ref _clonePath, value)) UpdateCloneSpaceWarning(); } }
    public string FolderName { get => _folderName; set => SetField(ref _folderName, value); }

    /// <summary>Low-disk-space message for the selected clone path, or null when there's ample room.</summary>
    public string? CloneSpaceWarning
    {
        get => _cloneSpaceWarning;
        private set { if (SetField(ref _cloneSpaceWarning, value)) OnPropertyChanged(nameof(HasCloneSpaceWarning)); }
    }
    public bool HasCloneSpaceWarning => !string.IsNullOrEmpty(_cloneSpaceWarning);
    /// <summary>True when free space is so low the clone will probably fail (drives the stronger styling).</summary>
    public bool IsCloneSpaceCritical { get => _cloneSpaceCritical; private set => SetField(ref _cloneSpaceCritical, value); }
    public BranchOption? SelectedBranch { get => _selectedBranch; set => SetField(ref _selectedBranch, value); }
    public string CloneProgress { get => _cloneProgress; set => SetField(ref _cloneProgress, value); }
    public bool IsCloning { get => _isCloning; set { if (SetField(ref _isCloning, value)) OnPropertyChanged(nameof(IsNotCloning)); } }
    public bool IsNotCloning => !_isCloning;
    public bool ShowCloneForm { get => _showCloneForm; set => SetField(ref _showCloneForm, value); }

    public string RepoUrl => BasisInstallService.BasisRepoUrl;

    public RelayCommand CloneCommand { get; }
    public RelayCommand BrowseCloneFolderCommand { get; }
    public RelayCommand AddExistingCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand<InstallRow> UpdateCoreCommand { get; }
    public RelayCommand<InstallRow> CheckUpdatesCommand { get; }
    public RelayCommand<InstallRow> OpenInUnityCommand { get; }
    public RelayCommand<InstallRow> ManagePackagesCommand { get; }
    public RelayCommand<InstallRow> ViewChangesCommand { get; }
    public RelayCommand<InstallRow> RemoveCommand { get; }
    public RelayCommand<InstallRow> SetActiveCommand { get; }
    public RelayCommand<InstallRow> BackupCommand { get; }
    public RelayCommand ToggleCloneFormCommand { get; }

    public InstallsViewModel(UserSettingsService settingsService, BasisInstallService installService, GitService git, MainWindowViewModel shell)
    {
        _settingsService = settingsService;
        _installService = installService;
        _git = git;
        _shell = shell;

        CloneCommand = new RelayCommand(CloneAsync);
        BrowseCloneFolderCommand = new RelayCommand(BrowseCloneFolderAsync);
        AddExistingCommand = new RelayCommand(AddExistingAsync);
        RefreshCommand = new RelayCommand(RefreshAsync);
        UpdateCoreCommand = new RelayCommand<InstallRow>(UpdateCoreAsync);
        CheckUpdatesCommand = new RelayCommand<InstallRow>(r => RefreshGitInfoAsync(r, fetch: true));
        OpenInUnityCommand = new RelayCommand<InstallRow>(OpenInUnity);
        ManagePackagesCommand = new RelayCommand<InstallRow>(r => Activate(r, "packages"));
        ViewChangesCommand = new RelayCommand<InstallRow>(r => Activate(r, "changes"));
        RemoveCommand = new RelayCommand<InstallRow>(RemoveAsync);
        SetActiveCommand = new RelayCommand<InstallRow>(r => Activate(r, null));
        BackupCommand = new RelayCommand<InstallRow>(BackupAsync);
        ToggleCloneFormCommand = new RelayCommand(() => { ShowCloneForm = !ShowCloneForm; });
    }

    public async Task LoadAsync(UserSettings settings)
    {
        ClonePath = string.IsNullOrWhiteSpace(settings.ClonePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : settings.ClonePath!;

        EnsureBranchSeed();
        _ = LoadBranchesAsync();

        Installs.Clear();
        foreach (var root in settings.Installs.ToList())
        {
            if (!Directory.Exists(root)) continue;
            var install = await _installService.LoadAsync(root, settings.InstallAliases.GetValueOrDefault(root));
            AddRow(install, activate: false);
        }

        if (_activeRow is null && Installs.Count > 0)
            Activate(Installs[0], null);
    }

    private async Task RefreshAsync()
    {
        var settings = await _settingsService.LoadAsync();
        var activePath = _activeRow?.RepoRoot;
        await LoadAsync(settings);
        if (activePath is not null)
        {
            var match = Installs.FirstOrDefault(r => string.Equals(r.RepoRoot, activePath, StringComparison.OrdinalIgnoreCase));
            if (match is not null) Activate(match, null);
        }
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

    private async Task RefreshGitInfoAsync(InstallRow? row, bool fetch)
    {
        if (row is null) return;
        if (!row.Install.IsGitRepo) { row.GitSummary = L.Tr("installs.git.notGitRepo"); return; }
        row.IsBusy = true;
        try
        {
            if (fetch)
            {
                row.GitSummary = L.Tr("installs.git.checkingRemote");
                await _git.FetchAsync(row.RepoRoot);
            }
            var status = await _git.GetStatusAsync(row.RepoRoot);
            row.Branch = status.Branch;
            row.Commit = status.ShortCommit;
            row.GitSummary = DescribeStatus(status);
            row.ChangeCount = status.ChangeCount;
            if (fetch)
                _shell.SetStatus(L.Tr("installs.status.rowSummary", row.Name, row.GitSummary), StatusKind.Info);
        }
        catch (Exception ex)
        {
            row.GitSummary = L.Tr("installs.git.error", ex.Message);
        }
        finally { row.IsBusy = false; }
    }

    private static string DescribeStatus(GitStatus status)
    {
        var parts = new List<string>();
        if (status.Upstream.HasUpstream)
        {
            if (status.Upstream.Behind > 0) parts.Add(L.Tr("installs.git.behindUpstream", status.Upstream.Behind));
            if (status.Upstream.Ahead > 0) parts.Add(L.Tr("installs.git.ahead", status.Upstream.Ahead));
            if (status.Upstream.IsUpToDate) parts.Add(L.Tr("installs.git.upToDate"));
        }
        else parts.Add(L.Tr("installs.git.noUpstream"));
        if (status.ChangeCount > 0) parts.Add(L.Tr("installs.git.localChanges", status.ChangeCount, status.ChangeCount == 1 ? "" : "s"));
        return string.Join("  ·  ", parts);
    }

    private void EnsureBranchSeed()
    {
        if (Branches.Count > 0) return;
        Branches.Add(new BranchOption(BasisInstallService.DefaultBranch, true));
        SelectedBranch = Branches[0];
    }

    private async Task LoadBranchesAsync()
    {
        if (!_git.IsAvailable) return;
        IReadOnlyList<string> remote;
        try { remote = await _git.ListRemoteBranchesAsync(RepoUrl); }
        catch { return; }
        if (remote.Count == 0) return;

        var keepName = SelectedBranch?.Name ?? BasisInstallService.DefaultBranch;
        Branches.Clear();
        foreach (var name in OrderBranches(remote))
            Branches.Add(new BranchOption(name, name == BasisInstallService.DefaultBranch));
        if (Branches.All(b => b.Name != BasisInstallService.DefaultBranch))
            Branches.Insert(0, new BranchOption(BasisInstallService.DefaultBranch, true));

        SelectedBranch = Branches.FirstOrDefault(b => b.Name == keepName)
            ?? Branches.FirstOrDefault(b => b.IsDefault)
            ?? Branches.FirstOrDefault();
    }

    private static IEnumerable<string> OrderBranches(IEnumerable<string> names) =>
        names.Distinct(StringComparer.Ordinal)
            .OrderBy(n => n == BasisInstallService.DefaultBranch ? 0 : n is "main" or "master" ? 1 : 2)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase);

    /// <summary>Refreshes the low-disk-space hint for the currently-typed clone path.</summary>
    private void UpdateCloneSpaceWarning()
    {
        var info = DiskSpace.ForPath(_clonePath);
        if (info is null || info.FreeBytes >= WarnBelowBytes)
        {
            IsCloneSpaceCritical = false;
            CloneSpaceWarning = null;
            return;
        }

        IsCloneSpaceCritical = info.FreeBytes < CriticalBelowBytes;
        var free = DiskSpace.Human(info.FreeBytes);
        CloneSpaceWarning = IsCloneSpaceCritical
            ? L.Tr("installs.space.criticalWarning", free, info.DriveName)
            : L.Tr("installs.space.lowWarning", free, info.DriveName);
    }

    private async Task CloneAsync()
    {
        if (!_git.IsAvailable)
        {
            _shell.SetStatus(L.Tr("installs.status.gitNotFound"), StatusKind.Error);
            return;
        }
        var parent = ClonePath?.Trim();
        if (string.IsNullOrEmpty(parent))
        {
            _shell.SetStatus(L.Tr("installs.status.chooseFolderFirst"), StatusKind.Error);
            return;
        }

        // Almost out of space on the target drive: make continuing an explicit choice.
        var space = DiskSpace.ForPath(parent);
        if (space is not null && space.FreeBytes < CriticalBelowBytes)
        {
            var proceed = await BasisPM.App.Services.Dialogs.ConfirmAsync(L.Tr("installs.dialog.lowDiskSpaceTitle"),
                L.Tr("installs.dialog.lowDiskSpaceBody", DiskSpace.Human(space.FreeBytes), space.DriveName));
            if (!proceed)
            {
                _shell.SetStatus(L.Tr("installs.status.cloneCancelledLowSpace"), StatusKind.Info);
                return;
            }
        }
        var folder = string.IsNullOrWhiteSpace(FolderName) ? BasisInstallService.DefaultFolderName : FolderName.Trim();
        var dest = Path.Combine(parent, folder);

        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
        {
            _shell.SetStatus(L.Tr("installs.status.destExists", dest), StatusKind.Error);
            return;
        }
        if (Installs.Any(r => string.Equals(r.RepoRoot, dest, StringComparison.OrdinalIgnoreCase)))
        {
            _shell.SetStatus(L.Tr("installs.status.alreadyInList"), StatusKind.Info);
            return;
        }

        IsCloning = true;
        CloneProgress = L.Tr("installs.progress.starting");
        _shell.SetStatus(L.Tr("installs.status.cloningInto", dest));
        try
        {
            Directory.CreateDirectory(parent);
            var branch = string.IsNullOrWhiteSpace(SelectedBranch?.Name) ? null : SelectedBranch!.Name.Trim();
            var result = await _git.CloneAsync(RepoUrl, dest, branch,
                line => Dispatcher.UIThread.Post(() => CloneProgress = line));

            if (!result.Ok)
            {
                _shell.SetStatus(L.Tr("installs.status.cloneFailed", result.Code, Tail(result.Output)), StatusKind.Error);
                return;
            }

            var install = await _installService.LoadAsync(dest);
            var alias = await BasisPM.App.Services.Dialogs.PromptAliasAsync(L.Tr("installs.dialog.nameThisInstall"), dest, install.Name);
            install.Alias = string.IsNullOrWhiteSpace(alias) ? null : alias;
            AddRow(install, activate: true);
            await PersistAsync();
            CloneProgress = "";
            var versionNote = install.HasUnityProject && install.UnityVersion != "unknown"
                ? L.Tr("installs.status.requiresUnityNote", install.UnityVersion)
                : "";
            _shell.SetStatus(L.Tr("installs.status.clonedInto", dest, versionNote), StatusKind.Success);

            // Fresh clone: offer the one-click "Basis Recommended" pack.
            await OfferRecommendedAsync(install);
        }
        catch (Exception ex)
        {
            _shell.SetStatus(L.Tr("installs.status.cloneError", ex.Message), StatusKind.Error);
        }
        finally { IsCloning = false; }
    }

    /// <summary>After a fresh clone, offers to add the full "Basis Recommended" package set in one press.</summary>
    private async Task OfferRecommendedAsync(BasisInstall install)
    {
        if (!install.HasUnityProject) return;            // no Unity project to add packages to
        var count = _shell.PackagesVM.RecommendedCount;
        if (count == 0) return;                          // catalog unavailable (e.g. offline)
        var yes = await BasisPM.App.Services.Dialogs.ConfirmAsync(
            L.Tr("installs.dialog.recommendedTitle"),
            L.Tr("installs.dialog.recommendedBody", count));
        if (!yes) return;
        _shell.NavigateTo("packages");
        await _shell.PackagesVM.InstallRecommendedAsync(install);
    }

    private async Task UpdateCoreAsync(InstallRow? row)
    {
        if (row is null) return;
        if (!row.Install.IsGitRepo)
        {
            _shell.SetStatus(L.Tr("installs.status.notGitRepo", row.Name), StatusKind.Error);
            return;
        }

        if (!await PromptBackupAsync(row, L.Tr("installs.action.updateCore")))
            return;

        row.IsBusy = true;
        _shell.SetStatus(L.Tr("installs.status.updating", row.Name));
        try
        {
            var result = await _git.PullAsync(row.RepoRoot);
            if (result.Ok)
            {
                var reloaded = await _installService.LoadAsync(row.RepoRoot, row.Install.Alias);
                row.UpdateInstall(reloaded);
                if (row.IsActive) _shell.SetActiveInstall(reloaded);
                await RefreshGitInfoAsync(row, fetch: false);
                _shell.SetStatus(L.Tr("installs.status.updated", row.Name, Tail(result.Output)), StatusKind.Success);
            }
            else
            {
                _shell.SetStatus(L.Tr("installs.status.updateFailed", row.Name, Tail(result.Output)), StatusKind.Error);
            }
        }
        catch (Exception ex)
        {
            _shell.SetStatus(L.Tr("installs.status.updateError", ex.Message), StatusKind.Error);
        }
        finally { row.IsBusy = false; }
    }

    private async Task BackupAsync(InstallRow? row)
    {
        if (row is null) return;
        if (!BackupService.LooksLikeUnityProject(row.UnityProjectPath))
        {
            _shell.SetStatus(L.Tr("installs.status.noUnityToBackup", row.Name), StatusKind.Error);
            return;
        }
        row.IsBusy = true;
        _shell.SetStatus(L.Tr("installs.status.backingUp", row.Name));
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var zip = await BackupService.CreateBackupAsync(row.UnityProjectPath, DefaultBackupDir(row), stamp,
                msg => Dispatcher.UIThread.Post(() => _shell.SetStatus(msg)));
            _shell.SetStatus(L.Tr("installs.status.backedUp", row.Name, zip), StatusKind.Success);
        }
        catch (Exception ex)
        {
            _shell.SetStatus(L.Tr("installs.status.backupFailed", ex.Message), StatusKind.Error);
        }
        finally { row.IsBusy = false; }
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
        var alias = await BasisPM.App.Services.Dialogs.PromptAliasAsync(L.Tr("installs.dialog.nameThisInstall"), picked, install.Name);
        install.Alias = string.IsNullOrWhiteSpace(alias) ? null : alias;
        AddRow(install, activate: true);
        await PersistAsync();
        var note = install.HasUnityProject ? "" : L.Tr("installs.status.noUnityDetectedNote");
        _shell.SetStatus(L.Tr("installs.status.added", install.Name, note), install.HasUnityProject ? StatusKind.Success : StatusKind.Info);
    }

    private async Task BrowseCloneFolderAsync()
    {
        var window = GetMainWindow();
        if (window is null) return;
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.Tr("installs.picker.chooseCloneFolder"),
            AllowMultiple = false,
        });
        var picked = folders?.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(picked))
        {
            ClonePath = picked;
            await PersistAsync();
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
        Installs.Remove(row);
        SyncPackageProjects();
        if (_activeRow == row)
        {
            _activeRow = null;
            var next = Installs.FirstOrDefault();
            if (next is not null) Activate(next, null);
        }
        await PersistAsync();
        _shell.SetStatus(L.Tr("installs.status.removed", row.Name), StatusKind.Info);
    }

    private async Task PersistAsync()
    {
        var settings = await _settingsService.LoadAsync();
        settings.Installs = Installs.Select(r => r.RepoRoot).ToList();
        settings.InstallAliases = Installs
            .Where(r => !string.IsNullOrWhiteSpace(r.Install.Alias))
            .ToDictionary(r => r.RepoRoot, r => r.Install.Alias!, StringComparer.OrdinalIgnoreCase);
        settings.ClonePath = string.IsNullOrWhiteSpace(ClonePath) ? null : ClonePath.Trim();
        await _settingsService.SaveAsync(settings);
    }

    private static string Tail(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "" : lines[^1].Trim();
    }

    private static Window? GetMainWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null;
}

public sealed record BranchOption(string Name, bool IsDefault)
{
    public string Display => IsDefault ? L.Tr("installs.branch.default", Name) : Name;
}

public sealed class InstallRow : ObservableObject
{
    private string _branch = "…";
    private string _commit = "";
    private string _gitSummary = L.Tr("installs.git.checking");
    private int _changeCount;
    private bool _isBusy;
    private bool _isActive;

    public BasisInstall Install { get; private set; }

    public InstallRow(BasisInstall install) => Install = install;

    public string Name => Install.DisplayName;
    public string FolderName => Install.Name;
    public string RepoRoot => Install.RepoRoot;
    public string UnityProjectPath => Install.UnityProjectPath;
    public string UnityVersion => Install.UnityVersion;
    public bool HasUnityProject => Install.HasUnityProject;
    public string UnityVersionLabel => Install.HasUnityProject ? L.Tr("installs.row.unityVersion", Install.UnityVersion) : L.Tr("installs.row.noUnityProject");

    public string Branch { get => _branch; set => SetField(ref _branch, value); }
    public string Commit { get => _commit; set { if (SetField(ref _commit, value)) OnPropertyChanged(nameof(BranchCommit)); } }
    public string BranchCommit => string.IsNullOrEmpty(_commit) ? _branch : $"{_branch} · {_commit}";
    public string GitSummary { get => _gitSummary; set => SetField(ref _gitSummary, value); }
    public int ChangeCount { get => _changeCount; set { if (SetField(ref _changeCount, value)) OnPropertyChanged(nameof(HasLocalChanges)); } }
    public bool HasLocalChanges => _changeCount > 0;
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }
    public bool IsActive { get => _isActive; set => SetField(ref _isActive, value); }

    public void UpdateInstall(BasisInstall install)
    {
        Install = install;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(UnityVersion));
        OnPropertyChanged(nameof(UnityVersionLabel));
        OnPropertyChanged(nameof(HasUnityProject));
    }
}
