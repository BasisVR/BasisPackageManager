using System.Collections.ObjectModel;
using System.Diagnostics;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class ServerViewModel : ObservableObject
{
    private readonly BasisServerService _service;
    private readonly MainWindowViewModel _shell;
    private BasisInstall? _install;
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
    public bool HasDotNet10Sdk { get => _hasDotNet10Sdk; private set => SetField(ref _hasDotNet10Sdk, value); }
    public string DotNetStatus { get => _dotNetStatus; private set => SetField(ref _dotNetStatus, value); }
    public string ProjectName => _install?.DisplayName ?? "No Basis project selected";
    public string RuntimeDirectory => _install is null ? "" : _service.GetPaths(_install.RepoRoot).RuntimeDirectory;
    public string Host { get => _host; set => SetField(ref _host, value); }
    public string Port { get => _port; set => SetField(ref _port, value); }
    public string ConnectionPassword { get => _connectionPassword; set => SetField(ref _connectionPassword, value); }
    public string ContentUrl { get => _contentUrl; set => SetField(ref _contentUrl, value); }
    public string ContentPassword { get => _contentPassword; set => SetField(ref _contentPassword, value); }
    public ContentModeOption? SelectedMode { get => _selectedMode; set => SetField(ref _selectedMode, value); }
    public bool AddToDefaultLibrary { get => _addToDefaultLibrary; set { if (SetField(ref _addToDefaultLibrary, value)) OnPropertyChanged(nameof(AddAsInitialResource)); } }
    public bool AddAsInitialResource { get => !_addToDefaultLibrary; set { if (value) AddToDefaultLibrary = false; OnPropertyChanged(); } }

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

    public ServerViewModel(BasisServerService service, MainWindowViewModel shell)
    {
        _service = service;
        _shell = shell;
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
        _ = RefreshAsync();
        _ = RefreshBuildRequirementsAsync();
    }

    public async Task RefreshAsync()
    {
        ConfigFields.Clear();
        ContentFiles.Clear();
        if (_install is null) { RaiseCollections(); return; }
        try
        {
            foreach (var field in _service.LoadConfig(_install.RepoRoot)) ConfigFields.Add(new ServerConfigFieldRow(field));
            foreach (var item in _service.ListContent(_install.RepoRoot)) ContentFiles.Add(item);
            var port = ConfigFields.FirstOrDefault(x => x.Name == "SetPort")?.Value;
            if (ushort.TryParse(port, out _)) Port = port!;
        }
        catch (Exception ex) { DiagnosticLog.Write("Loading the Basis server configuration", ex); _shell.SetStatus($"Could not load server configuration: {ex.Message}", StatusKind.Error); }
        RaiseCollections();
        await Task.CompletedTask;
    }

    private async Task BuildAsync()
    {
        if (_install is null) { MissingInstall(); return; }
        await BuildServerAsync(_install);
    }

    private async Task<bool> BuildServerAsync(BasisInstall install)
    {
        if (!await _service.HasRequiredDotNetSdkAsync())
        {
            HasDotNet10Sdk = false;
            DotNetStatus = ".NET 10 SDK not detected — install it before building the server.";
            _shell.SetStatus("The .NET 10 SDK is required to compile the Basis server.", StatusKind.Error);
            return false;
        }
        IsBusy = true;
        _shell.SetStatus("Building the Basis server…");
        try
        {
            var result = await _service.BuildAsync(install.RepoRoot);
            _shell.SetStatus(result.Success ? "Basis server build completed." : $"Server build failed: {LastUsefulLine(result.Output)}",
                result.Success ? StatusKind.Success : StatusKind.Error);
            return result.Success;
        }
        catch (Exception ex) { DiagnosticLog.Write("Building the Basis server", ex); _shell.SetStatus($"Server build failed: {ex.Message}", StatusKind.Error); return false; }
        finally { IsBusy = false; await RefreshAsync(); }
    }

    private async Task RunAsync()
    {
        if (_install is null) { MissingInstall(); return; }
        try
        {
            if (_serverProcess is { HasExited: false }) { _shell.SetStatus("The Basis server is already running."); return; }
            StartServer(_install);
            _shell.SetStatus("Basis server started in its console. Complete the setup wizard there on first run.", StatusKind.Success);
        }
        catch (Exception ex) { DiagnosticLog.Write("Starting the Basis server", ex); _shell.SetStatus($"Could not start server: {ex.Message}", StatusKind.Error); }
        await Task.CompletedTask;
    }

    private async Task StopAsync()
    {
        try
        {
            if (_serverProcess is null || _serverProcess.HasExited) { _shell.SetStatus("No server started by this app is running."); return; }
            _serverProcess.Kill(true);
            await _serverProcess.WaitForExitAsync();
            _shell.SetStatus("Basis server stopped.", StatusKind.Success);
        }
        catch (Exception ex) { DiagnosticLog.Write("Stopping the Basis server", ex); _shell.SetStatus($"Could not stop server: {ex.Message}", StatusKind.Error); }
    }

    private async Task LaunchClientAsync()
    {
        if (_install is null) { MissingInstall(); return; }
        if (!ushort.TryParse(Port, out var port) || port == 0) { _shell.SetStatus("Enter a valid server port.", StatusKind.Error); return; }
        var local = BasisServerService.IsLocalHost(Host);
        var reachable = await BasisServerService.CanConnectAsync(Host, port, TimeSpan.FromMilliseconds(350));
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
                    _shell.SetStatus("Server started. Waiting for it to accept connections…");
                }
                catch (Exception ex) { DiagnosticLog.Write("Starting the Basis server before connecting", ex); _shell.SetStatus($"Could not start server: {ex.Message}", StatusKind.Error); return; }
            }
        }

        if (local && trackedRunning && !reachable)
        {
            reachable = await WaitForServerAsync(Host, port, TimeSpan.FromSeconds(60));
            if (!reachable)
            {
                _shell.SetStatus("The server is not listening yet. Complete its first-run setup in the server console, then launch again.", StatusKind.Error);
                return;
            }
        }

        var connection = BasisServerService.BuildConnection(Host, port, ConnectionPassword);
        if (_service.LaunchSteamClient(connection))
            _shell.SetStatus($"Launching Basis Labs through Steam and connecting to {Host}:{port}.", StatusKind.Success);
        else _shell.SetStatus("Steam could not be found. Install Steam or add it to PATH.", StatusKind.Error);
        await Task.CompletedTask;
    }

    private async Task SaveConfigAsync()
    {
        if (_install is null) { MissingInstall(); return; }
        try
        {
            _service.SaveConfig(_install.RepoRoot, ConfigFields.Select(x => x.ToModel()));
            _shell.SetStatus("Server configuration saved. Restart the server to apply it.", StatusKind.Success);
        }
        catch (Exception ex) { DiagnosticLog.Write("Saving the Basis server configuration", ex); _shell.SetStatus($"Could not save server configuration: {ex.Message}", StatusKind.Error); }
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
            _shell.SetStatus("Default server content added. Restart the server to load it.", StatusKind.Success);
        }
        catch (Exception ex) { DiagnosticLog.Write("Adding content to the Basis server", ex); _shell.SetStatus($"Could not add server content: {ex.Message}", StatusKind.Error); }
    }

    private async Task RemoveContentAsync(ServerContentFile? item)
    {
        if (item is null || _install is null) return;
        try { _service.RemoveContent(_install.RepoRoot, item); await RefreshAsync(); _shell.SetStatus($"Removed {item.Name}.", StatusKind.Success); }
        catch (Exception ex) { DiagnosticLog.Write("Removing content from the Basis server", ex); _shell.SetStatus($"Could not remove content: {ex.Message}", StatusKind.Error); }
    }

    private void OpenRuntime()
    {
        if (_install is null) return;
        Directory.CreateDirectory(RuntimeDirectory);
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

    private static async Task<bool> WaitForServerAsync(string host, ushort port, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (await BasisServerService.CanConnectAsync(host, port, TimeSpan.FromMilliseconds(500))) return true;
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
    }

    private void MissingInstall() => _shell.SetStatus("Select a Basis project first.", StatusKind.Error);
    private void RaiseCollections() { OnPropertyChanged(nameof(HasConfig)); OnPropertyChanged(nameof(HasContent)); }
    private static string LastUsefulLine(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "unknown error";
}

public sealed class ServerConfigFieldRow : ObservableObject
{
    private string _value;
    public string Name { get; }
    public string Description { get; }
    public bool IsSecret => Name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || Name.EndsWith("Key", StringComparison.OrdinalIgnoreCase);
    public bool IsNotSecret => !IsSecret;
    public string Value { get => _value; set => SetField(ref _value, value); }
    public ServerConfigFieldRow(ServerConfigField field) { Name = field.Name; _value = field.Value; Description = field.Description; }
    public ServerConfigField ToModel() => new(Name, Value, Description);
}

public sealed record ContentModeOption(string Name, int Value);
