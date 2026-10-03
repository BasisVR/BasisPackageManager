using System.Text;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var app = new ConsoleApplication();
        return args.Length == 0
            ? await app.RunInteractiveAsync()
            : await app.RunCommandAsync(args);
    }
}

internal sealed class ConsoleApplication
{
    private readonly GitService _git = new();
    private readonly UnityProjectService _projects = new();
    private readonly CatalogService _catalogs = new();
    private readonly PackageListService _packageLists = new();
    private readonly UserSettingsService _settings = new();
    private readonly UnityHubService _unityHub = new();
    private readonly MountRegistry _mountRegistry = new();
    private readonly BasisServerService _servers = new();
    private readonly BasisInstallService _installs;
    private readonly MountService _mounts;
    private CancellationTokenSource? _activeOperation;
    private string? _basisPath;
    private Catalog? _catalog;

    public ConsoleApplication()
    {
        _installs = new BasisInstallService(_projects, _git);
        _mounts = new MountService(_git, _projects, _mountRegistry);
        Console.CancelKeyPress += (_, e) =>
        {
            if (_activeOperation is null) return;
            e.Cancel = true;
            _activeOperation.Cancel();
            Console.WriteLine("\nCancelling current operation…");
        };
    }

    public async Task<int> RunInteractiveAsync()
    {
        WriteBanner();
        Console.WriteLine("Type 'help' for commands. Paths containing spaces can be placed in quotes.\n");
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write(_basisPath is null ? "basispm> " : $"basispm [{Path.GetFileName(_basisPath)}]> ");
            Console.ResetColor();
            var input = Console.ReadLine();
            if (input is null) return 0;

            string[] args;
            try { args = Tokenize(input).ToArray(); }
            catch (FormatException ex) { WriteError(ex.Message); continue; }
            if (args.Length == 0) continue;
            if (args[0].Equals("exit", StringComparison.OrdinalIgnoreCase)
                || args[0].Equals("quit", StringComparison.OrdinalIgnoreCase)) return 0;
            await RunCommandAsync(args);
            Console.WriteLine();
        }
    }

    public async Task<int> RunCommandAsync(IReadOnlyList<string> input)
    {
        var args = input.ToList();
        var projectOption = args.FindIndex(a => a.Equals("--project", StringComparison.OrdinalIgnoreCase));
        if (projectOption >= 0)
        {
            if (projectOption + 1 >= args.Count) { WriteError("--project requires a path."); return 2; }
            UseProject(args[projectOption + 1], quiet: true);
            args.RemoveRange(projectOption, 2);
        }
        if (args.Count == 0) { WriteHelp(); return 0; }

        var command = args[0].ToLowerInvariant();
        var values = args.Skip(1).ToArray();
        try
        {
            switch (command)
            {
                case "help": case "--help": case "-h": case "?": WriteHelp(); break;
                case "use": RequireCount(values, 1, "use <Basis clone path>"); UseProject(values[0]); break;
                case "clone": case "clone-basis": await CloneBasisAsync(values); break;
                case "status": await ShowStatusAsync(); break;
                case "list-packages": case "packages": await ListPackagesAsync(values); break;
                case "install-package": case "install": await InstallPackageAsync(values); break;
                case "list-package-lists": case "list-lists": await ListPackageListsAsync(); break;
                case "install-package-list": case "install-list": await InstallPackageListAsync(values); break;
                case "branches": await ListBranchesAsync(); break;
                case "change-branch": case "checkout": await ChangeBranchAsync(values); break;
                case "update-basis": case "pull": await UpdateBasisAsync(); break;
                case "open-unity": await OpenUnityAsync(); break;
                case "server-build": await BuildServerAsync(); break;
                case "server-run": await RunServerAsync(); break;
                case "server-config": await ServerConfigAsync(values); break;
                case "server-content": await ListServerContentAsync(); break;
                case "server-add-library": await AddServerContentAsync(values, library: true); break;
                case "server-add-startup": await AddServerContentAsync(values, library: false); break;
                case "connect-client": await ConnectClientAsync(values); break;
                case "exit": case "quit": break;
                default: WriteError($"Unknown command '{args[0]}'. Type 'help' to see available commands."); return 2;
            }
            return 0;
        }
        catch (OperationCanceledException) { WriteWarning("Operation cancelled."); return 130; }
        catch (Exception ex) { WriteError(ex.Message); return 1; }
    }

    private async Task CloneBasisAsync(IReadOnlyList<string> values)
    {
        RequireRange(values, 1, 2, "clone-basis <empty-folder> [branch]");
        if (!_git.IsAvailable) throw new InvalidOperationException("Git was not found on PATH.");
        var destination = Path.GetFullPath(values[0]);
        var branch = values.Count == 2 ? values[1] : BasisInstallService.DefaultBranch;
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidOperationException($"The destination is not empty: {destination}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destination);

        await RunOperationAsync(async ct =>
        {
            var result = await _git.CloneAsync(BasisInstallService.BasisRepoUrl, destination, branch,
                line => Console.WriteLine(line), ct);
            if (!result.Ok) throw new InvalidOperationException($"Clone failed: {Tail(result.Output)}");
        });

        var install = await _installs.LoadAsync(destination);
        if (!install.HasUnityProject)
            throw new InvalidOperationException("The clone completed, but its Basis Unity project could not be found.");
        _basisPath = destination;
        WriteSuccess($"Basis cloned to {destination}");
        Console.WriteLine($"Unity project: {install.UnityProjectPath}");
    }

    private void UseProject(string path, bool quiet = false)
    {
        var full = Path.GetFullPath(path);
        var detection = _projects.Detect(full);
        if (!detection.IsValid)
            throw new DirectoryNotFoundException($"'{full}' is not a Basis/Unity project: {detection.Reason}");
        if (!_installs.IsBasisCheckout(full))
            throw new InvalidOperationException($"'{full}' is a Unity project, but it is not a Basis checkout. Clone BasisVR/Basis first.");
        _basisPath = FindRepoRoot(full, detection.ResolvedPath!);
        if (!quiet) WriteSuccess($"Using Basis clone: {_basisPath}");
    }

    private async Task ShowStatusAsync()
    {
        var install = await LoadInstallAsync();
        Console.WriteLine($"Basis clone       : {install.RepoRoot}");
        Console.WriteLine($"Unity project     : {install.UnityProjectPath}");
        Console.WriteLine($"Packages folder   : {Path.Combine(install.UnityProjectPath, "Packages")}");
        Console.WriteLine($"Unity version     : {install.UnityVersion}");
        Console.WriteLine($"Bundled packages  : {_projects.ListEmbeddedPackages(install.UnityProjectPath).Count}");
        if (install.IsGitRepo)
        {
            var status = await _git.GetStatusAsync(install.RepoRoot);
            Console.WriteLine($"Branch / commit   : {status.Branch} @ {status.ShortCommit}");
            Console.WriteLine($"Local changes     : {status.ChangeCount}");
        }
    }

    private async Task ListPackagesAsync(IReadOnlyList<string> values)
    {
        RequireRange(values, 0, 1, "list-packages [search]");
        var install = await LoadInstallAsync();
        var catalog = await LoadCatalogAsync();
        var bundled = _projects.ListEmbeddedPackages(install.UnityProjectPath)
            .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var search = values.Count == 1 ? values[0] : null;
        var packages = _catalogs.AllLatest(catalog).Where(p => string.IsNullOrWhiteSpace(search)
            || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || p.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (p.Category?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        foreach (var package in packages.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var state = bundled.ContainsKey(package.Name) ? "included"
                : install.Manifest.Dependencies.ContainsKey(package.Name) ? "installed"
                : "available";
            Console.WriteLine($"{package.Name,-44} {package.DisplayName,-30} [{state}]");
        }
    }

    private async Task InstallPackageAsync(IReadOnlyList<string> values)
    {
        RequireCount(values, 1, "install-package <package-id>");
        var install = await LoadInstallAsync();
        var id = values[0];
        if (PackageIsBundled(install, id))
        {
            WriteSuccess($"{id} is already included with this Basis clone.");
            return;
        }

        var catalog = await LoadCatalogAsync();
        var entry = _catalogs.AllLatest(catalog).FirstOrDefault(p => p.Name.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Package '{id}' was not found.");
        var resolution = new DependencyResolver(_catalogs).Resolve(catalog, new[] { (entry.Name, $"^{entry.Version}") });
        if (resolution.Conflicts.Count > 0)
            throw new InvalidOperationException(string.Join("; ", resolution.Conflicts));

        foreach (var (name, version) in resolution.Resolved)
        {
            if (name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase) || PackageIsBundled(install, name)) continue;
            install.Manifest.Dependencies[name] = version.Url ?? version.Version;
        }
        await _projects.SaveManifestAsync(install.UnityProjectPath, install.Manifest);

        if (!string.IsNullOrWhiteSpace(entry.Url) && _git.IsAvailable)
        {
            await RunOperationAsync(async ct =>
            {
                var mounted = await _mounts.MountAsync(install, entry.Name, entry.Url,
                    line => Console.WriteLine(line), ct);
                if (!mounted.Ok) throw new InvalidOperationException(mounted.Error);
            });
        }
        else
        {
            install.Manifest.Dependencies[entry.Name] = entry.Url ?? entry.Version;
            await _projects.SaveManifestAsync(install.UnityProjectPath, install.Manifest);
        }
        WriteSuccess($"Installed {entry.DisplayName} into {install.DisplayName}.");
    }

    private async Task ListPackageListsAsync()
    {
        var settings = await _settings.LoadAsync();
        foreach (var list in await _packageLists.LoadAsync(settings.CatalogUrl))
            Console.WriteLine($"{list.Id,-28} {list.Name} ({list.Packages.Count} packages)");
    }

    private async Task InstallPackageListAsync(IReadOnlyList<string> values)
    {
        RequireCount(values, 1, "install-list <package-list-id>");
        var install = await LoadInstallAsync();
        var settings = await _settings.LoadAsync();
        var list = (await _packageLists.LoadAsync(settings.CatalogUrl))
            .FirstOrDefault(x => x.Id.Equals(values[0], StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Package list '{values[0]}' was not found.");
        foreach (var package in list.Packages)
        {
            if (string.IsNullOrWhiteSpace(package.Id) || PackageIsBundled(install, package.Id)) continue;
            var value = !string.IsNullOrWhiteSpace(package.GitUrl) ? package.GitUrl : package.Version;
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!string.IsNullOrWhiteSpace(package.GitUrl) && !GitUrlPolicy.IsSafeUrl(package.GitUrl))
            {
                WriteWarning($"Skipped {package.Id}: unsupported git URL.");
                continue;
            }
            install.Manifest.Dependencies[package.Id] = value.Trim();
        }
        await _projects.SaveManifestAsync(install.UnityProjectPath, install.Manifest);
        WriteSuccess($"Installed package list {list.Name}; bundled Basis packages were skipped.");
    }

    private async Task ListBranchesAsync()
    {
        var install = await LoadInstallAsync();
        foreach (var branch in await _git.ListBranchesAsync(install.RepoRoot)) Console.WriteLine(branch);
    }

    private async Task ChangeBranchAsync(IReadOnlyList<string> values)
    {
        RequireCount(values, 1, "change-branch <branch>");
        var install = await LoadInstallAsync();
        await _git.FetchAsync(install.RepoRoot, line => Console.WriteLine(line));
        var result = await _git.CheckoutAsync(install.RepoRoot, values[0]);
        if (!result.Ok) throw new InvalidOperationException(result.Output);
        WriteSuccess($"Switched to {values[0]}.");
    }

    private async Task UpdateBasisAsync()
    {
        var install = await LoadInstallAsync();
        var result = await _git.PullAsync(install.RepoRoot, line => Console.WriteLine(line));
        if (!result.Ok) throw new InvalidOperationException(result.Output);
        WriteSuccess("Basis is up to date.");
    }

    private async Task OpenUnityAsync()
    {
        var install = await LoadInstallAsync();
        var settings = await _settings.LoadAsync();
        var extraEditors = settings.ManualEditors.Select(x => x.ToInstalledEditor());
        var opened = await _unityHub.OpenProjectAsync(install.UnityProjectPath, install.UnityVersion,
            settings.UnityHubPath, extraEditors);
        if (!opened) throw new InvalidOperationException($"Unity {install.UnityVersion} could not be opened.");
        WriteSuccess($"Opening {install.DisplayName} in Unity {install.UnityVersion}.");
    }

    private async Task BuildServerAsync()
    {
        var install = await LoadInstallAsync();
        Console.WriteLine("Building the Basis server…");
        (bool success, string output) result = (false, "");
        await RunOperationAsync(async ct => result = await _servers.BuildAsync(install.RepoRoot, ct));
        if (!result.success) throw new InvalidOperationException($"Server build failed: {Tail(result.output)}");
        WriteSuccess($"Server built in {_servers.GetPaths(install.RepoRoot).RuntimeDirectory}");
    }

    private async Task RunServerAsync()
    {
        var install = await LoadInstallAsync();
        _servers.Start(install.RepoRoot);
        WriteSuccess("Basis server started in its console.");
    }

    private async Task ServerConfigAsync(IReadOnlyList<string> values)
    {
        RequireRange(values, 0, 2, "server-config [name value]");
        var install = await LoadInstallAsync();
        var fields = _servers.LoadConfig(install.RepoRoot).ToList();
        if (fields.Count == 0) throw new InvalidOperationException("Run the server once to generate config/config.xml.");
        if (values.Count == 0)
        {
            foreach (var field in fields) Console.WriteLine($"{field.Name,-42} {field.Value}");
            return;
        }
        if (values.Count != 2) throw new ArgumentException("Usage: server-config [name value]");
        var index = fields.FindIndex(x => x.Name.Equals(values[0], StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException($"Unknown server setting '{values[0]}'.");
        fields[index] = fields[index] with { Value = values[1] };
        _servers.SaveConfig(install.RepoRoot, fields);
        WriteSuccess($"Set {fields[index].Name}. Restart the server to apply it.");
    }

    private async Task ListServerContentAsync()
    {
        var install = await LoadInstallAsync();
        foreach (var item in _servers.ListContent(install.RepoRoot))
            Console.WriteLine($"{(item.IsDefaultLibrary ? "library" : "startup"),-9} {item.Name}");
    }

    private async Task AddServerContentAsync(IReadOnlyList<string> values, bool library)
    {
        RequireRange(values, 2, 3, library
            ? "server-add-library <avatar|world|prop> <url> [password]"
            : "server-add-startup <avatar|world|prop> <url> [password]");
        var install = await LoadInstallAsync();
        var logicalMode = values[0].ToLowerInvariant() switch
        {
            "avatar" => 0, "world" or "scene" => 1, "prop" => 2,
            _ => throw new ArgumentException("Content type must be avatar, world, or prop."),
        };
        var password = values.Count == 3 ? values[2] : "";
        var path = library
            ? _servers.AddDefaultLibraryItem(install.RepoRoot, logicalMode, values[1], password)
            : _servers.AddInitialResource(install.RepoRoot, logicalMode switch { 0 => 2, 2 => 0, _ => 1 }, values[1], password);
        WriteSuccess($"Added {Path.GetFileName(path)}.");
    }

    private async Task ConnectClientAsync(IReadOnlyList<string> values)
    {
        RequireRange(values, 0, 3, "connect-client [host] [port] [password]");
        _ = await LoadInstallAsync();
        var host = values.Count > 0 ? values[0] : "127.0.0.1";
        if (!ushort.TryParse(values.Count > 1 ? values[1] : "4296", out var port) || port == 0)
            throw new ArgumentException("Port must be between 1 and 65535.");
        var password = values.Count > 2 ? values[2] : "";
        if (!_servers.LaunchSteamClient(BasisServerService.BuildConnection(host, port, password)))
            throw new InvalidOperationException("Steam could not be found.");
        WriteSuccess($"Launching Basis Labs and connecting to {host}:{port}.");
    }

    private async Task<BasisInstall> LoadInstallAsync()
    {
        if (_basisPath is null)
            throw new InvalidOperationException("No Basis clone selected. Run 'use <path>' interactively or pass --project <path>.");
        return await _installs.LoadAsync(_basisPath);
    }

    private async Task<Catalog> LoadCatalogAsync()
    {
        if (_catalog is not null) return _catalog;
        var settings = await _settings.LoadAsync();
        return _catalog = await _catalogs.LoadAsync(settings.CatalogUrl);
    }

    private bool PackageIsBundled(BasisInstall install, string id) =>
        _projects.ListEmbeddedPackages(install.UnityProjectPath)
            .Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        using var cancellation = new CancellationTokenSource();
        _activeOperation = cancellation;
        try { await operation(cancellation.Token); }
        finally { _activeOperation = null; }
    }

    private static string FindRepoRoot(string selected, string unityProject)
    {
        var cursor = new DirectoryInfo(selected);
        while (cursor is not null)
        {
            if (Directory.Exists(Path.Combine(cursor.FullName, ".git")) || File.Exists(Path.Combine(cursor.FullName, ".git")))
                return cursor.FullName;
            cursor = cursor.Parent;
        }
        return Path.GetFullPath(selected == unityProject ? selected : Path.GetDirectoryName(unityProject) ?? selected);
    }

    private static void WriteBanner()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("Basis Package Manager Console");
        Console.ResetColor();
        Console.WriteLine("Clone Basis first, then manage additional packages and Unity.\n");
    }

    private static void WriteHelp() => Console.WriteLine("""
Usage
  basispm                              Start interactive console mode
  basispm --project <path> <command>   Run a command against a Basis clone

Basis
  clone-basis <empty-folder> [branch]  Clone BasisVR/Basis (default: developer)
  use <path>                           Select a clone in interactive mode
  status                               Show clone, Unity and bundled-package details
  branches                             List local and remote branches
  change-branch <branch>               Fetch and switch branch
  update-basis                         Fast-forward the selected Basis clone

Packages
  list-packages [search]               Show included, installed and available packages
  install-package <package-id>         Install an additional package; skip bundled ones
  list-package-lists                   List curated package lists
  install-list <list-id>               Add a package list; skip bundled packages

Unity
  open-unity                           Open the selected project in its required editor

Server
  server-build                         Publish the server from the selected Basis clone
  server-run                           Start the built server in its console
  server-config [name value]           List or change generated config.xml settings
  server-content                       List default-library and startup content files
  server-add-library <type> <url> [pw] Add avatar/world/prop to the client library
  server-add-startup <type> <url> [pw] Load avatar/world/prop when the server starts
  connect-client [host] [port] [pw]    Launch Basis Labs through Steam and connect

General
  help                                 Show this help
  exit                                 Exit interactive mode

Press Ctrl+C to cancel a clone or package download.
""");

    private static void RequireCount(IReadOnlyList<string> values, int count, string usage)
    {
        if (values.Count != count) throw new ArgumentException($"Usage: {usage}");
    }

    private static void RequireRange(IReadOnlyList<string> values, int min, int max, string usage)
    {
        if (values.Count < min || values.Count > max) throw new ArgumentException($"Usage: {usage}");
    }

    private static IEnumerable<string> Tokenize(string input)
    {
        var token = new StringBuilder();
        char? quote = null;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                else if (c == '\\' && i + 1 < input.Length && input[i + 1] == quote) token.Append(input[++i]);
                else token.Append(c);
            }
            else if (c is '\'' or '"') quote = c;
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

    private static void WriteSuccess(string message) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(message); Console.ResetColor(); }
    private static void WriteWarning(string message) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(message); Console.ResetColor(); }
    private static void WriteError(string message) { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine($"Error: {message}"); Console.ResetColor(); }
}
