using System.Diagnostics;
using System.Reflection;
using System.Text;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private readonly GitService _git;
    private readonly UnityProjectService _projects;
    private readonly CatalogService _catalogs;
    private readonly PackageListService _packageLists;
    private readonly UserSettingsService _settings;
    private readonly UnityHubService _unityHub;
    private readonly UnityReleaseService _unityReleases;
    private readonly MountRegistry _mountRegistry;
    private readonly BasisInstallService _installs;
    private readonly MountService _mounts;
    private readonly BasisDevService _basisDev;
    private readonly BasisUpdateService _updates;
    private readonly ServerPackageService _serverPackages;
    private readonly BasisServerService _server;
    private readonly VersionService _versions;
    private readonly GitHubAuthService _ghAuth;
    private readonly GitHubApiService _ghApi;
    private readonly GitHubService _github;
    private readonly BasisContributeService _contribute;
    private readonly IReadOnlyList<CliCommand> _commands;
    private readonly Dictionary<string, CliCommand> _byName;
    private CancellationTokenSource? _activeOperation;
    private bool _cancelRequested;
    private DateTime _lastInterrupt;
    private Process? _foreground;
    private bool _interactive;
    private bool _noColor;
    private string? _sessionProject;
    private string? _commandProject;
    private int _exitCode;
    private Catalog? _catalog;
    private readonly string _stateDirectory;

    public ConsoleApplication(UserSettingsService? settings = null, MountRegistry? mountRegistry = null, string? stateDirectory = null, CatalogService? catalogs = null)
    {
        // Keep dependency construction explicit; instance field initialization otherwise runs before
        // this body and silently depends on declaration order.
        _git = new GitService();
        _projects = new UnityProjectService();
        _catalogs = catalogs ?? new CatalogService();
        _packageLists = new PackageListService();
        _settings = settings ?? new UserSettingsService();
        _unityHub = new UnityHubService();
        _unityReleases = new UnityReleaseService();
        _mountRegistry = mountRegistry ?? new MountRegistry();
        _installs = new BasisInstallService(_projects, _git);
        _mounts = new MountService(_git, _projects, _mountRegistry);
        _basisDev = new BasisDevService(_git, _projects, _mountRegistry, _mounts);
        _updates = new BasisUpdateService(_git);
        _serverPackages = new ServerPackageService(_git);
        _server = new BasisServerService();
        _versions = new VersionService(git: _git);
        _ghAuth = new GitHubAuthService();
        _ghApi = new GitHubApiService();
        _github = new GitHubService();
        _contribute = new BasisContributeService(_git, _updates, _ghApi);
        _stateDirectory = stateDirectory ?? AppDataPaths.Root;
        _commands = BuildCommands();
        _byName = new Dictionary<string, CliCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in _commands)
            foreach (var name in command.Names) _byName[name] = command;
        Console.CancelKeyPress += (_, e) =>
        {
            if (_foreground is { HasExited: false })
            {
                e.Cancel = true;
                _foregroundInterrupted = true;
                return;
            }
            var operation = _activeOperation;
            if (operation is not null && !operation.IsCancellationRequested)
            {
                e.Cancel = true;
                _cancelRequested = true;
                try { operation.Cancel(); }
                catch (ObjectDisposedException) { return; }
                Out.Warning("Cancelling the current operation" + Out.Ellipsis + " (press Ctrl+C again to quit at once)");
                return;
            }
            if (!_interactive || DateTime.UtcNow - _lastInterrupt < TimeSpan.FromSeconds(2)) return;
            _lastInterrupt = DateTime.UtcNow;
            e.Cancel = true;
            Out.Hint("Press Ctrl+C again to leave the console.");
        };
    }

    internal IReadOnlyList<CliCommand> Commands => _commands;

    public async Task<int> RunAsync(IReadOnlyList<string> input)
    {
        var args = input.ToList();
        _noColor = args.Any(a => a.Equals("--no-color", StringComparison.OrdinalIgnoreCase));
        Out.Configure(_noColor);
        var rest = args.Where(a => !a.Equals("--no-color", StringComparison.OrdinalIgnoreCase)).ToList();
        if (rest.Count == 0) return await RunInteractiveAsync(null);
        if (rest.Count == 2 && rest[0].Equals("--project", StringComparison.OrdinalIgnoreCase)) return await RunInteractiveAsync(rest[1]);
        if (rest.Count == 1 && rest[0].StartsWith("--project=", StringComparison.OrdinalIgnoreCase)) return await RunInteractiveAsync(rest[0]["--project=".Length..]);
        return await RunCommandAsync(args);
    }

    public async Task<int> RunInteractiveAsync(string? project)
    {
        _interactive = true;
        Out.Say(new Span("Basis Package Manager console", Tone.Accent), new Span("  " + VersionText, Tone.Dim));
        Out.Note("Type 'help' for commands, 'help <command>' for details, Tab to complete, 'exit' to leave.");
        try
        {
            var settings = await _settings.LoadAsync();
            var fromEnvironment = Environment.GetEnvironmentVariable("BASISPM_PROJECT");
            if (project is not null) _sessionProject = PickProject(settings, project).Root;
            else if (!string.IsNullOrWhiteSpace(fromEnvironment)) _sessionProject = PickProject(settings, fromEnvironment).Root;
            else _sessionProject = FindProjectAt(settings, Environment.CurrentDirectory) ?? DefaultProject(settings) ?? OnlyProject(settings);
            if (_sessionProject is not null) Out.Note($"Using {ProjectName(settings, _sessionProject)} ({_sessionProject}). 'use <name>' picks another.");
            else if (SavedProjects(settings).Count > 1) Out.Note("Pick a project with 'use <name>' ('projects' lists them).");
            else Out.Note("No project yet: 'clone-basis <folder>' makes one, 'projects add <path>' adds an existing clone.");
            _ = PrefetchCatalogAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write("Selecting the console project", ex);
            Out.Error(ex.Message);
        }
        Out.Say();

        var editor = new LineEditor(Path.Combine(_stateDirectory, "console-history.txt"), CompleteForEditor);
        while (true)
        {
            var line = editor.ReadLine(await PromptTextAsync(), Tone.Accent);
            if (line is null) return 0;
            string[] args;
            try { args = Tokenize(line).ToArray(); }
            catch (FormatException ex) { DiagnosticLog.Write("Parsing interactive console command", ex); Out.Error(ex.Message); continue; }
            if (args.Length > 0 && args[0].Equals("basispm", StringComparison.OrdinalIgnoreCase)) args = args[1..];
            if (args.Length == 0) continue;
            if (args[0].Length > 1 && args[0][0] == '/' && FindCommand(args[0][1..]) is not null) args[0] = args[0][1..];
            if (args[0].ToLowerInvariant() is "exit" or "quit") return 0;
            await RunCommandAsync(args);
            Out.Say();
        }
    }

    private async Task PrefetchCatalogAsync()
    {
        try
        {
            var catalog = await FetchCatalogAsync(await _settings.LoadAsync());
            _catalog ??= catalog;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            DiagnosticLog.Write("Loading the package registry in the background", ex);
        }
    }

    private async Task<string> PromptTextAsync()
    {
        if (_sessionProject is null) return "basispm> ";
        try { return $"basispm [{ProjectName(await _settings.LoadAsync(), _sessionProject)}]> "; }
        catch (Exception ex) when (ex is not OperationCanceledException) { DiagnosticLog.Write("Building the console prompt", ex); return "basispm> "; }
    }

    public async Task<int> RunCommandAsync(IReadOnlyList<string> input)
    {
        var args = input.ToList();
        var json = TakeEvery(args, "--json");
        var noColor = TakeEvery(args, "--no-color");
        var wantsHelp = false;
        string? project;
        try
        {
            project = TakeGlobalValue(args, "--project");
            var commandIndex = args.FindIndex(a => !a.StartsWith('-'));
            wantsHelp = commandIndex >= 0 && (TakeEvery(args, "--help") | TakeEvery(args, "-h"));
        }
        catch (UsageException ex)
        {
            Out.Error(ex.Message);
            return 2;
        }
        if (noColor && !_noColor) Out.Configure(true);
        Out.JsonMode = json;
        _commandProject = project;
        _exitCode = 0;
        _cancelRequested = false;
        try { return await DispatchAsync(args, json, wantsHelp); }
        finally
        {
            Out.StopStatus();
            Out.JsonMode = false;
            _commandProject = null;
            if (noColor && !_noColor) Out.Configure(false);
        }
    }

    private async Task<int> DispatchAsync(List<string> args, bool json, bool wantsHelp)
    {
        if (args.Count == 0)
        {
            WriteHelp();
            return 0;
        }
        if (args[0].Equals("--version", StringComparison.OrdinalIgnoreCase) && !json)
        {
            Out.Line("basispm " + VersionText);
            return 0;
        }
        var name = args[0].ToLowerInvariant() switch { "--version" => "version", "--help" or "-h" => "help", var other => other };
        var command = FindCommand(name);
        if (command is null)
        {
            Out.Error(UnknownCommandMessage(args[0]));
            return 2;
        }
        if (wantsHelp)
        {
            WriteCommandHelp(command);
            return 0;
        }
        if (json && !command.Json)
        {
            Out.Error($"'{command.Name}' has no JSON output.");
            return 2;
        }
        var values = NormalizeOptions(command, args.Skip(1).ToList());
        try
        {
            CheckOptions(command, values);
            await command.Run(values);
            return _exitCode;
        }
        catch (UsageException ex)
        {
            Out.StopStatus();
            Out.Error(ex.Message);
            Out.Hint(UsageHint(command));
            return 2;
        }
        catch (OperationCanceledException ex) when (_cancelRequested)
        {
            DiagnosticLog.Write($"Cancelling CLI command {command.Name}", ex);
            Out.StopStatus();
            Out.Warning("Cancelled.");
            return 130;
        }
        catch (OperationCanceledException ex)
        {
            DiagnosticLog.Write($"Timing out CLI command {command.Name}", ex);
            Out.StopStatus();
            Out.Error("Something took too long to answer and timed out. Check your connection, then try again.");
            Out.Hint($"Details are in {DiagnosticLog.FilePath}");
            return 1;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Running CLI command {command.Name}", ex);
            Out.StopStatus();
            Out.Error(ex.Message);
            return 1;
        }
    }

    private static string VersionText
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(ConsoleApplication).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var plus = version.IndexOf('+');
            return plus > 0 ? version[..plus] : version;
        }
    }

    private async Task<BasisInstall> LoadInstallAsync()
    {
        var settings = await _settings.LoadAsync();
        var root = ResolveProject(settings);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The project folder {root} is missing. Take it off the list with 'projects remove \"{ProjectName(settings, root)}\"'.");
        return await _installs.LoadAsync(root, AliasOf(settings, root));
    }

    private string ResolveProject(UserSettings settings)
    {
        if (_commandProject is not null) return PickProject(settings, _commandProject).Root;
        if (_sessionProject is not null) return _sessionProject;
        if (Environment.GetEnvironmentVariable("BASISPM_PROJECT") is { Length: > 0 } fromEnvironment) return PickProject(settings, fromEnvironment).Root;
        var picked = FindProjectAt(settings, Environment.CurrentDirectory) ?? DefaultProject(settings) ?? OnlyProject(settings);
        if (picked is not null)
        {
            if (!_interactive && !Out.JsonMode) Out.Hint($"Project: {ProjectName(settings, picked)} ({picked})");
            return picked;
        }
        var saved = SavedProjects(settings).Where(p => Directory.Exists(p.Root)).ToList();
        if (saved.Count == 0)
            throw new InvalidOperationException("No Basis project is set up yet. Clone one with 'clone-basis <folder>', add an existing clone with 'projects add <path>', or pass --project <path>.");
        throw new InvalidOperationException($"Which project? Pass --project <name>, run the command inside a project folder{(_interactive ? ", pick one with 'use <name>'" : "")}, or set one with 'projects default <name>'. Saved projects: {string.Join(", ", saved.Select(p => p.Name))}.");
    }

    private string? DefaultProject(UserSettings settings) =>
        LoadState().DefaultProject is { Length: > 0 } root && IsSaved(settings, root) && Directory.Exists(root) ? settings.Installs.First(r => Platform.PathsEqual(r, root)) : null;

    private static string? OnlyProject(UserSettings settings) =>
        SavedProjects(settings).Where(p => Directory.Exists(p.Root)).ToList() is { Count: 1 } only ? only[0].Root : null;

    internal sealed class ConsoleState
    {
        public string? DefaultProject { get; set; }
    }

    private string StatePath => Path.Combine(_stateDirectory, "console.json");

    private ConsoleState LoadState()
    {
        try { return File.Exists(StatePath) ? System.Text.Json.JsonSerializer.Deserialize<ConsoleState>(File.ReadAllText(StatePath)) ?? new ConsoleState() : new ConsoleState(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            DiagnosticLog.Write($"Reading {StatePath}", ex);
            return new ConsoleState();
        }
    }

    private void SaveState(ConsoleState state)
    {
        Directory.CreateDirectory(_stateDirectory);
        AtomicFile.WriteAllText(StatePath, System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private (string Root, string Name) PickProject(UserSettings settings, string value)
    {
        var saved = SavedProjects(settings);
        var trimmed = value.Trim();
        if (!LooksLikePath(trimmed) || !Directory.Exists(trimmed))
        {
            if (int.TryParse(trimmed.TrimStart('#'), out var number) && number >= 1 && number <= saved.Count) return saved[number - 1];
            if (MatchSaved(saved, trimmed) is { } match)
            {
                if (Directory.Exists(trimmed) && _projects.Detect(Path.GetFullPath(trimmed)) is { IsValid: true, ResolvedPath: { } here } && !Platform.PathsEqual(RootOf(settings, Path.GetFullPath(trimmed), here), match.Root))
                    throw new InvalidOperationException($"'{trimmed}' is both the saved project {match.Root} and the folder {Path.GetFullPath(trimmed)}. Use '.{Path.DirectorySeparatorChar}{trimmed}' for the folder, or the project's number from 'projects'.");
                return match;
            }
        }
        var full = FullPath(trimmed);
        var detection = _projects.Detect(full);
        if (!detection.IsValid)
        {
            var suggestions = Suggest(trimmed, saved.Select(p => p.Name));
            throw new DirectoryNotFoundException(Directory.Exists(full)
                ? $"'{full}' is not a Basis/Unity project: {detection.Reason}"
                : $"'{trimmed}' isn't a saved project or a folder." + (suggestions.Count > 0 ? $" Did you mean {JoinOr(suggestions.Select(s => $"'{s}'"))}?" : saved.Count > 0 ? $" Saved projects: {string.Join(", ", saved.Select(p => p.Name))}." : ""));
        }
        var root = RootOf(settings, full, detection.ResolvedPath!);
        return (root, ProjectName(settings, root));
    }

    private static (string Root, string Name)? MatchSaved(List<(string Root, string Name)> saved, string value)
    {
        var matches = saved.Select((p, i) => (Project: p, Number: i + 1)).Where(x => x.Project.Name.Equals(value, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) matches = saved.Select((p, i) => (Project: p, Number: i + 1)).Where(x => Path.GetFileName(x.Project.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Equals(value, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1)
            throw new InvalidOperationException($"{matches.Count} projects are called {value}: {string.Join(", ", matches.Select(m => $"#{m.Number} {m.Project.Root}"))}. Pick one by its number or path, or give them different names with 'projects rename'.");
        return matches.Count == 1 ? matches[0].Project : null;
    }

    private static string RootOf(UserSettings settings, string selected, string unityProject)
    {
        var saved = SavedProjects(settings).Where(p => Directory.Exists(p.Root) && IsAtOrInside(unityProject, p.Root)).OrderByDescending(p => p.Root.Length).FirstOrDefault();
        return saved.Root ?? FindRepoRoot(selected, unityProject);
    }

    private static bool IsAtOrInside(string path, string folder)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Platform.PathsEqual(full, parent) || full.StartsWith(parent + Path.DirectorySeparatorChar, Platform.PathComparison());
    }

    private static bool LooksLikePath(string value) =>
        value.Contains(Path.DirectorySeparatorChar) || value.Contains('/') || value.StartsWith('.') || value.StartsWith('~') || Path.IsPathRooted(value);

    private string? FindProjectAt(UserSettings settings, string folder)
    {
        try
        {
            var detection = _projects.Detect(folder);
            if (!detection.IsValid || detection.ResolvedPath is null || !_installs.IsBasisCheckout(detection.ResolvedPath)) return null;
            if (!IsAtOrInside(folder, detection.ResolvedPath) && Directory.EnumerateDirectories(folder).Count(_projects.IsUnityProject) != 1) return null;
            return RootOf(settings, folder, detection.ResolvedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Looking for a Basis project around {folder}", ex);
            return null;
        }
    }

    private static List<(string Root, string Name)> SavedProjects(UserSettings settings) =>
        settings.Installs.Where(r => !string.IsNullOrWhiteSpace(r)).Select(root => (root, ProjectName(settings, root))).ToList();

    private static string ProjectName(UserSettings settings, string root) => AliasOf(settings, root) ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static string? AliasOf(UserSettings settings, string root) =>
        settings.InstallAliases.FirstOrDefault(kv => Platform.PathsEqual(kv.Key, root)).Value is { Length: > 0 } alias ? alias : null;

    private static bool IsSaved(UserSettings settings, string root) => settings.Installs.Any(r => Platform.PathsEqual(r, root));

    private async Task<Catalog> LoadCatalogAsync()
    {
        if (_catalog is not null) return _catalog;
        var settings = await _settings.LoadAsync();
        var spin = !Out.IsBusy;
        if (spin) Out.Busy("Loading the package registry" + Out.Ellipsis);
        try { return _catalog = await FetchCatalogAsync(settings); }
        finally { if (spin) Out.StopStatus(); }
    }

    private async Task<Catalog> FetchCatalogAsync(UserSettings settings)
    {
        var catalog = await _catalogs.LoadAsync(settings.CatalogUrl);
        foreach (var url in settings.ExtraCatalogUrls.Where(u => !string.IsNullOrWhiteSpace(u)))
            if (await _catalogs.TryLoadAsync(url.Trim()) is { } extra)
                foreach (var (id, package) in extra.Packages) catalog.Packages.TryAdd(id, package);
        return catalog;
    }

    private static string FullPath(string path) =>
        Path.GetFullPath(path.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path);

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation, string? busy = null)
    {
        using var cancellation = new CancellationTokenSource();
        _activeOperation = cancellation;
        var spin = busy is not null && !Out.IsBusy;
        if (spin) Out.Busy(busy!);
        try { await operation(cancellation.Token); }
        finally
        {
            _activeOperation = null;
            if (spin) Out.StopStatus();
        }
    }

    private static bool Confirm(string question)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Add --yes to go ahead without a confirmation prompt.");
        Out.Prompt(question + " [y/N] ", Tone.Warn);
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    private static string FindRepoRoot(string selected, string unityProject)
    {
        var cursor = new DirectoryInfo(unityProject);
        while (cursor is not null)
        {
            if (Directory.Exists(Path.Combine(cursor.FullName, ".git")) || File.Exists(Path.Combine(cursor.FullName, ".git")))
                return cursor.FullName;
            cursor = cursor.Parent;
        }
        return Path.GetFullPath(Platform.PathsEqual(Path.GetFullPath(selected), Path.GetFullPath(unityProject)) ? unityProject : Path.GetDirectoryName(unityProject) ?? selected);
    }

    private static void Expect(IReadOnlyList<string> values, int min, int max, string what)
    {
        if (values.Count < min) throw new UsageException($"Missing {what}.");
        if (values.Count > max) throw new UsageException($"Unexpected {(values.Count - max == 1 ? "argument" : "arguments")}: {string.Join(" ", values.Skip(max).Select(v => $"'{v}'"))}.");
    }

    private static bool TakeFlag(List<string> options, string flag)
    {
        var index = options.FindIndex(o => o.Equals(flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        options.RemoveAt(index);
        return true;
    }

    private static bool TakeEvery(List<string> options, string flag)
    {
        var found = false;
        while (TakeFlag(options, flag)) found = true;
        return found;
    }

    private static string? TakeValue(List<string> options, string name)
    {
        var index = options.FindIndex(o => o.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index + 1 >= options.Count) throw new UsageException($"{name} needs a value.");
        var value = options[index + 1];
        options.RemoveRange(index, 2);
        return value;
    }

    private static List<string> TakeValues(List<string> options, string name)
    {
        var values = new List<string>();
        while (TakeValue(options, name) is { } value) values.Add(value);
        return values;
    }

    private static string? TakeGlobalValue(List<string> options, string name)
    {
        var inline = options.FindIndex(o => o.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        if (inline >= 0)
        {
            var value = options[inline][(name.Length + 1)..];
            options.RemoveAt(inline);
            return value.Length > 0 ? value : throw new UsageException($"{name} needs a value.");
        }
        return TakeValue(options, name);
    }

    private static int? TakeInt(List<string> options, string name, int min, int max)
    {
        if (TakeValue(options, name) is not { } text) return null;
        return int.TryParse(text, out var value) && value >= min && value <= max ? value : throw new UsageException($"{name} takes a number from {min} to {max}.");
    }

    internal static IEnumerable<string> Tokenize(string input)
    {
        var token = new StringBuilder();
        char? quote = null;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                else if (c == '\\' && i + 2 < input.Length && input[i + 1] == quote && !char.IsWhiteSpace(input[i + 2])) token.Append(input[++i]);
                else token.Append(c);
            }
            else if (c == '"' || c == '\'' && token.Length == 0) quote = c;
            else if (char.IsWhiteSpace(c)) { if (token.Length > 0) { yield return token.ToString(); token.Clear(); } }
            else token.Append(c);
        }
        if (quote is not null) throw new FormatException("The command contains an unclosed quote.");
        if (token.Length > 0) yield return token.ToString();
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? text.Trim() : lines[^1].Trim();
    }

    private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "" : sha.Length > 9 ? sha[..9] : sha;

    private static string Plural(int count, string one, string? many = null) => $"{count:N0} {(count == 1 ? one : many ?? one + "s")}";
}
