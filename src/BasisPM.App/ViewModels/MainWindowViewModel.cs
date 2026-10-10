using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Velopack;

namespace BasisPM.App.ViewModels;

public enum NavPage { Installs, Packages, Server, Unity, Settings, Support, Announcements, Documentation, Logs, Community }

public enum StatusKind { Info, Success, Error }

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly UserSettingsService _settingsService;
    private readonly UnityProjectService _projectService;
    private readonly GitService _gitService;
    private readonly BasisInstallService _installService;
    private readonly CatalogService _catalogService;
    private readonly AnnouncementService _announcementService;
    private readonly PackageListService _packageListService;
    private readonly UnityHubService _hubService;
    private readonly UnityReleaseService _releaseService;
    private readonly UpdateService _updateService;
    private readonly GitHubAuthService _ghAuth;
    private readonly GitHubApiService _ghApi;
    private readonly MountRegistry _mountRegistry;
    private readonly LogService _log;
    private readonly BasisUpdateService _basisUpdates;
    private readonly BasisContributeService _basisContribute;

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

    private string? _statusProject;

    private static readonly TimeSpan BasisUpdateCheckInterval = TimeSpan.FromHours(2);
    private readonly HashSet<string> _dismissedBasisUpdates = new(StringComparer.OrdinalIgnoreCase);
    private List<InstallRow> _basisBannerRows = new();
    private DispatcherTimer? _basisUpdateTimer;
    private bool _autoCheckBasisUpdates = true;
    private int _basisUpdateCount;
    private bool _basisBannerVisible;
    private string _basisBannerText = "";
    private string _basisBannerAction = "";

    public InstallsViewModel InstallsVM { get; }
    public PackagesViewModel PackagesVM { get; }
    public ServerViewModel ServerVM { get; }
    public UnityViewModel UnityVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public FundingViewModel FundingVM { get; }
    public AnnouncementsViewModel AnnouncementsVM { get; }
    public LogsViewModel LogsVM { get; }
    public DocumentationViewModel DocumentationVM { get; }
    public CommunityViewModel CommunityVM { get; }

    public RelayCommand InstallUpdateCommand { get; }
    public RelayCommand DismissUpdateCommand { get; }
    public RelayCommand CheckForUpdatesCommand { get; }
    public RelayCommand OpenIssueCommand { get; }
    public RelayCommand ReviewBasisUpdateCommand { get; }
    public RelayCommand DismissBasisUpdateCommand { get; }
    public RelayCommand ShowActivitiesCommand { get; }

    public MainWindowViewModel()
    {
        // Construct the object graph explicitly in dependency order. Field initializers run before
        // the constructor body and make order-sensitive changes easy to introduce accidentally.
        _settingsService = new UserSettingsService();
        _projectService = new UnityProjectService();
        _gitService = new GitService();
        _catalogService = new CatalogService();
        _announcementService = new AnnouncementService();
        _packageListService = new PackageListService();
        _hubService = new UnityHubService();
        _releaseService = new UnityReleaseService();
        _updateService = new UpdateService();
        _ghAuth = new GitHubAuthService();
        _ghApi = new GitHubApiService();
        _mountRegistry = new MountRegistry();
        _log = new LogService();
        _installService = new BasisInstallService(_projectService, _gitService);
        _basisUpdates = new BasisUpdateService(_gitService);
        _basisContribute = new BasisContributeService(_gitService, _basisUpdates, _ghApi);
        InstallsVM = new InstallsViewModel(_settingsService, _installService, _gitService, _basisUpdates, this);
        var mountService = new MountService(_gitService, _projectService, _mountRegistry);
        var contributeService = new ContributeService(_gitService, _ghApi);
        var cacheDriftService = new CacheDriftService(_gitService);
        // The mount / contribute / cache-drift workflow (formerly the Develop tab) now lives on the Packages page.
        PackagesVM = new PackagesViewModel(_settingsService, _catalogService, _projectService, _mountRegistry,
            mountService, contributeService, cacheDriftService, _ghAuth, _ghApi, _gitService, _basisUpdates, this);
        ServerVM = new ServerViewModel(new BasisServerService(), new ServerPackageService(_gitService), _catalogService, _settingsService, new VersionService(_ghApi, _gitService), new BasisPartsService(_gitService), this);
        UnityVM = new UnityViewModel(_hubService, _releaseService, _settingsService, this);
        LogsVM = new LogsViewModel(_log);
        // Logs live inside the Settings page (merged), so Settings gets a reference to the logs view-model.
        SettingsVM = new SettingsViewModel(_settingsService, _gitService, _hubService, this, LogsVM);
        FundingVM = new FundingViewModel();
        AnnouncementsVM = new AnnouncementsViewModel(_announcementService);
        DocumentationVM = new DocumentationViewModel();
        CommunityVM = new CommunityViewModel(AnnouncementsVM, DocumentationVM, FundingVM);

        InstallUpdateCommand = new RelayCommand(InstallUpdateAsync);
        DismissUpdateCommand = new RelayCommand(() => { UpdateAvailable = false; });
        CheckForUpdatesCommand = new RelayCommand(() => CheckForUpdatesAsync(manual: true));
        OpenIssueCommand = new RelayCommand(OpenIssue);
        ReviewBasisUpdateCommand = new RelayCommand(ReviewBasisUpdate);
        DismissBasisUpdateCommand = new RelayCommand(DismissBasisUpdateAsync);
        ShowActivitiesCommand = new RelayCommand(ShowActivities);
        ServerVM.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ServerViewModel.NeedsDotNet10Sdk)) RefreshBlockers(); };
        PackagesVM.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PackagesViewModel.IsInstalling)) OnPropertyChanged(nameof(IsAnythingRunning)); };
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
                    NavPage.Server => ServerVM,
                    NavPage.Unity => UnityVM,
                    NavPage.Settings => SettingsVM,
                    NavPage.Support => FundingVM,
                    NavPage.Announcements => AnnouncementsVM,
                    NavPage.Documentation => DocumentationVM,
                    NavPage.Community => CommunityVM,
                    _ => InstallsVM,
                };
                OnPropertyChanged(nameof(IsInstalls));
                OnPropertyChanged(nameof(IsPackages));
                OnPropertyChanged(nameof(IsServer));
                OnPropertyChanged(nameof(IsUnity));
                OnPropertyChanged(nameof(IsSettings));
                OnPropertyChanged(nameof(IsSupport));
                OnPropertyChanged(nameof(IsAnnouncements));
                OnPropertyChanged(nameof(IsDocumentation));
                OnPropertyChanged(nameof(IsCommunity));

                if (value == NavPage.Community) _ = MarkAnnouncementsSeenAsync();
                if (value == NavPage.Settings) RefreshBlockers();
            }
        }
    }

    public object? CurrentView { get => _currentView; private set => SetField(ref _currentView, value); }

    public bool IsInstalls => CurrentPage == NavPage.Installs;
    public bool IsPackages => CurrentPage == NavPage.Packages;
    public bool IsServer => CurrentPage == NavPage.Server;
    public bool IsUnity => CurrentPage == NavPage.Unity;
    public bool IsSettings => CurrentPage == NavPage.Settings;
    public bool IsSupport => CurrentPage == NavPage.Support;
    public bool IsAnnouncements => CurrentPage == NavPage.Announcements;
    public bool IsDocumentation => CurrentPage == NavPage.Documentation;
    public bool IsCommunity => CurrentPage == NavPage.Community;

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

    public int BasisUpdateCount
    {
        get => _basisUpdateCount;
        private set { if (SetField(ref _basisUpdateCount, value)) OnPropertyChanged(nameof(HasBasisUpdates)); }
    }
    public bool HasBasisUpdates => _basisUpdateCount > 0;
    public bool BasisBannerVisible { get => _basisBannerVisible; private set => SetField(ref _basisBannerVisible, value); }
    public string BasisBannerText { get => _basisBannerText; private set => SetField(ref _basisBannerText, value); }
    public string BasisBannerAction { get => _basisBannerAction; private set => SetField(ref _basisBannerAction, value); }

    public bool UpdateAvailable { get => _updateAvailable; private set => SetField(ref _updateAvailable, value); }
    public string UpdateBannerText { get => _updateBannerText; private set => SetField(ref _updateBannerText, value); }
    public bool IsUpdating { get => _isUpdating; private set => SetField(ref _isUpdating, value); }
    public int UpdateProgress { get => _updateProgress; private set => SetField(ref _updateProgress, value); }

    /// <summary>
    /// Every long-running operation that is still going, oldest first. The cross-page card shows the newest one and
    /// Settings lists them all, each with the project it belongs to.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<ActivityItem> Activities { get; } = new();
    public ActivityItem? CurrentActivity => Activities.Count == 0 ? null : Activities[^1];
    public bool IsActivityVisible => Activities.Count > 0;
    public string ActivityTitle => CurrentActivity?.Title ?? "";
    public string ActivityDetail => CurrentActivity?.Detail ?? "";
    public bool HasActivityDetail => CurrentActivity?.HasDetail == true;
    public double ActivityProgress => CurrentActivity?.Progress ?? 0;
    public bool ActivityIsIndeterminate => CurrentActivity?.IsIndeterminate ?? true;
    public bool HasMoreActivities => Activities.Count > 1;
    public bool IsAnythingRunning => Activities.Count > 0 || PackagesVM.IsInstalling;
    public string MoreActivitiesLabel => L.Tr("shell.activity.more", Activities.Count - 1);

    public Guid BeginActivity(string title, string? project = null)
    {
        var activity = new ActivityItem(title, project);
        Activities.Add(activity);
        RaiseActivities();
        return activity.Id;
    }

    public void ReportActivity(Guid id, string detail) => FindActivity(id)?.Report(detail);

    public void EndActivity(Guid id)
    {
        if (FindActivity(id) is not { } activity) return;
        Activities.Remove(activity);
        RaiseActivities();
    }

    public string? ProjectOf(Guid id) => FindActivity(id)?.Project;

    private ActivityItem? FindActivity(Guid id) => id == Guid.Empty ? null : Activities.FirstOrDefault(a => a.Id == id);

    private void RaiseActivities()
    {
        OnPropertyChanged(nameof(CurrentActivity));
        OnPropertyChanged(nameof(IsActivityVisible));
        OnPropertyChanged(nameof(HasMoreActivities));
        OnPropertyChanged(nameof(MoreActivitiesLabel));
        OnPropertyChanged(nameof(IsAnythingRunning));
    }

    public System.Collections.ObjectModel.ObservableCollection<BlockerItem> Blockers { get; } = new();
    public bool HasBlockers => Blockers.Count > 0;

    public void RefreshBlockers()
    {
        Blockers.Clear();
        foreach (var row in InstallsVM.Installs.Where(r => r.BasisUpdateInProgress))
            Blockers.Add(new BlockerItem(row.Name,
                L.Tr(row.BranchSwitchInProgress ? "settings.activity.blocker.switchPaused" : "settings.activity.blocker.updatePaused"),
                L.Tr("settings.activity.blocker.continue"), new RelayCommand(() =>
                {
                    NavigateTo("installs");
                    InstallsVM.UpdateBasisCommand.Execute(row);
                })));
        if (!_gitService.IsAvailable)
            Blockers.Add(new BlockerItem(null, L.Tr("settings.activity.blocker.git"), L.Tr("settings.activity.blocker.getGit"),
                new RelayCommand(() => ExternalLink.Open("https://git-scm.com/downloads"))));
        if (ServerVM.NeedsDotNet10Sdk)
            Blockers.Add(new BlockerItem(ActiveInstall?.DisplayName, L.Tr("settings.activity.blocker.dotnet"), L.Tr("settings.activity.blocker.getDotNet"),
                ServerVM.OpenDotNetDownloadCommand));
        OnPropertyChanged(nameof(HasBlockers));
    }

    public void ShowActivities()
    {
        BasisPM.App.Views.SectionState.Open("settings.activity");
        RefreshBlockers();
        CurrentPage = NavPage.Settings;
    }

    public void SetActiveInstall(BasisInstall install)
    {
        ActiveInstall = install;
        InstallsVM.MarkActive(install);
        PackagesVM.SetActiveInstall(install);
        ServerVM.SetActiveInstall(install);
        UnityVM.SetRequiredVersion(install.UnityVersion);
    }

    public void ClearActiveInstall()
    {
        ActiveInstall = null;
        PackagesVM.ClearActiveInstall();
        ServerVM.ClearActiveInstall();
        UnityVM.SetRequiredVersion(null);
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

    public string? StatusProject
    {
        get => _statusProject;
        private set { if (SetField(ref _statusProject, value)) OnPropertyChanged(nameof(HasStatusProject)); }
    }

    public bool HasStatusProject => !string.IsNullOrWhiteSpace(_statusProject);

    public void SetStatus(string message, StatusKind kind = StatusKind.Info, string? project = null)
    {
        StatusMessage = message;
        StatusKind = kind;
        StatusProject = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
        if (IsProgressNoise(message)) return;
        var line = StatusProject is null ? message : $"[{StatusProject}] {message}";
        RecordBreadcrumb(line, kind);
        _log.Add(kind switch { StatusKind.Error => LogLevel.Error, StatusKind.Success => LogLevel.Success, _ => LogLevel.Info }, line);
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
        body = Redact.Scrub(body);
        if (body.Length > 5500) body = body[..5500] + "\n… (truncated)";
        var url = $"https://github.com/{IssueRepo}/issues/new?labels=bug&title={Uri.EscapeDataString("Error: " + title)}&body={Uri.EscapeDataString(body)}";
        ExternalLink.Open(url);
    }

    public async Task InitializeAsync()
    {
        var settings = await _settingsService.LoadAsync();
        await ApplySettingsAsync(settings);

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
        StartBasisUpdateTimer();
        if (_autoCheckBasisUpdates) _ = InstallsVM.CheckBasisUpdatesAsync(manual: false);
    }

    // Pushes a settings snapshot into every tab / view-model. Shared by first launch (InitializeAsync)
    // and the Settings "Reset everything" flow, so both leave the app in exactly the same state.
    private async Task ApplySettingsAsync(UserSettings settings)
    {
        _catalogUrl = settings.CatalogUrl;
        _updateService.SetPrerelease(settings.PrereleaseUpdates);
        _autoCheckBasisUpdates = settings.AutoCheckBasisUpdates;
        _dismissedBasisUpdates.Clear();
        _dismissedBasisUpdates.UnionWith(settings.DismissedBasisUpdates);
        SettingsVM.Apply(settings);
        PackagesVM.SetInitialGridView(settings.PackagesGridView);
        await InstallsVM.LoadAsync(settings);
        await PackagesVM.LoadCatalogAsync(settings.CatalogUrl, settings.ExtraCatalogUrls);
    }

    // First-run prompts, one after another so two dialogs never open at once.
    private async Task RunFirstRunPromptsAsync()
    {
        await OfferCrashIssueIfAnyAsync();
        await MaybePromptDesktopShortcutAsync();
    }

    /// <summary>If the previous run crashed, offer to file a pre-filled issue with the captured details.</summary>
    private async Task OfferCrashIssueIfAnyAsync()
    {
        var (detail, unclean) = CrashReporter.TryTakePending();
        if (detail is null && !unclean) return;

        var yes = await Dialogs.ConfirmAsync(L.Tr("shell.crash.dialogTitle"),
            L.Tr("shell.crash.dialogMessage"));
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
        var url = $"https://github.com/{IssueRepo}/issues/new?labels=crash&title={Uri.EscapeDataString("Crash report")}&body={Uri.EscapeDataString(Redact.Scrub(body))}";
        OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { DiagnosticLog.Write($"Opening external URL {url}", ex); }
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

            var yes = await Dialogs.ConfirmAsync(L.Tr("shell.desktopShortcut.dialogTitle"),
                L.Tr("shell.desktopShortcut.dialogMessage"));
            if (!yes) return;

            _updateService.CreateDesktopShortcut();
            SetStatus(L.Tr("shell.status.desktopShortcutCreated"), StatusKind.Success);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Creating the desktop shortcut", ex);
            SetStatus(L.Tr("shell.status.desktopShortcutFailed", ex.Message), StatusKind.Error);
        }
    }

    /// <summary>
    /// Wipes every persisted trace of the app — settings, mounted-package records, activity logs and
    /// crash markers (the whole <c>%AppData%/BasisPM</c> folder) — then reloads defaults, so it behaves
    /// like a brand-new install. Basis clones on disk are untouched.
    /// </summary>
    public async Task ResetEverythingAsync()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BasisPM");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Resetting application data", ex);
            SetStatus(L.Tr("settings.reset.failed", ex.Message), StatusKind.Error);
            return;
        }

        // Recreate what the still-running session relies on, so nothing silently breaks before a restart:
        // re-arm the crash marker and re-make the log folder that file logging appends into.
        CrashReporter.ArmSession();
        try { Directory.CreateDirectory(_log.LogDirectory); } catch (Exception ex) { DiagnosticLog.Write($"Recreating log directory {_log.LogDirectory}", ex); }

        // Reset the live, in-memory state to what a fresh launch would show.
        LogsVM.ClearCommand.Execute(null);
        _breadcrumbs.Clear();
        Localizer.Instance.SetLanguage("en");
        BasisPM.App.Views.SectionState.Clear();
        CurrentPage = NavPage.Installs;

        var settings = await _settingsService.LoadAsync();   // the file is gone → default settings
        await ApplySettingsAsync(settings);

        SetStatus(L.Tr("settings.reset.done"), StatusKind.Success);
    }

    /// <summary>Handles a <c>basispm://install?…</c> link: choose a target install and add the git package to it.</summary>
    public void HandleDeepLink(string uri) => _ = HandleDeepLinkAsync(uri);

    private async Task HandleDeepLinkAsync(string uri)
    {
        if (DeepLink.TryParsePackageList(uri, out var packageListId))
        {
            await HandlePackageListLinkAsync(packageListId!);
            return;
        }
        if (!DeepLink.TryParseInstall(uri, out var req)) return;
        NavigateTo("packages");

        var label = req.Name ?? req.Id ?? L.Tr("shell.deeplink.thePackage");
        if (string.IsNullOrWhiteSpace(req.Git))
        {
            SetStatus(L.Tr("shell.status.installLinkMissingGit", label), StatusKind.Error);
            return;
        }

        var target = await ChooseInstallTargetAsync(label);
        if (target is null) return;

        SetActiveInstall(target);
        await PackagesVM.AddGitPackageAsync(req.Id, req.Name, req.Git, req.Repo);
    }

    /// <summary>Handles a <c>basispm://packagelist?id=…</c> link: fetch the package list and add all its packages to a chosen install.</summary>
    private async Task HandlePackageListLinkAsync(string packageListId)
    {
        NavigateTo("packages");
        SetStatus(L.Tr("shell.status.loadingPackageList"));
        var packageLists = await _packageListService.LoadAsync(_catalogUrl);
        var packageList = packageLists.FirstOrDefault(pl => string.Equals(pl.Id, packageListId, StringComparison.OrdinalIgnoreCase));
        if (packageList is null)
        {
            SetStatus(L.Tr("shell.status.packageListNotFound", packageListId), StatusKind.Error);
            return;
        }

        var target = await ChooseInstallTargetAsync(L.Tr("shell.packageList.pickerLabel", packageList.Name, packageList.Packages.Count));
        if (target is null) return;

        SetActiveInstall(target);
        await PackagesVM.AddPackageListAsync(packageList, target);
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
            SetStatus(L.Tr("shell.status.cloneFirst", label), StatusKind.Error);
            return null;
        }
        // Always show the picker so you explicitly choose the project (even with a single install).
        return await Dialogs.PickInstallAsync(L.Tr("shell.installPicker.prompt", label), targets);
    }

    /// <summary>Applies a change to the prerelease-channel preference and re-checks for updates.</summary>
    public void ApplyPrerelease(bool prerelease)
    {
        _updateService.SetPrerelease(prerelease);
        _ = CheckForUpdatesAsync(manual: false);
    }

    public void ApplyBasisUpdateChecks(bool enabled)
    {
        var turnedOn = enabled && !_autoCheckBasisUpdates;
        _autoCheckBasisUpdates = enabled;
        if (turnedOn) _ = InstallsVM.CheckBasisUpdatesAsync(manual: false);
    }

    private void StartBasisUpdateTimer()
    {
        if (_basisUpdateTimer is not null) return;
        _basisUpdateTimer = new DispatcherTimer { Interval = BasisUpdateCheckInterval };
        _basisUpdateTimer.Tick += (_, _) =>
        {
            if (_autoCheckBasisUpdates) _ = InstallsVM.CheckBasisUpdatesAsync(manual: false);
        };
        _basisUpdateTimer.Start();
    }

    public Task OpenBasisChangesAsync(BasisInstall install, string? packageId = null) =>
        Dialogs.ShowBasisChangesAsync(new BasisChangesViewModel(
            new BasisChangesHost(_basisContribute, _basisUpdates, _ghAuth, _ghApi, _gitService, install, this), install.DisplayName, packageId));

    public void RefreshBasisUpdateNotice()
    {
        var pending = InstallsVM.Installs.Where(r => r.BasisUpdateAvailable || r.BasisUpdateInProgress).ToList();
        BasisUpdateCount = pending.Count;
        _basisBannerRows = pending.Where(r => r.BasisUpdateInProgress || !_dismissedBasisUpdates.Contains(BasisNoticeKey(r))).ToList();
        BasisBannerVisible = _basisBannerRows.Count > 0;
        if (_basisBannerRows.Count == 1)
        {
            var row = _basisBannerRows[0];
            BasisBannerText = row.BranchSwitchInProgress ? L.Tr("shell.basisUpdate.bannerSwitchInProgress", row.Name)
                : row.BasisUpdateInProgress ? L.Tr("shell.basisUpdate.bannerInProgress", row.Name)
                : row.BasisBehind is > 1 and var behind ? L.Tr("shell.basisUpdate.bannerOneCount", row.Name, behind)
                : L.Tr("shell.basisUpdate.bannerOne", row.Name);
            BasisBannerAction = row.BranchSwitchInProgress ? L.Tr("shell.basisUpdate.finishSwitch")
                : row.BasisUpdateInProgress ? L.Tr("shell.basisUpdate.finish") : L.Tr("shell.basisUpdate.review");
        }
        else if (_basisBannerRows.Count > 1)
        {
            BasisBannerText = L.Tr("shell.basisUpdate.bannerMany", _basisBannerRows.Count);
            BasisBannerAction = L.Tr("shell.basisUpdate.viewProjects");
        }
        RefreshBlockers();
    }

    private static string BasisNoticeKey(InstallRow row) => $"{row.RepoRoot}|{row.BasisRemoteSha}";

    private void ReviewBasisUpdate()
    {
        NavigateTo("installs");
        if (_basisBannerRows.Count == 1) InstallsVM.UpdateBasisCommand.Execute(_basisBannerRows[0]);
    }

    private async Task DismissBasisUpdateAsync()
    {
        foreach (var row in _basisBannerRows.Where(r => !r.BasisUpdateInProgress))
            _dismissedBasisUpdates.Add(BasisNoticeKey(row));
        BasisBannerVisible = false;
        try
        {
            var settings = await _settingsService.LoadAsync();
            settings.DismissedBasisUpdates = _dismissedBasisUpdates.TakeLast(100).ToList();
            await _settingsService.SaveAsync(settings);
        }
        catch (Exception ex) { DiagnosticLog.Write("Saving dismissed Basis update notices", ex); }
    }

    /// <summary>Opens an install's Unity project in the matching editor (or Unity Hub if that version isn't installed).</summary>
    public async Task OpenProjectInUnityAsync(BasisInstall install)
    {
        if (install is null || !install.HasUnityProject) { SetStatus(L.Tr("shell.status.noUnityProject"), StatusKind.Error); return; }
        SetStatus(L.Tr("shell.status.openingInUnity", install.DisplayName));
        try
        {
            var settings = await _settingsService.LoadAsync();
            var manualEditors = settings.ManualEditors.Select(m => m.ToInstalledEditor());
            if (await _hubService.OpenProjectAsync(install.UnityProjectPath, install.UnityVersion, settings.UnityHubPath, manualEditors))
            {
                SetStatus(L.Tr("shell.status.openedInUnity", install.DisplayName, install.UnityVersion), StatusKind.Success);
                return;
            }
            if (_hubService.OpenHub(settings.UnityHubPath))
                SetStatus(L.Tr("shell.status.unityNotInstalledOpenedHub", install.UnityVersion), StatusKind.Error);
            else
                SetStatus(L.Tr("shell.status.unityNotInstalled", install.UnityVersion), StatusKind.Error);
        }
        catch (Exception ex) { DiagnosticLog.Write("Opening the Basis project in Unity", ex); SetStatus(L.Tr("shell.status.openUnityFailed", ex.Message), StatusKind.Error); }
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (!_updateService.IsSupported)
        {
            if (manual)
                SetStatus(L.Tr("shell.status.runningFromSource"), StatusKind.Info);
            return;
        }

        try
        {
            if (manual) SetStatus(L.Tr("shell.status.checkingForUpdates"));
            var info = await _updateService.CheckAsync();
            if (info is null)
            {
                _pendingUpdate = null;
                UpdateAvailable = false;
                if (manual) SetStatus(L.Tr("shell.status.upToDate"), StatusKind.Success);
                return;
            }

            _pendingUpdate = info;
            UpdateBannerText = L.Tr("shell.update.bannerAvailable", info.TargetFullRelease.Version);
            UpdateAvailable = true;
            if (manual) SetStatus(L.Tr("shell.status.updateAvailable"), StatusKind.Success);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Checking for application updates", ex);
            if (manual) SetStatus(L.Tr("shell.status.updateCheckFailed", ex.Message), StatusKind.Error);
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is null || IsUpdating) return;

        IsUpdating = true;
        UpdateProgress = 0;
        SetStatus(L.Tr("shell.status.downloadingUpdate"));
        try
        {
            // An update restarts the process — an intentional exit, not a crash. Clear the marker first.
            CrashReporter.MarkCleanExit();
            // Velopack reports progress off the UI thread; marshal each tick back.
            await _updateService.DownloadAndApplyAsync(_pendingUpdate,
                pct => Dispatcher.UIThread.Post(() => UpdateProgress = pct));
            // On success the process is restarted onto the new version and never returns here.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Applying the application update", ex);
            CrashReporter.ArmSession();   // update aborted — still running, so re-arm crash detection
            IsUpdating = false;
            SetStatus(L.Tr("shell.status.updateFailed", ex.Message), StatusKind.Error);
        }
    }

    /// <summary>Loads announcements from the website feed and updates the unread badge.</summary>
    private async Task LoadAnnouncementsAsync()
    {
        try
        {
            await AnnouncementsVM.LoadAsync();

            // If the user is already on Community (e.g. they opened it before the feed finished
            // loading), mark everything seen now that the ids exist — otherwise the earlier mark-seen
            // ran against an empty list and the badge would linger. On any other page, just count.
            if (CurrentPage == NavPage.Community)
            {
                await MarkAnnouncementsSeenAsync();
                return;
            }

            var settings = await _settingsService.LoadAsync();
            var seen = settings.SeenAnnouncementIds;
            AnnouncementsUnreadCount = AnnouncementsVM.AllIds.Count(id => !seen.Contains(id));
        }
        catch (Exception ex) { DiagnosticLog.Write("Refreshing the unread announcement count", ex); }
    }

    /// <summary>Marks every currently-shown announcement as read (persisted) and clears the badge.</summary>
    private async Task MarkAnnouncementsSeenAsync()
    {
        // The feed loads asynchronously at startup. If the user reaches Community before it finishes,
        // AllIds would still be empty and we'd persist an empty "seen" list — recording nothing and
        // wiping the ids saved on a previous run, so the unread badge returns on the next launch.
        // Awaiting the load (a no-op once it has completed) guarantees we record the real ids.
        await AnnouncementsVM.LoadAsync();

        AnnouncementsUnreadCount = 0;
        var ids = AnnouncementsVM.AllIds;
        if (ids.Count == 0) return;   // nothing loaded yet — don't overwrite what's already saved

        try
        {
            var settings = await _settingsService.LoadAsync();
            settings.SeenAnnouncementIds = ids.ToList();
            await _settingsService.SaveAsync(settings);
        }
        catch (Exception ex) { DiagnosticLog.Write("Saving the set of read announcements", ex); }
    }

    public void NavigateTo(string page)
    {
        CurrentPage = page switch
        {
            "installs" => NavPage.Installs,
            "packages" => NavPage.Packages,
            "server" => NavPage.Server,
            "unity" => NavPage.Unity,
            "settings" => NavPage.Settings,
            "support" => NavPage.Support,
            "announcements" => NavPage.Announcements,
            "documentation" => NavPage.Documentation,
            "community" => NavPage.Community,
            _ => CurrentPage,
        };
    }
}
