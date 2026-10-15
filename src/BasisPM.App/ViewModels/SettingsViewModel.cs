using System.Collections.ObjectModel;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly UserSettingsService _settingsService;
    private readonly GitService _gitService;
    private readonly UnityHubService _hubService;
    private readonly MainWindowViewModel _shell;

    private string _catalogUrl = "";
    private string _newCatalogUrl = "";
    private string _unityHubPath = "";
    private bool _prereleaseUpdates;
    private bool _autoCheckBasisUpdates = true;
    private string _settingsPath = "";
    private string _gitDetected = "";
    private string _hubDetected = "";
    private string _commandLineToolStatus = "";
    private readonly GitCloneCache _cloneCache = new();
    private bool _useCloneCache = true;
    private long _cloneCacheBytes;
    private bool _cloneCacheBusy;
    private string _cloneCacheStatus = "";

    public string CatalogUrl { get => _catalogUrl; set => SetField(ref _catalogUrl, value); }
    // Extra, user-added catalog URLs — all UNOFFICIAL (not vetted by BasisVR).
    public ObservableCollection<CatalogUrlItem> ExtraCatalogs { get; } = new();
    // URL typed into the "add" field before it's committed to the list.
    public string NewCatalogUrl { get => _newCatalogUrl; set => SetField(ref _newCatalogUrl, value); }
    public string UnityHubPath { get => _unityHubPath; set => SetField(ref _unityHubPath, value); }
    public bool PrereleaseUpdates { get => _prereleaseUpdates; set => SetField(ref _prereleaseUpdates, value); }
    public bool AutoCheckBasisUpdates { get => _autoCheckBasisUpdates; set => SetField(ref _autoCheckBasisUpdates, value); }
    public string SettingsPath { get => _settingsPath; private set => SetField(ref _settingsPath, value); }
    public string GitDetected { get => _gitDetected; private set => SetField(ref _gitDetected, value); }
    public string HubDetected { get => _hubDetected; private set => SetField(ref _hubDetected, value); }
    public bool CanInstallCommandLineTool { get; } = CommandLineTool.CurrentTarget is not null;
    public string CommandLineToolStatus { get => _commandLineToolStatus; private set => SetField(ref _commandLineToolStatus, value); }

    public bool UseCloneCache
    {
        get => _useCloneCache;
        set { if (SetField(ref _useCloneCache, value)) _ = SaveCloneCacheAsync(value); }
    }

    public string CloneCacheFolder => _cloneCache.Root;
    public bool HasCloneCache => _cloneCacheBytes > 0;
    public bool CloneCacheBusy { get => _cloneCacheBusy; private set { if (SetField(ref _cloneCacheBusy, value)) OnPropertyChanged(nameof(CanClearCloneCache)); } }
    public bool CanClearCloneCache => HasCloneCache && !_cloneCacheBusy;
    public string CloneCacheSizeText => _cloneCacheBytes > 0 ? L.Tr("settings.cloneCache.size", DiskSpace.Human(_cloneCacheBytes)) : L.Tr("settings.cloneCache.empty");
    public string CloneCacheStatus { get => _cloneCacheStatus; private set { if (SetField(ref _cloneCacheStatus, value)) OnPropertyChanged(nameof(HasCloneCacheStatus)); } }
    public bool HasCloneCacheStatus => _cloneCacheStatus.Length > 0;

    // Language picker. Selecting a language applies and persists it immediately (live preview),
    // independent of the Save button which covers the other settings.
    public IReadOnlyList<LanguageInfo> Languages => Localizer.Instance.Available;
    private LanguageInfo? _selectedLanguage;
    public LanguageInfo? SelectedLanguage
    {
        get => _selectedLanguage;
        set { if (SetField(ref _selectedLanguage, value) && value is not null) _ = ApplyLanguageAsync(value.Code); }
    }

    public string AppVersion => _shell.AppVersion;
    public MainWindowViewModel Shell => _shell;

    /// <summary>The activity log, shown as a section on this page (merged in from the old Logs tab).</summary>
    public LogsViewModel Logs { get; }

    public RelayCommand SaveCommand { get; }
    public RelayCommand AddCatalogCommand { get; }
    public RelayCommand<CatalogUrlItem> RemoveCatalogCommand { get; }
    public RelayCommand CheckForUpdatesCommand => _shell.CheckForUpdatesCommand;
    public RelayCommand ResetCommand { get; }
    public RelayCommand ClearCloneCacheCommand { get; }
    public RelayCommand OpenCloneCacheFolderCommand { get; }
    public RelayCommand InstallCommandLineToolCommand { get; }

    public SettingsViewModel(UserSettingsService settingsService, GitService gitService, UnityHubService hubService, MainWindowViewModel shell, LogsViewModel logs)
    {
        _settingsService = settingsService;
        _gitService = gitService;
        _hubService = hubService;
        _shell = shell;
        Logs = logs;
        SettingsPath = settingsService.SettingsPath;
        SaveCommand = new RelayCommand(SaveAsync);
        AddCatalogCommand = new RelayCommand(AddCatalog);
        RemoveCatalogCommand = new RelayCommand<CatalogUrlItem>(item => { if (item is not null) ExtraCatalogs.Remove(item); });
        ResetCommand = new RelayCommand(ResetAsync);
        InstallCommandLineToolCommand = new RelayCommand(InstallCommandLineTool);
        ClearCloneCacheCommand = new RelayCommand(ClearCloneCacheAsync);
        OpenCloneCacheFolderCommand = new RelayCommand(() => ExternalLink.OpenFolder(Directory.Exists(_cloneCache.Root) ? _cloneCache.Root : AppDataPaths.LocalRoot));
        _selectedLanguage = FindLanguage(Localizer.Instance.CurrentCode);
        // Re-pull the localized "detected tooling" fallbacks when the language changes.
        Localizer.Instance.LanguageChanged += _ => RefreshDetected();
        RefreshDetected();
    }

    public void Apply(UserSettings settings)
    {
        CatalogUrl = settings.CatalogUrl;
        ExtraCatalogs.Clear();
        foreach (var u in settings.ExtraCatalogUrls)
            if (!string.IsNullOrWhiteSpace(u)) ExtraCatalogs.Add(new CatalogUrlItem(u));
        UnityHubPath = settings.UnityHubPath ?? "";
        PrereleaseUpdates = settings.PrereleaseUpdates;
        AutoCheckBasisUpdates = settings.AutoCheckBasisUpdates;
        _useCloneCache = settings.UseCloneCache;
        OnPropertyChanged(nameof(UseCloneCache));
        ApplyCloneCache();
        _ = RefreshCloneCacheAsync();
        // Reflect the persisted language in the picker without re-triggering a save.
        _selectedLanguage = FindLanguage(string.IsNullOrWhiteSpace(settings.Language) ? Localizer.Instance.CurrentCode : settings.Language!);
        OnPropertyChanged(nameof(SelectedLanguage));
        RefreshDetected();
    }

    private static LanguageInfo? FindLanguage(string code)
        => Localizer.Instance.Available.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    private async Task ApplyLanguageAsync(string code)
    {
        Localizer.Instance.SetLanguage(code);
        var settings = await _settingsService.LoadAsync();
        settings.Language = code;
        await _settingsService.SaveAsync(settings);
    }

    private void RefreshDetected()
    {
        var git = _gitService.FindGit();
        GitDetected = git ?? L.Tr("settings.detected.gitNotFound");
        var hub = _hubService.FindHubPath(string.IsNullOrWhiteSpace(UnityHubPath) ? null : UnityHubPath.Trim());
        HubDetected = hub ?? L.Tr("settings.detected.hubNotFound");
        if (CanInstallCommandLineTool) CommandLineToolStatus = CommandLineTool.InstalledLink() ?? L.Tr("settings.cli.notInstalled");
    }

    private void InstallCommandLineTool()
    {
        try
        {
            var (link, onPath) = CommandLineTool.Install();
            var folder = Path.GetDirectoryName(link) ?? link;
            _shell.SetStatus(L.Tr(onPath switch { true => "settings.cli.status.added", false => "settings.cli.status.addedNotOnPath", null => "settings.cli.status.addedCheckPath" }, folder), StatusKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticLog.Write("Adding the basispm command-line tool", ex);
            _shell.SetStatus(L.Tr("settings.cli.status.failed", ex.Message), StatusKind.Error, null, () => { InstallCommandLineTool(); return Task.CompletedTask; }, "basispm-cli");
        }
        RefreshDetected();
    }

    private async Task SaveAsync()
    {
        var settings = await _settingsService.LoadAsync();
        settings.CatalogUrl = CatalogUrl?.Trim() ?? "";
        settings.ExtraCatalogUrls = ExtraCatalogs
            .Select(c => c.Url?.Trim() ?? "")
            .Where(u => u.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.UnityHubPath = string.IsNullOrWhiteSpace(UnityHubPath) ? null : UnityHubPath.Trim();
        settings.PrereleaseUpdates = PrereleaseUpdates;
        settings.AutoCheckBasisUpdates = AutoCheckBasisUpdates;
        await _settingsService.SaveAsync(settings);

        await _shell.PackagesVM.LoadCatalogAsync(settings.CatalogUrl, settings.ExtraCatalogUrls);
        _shell.ApplyPrerelease(settings.PrereleaseUpdates);
        _shell.ApplyBasisUpdateChecks(settings.AutoCheckBasisUpdates);
        RefreshDetected();
        _shell.SetStatus(L.Tr("settings.status.saved"), StatusKind.Success);
    }

    // Danger zone: confirm, then hand off to the shell to wipe all app data and return to a first-run state.
    private async Task ResetAsync()
    {
        var confirmed = await Dialogs.ConfirmAsync(
            L.Tr("settings.reset.dialogTitle"), L.Tr("settings.reset.dialogMessage"));
        if (!confirmed) return;
        await _shell.ResetEverythingAsync();
    }

    private void ApplyCloneCache() => _gitService.CloneCache = _useCloneCache ? _cloneCache : null;

    private async Task SaveCloneCacheAsync(bool enabled)
    {
        ApplyCloneCache();
        try { await _settingsService.UpdateAsync(s => s.UseCloneCache = enabled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            DiagnosticLog.Write("Saving the clone cache setting", ex);
            _shell.SetStatus(L.Tr("settings.cloneCache.saveFailed", ex.Message), StatusKind.Error);
        }
    }

    public async Task RefreshCloneCacheAsync()
    {
        try { _cloneCacheBytes = await _cloneCache.MeasureBytesAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("Measuring the clone cache", ex);
            _cloneCacheBytes = 0;
        }
        OnPropertyChanged(nameof(HasCloneCache));
        OnPropertyChanged(nameof(CanClearCloneCache));
        OnPropertyChanged(nameof(CloneCacheSizeText));
    }

    private async Task ClearCloneCacheAsync()
    {
        if (!await Dialogs.ConfirmAsync(L.Tr("settings.cloneCache.confirmTitle"), L.Tr("settings.cloneCache.confirmBody", DiskSpace.Human(_cloneCacheBytes)))) return;
        CloneCacheBusy = true;
        try
        {
            var result = await _cloneCache.ClearAsync();
            CloneCacheStatus = result.Busy > 0
                ? L.Tr("settings.cloneCache.clearedSome", DiskSpace.Human(result.Freed), result.Busy)
                : L.Tr("settings.cloneCache.cleared", DiskSpace.Human(result.Freed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("Clearing the clone cache", ex);
            CloneCacheStatus = L.Tr("settings.cloneCache.clearFailed", ex.Message);
        }
        finally { CloneCacheBusy = false; }
        await RefreshCloneCacheAsync();
    }

    private void AddCatalog()
    {
        var url = NewCatalogUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!ExtraCatalogs.Any(c => string.Equals(c.Url?.Trim(), url, StringComparison.OrdinalIgnoreCase)))
            ExtraCatalogs.Add(new CatalogUrlItem(url));
        NewCatalogUrl = "";
    }
}

/// <summary>One user-added (unofficial) catalog URL row in Settings; editable in place.</summary>
public sealed class CatalogUrlItem : ObservableObject
{
    private string _url;
    public CatalogUrlItem(string url) => _url = url;
    public string Url { get => _url; set => SetField(ref _url, value); }
}
