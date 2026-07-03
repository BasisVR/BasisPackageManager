using System.Collections.ObjectModel;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class UnityViewModel : ObservableObject
{
    private static readonly string[] DefaultModuleOptions =
    {
        "windows-il2cpp", "android", "linux-il2cpp", "linux-mono", "mac-il2cpp", "documentation",
    };

    private readonly UnityHubService _hubService;
    private readonly UnityReleaseService _releaseService;
    private readonly MainWindowViewModel _shell;

    private string _hubStatus = "";
    private bool _isBusy;
    private bool _isLoadingReleases;
    private UnityReleaseRow? _selectedRelease;
    private string _streamFilter = "All";
    private string _requiredVersion = "";

    public ObservableCollection<InstalledEditor> InstalledEditors { get; } = new();
    public ObservableCollection<ModuleOption> ModuleOptions { get; } = new();
    public ObservableCollection<UnityReleaseRow> AvailableReleases { get; } = new();
    public ObservableCollection<string> StreamFilters { get; } = new() { "All", "LTS", "SUPPORTED", "TECH", "BETA", "ALPHA" };

    private readonly List<UnityRelease> _allReleases = new();

    public string HubStatus { get => _hubStatus; set => SetField(ref _hubStatus, value); }
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }
    public bool IsLoadingReleases { get => _isLoadingReleases; set => SetField(ref _isLoadingReleases, value); }

    public UnityReleaseRow? SelectedRelease
    {
        get => _selectedRelease;
        set => SetField(ref _selectedRelease, value);
    }

    public string StreamFilter
    {
        get => _streamFilter;
        set { if (SetField(ref _streamFilter, value)) ApplyFilter(); }
    }

    public string RequiredVersion
    {
        get => _requiredVersion;
        private set
        {
            if (SetField(ref _requiredVersion, value))
            {
                OnPropertyChanged(nameof(HasRequiredVersion));
                OnPropertyChanged(nameof(RequiredVersionNote));
            }
        }
    }

    public bool HasRequiredVersion => !string.IsNullOrEmpty(_requiredVersion) && _requiredVersion != "unknown";

    public string RequiredVersionNote => HasRequiredVersion
        ? $"Your Basis install targets Unity {_requiredVersion}. Install that exact version below for a clean open."
        : "";

    public void SetRequiredVersion(string? version)
    {
        RequiredVersion = version ?? "";
        TrySelectRequired();
    }

    private void TrySelectRequired()
    {
        if (!HasRequiredVersion || AvailableReleases.Count == 0) return;
        var match = AvailableReleases.FirstOrDefault(r => string.Equals(r.Release.Version, _requiredVersion, StringComparison.OrdinalIgnoreCase));
        if (match is not null) SelectedRelease = match;
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand ReloadReleasesCommand { get; }
    public RelayCommand<InstalledEditor> UninstallCommand { get; }

    public UnityViewModel(UnityHubService hubService, UnityReleaseService releaseService, MainWindowViewModel shell)
    {
        _hubService = hubService;
        _releaseService = releaseService;
        _shell = shell;
        RefreshCommand = new RelayCommand(RefreshAsync);
        InstallCommand = new RelayCommand(InstallAsync);
        ReloadReleasesCommand = new RelayCommand(LoadReleasesAsync);
        UninstallCommand = new RelayCommand<InstalledEditor>(UninstallAsync);
        foreach (var m in DefaultModuleOptions)
            ModuleOptions.Add(new ModuleOption(m, false));
    }

    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var hub = _hubService.FindHubPath();
            HubStatus = hub is null ? "Unity Hub not detected." : $"Unity Hub: {hub}";

            InstalledEditors.Clear();
            var list = await _hubService.ListInstalledAsync();
            foreach (var e in list) InstalledEditors.Add(e);
            if (InstalledEditors.Count == 0 && hub is not null)
                HubStatus += " (no editors installed)";

            if (_allReleases.Count == 0)
                await LoadReleasesAsync();
            else
                ApplyFilter();
        }
        catch (Exception ex)
        {
            HubStatus = $"Error: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    public async Task LoadReleasesAsync()
    {
        IsLoadingReleases = true;
        try
        {
            var fetched = await _releaseService.FetchAllAsync("6000");
            _allReleases.Clear();
            _allReleases.AddRange(fetched);
            ApplyFilter();
            TrySelectRequired();
            SelectedRelease ??= AvailableReleases.FirstOrDefault(r => !r.IsInstalled && r.Release.Recommended)
                ?? AvailableReleases.FirstOrDefault(r => !r.IsInstalled)
                ?? AvailableReleases.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _shell.SetStatus($"Could not fetch Unity releases: {ex.Message}", StatusKind.Error);
        }
        finally { IsLoadingReleases = false; }
    }

    private void ApplyFilter()
    {
        var installedSet = new HashSet<string>(InstalledEditors.Select(e => e.Version), StringComparer.Ordinal);
        var previouslySelected = SelectedRelease?.Release.Version;

        AvailableReleases.Clear();
        IEnumerable<UnityRelease> source = _allReleases;
        if (!string.Equals(StreamFilter, "All", StringComparison.OrdinalIgnoreCase))
            source = source.Where(r => string.Equals(r.Stream, StreamFilter, StringComparison.OrdinalIgnoreCase));
        foreach (var r in source)
            AvailableReleases.Add(new UnityReleaseRow(r, installedSet.Contains(r.Version)));

        if (previouslySelected is not null)
            SelectedRelease = AvailableReleases.FirstOrDefault(r => r.Release.Version == previouslySelected);
    }

    private async Task InstallAsync()
    {
        if (SelectedRelease is null)
        {
            _shell.SetStatus("Pick a Unity 6 release from the dropdown first.", StatusKind.Error);
            return;
        }
        if (SelectedRelease.IsInstalled)
        {
            _shell.SetStatus($"Unity {SelectedRelease.Release.Version} is already installed.", StatusKind.Error);
            return;
        }

        IsBusy = true;
        try
        {
            var release = SelectedRelease.Release;
            var modules = ModuleOptions.Where(m => m.Selected).Select(m => m.Name).ToList();
            _shell.SetStatus($"Installing Unity {release.Version}...");
            var code = await _hubService.InstallEditorAsync(release.Version, release.ShortRevision, modules);
            if (code == 0)
                _shell.SetStatus($"Install kicked off via Unity Hub for {release.Version}.", StatusKind.Success);
            else
                _shell.SetStatus($"Install failed (exit code {code}).", StatusKind.Error);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _shell.SetStatus($"Install error: {ex.Message}", StatusKind.Error);
        }
        finally { IsBusy = false; }
    }

    private async Task UninstallAsync(InstalledEditor? editor)
    {
        if (editor is null) return;
        IsBusy = true;
        try
        {
            _shell.SetStatus($"Uninstalling Unity {editor.Version}...");
            var code = await _hubService.UninstallEditorAsync(editor.Version);
            if (code == 0)
                _shell.SetStatus($"Uninstalled Unity {editor.Version}.", StatusKind.Success);
            else
                _shell.SetStatus($"Uninstall failed (exit code {code}).", StatusKind.Error);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _shell.SetStatus($"Uninstall error: {ex.Message}", StatusKind.Error);
        }
        finally { IsBusy = false; }
    }
}

public sealed class UnityReleaseRow : ObservableObject
{
    private bool _isInstalled;
    public UnityRelease Release { get; }
    public bool IsInstalled { get => _isInstalled; set => SetField(ref _isInstalled, value); }

    public UnityReleaseRow(UnityRelease release, bool isInstalled)
    {
        Release = release;
        _isInstalled = isInstalled;
    }
}

public sealed class ModuleOption : ObservableObject
{
    private bool _selected;
    public string Name { get; }
    public bool Selected { get => _selected; set => SetField(ref _selected, value); }
    public ModuleOption(string name, bool selected) { Name = name; _selected = selected; }
}
