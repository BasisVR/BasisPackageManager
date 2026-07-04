using Avalonia.Threading;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Velopack;

namespace BasisPM.App.ViewModels;

public enum NavPage { Installs, Packages, Changes, Unity, Settings, Support, Develop, Announcements }

public enum StatusKind { Info, Success, Error }

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly UserSettingsService _settingsService = new();
    private readonly UnityProjectService _projectService = new();
    private readonly GitService _gitService = new();
    private readonly BasisInstallService _installService;
    private readonly CatalogService _catalogService = new();
    private readonly AnnouncementService _announcementService = new();
    private readonly BundleService _bundleService = new();
    private readonly UnityHubService _hubService = new();
    private readonly UnityReleaseService _releaseService = new();
    private readonly UpdateService _updateService = new();
    private readonly GitHubAuthService _ghAuth = new();
    private readonly GitHubApiService _ghApi = new();
    private readonly MountRegistry _mountRegistry = new();

    private NavPage _currentPage = NavPage.Installs;
    private object? _currentView;
    private string _statusMessage = "Ready";
    private StatusKind _statusKind = StatusKind.Info;
    private readonly List<string> _breadcrumbs = new();
    private const string IssueRepo = "BasisVR/BasisPackageManager";
    private BasisInstall? _activeInstall;
    private string? _catalogUrl;

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
    public FundingViewModel FundingVM { get; }
    public DevelopViewModel DevelopVM { get; }
    public AnnouncementsViewModel AnnouncementsVM { get; }

    public RelayCommand InstallUpdateCommand { get; }
    public RelayCommand DismissUpdateCommand { get; }
    public RelayCommand CheckForUpdatesCommand { get; }
    public RelayCommand OpenIssueCommand { get; }

    public MainWindowViewModel()
    {
        _installService = new BasisInstallService(_projectService, _gitService);
        InstallsVM = new InstallsViewModel(_settingsService, _installService, _gitService, this);
        PackagesVM = new PackagesViewModel(_catalogService, _projectService, this);
        ChangesVM = new ChangesViewModel(_gitService, this);
        UnityVM = new UnityViewModel(_hubService, _releaseService, this);
        SettingsVM = new SettingsViewModel(_settingsService, _gitService, _hubService, this);
        FundingVM = new FundingViewModel();
        var mountService = new MountService(_gitService, _projectService, _mountRegistry);
        var contributeService = new ContributeService(_gitService, _ghApi);
        DevelopVM = new DevelopViewModel(mountService, contributeService, _ghAuth, _ghApi, _gitService, _mountRegistry, this);
        AnnouncementsVM = new AnnouncementsViewModel(_announcementService);

        InstallUpdateCommand = new RelayCommand(InstallUpdateAsync);
        DismissUpdateCommand = new RelayCommand(() => { UpdateAvailable = false; });
        CheckForUpdatesCommand = new RelayCommand(() => CheckForUpdatesAsync(manual: true));
        OpenIssueCommand = new RelayCommand(OpenIssue);
        CrashReporter.BreadcrumbProvider = () => string.Join("\n", _breadcrumbs);
        CrashReporter.VersionProvider = () => AppVersion;

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
                    NavPage.Support => FundingVM,
                    NavPage.Develop => DevelopVM,
                    NavPage.Announcements => AnnouncementsVM,
                    _ => InstallsVM,
                };
                OnPropertyChanged(nameof(IsInstalls));
                OnPropertyChanged(nameof(IsPackages));
                OnPropertyChanged(nameof(IsChanges));
                OnPropertyChanged(nameof(IsUnity));
                OnPropertyChanged(nameof(IsSettings));
                OnPropertyChanged(nameof(IsSupport));
                OnPropertyChanged(nameof(IsDevelop));
                OnPropertyChanged(nameof(IsAnnouncements));

                if (value == NavPage.Changes) _ = ChangesVM.RefreshAsync();
                if (value == NavPage.Announcements) _ = MarkAnnouncementsSeenAsync();
            }
        }
    }

    public object? CurrentView { get => _currentView; private set => SetField(ref _currentView, value); }

    public bool IsInstalls => CurrentPage == NavPage.Installs;
    public bool IsPackages => CurrentPage == NavPage.Packages;
    public bool IsChanges => CurrentPage == NavPage.Changes;
    public bool IsUnity => CurrentPage == NavPage.Unity;
    public bool IsSettings => CurrentPage == NavPage.Settings;
    public bool IsSupport => CurrentPage == NavPage.Support;
    public bool IsDevelop => CurrentPage == NavPage.Develop;
    public bool IsAnnouncements => CurrentPage == NavPage.Announcements;

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

    private bool _showDevelopTab;
    public bool ShowDevelopTab
    {
        get => _showDevelopTab;
        set
        {
            if (SetField(ref _showDevelopTab, value) && !value && CurrentPage == NavPage.Develop)
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

    private int _announcementsUnread;
    public int AnnouncementsUnreadCount
    {
        get => _announcementsUnread;
        private set { if (SetField(ref _announcementsUnread, value)) OnPropertyChanged(nameof(HasUnreadAnnouncements)); }
    }
    public bool HasUnreadAnnouncements => _announcementsUnread > 0;

    public bool UpdateAvailable { get => _updateAvailable; private set => SetField(ref _updateAvailable, value); }
    public string UpdateBannerText { get => _updateBannerText; private set => SetField(ref _updateBannerText, value); }
    public bool IsUpdating { get => _isUpdating; private set => SetField(ref _isUpdating, value); }
    public int UpdateProgress { get => _updateProgress; private set => SetField(ref _updateProgress, value); }

    public void SetActiveInstall(BasisInstall install)
    {
        ActiveInstall = install;
        PackagesVM.SetActiveInstall(install);
        ChangesVM.SetActiveInstall(install);
        DevelopVM.SetActiveInstall(install);
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
        RecordBreadcrumb(message, kind);
    }

    public void DismissStatus() => SetStatus("Ready", StatusKind.Info);

    // A short trail of the meaningful things that happened, for the "Open an issue" bug report.
    private void RecordBreadcrumb(string message, StatusKind kind)
    {
        if (string.IsNullOrWhiteSpace(message) || message == "Ready" || IsProgressNoise(message)) return;
        var tag = kind switch { StatusKind.Error => "✗", StatusKind.Success => "✓", _ => "·" };
        var line = $"{tag} {message}";
        if (_breadcrumbs.Count > 0 && _breadcrumbs[^1] == line) return;   // dedupe consecutive
        _breadcrumbs.Add(line);
        if (_breadcrumbs.Count > 20) _breadcrumbs.RemoveAt(0);
    }

    // Keep raw git/clone progress chatter out of the breadcrumb trail.
    private static bool IsProgressNoise(string m) =>
        m.Contains('%') ||
        m.StartsWith("remote:", StringComparison.OrdinalIgnoreCase) ||
        m.StartsWith("Receiving", StringComparison.OrdinalIgnoreCase) ||
        m.StartsWith("Resolving", StringComparison.OrdinalIgnoreCase) ||
        m.StartsWith("Compressing", StringComparison.OrdinalIgnoreCase) ||
        m.StartsWith("Counting", StringComparison.OrdinalIgnoreCase) ||
        m.StartsWith("Enumerating", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens a pre-filled GitHub issue with the current error and the last few actions.</summary>
    private void OpenIssue()
    {
        var title = StatusMessage.Length > 90 ? StatusMessage[..90] + "…" : StatusMessage;
        var recent = _breadcrumbs.Skip(Math.Max(0, _breadcrumbs.Count - 10)).ToList();
        var actions = recent.Count > 0
            ? string.Join("\n", recent.Select((c, i) => $"{i + 1}. {c}"))
            : "_none recorded_";
        var body =
            "### What happened\n\n```\n" + StatusMessage + "\n```\n\n" +
            "### Last actions (most recent last)\n" + actions + "\n\n" +
            "### Environment\n" +
            $"- App: {AppVersion}\n" +
            $"- OS: {Environment.OSVersion}\n\n" +
            "_Filed from the Basis Package Manager error bar. Please add anything else that helps reproduce it._";
        if (body.Length > 5500) body = body[..5500] + "\n… (truncated)";
        var url = $"https://github.com/{IssueRepo}/issues/new?labels=bug&title={Uri.EscapeDataString("Error: " + title)}&body={Uri.EscapeDataString(body)}";
        ExternalLink.Open(url);
    }

    public async Task InitializeAsync()
    {
        var settings = await _settingsService.LoadAsync();
        _catalogUrl = settings.CatalogUrl;
        _updateService.SetPrerelease(settings.PrereleaseUpdates);
        SettingsVM.Apply(settings);
        ShowChangesTab = settings.ShowLocalChanges;
        ShowDevelopTab = settings.DeveloperMode;
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
        _ = LoadAnnouncementsAsync();
        _ = RunFirstRunPromptsAsync();
    }

    // First-run prompts, one after another so two dialogs never open at once.
    private async Task RunFirstRunPromptsAsync()
    {
        await OfferCrashIssueIfAnyAsync();
        await MaybeRunOnboardingAsync();
        await MaybePromptDesktopShortcutAsync();
    }

    /// <summary>If the previous run crashed, offer to file a pre-filled issue with the captured details.</summary>
    private async Task OfferCrashIssueIfAnyAsync()
    {
        var (detail, unclean) = CrashReporter.TryTakePending();
        if (detail is null && !unclean) return;

        var yes = await Dialogs.ConfirmAsync("Basis Package Manager closed unexpectedly",
            "It looks like the app closed unexpectedly last time. Open a pre-filled GitHub issue so we can look into it?");
        if (!yes) return;

        string body;
        if (detail is not null)
        {
            var d = detail.Length > 5000 ? detail[..5000] + "\n… (truncated)" : detail;
            body = "### The app crashed\n\n```\n" + d + "\n```\n\n" +
                   "_Auto-collected after a crash. Please add what you were doing when it happened._";
        }
        else
        {
            body = "### The app closed unexpectedly\n\n" +
                   "The previous session didn't shut down cleanly (a hang or force-close — no exception was captured).\n\n" +
                   $"- App: {AppVersion}\n- OS: {Environment.OSVersion}\n\n" +
                   "_Please describe what you were doing when it happened._";
        }
        var url = $"https://github.com/{IssueRepo}/issues/new?labels=crash&title={Uri.EscapeDataString("Crash report")}&body={Uri.EscapeDataString(body)}";
        OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { }
    }

    /// <summary>First run: ask the user's role so we show only the tools they need (toggle later in Settings).</summary>
    private async Task MaybeRunOnboardingAsync()
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            if (settings.CompletedOnboarding) return;

            var isDeveloper = await Dialogs.ChooseRoleAsync();
            settings.DeveloperMode = isDeveloper;
            settings.CompletedOnboarding = true;
            await _settingsService.SaveAsync(settings);

            ShowDevelopTab = isDeveloper;
            SettingsVM.Apply(settings);
        }
        catch { }
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
        if (DeepLink.TryParseBundle(uri, out var bundleId))
        {
            await HandleBundleLinkAsync(bundleId!);
            return;
        }
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

    /// <summary>Handles a <c>basispm://bundle?id=…</c> link: fetch the bundle and add all its packages to a chosen install.</summary>
    private async Task HandleBundleLinkAsync(string bundleId)
    {
        NavigateTo("packages");
        SetStatus("Loading bundle…");
        var bundles = await _bundleService.LoadAsync(_catalogUrl);
        var bundle = bundles.FirstOrDefault(b => string.Equals(b.Id, bundleId, StringComparison.OrdinalIgnoreCase));
        if (bundle is null)
        {
            SetStatus($"Couldn't find bundle “{bundleId}” in the registry.", StatusKind.Error);
            return;
        }

        var target = await ChooseInstallTargetAsync($"{bundle.Name} ({bundle.Packages.Count} packages)");
        if (target is null) return;

        SetActiveInstall(target);
        await PackagesVM.AddBundleAsync(bundle, target);
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

    /// <summary>Applies a change to the prerelease-channel preference and re-checks for updates.</summary>
    public void ApplyPrerelease(bool prerelease)
    {
        _updateService.SetPrerelease(prerelease);
        _ = CheckForUpdatesAsync(manual: false);
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

    /// <summary>Loads announcements from the website feed and updates the unread badge.</summary>
    private async Task LoadAnnouncementsAsync()
    {
        try
        {
            await AnnouncementsVM.LoadAsync();
            var settings = await _settingsService.LoadAsync();
            var seen = settings.SeenAnnouncementIds;
            AnnouncementsUnreadCount = AnnouncementsVM.AllIds.Count(id => !seen.Contains(id));
        }
        catch { }
    }

    /// <summary>Marks every currently-shown announcement as read (persisted) and clears the badge.</summary>
    private async Task MarkAnnouncementsSeenAsync()
    {
        AnnouncementsUnreadCount = 0;
        try
        {
            var settings = await _settingsService.LoadAsync();
            settings.SeenAnnouncementIds = AnnouncementsVM.AllIds.ToList();
            await _settingsService.SaveAsync(settings);
        }
        catch { }
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
            "support" => NavPage.Support,
            "develop" => NavPage.Develop,
            "announcements" => NavPage.Announcements,
            _ => CurrentPage,
        };
    }
}
