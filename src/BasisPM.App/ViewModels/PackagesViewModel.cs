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
    private readonly MountRegistry _mountRegistry;
    private readonly MountService _mountService;
    private readonly ContributeService _contributeService;
    private readonly CacheDriftService _driftService;
    private readonly GitHubAuthService _ghAuth;
    private readonly GitHubApiService _ghApi;
    private readonly GitService _gitService;
    private readonly GitHubService _githubService;
    private readonly VersionService _versionService = new();
    private readonly MainWindowViewModel _shell;
    private bool _isGridView;

    private Catalog _catalog = new();
    private BasisInstall? _install;
    private string _filter = "";
    private string _githubInput = "";
    private bool _isBusy;
    private bool _showInstalledOnly;
    private string _selectedSource = "all";
    private string _selectedCategory = "all";
    private string _sortKey = "popular";
    private BasisInstall? _selectedInstall;
    private bool _syncingSelection;
    // Configured catalog sources, remembered so Refresh reloads exactly what's configured.
    private string? _officialCatalogUrl;
    private IReadOnlyList<string> _extraCatalogUrls = Array.Empty<string>();
    // Package ids that came only from an unofficial (extra) catalog — drives the "Unofficial" badge.
    private readonly HashSet<string> _unofficialIds = new(StringComparer.OrdinalIgnoreCase);
    // Package ids mounted locally for editing in the active project — present as a folder in Packages/
    // (or a "file:" manifest dep), not the registry git URL, so they read as "Locally mounted" rather
    // than "available to install".
    private readonly HashSet<string> _mountedIds = new(StringComparer.OrdinalIgnoreCase);
    // Mount records for the active project (id → folder + original manifest value) so a package row
    // can Open folder / Swap back / Submit PR without re-reading the registry each time.
    private readonly Dictionary<string, MountRecord> _mounts = new(StringComparer.OrdinalIgnoreCase);
    // Ids whose local working copy has uncommitted changes — a dirty mounted clone, or accidental
    // edits in Library/PackageCache (cache drift). Drives the amber "edited" row; filled in by a
    // background git pass so it stays out of the synchronous list build.
    private readonly HashSet<string> _mountEditedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CacheDrift> _drift = new(StringComparer.OrdinalIgnoreCase);
    private Avalonia.Threading.DispatcherTimer? _editTimer;
    private bool _scanningEdits;
    private bool _scanningDrift;

    public ObservableCollection<PackageRow> Available { get; } = new();
    public ObservableCollection<BasisInstall> InstallOptions { get; } = new();

    // Website-style filter facets: Source + Category chips (each with a count) rebuilt whenever the
    // catalog loads; the sort dropdown mirrors the website's. Selections live in _selectedSource /
    // _selectedCategory / _sortKey and drive Refilter.
    public ObservableCollection<FacetChip> SourceFacets { get; } = new();
    public ObservableCollection<FacetChip> CategoryFacets { get; } = new();
    public bool HasCategoryFacets => CategoryFacets.Count > 1;
    public IReadOnlyList<SortOption> SortOptions { get; }
    private SortOption _selectedSort;
    public SortOption SelectedSort
    {
        get => _selectedSort;
        set { if (SetField(ref _selectedSort, value) && value is not null) { _sortKey = value.Key; Refilter(); } }
    }

    public string Filter { get => _filter; set { if (SetField(ref _filter, value)) Refilter(); } }
    public string GitHubInput { get => _githubInput; set => SetField(ref _githubInput, value); }
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }
    public bool ShowInstalledOnly
    {
        get => _showInstalledOnly;
        set
        {
            if (!SetField(ref _showInstalledOnly, value)) return;
            OnPropertyChanged(nameof(InstalledToggleLabel));
            OnPropertyChanged(nameof(ListHeaderLabel));
            OnPropertyChanged(nameof(ShowInstalledEmptyHint));
            Refilter();
        }
    }
    public string InstalledToggleLabel => _showInstalledOnly ? L.Tr("packages.button.showAll") : L.Tr("packages.button.showInstalled");
    public string ListHeaderLabel => _showInstalledOnly ? L.Tr("packages.header.installed") : L.Tr("packages.header.available");
    public bool ShowInstalledEmptyHint => _showInstalledOnly && Available.Count == 0;

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
    public RelayCommand ToggleInstalledCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand AddFromGitHubCommand { get; }
    public RelayCommand CreatePackageListCommand { get; }
    public RelayCommand InstallPackageListFromFileCommand { get; }
    public RelayCommand<CatalogPackageVersion> ChooseVersionCommand { get; }
    public RelayCommand ToggleLayoutCommand { get; }
    public RelayCommand<FacetChip> SelectFacetCommand { get; }
    public RelayCommand<string> OpenLinkCommand { get; }
    public RelayCommand CloseDetailCommand { get; }
    // Mount workflow (formerly the Develop tab): act on a package straight from its row/detail.
    public RelayCommand<PackageRow> MountCommand { get; }
    public RelayCommand<PackageRow> SwapBackCommand { get; }
    public RelayCommand<PackageRow> SubmitPrCommand { get; }
    public RelayCommand<PackageRow> OpenFolderCommand { get; }
    public RelayCommand<PackageRow> ReviewDriftCommand { get; }
    public RelayCommand InstallGitCommand { get; }

    public PackagesViewModel(UserSettingsService settingsService, CatalogService catalogService, UnityProjectService projectService,
        MountRegistry mountRegistry, MountService mountService, ContributeService contributeService, CacheDriftService driftService,
        GitHubAuthService ghAuth, GitHubApiService ghApi, GitService gitService, MainWindowViewModel shell)
    {
        _settingsService = settingsService;
        _catalogService = catalogService;
        _projectService = projectService;
        _mountRegistry = mountRegistry;
        _mountService = mountService;
        _contributeService = contributeService;
        _driftService = driftService;
        _ghAuth = ghAuth;
        _ghApi = ghApi;
        _gitService = gitService;
        _githubService = new GitHubService();
        _shell = shell;

        InstallCommand = new RelayCommand<CatalogPackageVersion>(InstallCuratedAsync);
        RemoveCommand = new RelayCommand<CatalogPackageVersion>(RemoveAvailableAsync);
        ToggleInstalledCommand = new RelayCommand(() => ShowInstalledOnly = !ShowInstalledOnly);
        RefreshCommand = new RelayCommand(RefreshAsync);
        AddFromGitHubCommand = new RelayCommand(AddFromGitHubAsync);
        CreatePackageListCommand = new RelayCommand(CreatePackageListAsync);
        InstallPackageListFromFileCommand = new RelayCommand(InstallPackageListFromFileAsync);
        ChooseVersionCommand = new RelayCommand<CatalogPackageVersion>(ChooseVersionAsync);
        ToggleLayoutCommand = new RelayCommand(() => IsGridView = !IsGridView);
        SelectFacetCommand = new RelayCommand<FacetChip>(SelectFacet);
        SortOptions = new List<SortOption>
        {
            new("popular", L.Tr("packages.sort.popular")),
            new("stars",   L.Tr("packages.sort.stars")),
            new("forks",   L.Tr("packages.sort.forks")),
            new("updated", L.Tr("packages.sort.updated")),
            new("name",    L.Tr("packages.sort.name")),
        };
        _selectedSort = SortOptions[0];
        OpenLinkCommand = new RelayCommand<string>(url => { if (!string.IsNullOrWhiteSpace(url)) ExternalLink.Open(url!); });
        CloseDetailCommand = new RelayCommand(() => SelectedPackage = null);
        MountCommand = new RelayCommand<PackageRow>(MountAsync);
        SwapBackCommand = new RelayCommand<PackageRow>(SwapBackAsync);
        SubmitPrCommand = new RelayCommand<PackageRow>(SubmitPrAsync);
        OpenFolderCommand = new RelayCommand<PackageRow>(OpenMountFolder);
        ReviewDriftCommand = new RelayCommand<PackageRow>(ReviewDriftAsync);
        InstallGitCommand = new RelayCommand(() => ExternalLink.Open("https://git-scm.com/downloads"));
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
            BuildFacets();
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

        // Catalog packages get the full website-style treatment: search across name/id/description/
        // author/tags, the Source and Category facets, then the chosen sort (mirrors PackageStore.Query).
        var catalog = _catalogService.AllLatest(_catalog).Where(v =>
            SearchMatches(v, f)
            && (_selectedSource == "all" || string.Equals(v.Source, _selectedSource, StringComparison.OrdinalIgnoreCase))
            && (_selectedCategory == "all" || string.Equals(v.Category, _selectedCategory, StringComparison.OrdinalIgnoreCase)));

        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in SortEntries(catalog, _sortKey))
        {
            var installedVersion = _install?.Manifest.Dependencies.GetValueOrDefault(v.Name);
            if (_showInstalledOnly && installedVersion is null && !_mountedIds.Contains(v.Name)) continue;
            _mounts.TryGetValue(v.Name, out var rec);
            Available.Add(new PackageRow(v, installedVersion, _unofficialIds.Contains(v.Name),
                _mountedIds.Contains(v.Name), rec?.FolderPath, _mountEditedIds.Contains(v.Name)));
            shown.Add(v.Name);
        }

        // Packages the catalog doesn't list but the project mounts or pulls straight from git still get
        // a row — so mounting, Open folder / Swap back / Submit PR and the amber "edited" state work for
        // community git deps too, not just registry packages. (These ids are kept out of the Installed
        // expander in RefreshInstalled, so a package never shows in both places.)
        foreach (var id in SyntheticRowIds())
        {
            if (shown.Contains(id) || !TextMatches(id, f)) continue;
            var installedVersion = _install?.Manifest.Dependencies.GetValueOrDefault(id);
            var gitUrl = installedVersion is not null && UpmGitUrl.Parse(installedVersion) is not null
                ? installedVersion
                : _mounts.TryGetValue(id, out var r) ? r.OriginalManifestValue : null;
            var entry = new CatalogPackageVersion { Name = id, DisplayName = id, Version = "local", Description = "", Url = gitUrl };
            Available.Add(new PackageRow(entry, installedVersion, isUnofficial: false, isMounted: _mountedIds.Contains(id),
                mountFolder: _mounts.TryGetValue(id, out var rec2) ? rec2.FolderPath : null, mountedHasEdits: _mountEditedIds.Contains(id)));
            shown.Add(id);
        }

        // Re-apply the drift flag (a separate, async signal) after the rows are rebuilt.
        foreach (var row in Available) row.HasDrift = _drift.ContainsKey(row.Name);

        // Keep an open detail panel pointed at the refreshed row so its state stays current.
        if (_selectedPackage is not null)
        {
            var match = Available.FirstOrDefault(r => string.Equals(r.Name, _selectedPackage.Name, StringComparison.Ordinal));
            if (match is not null) SelectedPackage = match;
        }

        OnPropertyChanged(nameof(ShowInstalledEmptyHint));
    }

    // Ids that deserve an Available row despite not being in the catalog: every mounted package, plus
    // any manifest dependency pulled straight from git (or a local file:) — the community packages the
    // former Develop tab let you mount.
    private IEnumerable<string> SyntheticRowIds()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _mounts.Keys)
            if (!_catalog.Packages.ContainsKey(id) && seen.Add(id)) yield return id;
        if (_install is null || !_install.HasUnityProject) yield break;
        foreach (var (id, val) in _install.Manifest.Dependencies)
            if (!_catalog.Packages.ContainsKey(id) && LooksMountable(val) && seen.Add(id)) yield return id;
    }

    // Rebuilds the Source + Category filter chips from the whole catalog. Counts are over every package,
    // independent of the active search/sort (matching the website). Keeps the current selection when that
    // value still exists after a reload, otherwise falls back to "all".
    private void BuildFacets()
    {
        var all = _catalogService.AllLatest(_catalog).ToList();

        SourceFacets.Clear();
        SourceFacets.Add(new FacetChip("source", "all", L.Tr("packages.source.all"), all.Count));
        foreach (var g in all.Where(v => !string.IsNullOrWhiteSpace(v.Source))
                             .GroupBy(v => v.Source!.Trim(), StringComparer.OrdinalIgnoreCase)
                             .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            SourceFacets.Add(new FacetChip("source", g.Key, SourceLabel(g.Key), g.Count()));

        CategoryFacets.Clear();
        CategoryFacets.Add(new FacetChip("category", "all", L.Tr("packages.category.all"), all.Count));
        foreach (var g in all.Where(v => !string.IsNullOrWhiteSpace(v.Category))
                             .GroupBy(v => v.Category!.Trim(), StringComparer.OrdinalIgnoreCase)
                             .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            CategoryFacets.Add(new FacetChip("category", g.Key, g.Key, g.Count()));

        // A previously-selected facet may have vanished after a catalog reload — fall back to "all".
        if (!SourceFacets.Any(c => string.Equals(c.Key, _selectedSource, StringComparison.OrdinalIgnoreCase)))
            _selectedSource = "all";
        if (!CategoryFacets.Any(c => string.Equals(c.Key, _selectedCategory, StringComparison.OrdinalIgnoreCase)))
            _selectedCategory = "all";
        foreach (var c in SourceFacets) c.IsSelected = string.Equals(c.Key, _selectedSource, StringComparison.OrdinalIgnoreCase);
        foreach (var c in CategoryFacets) c.IsSelected = string.Equals(c.Key, _selectedCategory, StringComparison.OrdinalIgnoreCase);

        OnPropertyChanged(nameof(HasCategoryFacets));
    }

    private void SelectFacet(FacetChip? chip)
    {
        if (chip is null) return;
        if (string.Equals(chip.Kind, "source", StringComparison.Ordinal)) SetSource(chip.Key);
        else SetCategory(chip.Key);
    }

    private void SetSource(string key)
    {
        if (string.Equals(_selectedSource, key, StringComparison.OrdinalIgnoreCase)) return;
        _selectedSource = key;
        foreach (var c in SourceFacets) c.IsSelected = string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase);
        Refilter();
    }

    private void SetCategory(string key)
    {
        if (string.Equals(_selectedCategory, key, StringComparison.OrdinalIgnoreCase)) return;
        _selectedCategory = key;
        foreach (var c in CategoryFacets) c.IsSelected = string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase);
        Refilter();
    }

    // Known provenance values get a friendly localized label; anything else shows as-is.
    private static string SourceLabel(string source) => source.Trim().ToLowerInvariant() switch
    {
        "official" => L.Tr("packages.source.official"),
        "community" => L.Tr("packages.source.community"),
        "built-in" or "builtin" => L.Tr("packages.source.builtin"),
        _ => source.Trim(),
    };

    // Search parity with the website / server Query: name, id, description, author and tags.
    private static bool SearchMatches(CatalogPackageVersion v, string f)
    {
        if (f.Length == 0) return true;
        return v.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase)
            || v.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || (v.Description?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
            || (v.Author?.Name?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
            || (v.Tags?.Any(t => t.Contains(f, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    private static bool TextMatches(string text, string f) =>
        f.Length == 0 || text.Contains(f, StringComparison.OrdinalIgnoreCase);

    // Sort keys mirror PackageStore.Query: popular (stars, then forks), stars, forks, updated, name.
    private static IEnumerable<CatalogPackageVersion> SortEntries(IEnumerable<CatalogPackageVersion> q, string sort) => sort switch
    {
        "stars" => q.OrderByDescending(v => v.Stars),
        "forks" => q.OrderByDescending(v => v.Forks),
        "updated" => q.OrderByDescending(v => v.Updated ?? "", StringComparer.Ordinal),
        "name" => q.OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase),
        _ => q.OrderByDescending(v => v.Stars).ThenByDescending(v => v.Forks),
    };

    private void RefreshInstalled()
    {
        _mountedIds.Clear();
        _mounts.Clear();
        if (_install is null || !_install.HasUnityProject) { Refilter(); OnPropertyChanged(nameof(GitMissing)); return; }

        // Packages mounted for editing are present as a local folder, not the registry git URL: a
        // root-level mount drops the manifest line entirely (cloned into Packages/<id>/), and a
        // subfolder mount rewrites it to a "file:" dep. Track both from the mount registry (+ any
        // stray file: dep) so they surface as mounted rows in the Available list.
        foreach (var rec in _mountRegistry.ForInstall(_install.UnityProjectPath))
        {
            _mounts[rec.PackageId] = rec;
            _mountedIds.Add(rec.PackageId);
        }

        foreach (var (name, version) in _install.Manifest.Dependencies)
            if (version.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) _mountedIds.Add(name);

        Refilter();
        OnPropertyChanged(nameof(GitMissing));
        StartEditScan();
    }

    /// <summary>Vendor/owner from a reverse-DNS package id: com.unity.2d.sprite → "Unity".</summary>
    internal static string OwnerOf(string id)
    {
        var parts = (id ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        var owner = parts.Length >= 2 ? parts[1] : parts.FirstOrDefault() ?? "";
        if (owner.Length == 0) return L.Tr("packages.filter.otherOwner");
        return char.ToUpperInvariant(owner[0]) + owner[1..];
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

    // ===== Mount workflow (absorbed from the former Develop tab) =====

    /// <summary>True when git isn't on PATH — mounting and PRs won't work, so a banner offers to install it.</summary>
    public bool GitMissing => !_gitService.IsAvailable;

    // A manifest value we can mount: a git URL (clone + swap) or an existing local "file:" dep.
    private static bool LooksMountable(string? manifestValue) =>
        !string.IsNullOrWhiteSpace(manifestValue) &&
        (manifestValue!.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || UpmGitUrl.Parse(manifestValue) is not null);

    /// <summary>Clones a git package into the project for editing and swaps the manifest over to it.</summary>
    private async Task MountAsync(PackageRow? row)
    {
        if (row is null || _install is null || !_install.HasUnityProject) return;
        if (!_gitService.IsAvailable) { _shell.SetStatus(L.Tr("develop.status.gitRequiredMount"), StatusKind.Error); return; }
        if (!_install.Manifest.Dependencies.TryGetValue(row.Name, out var gitValue) || !LooksMountable(gitValue))
        {
            _shell.SetStatus(L.Tr("packages.status.notGitPackage", row.DisplayName), StatusKind.Info);
            return;
        }

        // If Packages/<id> already exists (a leftover or a local copy), ask before replacing it.
        var dest = Path.Combine(_install.UnityProjectPath, "Packages", row.Name);
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
        {
            if (!await Dialogs.ConfirmAsync(L.Tr("develop.confirm.packageThereTitle"), L.Tr("develop.confirm.packageExists", row.Name)))
            {
                _shell.SetStatus(L.Tr("develop.status.mountCancelled", row.Name), StatusKind.Info);
                return;
            }
            try { TryForceDelete(dest); }
            catch (Exception ex) { _shell.SetStatus(L.Tr("develop.status.clearFailed", row.Name, ex.Message), StatusKind.Error); return; }
        }

        IsBusy = true;
        _shell.SetStatus(L.Tr("develop.status.mounting", row.Name));
        try
        {
            var result = await _mountService.MountAsync(_install, row.Name, gitValue,
                line => Avalonia.Threading.Dispatcher.UIThread.Post(() => _shell.SetStatus(line)));
            if (result.Ok) { _shell.SetStatus(L.Tr("develop.status.mounted", row.Name), StatusKind.Success); await ReloadInstalledAsync(); }
            else _shell.SetStatus(result.Error ?? L.Tr("develop.status.mountFailed"), StatusKind.Error);
        }
        catch (Exception ex) { _shell.SetStatus(L.Tr("develop.status.mountError", ex.Message), StatusKind.Error); }
        finally { IsBusy = false; }
    }

    /// <summary>Restores a mounted package's original manifest entry and removes the working clone.</summary>
    private async Task SwapBackAsync(PackageRow? row)
    {
        if (row is null || _install is null) return;
        IsBusy = true;
        _shell.SetStatus(L.Tr("develop.status.swapping", row.Name));
        try
        {
            var result = await _mountService.SwapBackAsync(_install, row.Name);
            if (result.Ok) { _shell.SetStatus(L.Tr("develop.status.swappedBack", row.Name), StatusKind.Success); await ReloadInstalledAsync(); }
            else _shell.SetStatus(result.Error ?? L.Tr("develop.status.swapBackFailed"), StatusKind.Error);
        }
        catch (Exception ex) { _shell.SetStatus(L.Tr("develop.status.swapBackError", ex.Message), StatusKind.Error); }
        finally { IsBusy = false; }
    }

    /// <summary>Reveals a mounted package's working-clone folder in the OS file manager.</summary>
    private void OpenMountFolder(PackageRow? row)
    {
        var folder = row?.MountFolder;
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) ExternalLink.OpenFolder(folder);
        else if (row is not null) _shell.SetStatus(L.Tr("packages.status.mountFolderMissing", row.DisplayName), StatusKind.Error);
    }

    /// <summary>Opens a pull request from a mounted package's working clone.</summary>
    private async Task SubmitPrAsync(PackageRow? row)
    {
        if (row is null) return;
        if (string.IsNullOrEmpty(row.MountFolder)) { _shell.SetStatus(L.Tr("packages.status.mountFolderMissing", row.DisplayName), StatusKind.Error); return; }
        await SubmitPrFromFolderAsync(row.Name, row.MountFolder);
    }

    /// <summary>Turns accidental Library/PackageCache edits into a PR, reusing the mounted-package PR flow.</summary>
    private async Task ReviewDriftAsync(PackageRow? row)
    {
        if (row is null || !_drift.TryGetValue(row.Name, out var drift)) return;
        await SubmitPrFromFolderAsync(row.Name, drift.WorkClonePath);
    }

    private async Task SubmitPrFromFolderAsync(string packageId, string folderPath)
    {
        if (!_gitService.IsAvailable) { _shell.SetStatus(L.Tr("develop.status.gitRequired"), StatusKind.Error); return; }

        // Nothing to PR if the working clone is clean — bail before sign-in and a fork.
        var status = await _gitService.GetStatusAsync(folderPath);
        if (status.ChangeCount == 0) { _shell.SetStatus(L.Tr("develop.status.noChangesToSubmit", packageId), StatusKind.Info); return; }

        var token = await GetOrPromptTokenAsync();
        if (string.IsNullOrEmpty(token)) { _shell.SetStatus(L.Tr("develop.status.signInFirst"), StatusKind.Error); return; }
        var user = await _ghApi.GetUserAsync(token);
        if (user is null) { _shell.SetStatus(L.Tr("develop.status.loginUnverified"), StatusKind.Error); return; }

        var draft = await Dialogs.SubmitPrAsync(packageId);
        if (draft is null) return;

        IsBusy = true;
        _shell.SetStatus(L.Tr("develop.status.submittingPr", packageId));
        try
        {
            var result = await _contributeService.SubmitPrAsync(folderPath, token, user, draft,
                line => Avalonia.Threading.Dispatcher.UIThread.Post(() => _shell.SetStatus(line)));
            if (result.Ok)
            {
                _shell.SetStatus(L.Tr("develop.status.prOpened", result.Forked ? L.Tr("develop.status.viaFork") : "", result.PrUrl), StatusKind.Success);
                if (!string.IsNullOrEmpty(result.PrUrl)) ExternalLink.Open(result.PrUrl!);
            }
            else _shell.SetStatus(result.Error ?? L.Tr("develop.status.prFailed"), StatusKind.Error);
        }
        catch (Exception ex) { _shell.SetStatus(L.Tr("develop.status.prError", ex.Message), StatusKind.Error); }
        finally { IsBusy = false; }
    }

    // Uses the gh CLI token if the user is signed in there; otherwise prompts once for a PAT.
    private async Task<string?> GetOrPromptTokenAsync()
    {
        var token = await _ghAuth.GetTokenAsync();
        if (!string.IsNullOrEmpty(token)) return token;
        var pat = await Dialogs.SignInAsync();
        if (string.IsNullOrWhiteSpace(pat)) return null;
        _ghAuth.SetPersonalAccessToken(pat);
        return await _ghAuth.GetTokenAsync();
    }

    // Re-reads the manifest from disk (mount/swap rewrote it) before rebuilding the lists.
    private async Task ReloadInstalledAsync()
    {
        if (_install is not null && _install.HasUnityProject)
        {
            try { var reloaded = await _projectService.LoadAsync(_install.UnityProjectPath); _install.Manifest = reloaded.Manifest; }
            catch { }
        }
        RefreshInstalled();
    }

    // git marks objects under .git read-only; clear attributes before deleting the clone.
    private static void TryForceDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(path, recursive: true);
    }

    // Kick off the background change scans and keep the cheap one polling so rows go amber as the user
    // edits mounted packages in Unity.
    private void StartEditScan()
    {
        _ = ScanMountEditsAsync();
        _ = ScanDriftAsync();
        if (_editTimer is null)
        {
            _editTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            _editTimer.Tick += (_, _) => { if (!_scanningEdits) _ = ScanMountEditsAsync(); };
        }
        _editTimer.Start();
    }

    // Cheap pass (a git status per mount): flags mounted clones with uncommitted edits so their rows
    // turn amber. Safe to run on the timer.
    private async Task ScanMountEditsAsync()
    {
        if (_scanningEdits || _install is null || !_gitService.IsAvailable || _mounts.Count == 0) return;
        _scanningEdits = true;
        try
        {
            var install = _install;
            var edited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in _mounts.Values.ToList())
            {
                try
                {
                    if (string.IsNullOrEmpty(rec.FolderPath) || !Directory.Exists(rec.FolderPath)) continue;
                    var status = await _gitService.GetStatusAsync(rec.FolderPath);
                    if (status.ChangeCount > 0) edited.Add(rec.PackageId);
                }
                catch { }
            }
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(install, _install)) return;   // project switched mid-scan
                _mountEditedIds.Clear();
                foreach (var id in edited) _mountEditedIds.Add(id);
                foreach (var r in Available) if (r.IsMounted) r.MountedHasEdits = _mountEditedIds.Contains(r.Name);
            });
        }
        finally { _scanningEdits = false; }
    }

    // Heavier pass (clones changed packages to temp), so it runs on activate/refresh only, not the
    // timer: surfaces accidental edits made directly in Library/PackageCache as an amber row + a
    // "Review changes & open PR" action.
    private async Task ScanDriftAsync()
    {
        if (_scanningDrift || _install is null || !_install.HasUnityProject || !_gitService.IsAvailable) return;
        _scanningDrift = true;
        try
        {
            var install = _install;
            var drifts = await _driftService.ScanAsync(install);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(install, _install)) return;
                _drift.Clear();
                foreach (var d in drifts) _drift[d.PackageId] = d;
                foreach (var r in Available) r.HasDrift = _drift.ContainsKey(r.Name);
            });
        }
        catch { }
        finally { _scanningDrift = false; }
    }

    // ===== Package lists =====

    /// <summary>Builds a package list from the current project's Basis + added packages and opens a GitHub issue to submit it.</summary>
    private async Task CreatePackageListAsync()
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
        var candidates = new List<PackageListEntry>();
        foreach (var (id, val) in target.Manifest.Dependencies)
        {
            var isVersion = IsSemverRange(val);
            if (isVersion && id.StartsWith("com.unity", StringComparison.OrdinalIgnoreCase)) continue;
            candidates.Add(new PackageListEntry
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

        var basisLine = L.Tr("packages.packageList.basisLine", row?.BranchCommit is { Length: > 0 } bc ? bc : L.Tr("packages.packageList.unknownCommit"), target.UnityVersion);
        var draft = await Dialogs.CreatePackageListAsync(target.DisplayName, basisLine,
            candidates.OrderByDescending(c => c.GitUrl != null).ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToList());
        if (draft is null) return;

        var packageList = new PackageList
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

        var json = JsonSerializer.Serialize(packageList, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });

        // Local: write the package-list JSON to a file the user keeps — no submission.
        if (draft.Destination == PackageListDestination.SaveToFile)
        {
            var savedPath = await Dialogs.SavePackageListFileAsync(packageList.Id, json);
            if (savedPath is not null)
                _shell.SetStatus(L.Tr("packages.status.packageListSaved", draft.Name, draft.Packages.Count, savedPath), StatusKind.Success);
            return;
        }

        var body = "### Package list submission\n\nAdd this entry to `src/BasisPM.Server/seed/packagelists.json`:\n\n```json\n" + json + "\n```\n";
        var url = "https://github.com/BasisVR/BasisPackageManager/issues/new?labels=packagelist-submission"
                + "&title=" + Uri.EscapeDataString("Add package list: " + draft.Name)
                + "&body=" + Uri.EscapeDataString(body);
        OpenUrl(url);
        _shell.SetStatus(L.Tr("packages.status.openingIssue", draft.Name, draft.Packages.Count), StatusKind.Success);
    }

    /// <summary>Reads a local package-list file (as saved by "Save to file") and installs its packages into a chosen project.</summary>
    private async Task InstallPackageListFromFileAsync()
    {
        var path = await Dialogs.OpenPackageListFileAsync();
        if (string.IsNullOrEmpty(path)) return;

        PackageList? packageList;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            packageList = JsonSerializer.Deserialize<PackageList>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _shell.SetStatus(L.Tr("packages.status.packageListFileInvalid", ex.Message), StatusKind.Error);
            return;
        }

        if (packageList is null || packageList.Packages.Count == 0)
        {
            _shell.SetStatus(L.Tr("packages.status.packageListFileEmpty"), StatusKind.Error);
            return;
        }

        var target = await _shell.ChooseInstallTargetAsync(L.Tr("shell.packageList.pickerLabel", packageList.Name, packageList.Packages.Count));
        if (target is null) return;

        _shell.SetActiveInstall(target);
        await AddPackageListAsync(packageList, target);
    }

    /// <summary>Adds every package in a package list to the target project's manifest (used by the package-list deep link).</summary>
    public async Task AddPackageListAsync(PackageList packageList, BasisInstall target)
    {
        IsBusy = true;
        try
        {
            var skipped = 0;
            foreach (var p in packageList.Packages)
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
            _shell.SetStatus(L.Tr("packages.status.addedPackageList", packageList.Name, packageList.Packages.Count, skipNote, target.DisplayName),
                skipped > 0 ? StatusKind.Info : StatusKind.Success);
            RefreshInstalled();
        }
        catch (Exception ex)
        {
            _shell.SetStatus(L.Tr("packages.status.packageListInstallFailed", ex.Message), StatusKind.Error);
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
            .Select(v => new PackageListEntry { Id = v.Name, Name = v.DisplayName, GitUrl = v.Url })
            .ToList();
        if (packages.Count == 0)
        {
            _shell.SetStatus(L.Tr("packages.status.recommendedEmpty"), StatusKind.Info);
            return;
        }
        var packageList = new PackageList
        {
            Id = "basis-recommended",
            Name = L.Tr("packages.recommended.name"),
            Packages = packages,
        };
        await AddPackageListAsync(packageList, target);
    }

    private static string Slugify(string s)
    {
        var slug = new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length == 0 ? "packagelist" : slug;
    }

    private static void OpenUrl(string url) => ExternalLink.Open(url);
}

