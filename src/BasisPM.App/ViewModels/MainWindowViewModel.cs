using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public enum NavPage { Installs, Packages, Changes, Unity, Settings }

public enum StatusKind { Info, Success, Error }

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly UserSettingsService _settingsService = new();
    private readonly UnityProjectService _projectService = new();
    private readonly GitService _gitService = new();
    private readonly BasisInstallService _installService;
    private readonly CatalogService _catalogService = new();
    private readonly NuGetService _nugetService = new();
    private readonly UnityHubService _hubService = new();
    private readonly UnityReleaseService _releaseService = new();

    private NavPage _currentPage = NavPage.Installs;
    private object? _currentView;
    private string _statusMessage = "Ready";
    private StatusKind _statusKind = StatusKind.Info;
    private BasisInstall? _activeInstall;

    public InstallsViewModel InstallsVM { get; }
    public PackagesViewModel PackagesVM { get; }
    public ChangesViewModel ChangesVM { get; }
    public UnityViewModel UnityVM { get; }
    public SettingsViewModel SettingsVM { get; }

    public MainWindowViewModel()
    {
        _installService = new BasisInstallService(_projectService, _gitService);
        InstallsVM = new InstallsViewModel(_settingsService, _installService, _gitService, this);
        PackagesVM = new PackagesViewModel(_catalogService, _projectService, _nugetService, this);
        ChangesVM = new ChangesViewModel(_gitService, this);
        UnityVM = new UnityViewModel(_hubService, _releaseService, this);
        SettingsVM = new SettingsViewModel(_settingsService, _gitService, _hubService, this);
        CurrentView = InstallsVM;
    }

    public NavPage CurrentPage
    {
        get => _currentPage;
        set
        {
            if (SetField(ref _currentPage, value))
            {
                CurrentView = value switch
                {
                    NavPage.Installs => InstallsVM,
                    NavPage.Packages => PackagesVM,
                    NavPage.Changes => ChangesVM,
                    NavPage.Unity => UnityVM,
                    NavPage.Settings => SettingsVM,
                    _ => InstallsVM,
                };
                OnPropertyChanged(nameof(IsInstalls));
                OnPropertyChanged(nameof(IsPackages));
                OnPropertyChanged(nameof(IsChanges));
                OnPropertyChanged(nameof(IsUnity));
                OnPropertyChanged(nameof(IsSettings));

                if (value == NavPage.Changes) _ = ChangesVM.RefreshAsync();
            }
        }
    }

    public object? CurrentView { get => _currentView; private set => SetField(ref _currentView, value); }

    public bool IsInstalls => CurrentPage == NavPage.Installs;
    public bool IsPackages => CurrentPage == NavPage.Packages;
    public bool IsChanges => CurrentPage == NavPage.Changes;
    public bool IsUnity => CurrentPage == NavPage.Unity;
    public bool IsSettings => CurrentPage == NavPage.Settings;

    public BasisInstall? ActiveInstall
    {
        get => _activeInstall;
        private set
        {
            if (SetField(ref _activeInstall, value))
                OnPropertyChanged(nameof(ActiveInstallName));
        }
    }

    public string ActiveInstallName => _activeInstall?.Name ?? "No install selected";

    public void SetActiveInstall(BasisInstall install)
    {
        ActiveInstall = install;
        PackagesVM.SetActiveInstall(install);
        ChangesVM.SetActiveInstall(install);
        UnityVM.SetRequiredVersion(install.UnityVersion);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetField(ref _statusMessage, value))
                OnPropertyChanged(nameof(CanCopyStatus));
        }
    }

    public bool CanCopyStatus =>
        !string.IsNullOrWhiteSpace(_statusMessage) &&
        !string.Equals(_statusMessage, "Ready", StringComparison.Ordinal);

    public StatusKind StatusKind
    {
        get => _statusKind;
        private set
        {
            if (SetField(ref _statusKind, value))
            {
                OnPropertyChanged(nameof(IsStatusError));
                OnPropertyChanged(nameof(IsStatusSuccess));
            }
        }
    }

    public bool IsStatusError => StatusKind == StatusKind.Error;
    public bool IsStatusSuccess => StatusKind == StatusKind.Success;

    public void SetStatus(string message, StatusKind kind = StatusKind.Info)
    {
        StatusMessage = message;
        StatusKind = kind;
    }

    public void DismissStatus() => SetStatus("Ready", StatusKind.Info);

    public async Task InitializeAsync()
    {
        var settings = await _settingsService.LoadAsync();
        SettingsVM.Apply(settings);
        await InstallsVM.LoadAsync(settings);
        await PackagesVM.LoadCatalogAsync(settings.CatalogUrl);
        PackagesVM.IncludePrerelease = settings.NuGetPrerelease;
        await UnityVM.RefreshAsync();
    }

    public void NavigateTo(string page)
    {
        CurrentPage = page switch
        {
            "installs" => NavPage.Installs,
            "packages" => NavPage.Packages,
            "changes" => NavPage.Changes,
            "unity" => NavPage.Unity,
            "settings" => NavPage.Settings,
            _ => CurrentPage,
        };
    }
}
