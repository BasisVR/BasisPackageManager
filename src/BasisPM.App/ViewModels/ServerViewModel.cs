using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class ServerViewModel : ObservableObject
{
    private readonly BasisServerService _service;
    private readonly ServerPackageService _packages;
    private readonly CatalogService _catalogs;
    private readonly UserSettingsService _settings;
    private readonly VersionService _versions;
    private readonly BasisPartsService _parts;
    private readonly MainWindowViewModel _shell;
    private Catalog? _catalog;
    private bool _isServerLeftOut;
    private string _serverPackageSource = "";
    private string _serverPackagesNotice = "";
    private bool _canManageServerPackages;
    private BasisInstall? _install;
    private string? _configRoot;
    private Process? _serverProcess;
    private bool _isBusy;
    private string _host = "127.0.0.1";
    private string _port = "4296";
    private string _connectionPassword = "";
    private string _contentUrl = "";
    private string _contentPassword = "";
    private ContentModeOption? _selectedMode;
    private bool _addToDefaultLibrary = true;
    private bool _hasDotNet10Sdk;
    private bool _dotNetChecked;
    private string _dotNetStatus = "Checking for the .NET 10 SDK…";

    public ObservableCollection<ServerConfigFieldRow> ConfigFields { get; } = new();
    public ObservableCollection<ServerContentFile> ContentFiles { get; } = new();
    public ObservableCollection<ContentModeOption> ContentModes { get; } = new()
    {
        new("Avatar", 0), new("World / Scene", 1), new("Prop", 2),
    };

    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public bool HasInstall => _install is not null;
    public bool HasConfig => ConfigFields.Count > 0;
    public bool HasContent => ContentFiles.Count > 0;
    public bool HasDotNet10Sdk { get => _hasDotNet10Sdk; private set { if (SetField(ref _hasDotNet10Sdk, value)) OnPropertyChanged(nameof(NeedsDotNet10Sdk)); } }
    public bool NeedsDotNet10Sdk => _dotNetChecked && !_hasDotNet10Sdk && _install is not null && _service.HasServerProject(_install.RepoRoot);
    public string DotNetStatus { get => _dotNetStatus; private set => SetField(ref _dotNetStatus, value); }
    public string ProjectName => _install?.DisplayName ?? "No Basis project selected";
    public string RuntimeDirectory => _install is null ? "" : _service.GetPaths(_install.RepoRoot).RuntimeDirectory;
    public string Host { get => _host; set { if (SetField(ref _host, value)) OnPropertyChanged(nameof(ConnectionPasswordHint)); } }
    public string ConnectionPasswordHint => string.IsNullOrWhiteSpace(Host) || BasisServerService.IsLocalHost(Host) ? "Uses this server's password" : "";
    public string Port { get => _port; set => SetField(ref _port, value); }
    public string ConnectionPassword { get => _connectionPassword; set => SetField(ref _connectionPassword, value); }
    public string ContentUrl { get => _contentUrl; set => SetField(ref _contentUrl, value); }
    public string ContentPassword { get => _contentPassword; set => SetField(ref _contentPassword, value); }
    public ContentModeOption? SelectedMode { get => _selectedMode; set => SetField(ref _selectedMode, value); }
    public bool AddToDefaultLibrary { get => _addToDefaultLibrary; set { if (SetField(ref _addToDefaultLibrary, value)) OnPropertyChanged(nameof(AddAsInitialResource)); } }
    public bool AddAsInitialResource { get => !_addToDefaultLibrary; set { if (value) AddToDefaultLibrary = false; OnPropertyChanged(); } }

    public ObservableCollection<ServerPackageRow> ServerPackages { get; } = new();
    public ObservableCollection<CatalogPackageVersion> AvailableServerPackages { get; } = new();
    public bool HasServerPackages => ServerPackages.Count > 0;
    public bool HasAvailableServerPackages => AvailableServerPackages.Count > 0;
    public bool CanManageServerPackages { get => _canManageServerPackages; private set => SetField(ref _canManageServerPackages, value); }
    public string ServerPackageSource { get => _serverPackageSource; set => SetField(ref _serverPackageSource, value); }
    public string ServerPackagesNotice
    {
        get => _serverPackagesNotice;
        private set { if (SetField(ref _serverPackagesNotice, value)) OnPropertyChanged(nameof(HasServerPackagesNotice)); }
    }
    public bool HasServerPackagesNotice => !string.IsNullOrEmpty(_serverPackagesNotice);
    public bool IsServerLeftOut { get => _isServerLeftOut; private set => SetField(ref _isServerLeftOut, value); }

    public RelayCommand BuildCommand { get; }
    public RelayCommand RunCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand LaunchClientCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveConfigCommand { get; }
    public RelayCommand AddContentCommand { get; }
    public RelayCommand<ServerContentFile> RemoveContentCommand { get; }
    public RelayCommand OpenRuntimeCommand { get; }
    public RelayCommand OpenDotNetDownloadCommand { get; }
    public RelayCommand AddServerPackageCommand { get; }
    public RelayCommand RestoreServerPackagesCommand { get; }
    public RelayCommand<ServerPackageRow> UpdateServerPackageCommand { get; }
    public RelayCommand<ServerPackageRow> RemoveServerPackageCommand { get; }
    public RelayCommand<ServerPackageRow> OpenServerPackageFolderCommand { get; }
    public RelayCommand<CatalogPackageVersion> InstallServerCatalogPackageCommand { get; }
    public RelayCommand AddServerBackCommand { get; }

    public ServerViewModel(BasisServerService service, ServerPackageService packages, CatalogService catalogs, UserSettingsService settings, VersionService versions, BasisPartsService parts, MainWindowViewModel shell)
    {
        _service = service;
        _packages = packages;
        _catalogs = catalogs;
        _settings = settings;
        _versions = versions;
        _parts = parts;
        _shell = shell;
        AddServerBackCommand = new RelayCommand(AddServerBackAsync);
        AddServerPackageCommand = new RelayCommand(AddServerPackageAsync);
        RestoreServerPackagesCommand = new RelayCommand(RestoreServerPackagesAsync);
        UpdateServerPackageCommand = new RelayCommand<ServerPackageRow>(UpdateServerPackageAsync);
        RemoveServerPackageCommand = new RelayCommand<ServerPackageRow>(RemoveServerPackageAsync);
        OpenServerPackageFolderCommand = new RelayCommand<ServerPackageRow>(OpenServerPackageFolder);
        InstallServerCatalogPackageCommand = new RelayCommand<CatalogPackageVersion>(InstallServerCatalogPackageAsync);
        _selectedMode = ContentModes[0];
        BuildCommand = new RelayCommand(BuildAsync);
        RunCommand = new RelayCommand(RunAsync);
        StopCommand = new RelayCommand(StopAsync);
        LaunchClientCommand = new RelayCommand(LaunchClientAsync);
        RefreshCommand = new RelayCommand(RefreshAsync);
        SaveConfigCommand = new RelayCommand(SaveConfigAsync);
        AddContentCommand = new RelayCommand(AddContentAsync);
        RemoveContentCommand = new RelayCommand<ServerContentFile>(RemoveContentAsync);
        OpenRuntimeCommand = new RelayCommand(OpenRuntime);
        OpenDotNetDownloadCommand = new RelayCommand(() => ExternalLink.Open("https://dotnet.microsoft.com/download/dotnet/10.0"));
        _ = RefreshBuildRequirementsAsync();
    }

    public void SetActiveInstall(BasisInstall install)
    {
        _install = install;
        OnPropertyChanged(nameof(HasInstall));
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(RuntimeDirectory));
        OnPropertyChanged(nameof(NeedsDotNet10Sdk));
        _ = RefreshAsync();
        _ = RefreshBuildRequirementsAsync();
    }

    public void ClearActiveInstall()
    {
        _install = null;
        OnPropertyChanged(nameof(HasInstall));
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(RuntimeDirectory));
        OnPropertyChanged(nameof(NeedsDotNet10Sdk));
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var edited = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_install is not null && Platform.PathsEqual(_configRoot, _install.RepoRoot))
            foreach (var row in ConfigFields.Where(x => x.IsEdited)) edited[row.Name] = row.Value;
        ConfigFields.Clear();
        ContentFiles.Clear();
        _configRoot = _install?.RepoRoot;
        _ = RefreshServerLeftOutAsync();
        if (_install is null) { RaiseCollections(); await RefreshServerPackagesAsync(); return; }
        try
        {
            foreach (var field in _service.LoadConfig(_install.RepoRoot))
            {
                var row = new ServerConfigFieldRow(field);
                if (edited.TryGetValue(row.Name, out var value)) row.Value = value;
                ConfigFields.Add(row);
            }
            foreach (var item in _service.ListContent(_install.RepoRoot)) ContentFiles.Add(item);
            var port = ConfigFields.FirstOrDefault(x => x.Name == "SetPort")?.Value;
            if (ushort.TryParse(port, out _)) Port = port!;
        }
        catch (Exception ex) { DiagnosticLog.Write("Loading the Basis server configuration", ex); SetStatus($"Could not load server configuration: {ex.Message}", StatusKind.Error); }
        RaiseCollections();
        await RefreshServerPackagesAsync();
    }

    private async Task RefreshServerLeftOutAsync()
    {
        var install = _install;
        var leftOut = false;
        if (install is not null)
        {
            try { leftOut = (await _parts.LeftOutAsync(install.RepoRoot, install.UnityProjectPath)).Contains(BasisPartsService.Server); }
            catch (Exception ex) { DiagnosticLog.Write("Checking whether the Basis Server is left out", ex); }
        }
        if (ReferenceEquals(install, _install)) IsServerLeftOut = leftOut;
    }

    private async Task AddServerBackAsync()
    {
        if (_install is null) { MissingInstall(); return; }
        await _shell.InstallsVM.IncludePartAsync(_install, BasisPartsService.Server);
    }

    private async Task RefreshServerPackagesAsync()
    {
        var install = _install;
        if (install is null || !ServerPackageService.HasServer(install.RepoRoot))
        {
            ServerPackages.Clear();
            AvailableServerPackages.Clear();
            CanManageServerPackages = false;
            ServerPackagesNotice = install is null ? "" : L.Tr("server.packages.noServer");
            RaiseServerPackages();
            return;
        }
        CanManageServerPackages = ServerPackageService.SupportsPackages(install.RepoRoot);
        ServerPackagesNotice = CanManageServerPackages ? "" : L.Tr("server.packages.unsupported");
        try
        {
            var packages = await _packages.ListAsync(install.RepoRoot);
            if (!ReferenceEquals(install, _install)) return;
            ServerPackages.Clear();
            foreach (var package in packages) ServerPackages.Add(new ServerPackageRow(package));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Listing the Basis server packages", ex);
            ServerPackagesNotice = L.Tr("server.packages.listError", ex.Message);
        }
        RaiseServerPackages();
        if (CanManageServerPackages) _ = RefreshAvailableServerPackagesAsync(install);
    }

    private async Task RefreshAvailableServerPackagesAsync(BasisInstall install)
    {
        try
        {
            var catalog = await LoadCatalogAsync();
            if (!ReferenceEquals(install, _install)) return;
            var installed = ServerPackages.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            AvailableServerPackages.Clear();
            foreach (var entry in _catalogs.AllLatest(catalog)
                .Where(e => e.Server && !string.IsNullOrWhiteSpace(e.Url) && !installed.Contains(e.Name))
                .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase))
                AvailableServerPackages.Add(entry);
        }
        catch (Exception ex) { DiagnosticLog.Write("Loading server packages from the package catalog", ex); }
        OnPropertyChanged(nameof(HasAvailableServerPackages));
    }

    private async Task<Catalog> LoadCatalogAsync()
    {
        if (_catalog is not null) return _catalog;
        var settings = await _settings.LoadAsync();
        return _catalog = await _catalogs.LoadAsync(settings.CatalogUrl);
    }

    private async Task AddServerPackageAsync()
    {
        if (_install is null) { MissingInstall(); return; }
        var install = _install;
        var input = ServerPackageSource.Trim();
        var looksLikeId = ServerPackageService.IsValidId(input) && !input.Contains('/') && !input.Contains(':');
        BasisPM.Core.Models.ServerPackageSource resolved;
        try { resolved = ServerPackageService.ResolveSource(install.RepoRoot, input, looksLikeId ? await LoadCatalogAsync() : null); }
        catch (Exception ex) { DiagnosticLog.Write("Resolving a Basis server package source", ex); SetStatus(L.Tr("server.packages.error", ex.Message), StatusKind.Error); return; }
        if (resolved.Error is not null) { SetStatus(resolved.Error, StatusKind.Error); return; }
        var source = resolved.Entry is null ? resolved.Source : await LatestReleaseAsync(resolved.Source) ?? resolved.Source;
        if (await InstallServerPackageAsync(install, source, resolved.ExpectedId, resolved.Entry?.DisplayName ?? input)) ServerPackageSource = "";
    }

    private async Task InstallServerCatalogPackageAsync(CatalogPackageVersion? entry)
    {
        if (entry?.Url is not { Length: > 0 } url || _install is null) return;
        var install = _install;
        await InstallServerPackageAsync(install, await LatestReleaseAsync(url) ?? url, entry.Name, entry.DisplayName);
    }

    private async Task<bool> InstallServerPackageAsync(BasisInstall install, string source, string? expectedId, string label)
    {
        IsBusy = true;
        SetStatus(L.Tr("server.packages.installing", label), StatusKind.Info, install);
        try
        {
            var result = await _packages.InstallAsync(install.RepoRoot, source, expectedId, line => Dispatcher.UIThread.Post(() => SetStatus(line, StatusKind.Info, install)));
            ReportPackageResult(result);
            return result.Ok;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Installing a Basis server package", ex);
            SetStatus(L.Tr("server.packages.error", ex.Message), StatusKind.Error);
            return false;
        }
        finally { IsBusy = false; await RefreshServerPackagesAsync(); }
    }

    private async Task UpdateServerPackageAsync(ServerPackageRow? row)
    {
        if (row is null || _install is null) return;
        var install = _install;
        var discard = false;
        if (await _packages.HasLocalWorkAsync(install.RepoRoot, row.Id))
        {
            if (!await Dialogs.ConfirmAsync(L.Tr("server.packages.discardTitle"), L.Tr("server.packages.discardUpdateBody", row.DisplayName))) return;
            discard = true;
        }
        IsBusy = true;
        SetStatus(L.Tr("server.packages.updating", row.DisplayName));
        try { ReportPackageResult(await _packages.UpdateAsync(install.RepoRoot, row.Id, null, discard, line => Dispatcher.UIThread.Post(() => SetStatus(line, StatusKind.Info, install)))); }
        catch (Exception ex) { DiagnosticLog.Write($"Updating Basis server package {row.Id}", ex); SetStatus(L.Tr("server.packages.error", ex.Message), StatusKind.Error); }
        finally { IsBusy = false; await RefreshServerPackagesAsync(); }
    }

    private async Task RemoveServerPackageAsync(ServerPackageRow? row)
    {
        if (row is null || _install is null) return;
        var install = _install;
        var discard = false;
        if (await _packages.HasLocalWorkAsync(install.RepoRoot, row.Id))
        {
            if (!await Dialogs.ConfirmAsync(L.Tr("server.packages.discardTitle"), L.Tr("server.packages.discardRemoveBody", row.DisplayName))) return;
            discard = true;
        }
        IsBusy = true;
        try { ReportPackageResult(await _packages.RemoveAsync(install.RepoRoot, row.Id, discard)); }
        catch (Exception ex) { DiagnosticLog.Write($"Removing Basis server package {row.Id}", ex); SetStatus(L.Tr("server.packages.error", ex.Message), StatusKind.Error); }
        finally { IsBusy = false; await RefreshServerPackagesAsync(); }
    }

    private async Task RestoreServerPackagesAsync()
    {
        if (_install is null) { MissingInstall(); return; }
        var install = _install;
        IsBusy = true;
        SetStatus(L.Tr("server.packages.restoring"));
        try { ReportPackageResult(await _packages.RestoreAsync(install.RepoRoot, line => Dispatcher.UIThread.Post(() => SetStatus(line, StatusKind.Info, install)))); }
        catch (Exception ex) { DiagnosticLog.Write("Restoring the Basis server packages", ex); SetStatus(L.Tr("server.packages.error", ex.Message), StatusKind.Error); }
        finally { IsBusy = false; await RefreshServerPackagesAsync(); }
    }

    private void OpenServerPackageFolder(ServerPackageRow? row)
    {
        if (row?.Folder is { } folder && Directory.Exists(folder)) ExternalLink.OpenFolder(folder);
        else SetStatus(L.Tr("server.packages.folderMissing"), StatusKind.Error);
    }

    private async Task<string?> LatestReleaseAsync(string gitUrl)
    {
        var location = UpmGitUrl.Parse(gitUrl);
        if (location is null || location.Ref is not null) return null;
        try
        {
            var versions = await _versions.GetVersionsAsync(gitUrl);
            return versions.LatestStable?.Ref is { } tag ? location.ToManifestUrl(tag, location.Path) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            DiagnosticLog.Write($"Resolving the latest release of {gitUrl}", ex);
            return null;
        }
    }

    private void ReportPackageResult(ServerPackageResult result) =>
        SetStatus(result.Ok ? L.Tr("server.packages.rebuildHint", result.Message) : result.Message, result.Ok ? StatusKind.Success : StatusKind.Error);

    private void RaiseServerPackages()
    {
        OnPropertyChanged(nameof(HasServerPackages));
        OnPropertyChanged(nameof(HasAvailableServerPackages));
    }

    private async Task BuildAsync()
    {
        if (_install is null) { MissingInstall(); return; }
        await BuildServerAsync(_install);
    }

    private async Task<bool> BuildServerAsync(BasisInstall install)
    {
        bool hasSdk;
        try { hasSdk = await _service.HasRequiredDotNetSdkAsync(); }
        catch (Exception ex) { DiagnosticLog.Write("Detecting the installed .NET SDK", ex); hasSdk = false; }
        if (!hasSdk)
        {
            HasDotNet10Sdk = false;
            DotNetStatus = ".NET 10 SDK not detected — install it before building the server.";
            SetStatus("The .NET 10 SDK is required to compile the Basis server.", StatusKind.Error, install);
            return false;
        }
        IsBusy = true;
        SetStatus("Building the Basis server…", StatusKind.Info, install);
        try
        {
            var result = await _service.BuildAsync(install.RepoRoot);
            SetStatus(result.Success ? "Basis server build completed." : $"Server build failed: {LastUsefulLine(result.Output)}",
                result.Success ? StatusKind.Success : StatusKind.Error, install);
            return result.Success;
        }
        catch (Exception ex) { DiagnosticLog.Write("Building the Basis server", ex); SetStatus($"Server build failed: {ex.Message}", StatusKind.Error, install); return false; }
        finally { IsBusy = false; await RefreshAsync(); }
    }

    private async Task RunAsync()
    {
        if (_install is null) { MissingInstall(); return; }
        try
        {
            if (_serverProcess is { HasExited: false }) { SetStatus("The Basis server is already running."); return; }
            StartServer(_install);
            SetStatus("Basis server started in its console. Complete the setup wizard there on first run.", StatusKind.Success);
        }
        catch (Exception ex) { DiagnosticLog.Write("Starting the Basis server", ex); SetStatus($"Could not start server: {ex.Message}", StatusKind.Error); }
        await Task.CompletedTask;
    }

    private async Task StopAsync()
    {
        try
        {
            if (_serverProcess is null || _serverProcess.HasExited) { SetStatus("No server started by this app is running."); return; }
            _serverProcess.Kill(true);
            await _serverProcess.WaitForExitAsync();
            SetStatus("Basis server stopped.", StatusKind.Success);
        }
        catch (Exception ex) { DiagnosticLog.Write("Stopping the Basis server", ex); SetStatus($"Could not stop server: {ex.Message}", StatusKind.Error); }
    }

    private async Task LaunchClientAsync()
    {
        if (_install is null) { MissingInstall(); return; }
        if (!ushort.TryParse(Port, out var port) || port == 0) { SetStatus("Enter a valid server port.", StatusKind.Error); return; }
        var local = BasisServerService.IsLocalHost(Host);
        // SetPort is a UDP listener, so a TCP probe always says "not listening" even when the
        // server is ready. Local launches use the server's configured HTTP health endpoint.
        var reachable = !local || await _service.IsReadyAsync(_install.RepoRoot, TimeSpan.FromMilliseconds(750));
        var trackedRunning = _serverProcess is { HasExited: false };

        if (local && !reachable && !trackedRunning)
        {
            var paths = _service.GetPaths(_install.RepoRoot);
            var built = File.Exists(paths.ExecutablePath);
            var message = built
                ? "The local Basis server does not appear to be running. Start it before launching the Steam client?"
                : "The local Basis server has not been built. Building requires the .NET 10 SDK. Build and start it before launching the Steam client?";
            if (await Dialogs.ConfirmAsync("Start Basis server?", message))
            {
                if (!built && !await BuildServerAsync(_install)) return;
                try
                {
                    StartServer(_install);
                    trackedRunning = true;
                    SetStatus("Server started. Waiting for it to accept connections…");
                }
                catch (Exception ex) { DiagnosticLog.Write("Starting the Basis server before connecting", ex); SetStatus($"Could not start server: {ex.Message}", StatusKind.Error); return; }
            }
        }

        if (local && trackedRunning && !reachable)
        {
            var serverProcess = _serverProcess!;
            reachable = await WaitForServerAsync(_install.RepoRoot, serverProcess, TimeSpan.FromMinutes(10));
            if (!reachable)
            {
                SetStatus(serverProcess.HasExited
                    ? $"The server exited before becoming ready (exit code {serverProcess.ExitCode})."
                    : "The server did not become ready. Check its console and health-endpoint configuration.", StatusKind.Error);
                return;
            }
        }

        var connection = _service.BuildClientConnection(_install.RepoRoot, Host, port, ConnectionPassword);
        try
        {
            if (_service.LaunchSteamClient(connection))
                SetStatus($"Launching Basis Labs through Steam and connecting to {Host}:{port}.", StatusKind.Success);
            else SetStatus("Steam could not be found. Install Steam or add it to PATH.", StatusKind.Error);
        }
        catch (Exception ex) { DiagnosticLog.Write("Launching the Basis client through Steam", ex); SetStatus($"Could not launch Steam: {ex.Message}", StatusKind.Error); }
    }

    private async Task SaveConfigAsync()
    {
        if (_install is null) { MissingInstall(); return; }
        try
        {
            _service.SaveConfig(_install.RepoRoot, ConfigFields.Select(x => x.ToModel()));
            foreach (var row in ConfigFields) row.MarkSaved();
            SetStatus("Server configuration saved. Restart the server to apply it.", StatusKind.Success);
        }
        catch (Exception ex) { DiagnosticLog.Write("Saving the Basis server configuration", ex); SetStatus($"Could not save server configuration: {ex.Message}", StatusKind.Error); }
        await Task.CompletedTask;
    }

    private async Task AddContentAsync()
    {
        if (_install is null) { MissingInstall(); return; }
        try
        {
            var mode = SelectedMode?.Value ?? 0;
            if (AddToDefaultLibrary) _service.AddDefaultLibraryItem(_install.RepoRoot, mode, ContentUrl, ContentPassword);
            else _service.AddInitialResource(_install.RepoRoot, mode switch { 0 => 2, 2 => 0, _ => 1 }, ContentUrl, ContentPassword);
            ContentUrl = "";
            ContentPassword = "";
            await RefreshAsync();
            SetStatus("Default server content added. Restart the server to load it.", StatusKind.Success);
        }
        catch (Exception ex) { DiagnosticLog.Write("Adding content to the Basis server", ex); SetStatus($"Could not add server content: {ex.Message}", StatusKind.Error); }
    }

    private async Task RemoveContentAsync(ServerContentFile? item)
    {
        if (item is null || _install is null) return;
        try { _service.RemoveContent(_install.RepoRoot, item); await RefreshAsync(); SetStatus($"Removed {item.Name}.", StatusKind.Success); }
        catch (Exception ex) { DiagnosticLog.Write("Removing content from the Basis server", ex); SetStatus($"Could not remove content: {ex.Message}", StatusKind.Error); }
    }

    private void OpenRuntime()
    {
        if (_install is null) return;
        if (!_service.HasServerProject(_install.RepoRoot)) { SetStatus(BasisServerService.MissingProjectMessage, StatusKind.Error); return; }
        try { Directory.CreateDirectory(RuntimeDirectory); }
        catch (Exception ex) { DiagnosticLog.Write("Creating the Basis server runtime folder", ex); SetStatus($"Could not open the runtime folder: {ex.Message}", StatusKind.Error); return; }
        ExternalLink.OpenFolder(RuntimeDirectory);
    }

    private void StartServer(BasisInstall install)
    {
        _serverProcess = _service.Start(install.RepoRoot);
        _ = RefreshAfterServerStartAsync(install.RepoRoot, _serverProcess);
    }

    private async Task RefreshAfterServerStartAsync(string repoRoot, Process process)
    {
        await RefreshAsync();
        var configFile = _service.GetPaths(repoRoot).ConfigFile;
        for (var attempt = 0; attempt < 120 && !process.HasExited; attempt++)
        {
            if (File.Exists(configFile))
            {
                if (Platform.PathsEqual(_install?.RepoRoot, repoRoot)) await RefreshAsync();
                return;
            }
            await Task.Delay(500);
        }
    }

    private async Task<bool> WaitForServerAsync(string repoRoot, Process process, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !process.HasExited)
        {
            if (await _service.IsReadyAsync(repoRoot, TimeSpan.FromSeconds(1))) return true;
            await Task.Delay(500);
        }
        return false;
    }

    private async Task RefreshBuildRequirementsAsync()
    {
        try
        {
            HasDotNet10Sdk = await _service.HasRequiredDotNetSdkAsync();
            DotNetStatus = HasDotNet10Sdk
                ? ".NET 10 SDK detected — this computer can compile the Basis server."
                : ".NET 10 SDK not detected — install it before building the Basis server.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Detecting the installed .NET SDK", ex);
            HasDotNet10Sdk = false;
            DotNetStatus = "Could not detect the .NET 10 SDK. It is required to compile the Basis server.";
        }
        _dotNetChecked = true;
        OnPropertyChanged(nameof(NeedsDotNet10Sdk));
    }

    private void MissingInstall() => SetStatus("Select a Basis project first.", StatusKind.Error);
    private void SetStatus(string message, StatusKind kind = StatusKind.Info, BasisInstall? install = null) => _shell.SetStatus(message, kind, (install ?? _install)?.DisplayName);
    private void RaiseCollections() { OnPropertyChanged(nameof(HasConfig)); OnPropertyChanged(nameof(HasContent)); }
    private static string LastUsefulLine(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "unknown error";
}

