using System.Collections.ObjectModel;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class PackagesViewModel : ObservableObject
{
    private readonly CatalogService _catalogService;
    private readonly UnityProjectService _projectService;
    private readonly GitHubService _githubService;
    private readonly MainWindowViewModel _shell;

    private Catalog _catalog = new();
    private BasisInstall? _install;
    private string _filter = "";
    private string _githubInput = "";
    private bool _isBusy;
    private string _installedOwner = AllOwnersLabel;
    private readonly List<InstalledPackageRow> _allInstalled = new();
    private BasisInstall? _selectedInstall;
    private bool _syncingSelection;

    private const string AllOwnersLabel = "All owners";

    public ObservableCollection<PackageRow> Available { get; } = new();
    public ObservableCollection<InstalledPackageRow> Installed { get; } = new();
    public ObservableCollection<string> InstalledOwners { get; } = new();
    public ObservableCollection<BasisInstall> InstallOptions { get; } = new();

    public string Filter { get => _filter; set { if (SetField(ref _filter, value)) Refilter(); } }
    public string GitHubInput { get => _githubInput; set => SetField(ref _githubInput, value); }
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }
    public string SelectedInstalledOwner { get => _installedOwner; set { if (SetField(ref _installedOwner, value)) ApplyInstalledFilter(); } }

    public BasisInstall? SelectedInstall
    {
        get => _selectedInstall;
        set
        {
            if (!SetField(ref _selectedInstall, value)) return;
            // Picking a project from the dropdown makes it the working context.
            if (!_syncingSelection && value is not null &&
                !string.Equals(value.RepoRoot, _install?.RepoRoot, StringComparison.OrdinalIgnoreCase))
                _shell.SetActiveInstall(value);
        }
    }

    public string InstallName => _install?.DisplayName ?? "No install selected";
    public bool HasInstall => _install is not null && _install.HasUnityProject;
    public bool HasInstalls => InstallOptions.Count > 0;

    public RelayCommand<CatalogPackageVersion> InstallCommand { get; }
    public RelayCommand<InstalledPackageRow> UninstallCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand AddFromGitHubCommand { get; }

    public PackagesViewModel(CatalogService catalogService, UnityProjectService projectService, MainWindowViewModel shell)
    {
        _catalogService = catalogService;
        _projectService = projectService;
        _githubService = new GitHubService();
        _shell = shell;

        InstallCommand = new RelayCommand<CatalogPackageVersion>(InstallCuratedAsync);
        UninstallCommand = new RelayCommand<InstalledPackageRow>(UninstallAsync);
        RefreshCommand = new RelayCommand(RefreshAsync);
        AddFromGitHubCommand = new RelayCommand(AddFromGitHubAsync);
    }

    public void SetActiveInstall(BasisInstall install)
    {
        _install = install;
        _syncingSelection = true;
        _selectedInstall = InstallOptions.FirstOrDefault(i => string.Equals(i.RepoRoot, install.RepoRoot, StringComparison.OrdinalIgnoreCase)) ?? install;
        OnPropertyChanged(nameof(SelectedInstall));
        _syncingSelection = false;
        OnPropertyChanged(nameof(InstallName));
        OnPropertyChanged(nameof(HasInstall));
        RefreshInstalled();
    }

    /// <summary>Projects listed in the Packages project selector; keeps the current pick if still present.</summary>
    public void SetInstallOptions(IReadOnlyList<BasisInstall> installs)
    {
        _syncingSelection = true;
        InstallOptions.Clear();
        foreach (var i in installs) InstallOptions.Add(i);
        _selectedInstall = _install is null
            ? null
            : InstallOptions.FirstOrDefault(i => string.Equals(i.RepoRoot, _install.RepoRoot, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(SelectedInstall));
        OnPropertyChanged(nameof(HasInstalls));
        _syncingSelection = false;
    }

    public async Task LoadCatalogAsync(string? url)
    {
        IsBusy = true;
        try
        {
            _catalog = await _catalogService.LoadAsync(url);
            Refilter();
        }
        finally { IsBusy = false; }
    }

    private async Task RefreshAsync()
    {
        await LoadCatalogAsync(null);
        if (_install is not null && _install.HasUnityProject)
        {
            try
            {
                var reloaded = await _projectService.LoadAsync(_install.UnityProjectPath);
                _install.Manifest = reloaded.Manifest;
            }
            catch { }
            RefreshInstalled();
        }
    }

    private void Refilter()
    {
        Available.Clear();
        var f = _filter?.Trim() ?? "";
        foreach (var v in _catalogService.AllLatest(_catalog))
        {
            if (f.Length > 0 &&
                !v.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) &&
                !v.Name.Contains(f, StringComparison.OrdinalIgnoreCase))
                continue;

            var installedVersion = _install?.Manifest.Dependencies.GetValueOrDefault(v.Name);
            Available.Add(new PackageRow(v, installedVersion));
        }
    }

    private void RefreshInstalled()
    {
        _allInstalled.Clear();
        Installed.Clear();
        if (_install is null || !_install.HasUnityProject) { RebuildInstalledOwners(); Refilter(); return; }

        foreach (var (name, version) in _install.Manifest.Dependencies)
        {
            var displayName = _catalog.Packages.TryGetValue(name, out var pkg) && pkg.Versions.Count > 0
                ? pkg.Versions.Values.First().DisplayName
                : name;
            var isGit = version.Contains("github.com", StringComparison.OrdinalIgnoreCase) || version.StartsWith("git", StringComparison.OrdinalIgnoreCase);
            _allInstalled.Add(new InstalledPackageRow(name, displayName, version, isGit));
        }

        RebuildInstalledOwners();
        ApplyInstalledFilter();
        Refilter();
    }

    /// <summary>Vendor/owner from a reverse-DNS package id: com.unity.2d.sprite → "Unity".</summary>
    private static string OwnerOf(string id)
    {
        var parts = (id ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        var owner = parts.Length >= 2 ? parts[1] : parts.FirstOrDefault() ?? "";
        if (owner.Length == 0) return "Other";
        return char.ToUpperInvariant(owner[0]) + owner[1..];
    }

    private void RebuildInstalledOwners()
    {
        var current = _installedOwner;
        var owners = _allInstalled.Select(r => OwnerOf(r.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();

        InstalledOwners.Clear();
        InstalledOwners.Add(AllOwnersLabel);
        foreach (var o in owners) InstalledOwners.Add(o);

        // Keep the current pick if it still exists, otherwise fall back to "All owners".
        _installedOwner = InstalledOwners.Contains(current) ? current : AllOwnersLabel;
        OnPropertyChanged(nameof(SelectedInstalledOwner));
    }

    private void ApplyInstalledFilter()
    {
        Installed.Clear();
        var all = string.Equals(_installedOwner, AllOwnersLabel, StringComparison.Ordinal);
        foreach (var r in _allInstalled)
        {
            if (all || string.Equals(OwnerOf(r.Name), _installedOwner, StringComparison.OrdinalIgnoreCase))
                Installed.Add(r);
        }
    }

    private async Task InstallCuratedAsync(CatalogPackageVersion? entry)
    {
        if (entry is null || _install is null || !_install.HasUnityProject)
        {
            _shell.SetStatus("Choose a project first.", StatusKind.Error);
            return;
        }
        var target = _install;

        IsBusy = true;
        try
        {
            var resolver = new DependencyResolver(_catalogService);
            var requested = new List<(string, string)> { (entry.Name, $"^{entry.Version}") };
            foreach (var (name, range) in target.Manifest.Dependencies)
                requested.Add((name, range));

            var result = resolver.Resolve(_catalog, requested);
            if (!result.Ok)
            {
                _shell.SetStatus($"Resolve failed: {string.Join("; ", result.Missing.Concat(result.Conflicts))}", StatusKind.Error);
                return;
            }

            foreach (var (name, ver) in result.Resolved)
                target.Manifest.Dependencies[name] = ver.Url ?? ver.Version;

            await _projectService.SaveManifestAsync(target.UnityProjectPath, target.Manifest);
            _shell.SetStatus($"Installed {entry.DisplayName} into {target.DisplayName}.", StatusKind.Success);
            RefreshInstalled();
        }
        catch (Exception ex)
        {
            _shell.SetStatus($"Install error: {ex.Message}", StatusKind.Error);
        }
        finally { IsBusy = false; }
    }

    private async Task UninstallAsync(InstalledPackageRow? row)
    {
        if (row is null || _install is null) return;
        if (_install.Manifest.Dependencies.Remove(row.Name))
        {
            await _projectService.SaveManifestAsync(_install.UnityProjectPath, _install.Manifest);
            _shell.SetStatus($"Removed {row.DisplayName}.", StatusKind.Success);
            RefreshInstalled();
        }
    }

    private async Task AddFromGitHubAsync()
    {
        if (_install is null || !_install.HasUnityProject)
        {
            _shell.SetStatus("Choose a project first.", StatusKind.Error);
            return;
        }
        if (string.IsNullOrWhiteSpace(GitHubInput))
        {
            _shell.SetStatus("Paste a GitHub URL or owner/repo.", StatusKind.Error);
            return;
        }
        var target = _install;

        IsBusy = true;
        try
        {
            GitHubLocator loc;
            try { loc = GitHubService.Parse(GitHubInput); }
            catch (Exception ex)
            {
                _shell.SetStatus($"Invalid GitHub reference: {ex.Message}", StatusKind.Error);
                return;
            }

            var pkg = await _githubService.FetchPackageJsonAsync(loc);
            if (pkg is null || string.IsNullOrEmpty(pkg.Name))
            {
                _shell.SetStatus(
                    $"Could not load package.json from {loc.Owner}/{loc.Repo}{(loc.Path is null ? "" : "/" + loc.Path)} — check the path and that the repo is public.",
                    StatusKind.Error);
                return;
            }

            var manifestUrl = GitHubService.BuildManifestUrl(loc);
            var existed = target.Manifest.Dependencies.ContainsKey(pkg.Name);
            target.Manifest.Dependencies[pkg.Name] = manifestUrl;
            await _projectService.SaveManifestAsync(target.UnityProjectPath, target.Manifest);

            _shell.SetStatus($"{(existed ? "Updated" : "Added")} {pkg.Name} from GitHub.", StatusKind.Success);
            GitHubInput = "";
            RefreshInstalled();
        }
        catch (Exception ex)
        {
            _shell.SetStatus($"GitHub add failed: {ex.Message}", StatusKind.Error);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Adds a git (UPM) package to the active install's manifest — used by the website "Install in app" deep link.</summary>
    public async Task AddGitPackageAsync(string? id, string? name, string? gitUrl, string? repoUrl)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(gitUrl))
        {
            _shell.SetStatus("Install link was missing the package id or git URL.", StatusKind.Error);
            return;
        }
        if (_install is null || !_install.HasUnityProject)
        {
            _shell.SetStatus($"Open an install with a Unity project first, then click Install for {name ?? id} again.", StatusKind.Error);
            return;
        }
        try
        {
            var existed = _install.Manifest.Dependencies.ContainsKey(id);
            _install.Manifest.Dependencies[id] = gitUrl.Trim();
            await _projectService.SaveManifestAsync(_install.UnityProjectPath, _install.Manifest);
            _shell.SetStatus($"{(existed ? "Updated" : "Added")} {name ?? id} → {_install.Name}.", StatusKind.Success);
            RefreshInstalled();
        }
        catch (Exception ex)
        {
            _shell.SetStatus($"Deep-link install failed: {ex.Message}", StatusKind.Error);
        }
    }
}

public sealed record PackageRow(CatalogPackageVersion Entry, string? InstalledVersion)
{
    public string DisplayName => Entry.DisplayName;
    public string Name => Entry.Name;
    public string Version => Entry.Version;
    public string Description => Entry.Description;
    public bool IsInstalled => !string.IsNullOrEmpty(InstalledVersion);
    public string ButtonLabel => IsInstalled ? "Update" : "Install";
}

public sealed record InstalledPackageRow(string Name, string DisplayName, string Version, bool IsFromGit);
