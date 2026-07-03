using Avalonia.Threading;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Velopack;

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
    private readonly UnityHubService _hubService = new();
    private readonly UnityReleaseService _releaseService = new();
    private readonly UpdateService _updateService = new();

    private NavPage _currentPage = NavPage.Installs;
    private object? _currentView;
    private string _statusMessage = "Ready";
    private StatusKind _statusKind = StatusKind.Info;
    private BasisInstall? _activeInstall;

    private UpdateInfo? _pendingUpdate;
    private bool _updateAvailable;
    private string _updateBannerText = "";
    private bool _isUpdating;
    private int _updateProgress;

    public InstallsViewModel InstallsVM { get; }
    public PackagesViewModel PackagesVM { get; }
    public ChangesViewModel ChangesVM { get; }
    public UnityViewModel UnityVM { get; }
    public SettingsViewModel SettingsVM { get; }

    public RelayCommand InstallUpdateCommand { get; }
    public RelayCommand DismissUpdateCommand { get; }
    public RelayCommand CheckForUpdatesCommand { get; }

    public MainWindowViewModel()
    {
        _installService = new BasisInstallService(_projectService, _gitService);
        InstallsVM = new InstallsViewModel(_settingsService, _installService, _gitService, this);
        PackagesVM = new PackagesViewModel(_catalogService, _projectService, this);
        ChangesVM = new ChangesViewModel(_gitService, this);
        UnityVM = new UnityViewModel(_hubService, _releaseService, this);
        SettingsVM = new SettingsViewModel(_settingsService, _gitService, _hubService, this);

        InstallUpdateCommand = new RelayCommand(InstallUpdateAsync);
        DismissUpdateCommand = new RelayCommand(() => { UpdateAvailable = false; });
        CheckForUpdatesCommand = new RelayCommand(() => CheckForUpdatesAsync(manual: true));

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

    private bool _showChangesTab;
    public bool ShowChangesTab
    {
        get => _showChangesTab;
        set
        {
            // If it's turned off while the user is looking at it, send them back to Installs.
            if (SetField(ref _showChangesTab, value) && !value && CurrentPage == NavPage.Changes)
                CurrentPage = NavPage.Installs;
        }
    }

    public BasisInstall? ActiveInstall
    {
        get => _activeInstall;
        private set
        {
            if (SetField(ref _activeInstall, value))
                OnPropertyChanged(nameof(ActiveInstallName));
        }
    }

    public string ActiveInstallName => _activeInstall?.DisplayName ?? "No install selected";

    public string AppVersion => _updateService.CurrentVersion;

    public bool UpdateAvailable { get => _updateAvailable; private set => SetField(ref _updateAvailable, value); }
    public string UpdateBannerText { get => _updateBannerText; private set => SetField(ref _updateBannerText, value); }
    public bool IsUpdating { get => _isUpdating; private set => SetField(ref _isUpdating, value); }
    public int UpdateProgress { get => _updateProgress; private set => SetField(ref _updateProgress, value); }

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
        ShowChangesTab = settings.ShowLocalChanges;
        await InstallsVM.LoadAsync(settings);
        await PackagesVM.LoadCatalogAsync(settings.CatalogUrl);

        // Handle a launch-time deep link now (active install + packages are ready) — don't wait on the slower Unity refresh.
        if (DeepLinkDispatcher.Pending is { } pending)
        {
            DeepLinkDispatcher.Pending = null;
            HandleDeepLink(pending);
        }

        await UnityVM.RefreshAsync();

        _ = CheckForUpdatesAsync(manual: false);
        _ = MaybePromptDesktopShortcutAsync();
    }

    /// <summary>On the first run of an installed build, offer to add a desktop shortcut (asked once).</summary>
    private async Task MaybePromptDesktopShortcutAsync()
    {
        if (!_updateService.IsSupported || !OperatingSystem.IsWindows()) return;
        try
        {
            var settings = await _settingsService.LoadAsync();
            if (settings.AskedDesktopShortcut) return;
            settings.AskedDesktopShortcut = true;
            await _settingsService.SaveAsync(settings); // ask once, even if they decline

            var yes = await Dialogs.ConfirmAsync("Desktop shortcut",
                "Add a desktop shortcut for Basis Package Manager? You can always find it from the Start menu.");
            if (!yes) return;

            _updateService.CreateDesktopShortcut();
            SetStatus("Desktop shortcut created.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't create the desktop shortcut: {ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>Handles a <c>basispm://install?…</c> link: choose a target install and add the git package to it.</summary>
    public void HandleDeepLink(string uri) => _ = HandleDeepLinkAsync(uri);

    private async Task HandleDeepLinkAsync(string uri)
    {
        if (!DeepLink.TryParseInstall(uri, out var req)) return;
        NavigateTo("packages");

        var label = req.Name ?? req.Id ?? "the package";
        if (string.IsNullOrWhiteSpace(req.Git))
        {
            SetStatus($"Install link for {label} was missing its git URL.", StatusKind.Error);
            return;
        }

        var target = await ChooseInstallTargetAsync(label);
        if (target is null) return;

        SetActiveInstall(target);
        await PackagesVM.AddGitPackageAsync(req.Id, req.Name, req.Git, req.Repo);
    }

    /// <summary>
    /// Picks which install a package should be added to: none → guide to Installs; one → use it;
    /// several → the "add to which project?" window. Null if there's nowhere to add or the user cancels.
    /// </summary>
    public async Task<BasisInstall?> ChooseInstallTargetAsync(string label)
    {
        var targets = InstallsVM.Installs.Select(r => r.Install).Where(i => i.HasUnityProject).ToList();
        if (targets.Count == 0)
        {
            NavigateTo("installs");
            SetStatus($"Clone or add a Basis install first, then install {label}.", StatusKind.Error);
            return null;
        }
        // Always show the picker so you explicitly choose the project (even with a single install).
        return await Dialogs.PickInstallAsync($"Add “{label}” to which project?", targets);
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (!_updateService.IsSupported)
        {
            if (manual)
                SetStatus("Running from source — updates are handled by your build, not the in-app updater.", StatusKind.Info);
            return;
        }

        try
        {
            if (manual) SetStatus("Checking for updates…");
            var info = await _updateService.CheckAsync();
            if (info is null)
            {
                _pendingUpdate = null;
                UpdateAvailable = false;
                if (manual) SetStatus("You're on the latest version.", StatusKind.Success);
                return;
            }

            _pendingUpdate = info;
            UpdateBannerText = $"Basis Package Manager {info.TargetFullRelease.Version} is available.";
            UpdateAvailable = true;
            if (manual) SetStatus("An update is available.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            if (manual) SetStatus($"Update check failed: {ex.Message}", StatusKind.Error);
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is null || IsUpdating) return;

        IsUpdating = true;
        UpdateProgress = 0;
        SetStatus("Downloading update…");
        try
        {
            // Velopack reports progress off the UI thread; marshal each tick back.
            await _updateService.DownloadAndApplyAsync(_pendingUpdate,
                pct => Dispatcher.UIThread.Post(() => UpdateProgress = pct));
            // On success the process is restarted onto the new version and never returns here.
        }
        catch (Exception ex)
        {
            IsUpdating = false;
            SetStatus($"Update failed: {ex.Message}", StatusKind.Error);
        }
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
