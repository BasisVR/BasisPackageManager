using BasisPM.App.Localization;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly UserSettingsService _settingsService;
    private readonly GitService _gitService;
    private readonly UnityHubService _hubService;
    private readonly MainWindowViewModel _shell;

    private string _clonePath = "";
    private string _catalogUrl = "";
    private string _unityHubPath = "";
    private bool _showLocalChanges;
    private bool _developerMode;
    private bool _prereleaseUpdates;
    private string _settingsPath = "";
    private string _gitDetected = "";
    private string _hubDetected = "";

    public string ClonePath { get => _clonePath; set => SetField(ref _clonePath, value); }
    public string CatalogUrl { get => _catalogUrl; set => SetField(ref _catalogUrl, value); }
    public string UnityHubPath { get => _unityHubPath; set => SetField(ref _unityHubPath, value); }
    public bool ShowLocalChanges { get => _showLocalChanges; set => SetField(ref _showLocalChanges, value); }
    public bool DeveloperMode { get => _developerMode; set => SetField(ref _developerMode, value); }
    public bool PrereleaseUpdates { get => _prereleaseUpdates; set => SetField(ref _prereleaseUpdates, value); }
    public string SettingsPath { get => _settingsPath; private set => SetField(ref _settingsPath, value); }
    public string GitDetected { get => _gitDetected; private set => SetField(ref _gitDetected, value); }
    public string HubDetected { get => _hubDetected; private set => SetField(ref _hubDetected, value); }

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

    /// <summary>The activity log, shown as a section on this page (merged in from the old Logs tab).</summary>
    public LogsViewModel Logs { get; }

    public RelayCommand SaveCommand { get; }
    public RelayCommand CheckForUpdatesCommand => _shell.CheckForUpdatesCommand;

    public SettingsViewModel(UserSettingsService settingsService, GitService gitService, UnityHubService hubService, MainWindowViewModel shell, LogsViewModel logs)
    {
        _settingsService = settingsService;
        _gitService = gitService;
        _hubService = hubService;
        _shell = shell;
        Logs = logs;
        SettingsPath = settingsService.SettingsPath;
        SaveCommand = new RelayCommand(SaveAsync);
        _selectedLanguage = FindLanguage(Localizer.Instance.CurrentCode);
        // Re-pull the localized "detected tooling" fallbacks when the language changes.
        Localizer.Instance.LanguageChanged += _ => RefreshDetected();
        RefreshDetected();
    }

    public void Apply(UserSettings settings)
    {
        ClonePath = settings.ClonePath ?? "";
        CatalogUrl = settings.CatalogUrl;
        UnityHubPath = settings.UnityHubPath ?? "";
        ShowLocalChanges = settings.ShowLocalChanges;
        DeveloperMode = settings.DeveloperMode;
        PrereleaseUpdates = settings.PrereleaseUpdates;
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
    }

    private async Task SaveAsync()
    {
        var settings = await _settingsService.LoadAsync();
        settings.ClonePath = string.IsNullOrWhiteSpace(ClonePath) ? null : ClonePath.Trim();
        settings.CatalogUrl = CatalogUrl?.Trim() ?? "";
        settings.UnityHubPath = string.IsNullOrWhiteSpace(UnityHubPath) ? null : UnityHubPath.Trim();
        settings.ShowLocalChanges = ShowLocalChanges;
        settings.DeveloperMode = DeveloperMode;
        settings.PrereleaseUpdates = PrereleaseUpdates;
        await _settingsService.SaveAsync(settings);

        await _shell.PackagesVM.LoadCatalogAsync(settings.CatalogUrl);
        _shell.ShowChangesTab = settings.ShowLocalChanges;
        _shell.ShowDevelopTab = settings.DeveloperMode;
        _shell.ApplyPrerelease(settings.PrereleaseUpdates);
        RefreshDetected();
        _shell.SetStatus(L.Tr("settings.status.saved"), StatusKind.Success);
    }
}
