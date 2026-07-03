using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

    public ObservableCollection<InstallRow> Installs { get; } = new();
    public ObservableCollection<BranchOption> Branches { get; } = new();

    public string ClonePath { get => _clonePath; set => SetField(ref _clonePath, value); }
    public string FolderName { get => _folderName; set => SetField(ref _folderName, value); }
    public BranchOption? SelectedBranch { get => _selectedBranch; set => SetField(ref _selectedBranch, value); }
    public string CloneProgress { get => _cloneProgress; set => SetField(ref _cloneProgress, value); }
    public bool IsCloning { get => _isCloning; set { if (SetField(ref _isCloning, value)) OnPropertyChanged(nameof(IsNotCloning)); } }
    public bool IsNotCloning => !_isCloning;

    public string RepoUrl => BasisInstallService.BasisRepoUrl;

    public RelayCommand CloneCommand { get; }
    public RelayCommand BrowseCloneFolderCommand { get; }
    public RelayCommand AddExistingCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand<InstallRow> UpdateCoreCommand { get; }
    public RelayCommand<InstallRow> CheckUpdatesCommand { get; }
    public RelayCommand<InstallRow> OpenFolderCommand { get; }
    public RelayCommand<InstallRow> ManagePackagesCommand { get; }
    public RelayCommand<InstallRow> ViewChangesCommand { get; }
    public RelayCommand<InstallRow> RemoveCommand { get; }
    public RelayCommand<InstallRow> SetActiveCommand { get; }
    public RelayCommand<InstallRow> BackupCommand { get; }

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
        OpenFolderCommand = new RelayCommand<InstallRow>(OpenFolder);
        ManagePackagesCommand = new RelayCommand<InstallRow>(r => Activate(r, "packages"));
        ViewChangesCommand = new RelayCommand<InstallRow>(r => Activate(r, "changes"));
        RemoveCommand = new RelayCommand<InstallRow>(RemoveAsync);
        SetActiveCommand = new RelayCommand<InstallRow>(r => Activate(r, null));
        BackupCommand = new RelayCommand<InstallRow>(BackupAsync);
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
            var install = await _installService.LoadAsync(root);
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

    private InstallRow AddRow(BasisInstall install, bool activate)
    {
        var row = new InstallRow(install);
        Installs.Add(row);
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
        if (!row.Install.IsGitRepo) { row.GitSummary = "not a git repository"; return; }
        row.IsBusy = true;
        try
        {
            if (fetch)
            {
                row.GitSummary = "checking remote…";
                await _git.FetchAsync(row.RepoRoot);
            }
            var status = await _git.GetStatusAsync(row.RepoRoot);
            row.Branch = status.Branch;
            row.Commit = status.ShortCommit;
            row.GitSummary = DescribeStatus(status);
            row.ChangeCount = status.ChangeCount;
            if (fetch)
                _shell.SetStatus($"{row.Name}: {row.GitSummary}", StatusKind.Info);
        }
        catch (Exception ex)
        {
            row.GitSummary = $"git error: {ex.Message}";
        }
        finally { row.IsBusy = false; }
    }

    private static string DescribeStatus(GitStatus status)
    {
        var parts = new List<string>();
        if (status.Upstream.HasUpstream)
        {
            if (status.Upstream.Behind > 0) parts.Add($"{status.Upstream.Behind} behind upstream");
            if (status.Upstream.Ahead > 0) parts.Add($"{status.Upstream.Ahead} ahead");
            if (status.Upstream.IsUpToDate) parts.Add("up to date");
        }
        else parts.Add("no upstream");
        if (status.ChangeCount > 0) parts.Add($"{status.ChangeCount} local change{(status.ChangeCount == 1 ? "" : "s")}");
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

    private async Task CloneAsync()
    {
        if (!_git.IsAvailable)
        {
            _shell.SetStatus("Git was not found. Install Git and add it to your PATH (see Settings).", StatusKind.Error);
            return;
        }
        var parent = ClonePath?.Trim();
        if (string.IsNullOrEmpty(parent))
        {
            _shell.SetStatus("Choose a folder to clone into first.", StatusKind.Error);
            return;
        }
        var folder = string.IsNullOrWhiteSpace(FolderName) ? BasisInstallService.DefaultFolderName : FolderName.Trim();
        var dest = Path.Combine(parent, folder);

        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
        {
            _shell.SetStatus($"{dest} already exists and is not empty.", StatusKind.Error);
            return;
        }
        if (Installs.Any(r => string.Equals(r.RepoRoot, dest, StringComparison.OrdinalIgnoreCase)))
        {
            _shell.SetStatus("That install is already in the list.", StatusKind.Info);
            return;
        }

        IsCloning = true;
        CloneProgress = "Starting clone…";
        _shell.SetStatus($"Cloning Basis into {dest}…");
        try
        {
            Directory.CreateDirectory(parent);
            var branch = string.IsNullOrWhiteSpace(SelectedBranch?.Name) ? null : SelectedBranch!.Name.Trim();
            var result = await _git.CloneAsync(RepoUrl, dest, branch,
                line => Dispatcher.UIThread.Post(() => CloneProgress = line));

            if (!result.Ok)
            {
                _shell.SetStatus($"Clone failed (exit {result.Code}). {Tail(result.Output)}", StatusKind.Error);
                return;
            }

            var install = await _installService.LoadAsync(dest);
            AddRow(install, activate: true);
            await PersistAsync();
            CloneProgress = "";
            var versionNote = install.HasUnityProject && install.UnityVersion != "unknown"
                ? $" Requires Unity {install.UnityVersion} — open the Unity tab to install it."
                : "";
            _shell.SetStatus($"Cloned Basis into {dest}.{versionNote}", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _shell.SetStatus($"Clone error: {ex.Message}", StatusKind.Error);
        }
        finally { IsCloning = false; }
    }

    private async Task UpdateCoreAsync(InstallRow? row)
    {
        if (row is null) return;
        if (!row.Install.IsGitRepo)
        {
            _shell.SetStatus($"{row.Name} is not a git repository.", StatusKind.Error);
            return;
        }

        if (!await PromptBackupAsync(row, "Update Core runs 'git pull', which can change files in your Basis project."))
            return;

        row.IsBusy = true;
        _shell.SetStatus($"Updating {row.Name} (git pull)…");
        try
        {
            var result = await _git.PullAsync(row.RepoRoot);
            if (result.Ok)
            {
                var reloaded = await _installService.LoadAsync(row.RepoRoot);
                row.UpdateInstall(reloaded);
                if (row.IsActive) _shell.SetActiveInstall(reloaded);
                await RefreshGitInfoAsync(row, fetch: false);
                _shell.SetStatus($"Updated {row.Name}. {Tail(result.Output)}", StatusKind.Success);
            }
            else
            {
                _shell.SetStatus($"Update failed for {row.Name}: {Tail(result.Output)}", StatusKind.Error);
            }
        }
        catch (Exception ex)
        {
            _shell.SetStatus($"Update error: {ex.Message}", StatusKind.Error);
        }
        finally { row.IsBusy = false; }
    }

    private async Task BackupAsync(InstallRow? row)
    {
        if (row is null) return;
        if (!BackupService.LooksLikeUnityProject(row.UnityProjectPath))
        {
            _shell.SetStatus($"{row.Name} has no Unity project to back up.", StatusKind.Error);
            return;
        }
        row.IsBusy = true;
        _shell.SetStatus($"Backing up {row.Name}…");
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var zip = await BackupService.CreateBackupAsync(row.UnityProjectPath, DefaultBackupDir(row), stamp,
                msg => Dispatcher.UIThread.Post(() => _shell.SetStatus(msg)));
            _shell.SetStatus($"Backed up {row.Name} → {zip}", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _shell.SetStatus($"Backup failed: {ex.Message}", StatusKind.Error);
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

        var message = $"{action}\n\nBack up {row.Name} first? This zips Assets, Packages and " +
                      "ProjectSettings (not the Library cache) into a BasisBackups folder next to your install.";
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
            Title = "Select an existing Basis clone",
            AllowMultiple = false,
        });
        var picked = folders?.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(picked)) return;

        if (Installs.Any(r => string.Equals(r.RepoRoot, picked, StringComparison.OrdinalIgnoreCase)))
        {
            _shell.SetStatus("That install is already in the list.", StatusKind.Info);
            return;
        }

        var install = await _installService.LoadAsync(picked);
        AddRow(install, activate: true);
        await PersistAsync();
        var note = install.HasUnityProject ? "" : " (no Unity project detected under it)";
        _shell.SetStatus($"Added {install.Name}{note}.", install.HasUnityProject ? StatusKind.Success : StatusKind.Info);
    }

    private async Task BrowseCloneFolderAsync()
    {
        var window = GetMainWindow();
        if (window is null) return;
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder to clone Basis into",
            AllowMultiple = false,
        });
        var picked = folders?.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(picked))
        {
            ClonePath = picked;
            await PersistAsync();
        }
    }

    private void OpenFolder(InstallRow? row)
    {
        if (row is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = row.RepoRoot,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _shell.SetStatus($"Could not open folder: {ex.Message}", StatusKind.Error);
        }
    }

    private async Task RemoveAsync(InstallRow? row)
    {
        if (row is null) return;
        Installs.Remove(row);
        if (_activeRow == row)
        {
            _activeRow = null;
            var next = Installs.FirstOrDefault();
            if (next is not null) Activate(next, null);
        }
        await PersistAsync();
        _shell.SetStatus($"Removed {row.Name} from the list (files left on disk).", StatusKind.Info);
    }

    private async Task PersistAsync()
    {
        var settings = await _settingsService.LoadAsync();
        settings.Installs = Installs.Select(r => r.RepoRoot).ToList();
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
    public string Display => IsDefault ? $"{Name} (main)" : Name;
}

public sealed class InstallRow : ObservableObject
{
    private string _branch = "…";
    private string _commit = "";
    private string _gitSummary = "checking…";
    private int _changeCount;
    private bool _isBusy;
    private bool _isActive;

    public BasisInstall Install { get; private set; }

    public InstallRow(BasisInstall install) => Install = install;

    public string Name => Install.Name;
    public string RepoRoot => Install.RepoRoot;
    public string UnityProjectPath => Install.UnityProjectPath;
    public string UnityVersion => Install.UnityVersion;
    public bool HasUnityProject => Install.HasUnityProject;
    public string UnityVersionLabel => Install.HasUnityProject ? $"Unity {Install.UnityVersion}" : "No Unity project";

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
