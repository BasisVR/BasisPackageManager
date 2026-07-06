using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.App.Views;
using BasisPM.Core;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class PackagesViewModel : ObservableObject
{
    private readonly UserSettingsService _settingsService;
    private readonly CatalogService _catalogService;
    private readonly UnityProjectService _projectService;
    private readonly GitHubService _githubService;
    private readonly VersionService _versionService = new();
    private readonly MainWindowViewModel _shell;
    private bool _isGridView;

    private Catalog _catalog = new();
    private BasisInstall? _install;
    private string _filter = "";
    private string _githubInput = "";
    private bool _isBusy;
    private string _installedOwner = AllOwnersLabel;
    private readonly List<InstalledPackageRow> _allInstalled = new();
    private BasisInstall? _selectedInstall;
    private bool _syncingSelection;
    // Configured catalog sources, remembered so Refresh reloads exactly what's configured.
    private string? _officialCatalogUrl;
    private IReadOnlyList<string> _extraCatalogUrls = Array.Empty<string>();
    // Package ids that came only from an unofficial (extra) catalog — drives the "Unofficial" badge.
    private readonly HashSet<string> _unofficialIds = new(StringComparer.OrdinalIgnoreCase);

    private static string AllOwnersLabel => L.Tr("packages.filter.allOwners");

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

    public string InstallName => _install?.DisplayName ?? L.Tr("packages.empty.noInstallSelected");
    public bool HasInstall => _install is not null && _install.HasUnityProject;
    public bool HasInstalls => InstallOptions.Count > 0;

    // Packages "Available" list layout: false = rows (default), true = a grid of cards. Persisted.
    public bool IsGridView
    {
        get => _isGridView;
        set
        {
            if (!SetField(ref _isGridView, value)) return;
            OnPropertyChanged(nameof(IsListView));
            OnPropertyChanged(nameof(LayoutToggleLabel));
            _ = PersistGridViewAsync(value);
        }
    }
    public bool IsListView => !_isGridView;
    public string LayoutToggleLabel => _isGridView ? L.Tr("packages.button.listView") : L.Tr("packages.button.gridView");

    // The package whose detail panel is open (null = closed). Clicking a row opens it.
    private PackageRow? _selectedPackage;
    public PackageRow? SelectedPackage
    {
        get => _selectedPackage;
        private set { if (SetField(ref _selectedPackage, value)) OnPropertyChanged(nameof(ShowDetail)); }
    }
    public bool ShowDetail => _selectedPackage is not null;
    public void OpenDetail(PackageRow row) => SelectedPackage = row;

    public RelayCommand<CatalogPackageVersion> InstallCommand { get; }
    public RelayCommand<CatalogPackageVersion> RemoveCommand { get; }
    public RelayCommand<InstalledPackageRow> UninstallCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand AddFromGitHubCommand { get; }
    public RelayCommand CreateBundleCommand { get; }
    public RelayCommand<CatalogPackageVersion> ChooseVersionCommand { get; }
    public RelayCommand ToggleLayoutCommand { get; }
    public RelayCommand<string> OpenLinkCommand { get; }
    public RelayCommand CloseDetailCommand { get; }

    public PackagesViewModel(UserSettingsService settingsService, CatalogService catalogService, UnityProjectService projectService, MainWindowViewModel shell)
    {
        _settingsService = settingsService;
        _catalogService = catalogService;
        _projectService = projectService;
        _githubService = new GitHubService();
        _shell = shell;

        InstallCommand = new RelayCommand<CatalogPackageVersion>(InstallCuratedAsync);
        RemoveCommand = new RelayCommand<CatalogPackageVersion>(RemoveAvailableAsync);
        UninstallCommand = new RelayCommand<InstalledPackageRow>(UninstallAsync);
        RefreshCommand = new RelayCommand(RefreshAsync);
        AddFromGitHubCommand = new RelayCommand(AddFromGitHubAsync);
        CreateBundleCommand = new RelayCommand(CreateBundleAsync);
        ChooseVersionCommand = new RelayCommand<CatalogPackageVersion>(ChooseVersionAsync);
        ToggleLayoutCommand = new RelayCommand(() => IsGridView = !IsGridView);
        OpenLinkCommand = new RelayCommand<string>(url => { if (!string.IsNullOrWhiteSpace(url)) ExternalLink.Open(url!); });
        CloseDetailCommand = new RelayCommand(() => SelectedPackage = null);
    }

    /// <summary>Applies the persisted layout choice at startup without re-saving it.</summary>
    public void SetInitialGridView(bool grid)
    {
        if (_isGridView == grid) return;
        _isGridView = grid;
        OnPropertyChanged(nameof(IsGridView));
        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(LayoutToggleLabel));
    }

    private async Task PersistGridViewAsync(bool grid)
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            settings.PackagesGridView = grid;
            await _settingsService.SaveAsync(settings);
        }
        catch { /* a view preference isn't worth surfacing a settings-write error */ }
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

    public Task LoadCatalogAsync(string? officialUrl, IReadOnlyList<string>? extraUrls = null)
    {
        _officialCatalogUrl = officialUrl;
        _extraCatalogUrls = extraUrls ?? Array.Empty<string>();
        return ReloadCatalogAsync();
    }

    /// <summary>Reloads the official Basis catalog plus any configured unofficial extras, merged into
    /// one list. The official catalog always wins a package-id conflict; extras contribute only ids it
    /// doesn't already define, tracked in <see cref="_unofficialIds"/> so they can be badged.</summary>
    private async Task ReloadCatalogAsync()
    {
        IsBusy = true;
        try
        {
            var merged = await _catalogService.LoadAsync(_officialCatalogUrl);
            _unofficialIds.Clear();
            foreach (var extraUrl in _extraCatalogUrls)
            {
                var extra = await _catalogService.TryLoadAsync(extraUrl?.Trim() ?? "");
                if (extra is null) continue;
                foreach (var kv in extra.Packages)
                {
                    if (merged.Packages.ContainsKey(kv.Key)) continue; // official / earlier extra wins
                    merged.Packages[kv.Key] = kv.Value;
                    _unofficialIds.Add(kv.Key);
                }
            }
            _catalog = merged;
            Refilter();
        }
        finally { IsBusy = false; }
    }

    private async Task RefreshAsync()
    {
        await ReloadCatalogAsync();
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
            Available.Add(new PackageRow(v, installedVersion, _unofficialIds.Contains(v.Name)));
        }

        // Keep an open detail panel pointed at the refreshed row so its installed-state stays current.
        if (_selectedPackage is not null)
        {
            var match = Available.FirstOrDefault(r => string.Equals(r.Name, _selectedPackage.Name, StringComparison.Ordinal));
            if (match is not null) SelectedPackage = match;
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
    internal static string OwnerOf(string id)
    {
        var parts = (id ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        var owner = parts.Length >= 2 ? parts[1] : parts.FirstOrDefault() ?? "";
        if (owner.Length == 0) return L.Tr("packages.filter.otherOwner");
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

    /// <summary>
    /// Recursively adds the registry-catalog dependencies of <paramref name="requested"/> to the
    /// manifest as git deps, skipping anything already present. Dependencies not in the catalog
    /// (e.g. com.unity.*) are left for Unity's Package Manager to resolve. Returns the count added.
    /// </summary>
    private int AddCatalogDependencies(BasisInstall target, IEnumerable<(string Name, string Range)> requested)
    {
        var result = new DependencyResolver(_catalogService).Resolve(_catalog, requested);
        var added = 0;
        foreach (var (name, ver) in result.Resolved)
        {
            if (target.Manifest.Dependencies.ContainsKey(name)) continue;
            target.Manifest.Dependencies[name] = ver.Url ?? ver.Version;
            added++;
        }
        return added;
    }

    // A manifest value like "1.2.3" / "^1.0" is a version range; a git URL or "file:.." is not.
    private static bool IsSemverRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return false;
        try { SemVerRange.Parse(range); return true; }
        catch { return false; }
    }

    private async Task InstallCuratedAsync(CatalogPackageVersion? entry)
    {
        if (entry is null || _install is null || !_install.HasUnityProject)
        {
            _shell.SetStatus(L.Tr("packages.status.chooseProject"), StatusKind.Error);
            return;
        }
        var target = _install;

        IsBusy = true;
        try
        {
            var resolver = new DependencyResolver(_catalogService);
            var requested = new List<(string, string)> { (entry.Name, $"^{entry.Version}") };
            foreach (var (name, range) in target.Manifest.Dependencies)
                if (IsSemverRange(range))   // skip git-URL / file: deps — they aren't version-resolvable
                    requested.Add((name, range));

            var result = resolver.Resolve(_catalog, requested);
            if (result.Conflicts.Count > 0)
            {
                _shell.SetStatus(L.Tr("packages.status.dependencyConflict", string.Join("; ", result.Conflicts)), StatusKind.Error);
                return;
            }

            // Add the package plus every registry dependency; anything not in the catalog
            // (e.g. com.unity.*) is left for Unity's Package Manager to resolve at import.
            foreach (var (name, ver) in result.Resolved)
                target.Manifest.Dependencies[name] = ver.Url ?? ver.Version;

            // Prefer the installed package's latest published release (pin #tag); its deps keep their catalog url.
            if (result.Resolved.TryGetValue(entry.Name, out var mainVer) && mainVer.Url is { } mainUrl)
            {
                var pinned = await ResolveLatestReleaseUrlAsync(mainUrl);
                if (pinned is not null) target.Manifest.Dependencies[entry.Name] = pinned;
            }

            await _projectService.SaveManifestAsync(target.UnityProjectPath, target.Manifest);
            _shell.SetStatus(L.Tr("packages.status.installed", entry.DisplayName, target.DisplayName), StatusKind.Success);
            RefreshInstalled();
        }
        catch (Exception ex)
        {
            _shell.SetStatus(L.Tr("packages.status.installError", ex.Message), StatusKind.Error);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Latest published stable release URL (…git#tag) for a catalog git url, or null to leave it as-is.</summary>
    private async Task<string?> ResolveLatestReleaseUrlAsync(string gitUrl)
    {
        var loc = UpmGitUrl.Parse(gitUrl);
        if (loc is null || loc.Ref is not null) return null;   // unparseable, or the url already pins a ref
        var versions = await _versionService.GetVersionsAsync(gitUrl, null);
        return versions.LatestStable?.Ref is { } tag ? loc.ToManifestUrl(tag, loc.Path) : null;
    }

    /// <summary>Opens the version picker for a package and installs the chosen release/tag/branch.</summary>
    private async Task ChooseVersionAsync(CatalogPackageVersion? entry)
    {
        if (entry is null || _install is null || !_install.HasUnityProject)
        {
            _shell.SetStatus(L.Tr("packages.status.chooseProject"), StatusKind.Error);
            return;
        }
        if (string.IsNullOrWhiteSpace(entry.Url))
        {
            _shell.SetStatus(L.Tr("packages.status.notGitPackage", entry.DisplayName), StatusKind.Info);
            return;
        }
        var target = _install;

        IsBusy = true;
        _shell.SetStatus(L.Tr("packages.status.loadingVersions", entry.DisplayName));
        try
        {
            var versions = await _versionService.GetVersionsAsync(entry.Url, null);
            if (versions.Options.Count == 0)
            {
                _shell.SetStatus(L.Tr("packages.status.noVersionsFound", entry.DisplayName), StatusKind.Error);
                return;
            }

            var chosen = await Dialogs.PickVersionAsync(L.Tr("packages.dialog.installTitle", entry.DisplayName), versions);
            if (chosen is null) return;

            var loc = UpmGitUrl.Parse(entry.Url);
            if (loc is null) { _shell.SetStatus(L.Tr("packages.status.gitUrlParseFailed"), StatusKind.Error); return; }

            target.Manifest.Dependencies[entry.Name] = loc.ToManifestUrl(chosen.Ref, loc.Path);
            AddCatalogDependencies(target, new[] { (entry.Name, "*") });   // pull in its registry deps too
            await _projectService.SaveManifestAsync(target.UnityProjectPath, target.Manifest);

            _shell.SetStatus(L.Tr("packages.status.installedVersion", entry.DisplayName, chosen.Ref ?? L.Tr("packages.status.defaultBranch"), target.DisplayName), StatusKind.Success);
            RefreshInstalled();
        }
        catch (Exception ex) { _shell.SetStatus(L.Tr("packages.status.versionInstallError", ex.Message), StatusKind.Error); }
        finally { IsBusy = false; }
    }

    private async Task UninstallAsync(InstalledPackageRow? row)
    {
        if (row is null) return;
        await RemoveByNameAsync(row.Name, row.DisplayName);
    }

    /// <summary>Removes an already-installed package straight from the "Available" list.</summary>
    private async Task RemoveAvailableAsync(CatalogPackageVersion? entry)
    {
        if (entry is null) return;
        await RemoveByNameAsync(entry.Name, entry.DisplayName);
    }

    private async Task RemoveByNameAsync(string name, string displayName)
    {
        if (_install is null) return;
        if (_install.Manifest.Dependencies.Remove(name))
        {
            await _projectService.SaveManifestAsync(_install.UnityProjectPath, _install.Manifest);
            _shell.SetStatus(L.Tr("packages.status.removed", displayName), StatusKind.Success);
            RefreshInstalled();
        }
    }

    private async Task AddFromGitHubAsync()
    {
        if (_install is null || !_install.HasUnityProject)
        {
            _shell.SetStatus(L.Tr("packages.status.chooseProject"), StatusKind.Error);
            return;
        }
        if (string.IsNullOrWhiteSpace(GitHubInput))
        {
            _shell.SetStatus(L.Tr("packages.status.pasteGitHub"), StatusKind.Error);
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
                _shell.SetStatus(L.Tr("packages.status.invalidGitHub", ex.Message), StatusKind.Error);
                return;
            }

            var pkg = await _githubService.FetchPackageJsonAsync(loc);
            if (pkg is null || string.IsNullOrEmpty(pkg.Name))
            {
                _shell.SetStatus(
                    L.Tr("packages.status.packageJsonLoadFailed", loc.Owner, loc.Repo, loc.Path is null ? "" : "/" + loc.Path),
                    StatusKind.Error);
                return;
            }

            var manifestUrl = GitHubService.BuildManifestUrl(loc);
            var existed = target.Manifest.Dependencies.ContainsKey(pkg.Name);
            target.Manifest.Dependencies[pkg.Name] = manifestUrl;
            // Pull in the package's registry dependencies too (Unity resolves com.unity.* itself).
            var deps = pkg.Dependencies is { Count: > 0 }
                ? AddCatalogDependencies(target, pkg.Dependencies.Select(d => (d.Key, d.Value)))
                : 0;
            await _projectService.SaveManifestAsync(target.UnityProjectPath, target.Manifest);

            var depNote = deps > 0 ? L.Tr("packages.status.depNote", deps, deps == 1 ? "" : "s") : "";
            _shell.SetStatus(L.Tr("packages.status.addedFromGitHub", existed ? L.Tr("packages.status.updated") : L.Tr("packages.status.added"), pkg.Name, depNote), StatusKind.Success);
            GitHubInput = "";
            RefreshInstalled();
        }
        catch (Exception ex)
        {
            _shell.SetStatus(L.Tr("packages.status.gitHubAddFailed", ex.Message), StatusKind.Error);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Adds a git (UPM) package to the active install's manifest — used by the website "Install in app" deep link.</summary>
    public async Task AddGitPackageAsync(string? id, string? name, string? gitUrl, string? repoUrl)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(gitUrl))
        {
            _shell.SetStatus(L.Tr("packages.status.installLinkMissing"), StatusKind.Error);
            return;
        }
        if (!GitUrlPolicy.IsSafeUrl(gitUrl))
        {
            _shell.SetStatus(L.Tr("packages.status.refusedUnsafeUrl", name ?? id), StatusKind.Error);
            return;
        }
        if (_install is null || !_install.HasUnityProject)
        {
            _shell.SetStatus(L.Tr("packages.status.openInstallFirst", name ?? id), StatusKind.Error);
            return;
        }
        try
        {
            var existed = _install.Manifest.Dependencies.ContainsKey(id);
            _install.Manifest.Dependencies[id] = gitUrl.Trim();
            // If the package is in the registry, add its Basis-ecosystem dependencies too.
            var deps = AddCatalogDependencies(_install, new[] { (id!, "*") });
            await _projectService.SaveManifestAsync(_install.UnityProjectPath, _install.Manifest);
            var depNote = deps > 0 ? L.Tr("packages.status.depNote", deps, deps == 1 ? "" : "s") : "";
            _shell.SetStatus(L.Tr("packages.status.addedDeepLink", existed ? L.Tr("packages.status.updated") : L.Tr("packages.status.added"), name ?? id, depNote, _install.Name), StatusKind.Success);
            RefreshInstalled();
        }
        catch (Exception ex)
        {
            _shell.SetStatus(L.Tr("packages.status.deepLinkFailed", ex.Message), StatusKind.Error);
        }
    }

    // ===== Bundles =====

    /// <summary>Builds a bundle from the current project's Basis + added packages and opens a GitHub issue to submit it.</summary>
    private async Task CreateBundleAsync()
    {
        if (_install is null || !_install.HasUnityProject)
        {
            _shell.SetStatus(L.Tr("packages.status.chooseProject"), StatusKind.Error);
            return;
        }
        var target = _install;

        // Basis version comes from the install's git status (already fetched for the Installs list).
        var row = _shell.InstallsVM.Installs.FirstOrDefault(r => string.Equals(r.RepoRoot, target.RepoRoot, StringComparison.OrdinalIgnoreCase));
        var branch = string.IsNullOrWhiteSpace(row?.Branch) ? null : row!.Branch;
        var commit = string.IsNullOrWhiteSpace(row?.Commit) ? null : row!.Commit;

        // Candidates = packages added on top of vanilla Basis: git deps + anything not com.unity.*.
        var candidates = new List<BundlePackage>();
        foreach (var (id, val) in target.Manifest.Dependencies)
        {
            var isVersion = IsSemverRange(val);
            if (isVersion && id.StartsWith("com.unity", StringComparison.OrdinalIgnoreCase)) continue;
            candidates.Add(new BundlePackage
            {
                Id = id,
                GitUrl = isVersion ? null : val,
                Version = isVersion ? val : null,
            });
        }
        if (candidates.Count == 0)
        {
            _shell.SetStatus(L.Tr("packages.status.noAddonPackages"), StatusKind.Info);
            return;
        }

        var basisLine = L.Tr("packages.bundle.basisLine", row?.BranchCommit is { Length: > 0 } bc ? bc : L.Tr("packages.bundle.unknownCommit"), target.UnityVersion);
        var draft = await Dialogs.CreateBundleAsync(target.DisplayName, basisLine,
            candidates.OrderByDescending(c => c.GitUrl != null).ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToList());
        if (draft is null) return;

        var bundle = new Bundle
        {
            Id = Slugify(draft.Name),
            Name = draft.Name,
            Description = draft.Description,
            Author = Environment.UserName,
            BasisBranch = branch,
            BasisCommit = commit,
            Unity = target.UnityVersion,
            Icon = "🧩",
            Packages = draft.Packages,
        };

        var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        var body = "### Bundle submission\n\nAdd this entry to `src/BasisPM.Server/seed/bundles.json`:\n\n```json\n" + json + "\n```\n";
        var url = "https://github.com/BasisVR/BasisPackageManager/issues/new?labels=bundle-submission"
                + "&title=" + Uri.EscapeDataString("Add bundle: " + draft.Name)
                + "&body=" + Uri.EscapeDataString(body);
        OpenUrl(url);
        _shell.SetStatus(L.Tr("packages.status.openingIssue", draft.Name, draft.Packages.Count), StatusKind.Success);
    }

    /// <summary>Adds every package in a bundle to the target project's manifest (used by the bundle deep link).</summary>
    public async Task AddBundleAsync(Bundle bundle, BasisInstall target)
    {
        IsBusy = true;
        try
        {
            var skipped = 0;
            foreach (var p in bundle.Packages)
            {
                if (string.IsNullOrWhiteSpace(p.Id)) continue;
                if (!string.IsNullOrWhiteSpace(p.GitUrl))
                {
                    if (!GitUrlPolicy.IsSafeUrl(p.GitUrl)) { skipped++; continue; }  // never write an unsafe transport to the manifest
                    target.Manifest.Dependencies[p.Id] = p.GitUrl!.Trim();
                }
                // Resolve the package + its registry dependencies from the catalog…
                AddCatalogDependencies(target, new[] { (p.Id, string.IsNullOrWhiteSpace(p.Version) ? "*" : p.Version!) });
                // …otherwise fall back to the declared version so Unity's registry can resolve it.
                if (!target.Manifest.Dependencies.ContainsKey(p.Id) && !string.IsNullOrWhiteSpace(p.Version))
                    target.Manifest.Dependencies[p.Id] = p.Version!.Trim();
            }
            await _projectService.SaveManifestAsync(target.UnityProjectPath, target.Manifest);
            var skipNote = skipped > 0 ? L.Tr("packages.status.skipNote", skipped) : "";
            _shell.SetStatus(L.Tr("packages.status.addedBundle", bundle.Name, bundle.Packages.Count, skipNote, target.DisplayName),
                skipped > 0 ? StatusKind.Info : StatusKind.Success);
            RefreshInstalled();
        }
        catch (Exception ex)
        {
            _shell.SetStatus(L.Tr("packages.status.bundleInstallFailed", ex.Message), StatusKind.Error);
        }
        finally { IsBusy = false; }
    }

    /// <summary>How many packages the "Basis Recommended" pack would add — the whole catalog.</summary>
    public int RecommendedCount => _catalogService.AllLatest(_catalog).Count();

    /// <summary>Installs the full "Basis Recommended" set (every catalog package) into the target project.</summary>
    public async Task InstallRecommendedAsync(BasisInstall target)
    {
        var packages = _catalogService.AllLatest(_catalog)
            .Where(v => !string.IsNullOrWhiteSpace(v.Url))
            .Select(v => new BundlePackage { Id = v.Name, Name = v.DisplayName, GitUrl = v.Url })
            .ToList();
        if (packages.Count == 0)
        {
            _shell.SetStatus(L.Tr("packages.status.recommendedEmpty"), StatusKind.Info);
            return;
        }
        var bundle = new Bundle
        {
            Id = "basis-recommended",
            Name = L.Tr("packages.recommended.name"),
            Packages = packages,
        };
        await AddBundleAsync(bundle, target);
    }

    private static string Slugify(string s)
    {
        var slug = new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length == 0 ? "bundle" : slug;
    }

    private static void OpenUrl(string url) => ExternalLink.Open(url);
}

public sealed record PackageRow(CatalogPackageVersion Entry, string? InstalledVersion, bool IsUnofficial = false)
{
    public string DisplayName => Entry.DisplayName;
    public string Name => Entry.Name;
    public string Version => Entry.Version;
    public string Description => Entry.Description;
    public bool IsInstalled => !string.IsNullOrEmpty(InstalledVersion);
    public bool IsNotInstalled => !IsInstalled;
    public string ButtonLabel => IsInstalled ? L.Tr("packages.button.update") : L.Tr("packages.button.install");
    public string Author => Entry.Author?.Name ?? "";
    public bool HasAuthor => !string.IsNullOrWhiteSpace(Entry.Author?.Name);
    public string? Unity => Entry.Unity;
    public bool HasUnity => !string.IsNullOrWhiteSpace(Entry.Unity);
    public string? License => Entry.License;
    public bool HasLicense => !string.IsNullOrWhiteSpace(Entry.License);
    public string Owner => PackagesViewModel.OwnerOf(Entry.Name);
    public string Initial => string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName.TrimStart()[..1].ToUpperInvariant();
    // Icon-tile glyph: the package's registry emoji when set, else its initial letter.
    public string TileGlyph => string.IsNullOrWhiteSpace(Entry.Icon) ? Initial : Entry.Icon!.Trim();
    public bool HasGit => !string.IsNullOrWhiteSpace(Entry.Url);
    public string? GitUrl => Entry.Url;
    public bool HasGitUrl => !string.IsNullOrWhiteSpace(Entry.Url);
    public bool HasDependencies => Entry.Dependencies is { Count: > 0 };
    public IReadOnlyList<string> DependencyList =>
        Entry.Dependencies?.Select(d => $"{d.Key}  {d.Value}").ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
    public string DependencySummary => string.Join("      ·      ", DependencyList);
    public string? Link => Entry.Link;
    public bool HasLink => !string.IsNullOrWhiteSpace(Entry.Link);
}

public sealed record InstalledPackageRow(string Name, string DisplayName, string Version, bool IsFromGit);