public sealed class PackageRow : ObservableObject
{
    public PackageRow(CatalogPackageVersion entry, string? installedVersion, bool isUnofficial = false,
        bool isMounted = false, string? mountFolder = null, bool mountedHasEdits = false)
    {
        Entry = entry;
        InstalledVersion = installedVersion;
        IsUnofficial = isUnofficial;
        IsMounted = isMounted;
        MountFolder = mountFolder;
        _mountedHasEdits = mountedHasEdits;
    }

    public CatalogPackageVersion Entry { get; }
    public string? InstalledVersion { get; }
    public bool IsUnofficial { get; }
    public bool IsMounted { get; }
    // The mounted working-clone folder (Packages/<id> or .basisdev/<id>); null when not mounted.
    public string? MountFolder { get; }
    public bool HasMountFolder => !string.IsNullOrWhiteSpace(MountFolder);

    // Uncommitted edits in the mounted working clone, and accidental Library/PackageCache edits — both
    // observable, set by a background scan after the list is built, so the row re-tints amber live.
    private bool _mountedHasEdits;
    public bool MountedHasEdits
    {
        get => _mountedHasEdits;
        set { if (SetField(ref _mountedHasEdits, value)) { OnPropertyChanged(nameof(IsChanged)); OnPropertyChanged(nameof(MountedStateLabel)); } }
    }
    private bool _hasDrift;
    public bool HasDrift
    {
        get => _hasDrift;
        set { if (SetField(ref _hasDrift, value)) OnPropertyChanged(nameof(IsChanged)); }
    }
    // The row goes amber when the local copy has changes (a dirty mount or cache drift).
    public bool IsChanged => MountedHasEdits || HasDrift;

