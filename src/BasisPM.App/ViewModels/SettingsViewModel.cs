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

    public string AppVersion => _shell.AppVersion;

    public RelayCommand SaveCommand { get; }
    public RelayCommand CheckForUpdatesCommand => _shell.CheckForUpdatesCommand;

    public SettingsViewModel(UserSettingsService settingsService, GitService gitService, UnityHubService hubService, MainWindowViewModel shell)
    {
        _settingsService = settingsService;
        _gitService = gitService;
        _hubService = hubService;
        _shell = shell;
        SettingsPath = settingsService.SettingsPath;
        SaveCommand = new RelayCommand(SaveAsync);
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
        RefreshDetected();
    }

    private void RefreshDetected()
    {
        var git = _gitService.FindGit();
        GitDetected = git ?? "Git not found on PATH — install it from git-scm.com.";
        var hub = _hubService.FindHubPath(string.IsNullOrWhiteSpace(UnityHubPath) ? null : UnityHubPath.Trim());
        HubDetected = hub ?? "Unity Hub not detected.";
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
        _shell.SetStatus("Settings saved.", StatusKind.Success);
    }
}