public sealed class ServerConfigFieldRow : ObservableObject
{
    private string _value;
    private string _savedValue;
    public string Name { get; }
    public string Description { get; }
    public bool IsSecret => Name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || Name.EndsWith("Key", StringComparison.OrdinalIgnoreCase);
    public bool IsNotSecret => !IsSecret;
    public string Value { get => _value; set => SetField(ref _value, value); }
    public bool IsEdited => !string.Equals(_value, _savedValue, StringComparison.Ordinal);
    public ServerConfigFieldRow(ServerConfigField field) { Name = field.Name; _value = _savedValue = field.Value; Description = field.Description; }
    public void MarkSaved() => _savedValue = _value;
    public ServerConfigField ToModel() => new(Name, Value, Description);
}

public sealed record ContentModeOption(string Name, int Value);

public sealed class ServerPackageRow
{
    public ServerPackageRow(ServerPackageInfo info) => Info = info;
    public ServerPackageInfo Info { get; }
    public string Id => Info.Id;
    public string DisplayName => Info.DisplayName;
    public string Version => Info.Version;
    public bool HasVersion => !string.IsNullOrWhiteSpace(Info.Version);
    public string? Folder => Info.Folder;
    public bool IsReady => Info.Status == ServerPackageStatus.Ready;
    public bool NeedsAttention => !IsReady;
    public bool CanUpdate => !Info.IsLocal;
    public string StatusText => Info.Status switch
    {
        ServerPackageStatus.Ready => L.Tr(Info.IsLinked ? "server.packages.status.linked" : "server.packages.status.ready"),
        ServerPackageStatus.NotRestored => L.Tr("server.packages.status.notRestored"),
        ServerPackageStatus.Changed => L.Tr("server.packages.status.changed"),
        ServerPackageStatus.Missing => L.Tr("server.packages.status.missing"),
        _ => L.Tr("server.packages.status.invalid"),
    };
    public string AssembliesText => L.Tr("server.packages.compiledInto", Info.Assemblies);
    public bool HasAssemblies => Info.Assemblies.Length > 0;
    public string SourceText => Info.IsLinked
        ? L.Tr("server.packages.builtFrom", Info.LinkedFolder)
        : Info.ShortCommit.Length > 0 ? $"{Info.Source} @ {Info.ShortCommit}" : Info.Source;
    public string? Detail => Info.Detail;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Info.Detail);
}