    public string DisplayName => Entry.DisplayName;
    public string Name => Entry.Name;
    public string Version => Entry.Version;
    public string Description => Entry.Description;
    public bool IsInstalled => !string.IsNullOrEmpty(InstalledVersion);
    public bool IsNotInstalled => !IsInstalled;
    public string? InstalledLabel
    {
        get
        {
            var v = InstalledVersion;
            if (string.IsNullOrEmpty(v) || v.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
            var git = UpmGitUrl.Parse(v);
            if (git is not null) return git.Ref ?? L.Tr("packages.version.defaultBranch");
            return v;
        }
    }
    public bool HasInstalledVersion => !IsMounted && !string.IsNullOrEmpty(InstalledLabel);
    public string InstalledVersionText => L.Tr("packages.version.installed", InstalledLabel ?? "");
    // The action-area states are mutually exclusive. A package mounted for editing is present as a
    // local folder (not the registry git URL), so it's neither "install" nor "manage" — it shows the
    // mount actions (Open folder / Swap back / Submit PR) and goes amber once it has local edits.
    public bool IsAvailableToInstall => !IsInstalled && !IsMounted;
    public bool IsManageable => IsInstalled && !IsMounted;
    // Installed straight from git (there's a URL to clone + swap) and not already mounted → mountable.
    public bool CanMountToEdit => IsInstalled && !IsMounted && InstalledVersion is not null && UpmGitUrl.Parse(InstalledVersion) is not null;
    public bool CanChooseVersion => HasGit && !IsMounted;
    public string MountedLabel => L.Tr("packages.state.mounted");
    // The inline mounted pill: "Locally mounted", or "Local edits" once the working clone is dirty.
    public string MountedStateLabel => MountedHasEdits ? L.Tr("packages.state.mountedEdited") : L.Tr("packages.state.mounted");
    public string MountedHint => L.Tr("packages.mounted.hint");
    public string ButtonLabel => IsInstalled ? L.Tr("packages.button.update") : L.Tr("packages.button.install");
    public string Author => Entry.Author?.Name ?? "";
    public bool HasAuthor => !string.IsNullOrWhiteSpace(Entry.Author?.Name);
    public string? Unity => Entry.Unity;
    public bool HasUnity => !string.IsNullOrWhiteSpace(Entry.Unity);
    public string? License => Entry.License;
    public bool HasLicense => !string.IsNullOrWhiteSpace(Entry.License);
    // Registry metadata surfaced on the row: a category pill and (when known) a GitHub star count.
    public string? Category => Entry.Category;
    public bool HasCategory => !string.IsNullOrWhiteSpace(Entry.Category);
    public int Stars => Entry.Stars;
    public string StarsText => Entry.Stars.ToString("N0");
    public bool HasStars => Entry.Stars > 0;
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

/// <summary>One Source/Category filter chip: a label, a live count, and whether it's the active pick.</summary>
public sealed class FacetChip : ObservableObject
{
    public FacetChip(string kind, string key, string label, int count)
    {
        Kind = kind; Key = key; Label = label; Count = count;
    }
    public string Kind { get; }   // "source" | "category" — which facet row this chip belongs to
    public string Key { get; }    // the value to filter by ("all", "official", "Rendering", …)
    public string Label { get; }
    public int Count { get; }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
}

/// <summary>One entry in the sort dropdown: a stable key and its localized label.</summary>
public sealed record SortOption(string Key, string Label);
