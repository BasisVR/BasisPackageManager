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
    private readonly GitService _git;
    private readonly UnityProjectService _projects;
    private readonly CatalogService _catalogs;
    private readonly PackageListService _packageLists;
    private readonly UserSettingsService _settings;
    private readonly UnityHubService _unityHub;
    private readonly MountRegistry _mountRegistry;
    private readonly BasisInstallService _installs;
    private readonly MountService _mounts;
    private readonly BasisUpdateService _updates;
    private readonly ServerPackageService _serverPackages;
    private readonly BasisServerService _server;
    private readonly VersionService _versions;
    private CancellationTokenSource? _activeOperation;
    private string? _basisPath;
    private Catalog? _catalog;

    public ConsoleApplication()
    {
        // Keep dependency construction explicit; instance field initialization otherwise runs before
        // this body and silently depends on declaration order.
        _git = new GitService();
        _projects = new UnityProjectService();
        _catalogs = new CatalogService();
        _packageLists = new PackageListService();
        _settings = new UserSettingsService();
        _unityHub = new UnityHubService();
        _mountRegistry = new MountRegistry();
        _installs = new BasisInstallService(_projects, _git);
        _mounts = new MountService(_git, _projects, _mountRegistry);
        _updates = new BasisUpdateService(_git);
        _serverPackages = new ServerPackageService(_git);
        _server = new BasisServerService();
        _versions = new VersionService(git: _git);
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
            catch (FormatException ex) { DiagnosticLog.Write("Parsing interactive console command", ex); WriteError(ex.Message); continue; }
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
            try { UseProject(args[projectOption + 1], quiet: true); }
            catch (Exception ex) { DiagnosticLog.Write("Selecting the CLI project", ex); WriteError(ex.Message); return 2; }
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
                case "update-basis": case "update": case "pull": await UpdateBasisAsync(values); break;
                case "check-updates": case "check": await CheckUpdatesAsync(); break;
                case "conflicts": await ShowConflictsAsync(); break;
                case "resolve": await ResolveConflictAsync(values); break;
                case "basis-branch": await BasisBranchAsync(values); break;
                case "open-unity": await OpenUnityAsync(); break;
                case "server-packages": case "server-list": await ListServerPackagesAsync(values); break;
                case "server-install": case "server-add": await InstallServerPackageAsync(values); break;
                case "server-update": await UpdateServerPackagesAsync(values); break;
                case "server-remove": case "server-uninstall": await RemoveServerPackageAsync(values); break;
                case "server-restore": await RestoreServerPackagesAsync(values); break;
                case "server-link": await LinkServerPackageAsync(values); break;
                case "server-unlink": await UnlinkServerPackageAsync(values); break;
                case "server-build": await BuildServerAsync(values); break;
                case "exit": case "quit": break;
                default: WriteError($"Unknown command '{args[0]}'. Type 'help' to see available commands."); return 2;
            }
            return 0;
        }
        catch (OperationCanceledException ex) { DiagnosticLog.Write($"Cancelling CLI command {command}", ex); WriteWarning("Operation cancelled."); return 130; }
        catch (Exception ex) { DiagnosticLog.Write($"Running CLI command {command}", ex); WriteError(ex.Message); return 1; }
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
        await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, install.Manifest);

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
            await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, install.Manifest);
        }
        WriteSuccess($"Installed {entry.DisplayName} into {install.DisplayName}.");
        if (entry.Server && !string.IsNullOrWhiteSpace(entry.Url)) await InstallServerSideAsync(install, entry, entry.Url);
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
            if (!string.IsNullOrWhiteSpace(package.GitUrl) && !GitUrlPolicy.IsSafeDependencyUrl(package.GitUrl))
            {
                WriteWarning($"Skipped {package.Id}: unsupported git URL.");
                continue;
            }
            install.Manifest.Dependencies[package.Id] = value.Trim();
        }
        await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, install.Manifest);
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

    private async Task UpdateBasisAsync(IReadOnlyList<string> values)
    {
        var options = values.ToList();
        var install = await LoadInstallAsync();
        if (TakeFlag(options, "--continue"))
        {
            BasisUpdateResult continued = null!;
            await RunOperationAsync(async ct => continued = await _updates.ContinueAsync(install.RepoRoot, Console.WriteLine, ct));
            await ReportUpdateAsync(install, continued);
            return;
        }
        if (TakeFlag(options, "--abort"))
        {
            await ReportUpdateAsync(install, await _updates.AbortAsync(install.RepoRoot));
            return;
        }
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        var link = TakeFlag(options, "--link");
        var initGit = TakeFlag(options, "--init-git");
        var branch = TakeValue(options, "--branch");
        if (options.Count > 0)
            throw new ArgumentException("Usage: update-basis [--branch <name>] [--yes] [--link] [--init-git] | --continue | --abort");

        var plan = await PlanUpdateAsync(install, branch);
        if (plan.Kind == BasisUpdateKind.NotGitRepo)
        {
            if (!initGit)
                throw new InvalidOperationException("This project isn't tracked by git. Run 'update-basis --init-git' to record it in a new local git repository first (nothing is uploaded).");
            var init = await _updates.InitializeRepositoryAsync(install.RepoRoot, install.UnityProjectPath, Console.WriteLine);
            if (!init.Ok) throw new InvalidOperationException(DescribeFailure(init.Failure, init.Detail));
            plan = await PlanUpdateAsync(install, branch);
        }
        if (plan.Kind == BasisUpdateKind.Unrelated)
        {
            var match = plan.SuggestedBase
                ?? throw new InvalidOperationException("This project's git history isn't connected to Basis, and no matching Basis version was found to start from.");
            Console.WriteLine($"Closest Basis version: {match.ShortSha} {match.Date:yyyy-MM-dd} \"{match.Subject}\" ({match.MatchRatio:P0} of its files match).");
            if (!link)
                throw new InvalidOperationException("Run 'update-basis --link' to record that version as this project's starting point and merge the newer Basis changes.");
            var linked = await _updates.LinkAsync(install.RepoRoot, match.Sha);
            if (!linked.Ok) throw new InvalidOperationException(DescribeFailure(linked.Failure, linked.Detail));
            plan = await PlanUpdateAsync(install, branch);
        }

        switch (plan.Kind)
        {
            case BasisUpdateKind.InProgress:
                WriteWarning("A Basis update is already in progress.");
                await ShowConflictsAsync();
                return;
            case BasisUpdateKind.UpToDate:
                WriteSuccess($"Already up to date with Basis {plan.BasisBranch} ({Short(plan.UpstreamSha)}).");
                return;
            case BasisUpdateKind.Blocked:
                throw new InvalidOperationException(DescribeBlock(plan));
            case BasisUpdateKind.FastForward or BasisUpdateKind.Merge:
                break;
            default:
                throw new InvalidOperationException("This project can't be updated automatically.");
        }

        PrintPlan(plan);
        if (plan.BlockingPaths.Count > 0)
            throw new InvalidOperationException("Files ignored by git are in the way of new Basis files. Remove or move them, then try again:\n  " + string.Join("\n  ", plan.BlockingPaths.Take(10)));
        if (UnityProjectService.IsOpenInUnity(install.UnityProjectPath))
        {
            if (OperatingSystem.IsWindows()) throw new InvalidOperationException("Unity has this project open. Close Unity, then run the update again.");
            WriteWarning("Unity may still have this project open. Close it first if it is running.");
        }
        if (!assumeYes)
        {
            if (Console.IsInputRedirected) throw new InvalidOperationException("Add --yes to update without a confirmation prompt.");
            Console.Write("Update now? [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                WriteWarning("Update cancelled.");
                return;
            }
        }

        BasisUpdateResult result = null!;
        await RunOperationAsync(async ct => result = await _updates.ApplyAsync(install.RepoRoot, plan, Console.WriteLine, ct));
        await ReportUpdateAsync(install, result);
    }

    private async Task<BasisUpdatePlan> PlanUpdateAsync(BasisInstall install, string? branch)
    {
        BasisUpdatePlan plan = null!;
        await RunOperationAsync(async ct => plan = await _updates.PlanAsync(install.RepoRoot, branch, Console.WriteLine, ct));
        return plan;
    }

    private static void PrintPlan(BasisUpdatePlan plan)
    {
        Console.WriteLine($"Basis {plan.BasisBranch} -> {plan.LocalBranch}: {plan.IncomingCount} new Basis commit(s).");
        foreach (var commit in plan.IncomingCommits.Take(15))
            Console.WriteLine($"  {commit.ShortSha} {commit.Date:yyyy-MM-dd} {commit.Subject}");
        if (plan.IncomingCount > 15) Console.WriteLine($"  … and {plan.IncomingCount - 15} more");
        Console.WriteLine(plan.Kind == BasisUpdateKind.FastForward
            ? "Your branch has no commits of its own, so it moves straight to the new Basis."
            : $"Your {plan.LocalCommitCount} commit(s) will be merged with Basis in a new merge commit.");
        if (plan.UncommittedCount > 0)
            Console.WriteLine($"{plan.UncommittedCount} uncommitted change(s) stay as they are; {plan.CollidingPaths.Count} of them are files Basis also changes and will be merged back after the update.");
        if (plan.PredictedConflicts is { Count: > 0 } predicted)
        {
            Console.WriteLine($"{predicted.Count} file(s) changed on both sides will need a decision:");
            foreach (var path in predicted.Take(10)) Console.WriteLine($"  {path}");
        }
    }

    private async Task ReportUpdateAsync(BasisInstall before, BasisUpdateResult result)
    {
        switch (result.Kind)
        {
            case BasisUpdateResultKind.Updated:
                var after = await _installs.LoadAsync(before.RepoRoot);
                WriteSuccess($"Updated {after.DisplayName} to the latest Basis ({Short(result.NewHead)}).");
                if (!string.Equals(before.UnityVersion, after.UnityVersion, StringComparison.Ordinal))
                    WriteWarning($"Basis now uses Unity {after.UnityVersion} (was {before.UnityVersion}).");
                break;
            case BasisUpdateResultKind.Aborted:
                WriteSuccess("Update cancelled. The project is back to how it was before the update.");
                break;
            case BasisUpdateResultKind.Conflicts:
                PrintConflicts(result.Phase ?? BasisUpdatePhase.Merging, result.Conflicts ?? Array.Empty<BasisConflict>());
                break;
            default:
                throw new InvalidOperationException(DescribeFailure(result.Failure, result.Detail));
        }
    }

    private async Task CheckUpdatesAsync()
    {
        var install = await LoadInstallAsync();
        BasisUpdateCheck check = null!;
        await RunOperationAsync(async ct => check = await _updates.CheckAsync(install.RepoRoot, allowFetch: true, Console.WriteLine, ct));
        switch (check.Status)
        {
            case BasisUpdateStatus.UpToDate:
                WriteSuccess($"Up to date with Basis {check.BasisBranch}.");
                break;
            case BasisUpdateStatus.UpdateAvailable when check.InProgress:
                WriteWarning("A Basis update is in progress. Run 'conflicts' to see what still needs a decision.");
                break;
            case BasisUpdateStatus.UpdateAvailable:
                WriteWarning(check.Behind is int behind
                    ? $"Basis {check.BasisBranch} has {behind} new commit(s). Run 'update-basis' to update."
                    : $"A newer Basis {check.BasisBranch} is available. Run 'update-basis' to update.");
                break;
            case BasisUpdateStatus.NotGitRepo:
                WriteWarning("This project isn't tracked by git, so updates can't be checked. Run 'update-basis --init-git' to set that up.");
                break;
            default:
                throw new InvalidOperationException(check.Detail ?? "Couldn't check for Basis updates.");
        }
    }

    private async Task ShowConflictsAsync()
    {
        var install = await LoadInstallAsync();
        var state = await _updates.LoadStateAsync(install.RepoRoot);
        if (state is null)
        {
            WriteSuccess("No Basis update is in progress.");
            return;
        }
        PrintConflicts(state.Phase, await _updates.GetConflictsAsync(install.RepoRoot));
    }

    private static void PrintConflicts(BasisUpdatePhase phase, IReadOnlyList<BasisConflict> conflicts)
    {
        WriteWarning(phase == BasisUpdatePhase.Merging
            ? "Basis and your project changed the same files. Choose a version for each one:"
            : "Basis is updated, but some of your uncommitted edits overlap the new Basis changes. Choose a version for each one:");
        foreach (var conflict in conflicts)
            Console.WriteLine($"  [{DescribeConflict(conflict.Kind)}] {conflict.Path}{(conflict.IsUnityAsset ? "  (Unity asset: pick a version, don't hand-merge)" : "")}");
        Console.WriteLine(conflicts.Count == 0
            ? "Nothing left to decide. Run 'update-basis --continue' to finish."
            : "Use 'resolve <path> mine|basis|done' (or 'resolve --all mine|basis'), then 'update-basis --continue'. 'update-basis --abort' puts everything back.");
    }

    private async Task ResolveConflictAsync(IReadOnlyList<string> values)
    {
        RequireCount(values, 2, "resolve <path>|--all mine|basis|done");
        var choice = values[1].ToLowerInvariant() switch
        {
            "mine" => ConflictChoice.Mine,
            "basis" => ConflictChoice.Basis,
            "done" => ConflictChoice.Resolved,
            _ => throw new ArgumentException("Usage: resolve <path>|--all mine|basis|done"),
        };
        var install = await LoadInstallAsync();
        var conflicts = await _updates.GetConflictsAsync(install.RepoRoot);
        var targets = values[0] == "--all"
            ? conflicts.Select(c => c.Path).ToList()
            : new List<string> { values[0].Replace('\\', '/') };
        if (values[0] == "--all" && choice == ConflictChoice.Resolved) throw new ArgumentException("'--all' needs mine or basis.");
        foreach (var path in targets)
        {
            var resolved = await _updates.ResolveAsync(install.RepoRoot, path, choice);
            if (!resolved.Ok) throw new InvalidOperationException(DescribeFailure(resolved.Failure, resolved.Detail));
            WriteSuccess($"Resolved {path}.");
        }
        var remaining = (await _updates.GetConflictsAsync(install.RepoRoot)).Count;
        Console.WriteLine(remaining == 0
            ? "All files are decided. Run 'update-basis --continue' to finish the update."
            : $"{remaining} file(s) still need a decision.");
    }

    private async Task BasisBranchAsync(IReadOnlyList<string> values)
    {
        RequireRange(values, 0, 1, "basis-branch [branch]");
        var install = await LoadInstallAsync();
        if (values.Count == 1)
        {
            var set = await _updates.SetBasisBranchAsync(install.RepoRoot, values[0]);
            if (!set.Ok) throw new InvalidOperationException("Couldn't save the Basis branch. Make sure the project is on a git branch.");
            WriteSuccess($"This branch now follows Basis {values[0]}.");
            return;
        }
        var heads = await _updates.GetBasisHeadsAsync(refresh: true);
        Console.WriteLine($"Follows Basis: {await _updates.ResolveBasisBranchAsync(install.RepoRoot, heads)}");
        foreach (var name in BasisUpdateService.OrderBranches(heads.Keys).Take(12)) Console.WriteLine($"  {name}");
    }

    private static string DescribeBlock(BasisUpdatePlan plan) => plan.Block switch
    {
        BasisUpdateBlock.GitMissing => "Git was not found on PATH.",
        BasisUpdateBlock.GitTooOld => $"Git {plan.Detail} is too old; Basis updates need Git {BasisUpdateService.MinimumGitVersion.ToString(2)} or newer.",
        BasisUpdateBlock.DetachedHead => "The project isn't on a branch. Switch to a branch first (change-branch <name>).",
        BasisUpdateBlock.OperationInProgress => $"A git {plan.Detail} is already in progress in this project. Finish or abort it first.",
        BasisUpdateBlock.BranchNotFound => $"Basis has no branch called '{plan.Detail}'. Pick another with --branch.",
        BasisUpdateBlock.ShallowClone => "This is a shallow clone, so its history can't be matched to Basis. Run 'git fetch --unshallow' first.",
        _ => $"Couldn't download Basis: {plan.Detail}",
    };

    private static string DescribeFailure(BasisUpdateFailure failure, string? detail) => failure switch
    {
        BasisUpdateFailure.HeadMoved => "The project changed since the update was checked. Run update-basis again.",
        BasisUpdateFailure.AlreadyInProgress => "A Basis update is already in progress. Run 'conflicts' to see it.",
        BasisUpdateFailure.NoUpdateInProgress => "No Basis update is in progress.",
        BasisUpdateFailure.IgnoredFilesInTheWay => "Files ignored by git are in the way of new Basis files:\n  " + detail?.Replace("\n", "\n  "),
        BasisUpdateFailure.MarkersRemain => $"{detail} still contains conflict markers (<<<<<<< / >>>>>>>). Finish editing it first.",
        BasisUpdateFailure.LibraryNotIgnored => $"'{detail}' isn't covered by a .gitignore, so git would record the Library cache. Add a Unity .gitignore first.",
        BasisUpdateFailure.SetAsideFailed => $"Couldn't set your edits aside before updating: {detail}",
        BasisUpdateFailure.MergeFailed => $"Git couldn't merge Basis, so nothing was changed: {detail}",
        BasisUpdateFailure.CommitFailed => $"Couldn't record the merge: {detail}",
        BasisUpdateFailure.RestoreFailed => $"Basis is updated, but your edits couldn't be put back yet (they are safe in the git stash): {detail}",
        BasisUpdateFailure.AbortFailed => $"Couldn't undo the update: {detail}",
        BasisUpdateFailure.ResolveFailed => $"Couldn't resolve that file: {detail}",
        BasisUpdateFailure.LinkFailed => $"Couldn't link the project to Basis: {detail}",
        BasisUpdateFailure.InitFailed => $"Couldn't record the project in git: {detail}",
        _ => "This project can't be updated automatically.",
    };

    private static string DescribeConflict(ConflictKind kind) => kind switch
    {
        ConflictKind.BothChanged => "both changed",
        ConflictKind.BothAdded => "both added",
        ConflictKind.DeletedByYou => "you deleted, Basis changed",
        ConflictKind.DeletedByBasis => "Basis deleted, you changed",
        _ => "both deleted",
    };

    private static bool TakeFlag(List<string> options, string flag)
    {
        var index = options.FindIndex(o => o.Equals(flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        options.RemoveAt(index);
        return true;
    }

    private static string? TakeValue(List<string> options, string name)
    {
        var index = options.FindIndex(o => o.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index + 1 >= options.Count) throw new ArgumentException($"{name} needs a value.");
        var value = options[index + 1];
        options.RemoveRange(index, 2);
        return value;
    }

    private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "" : sha.Length > 9 ? sha[..9] : sha;

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

    private async Task ListServerPackagesAsync(IReadOnlyList<string> values)
    {
        RequireCount(values, 0, "server-packages");
        var install = await LoadServerInstallAsync();
        var packages = await _serverPackages.ListAsync(install.RepoRoot);
        if (packages.Count == 0)
        {
            Console.WriteLine("No server packages are installed. Add one with 'server-install <package-id|git-url|file:path>'.");
            return;
        }
        foreach (var package in packages)
        {
            Console.WriteLine($"{package.Id,-44} {package.Version,-12} {DescribeServerStatus(package.Status),-14} {package.ShortCommit,-8} {package.Assemblies}");
            Console.WriteLine($"    {(package.IsLinked ? "built from " + package.LinkedFolder : package.Source)}");
            if (!string.IsNullOrWhiteSpace(package.Detail)) Console.WriteLine($"    {package.Detail}");
        }
    }

    private async Task InstallServerPackageAsync(IReadOnlyList<string> values)
    {
        var options = values.ToList();
        var link = TakeValue(options, "--link");
        if (options.Count != 1) throw new ArgumentException("Usage: server-install <package-id|git-url|file:path> [--link <folder>]");
        var install = await LoadServerInstallAsync();
        var (source, expectedId) = await ResolveServerSourceAsync(install, options[0]);
        ServerPackageResult result = null!;
        if (link is not null) result = await _serverPackages.InstallLinkedAsync(install.RepoRoot, source, Path.GetFullPath(link), expectedId);
        else await RunOperationAsync(async ct => result = await _serverPackages.InstallAsync(install.RepoRoot, source, expectedId, Console.WriteLine, ct));
        Report(result);
        Console.WriteLine("Run 'server-build' (or build the server) to compile it in.");
    }

    private async Task<(string Source, string? ExpectedId)> ResolveServerSourceAsync(BasisInstall install, string value)
    {
        var looksLikeId = ServerPackageService.IsValidId(value.Trim()) && !value.Contains('/') && !value.Contains(':');
        var resolved = ServerPackageService.ResolveSource(install.RepoRoot, value, looksLikeId ? await LoadCatalogAsync() : null);
        if (resolved.Error is not null) throw new InvalidOperationException(resolved.Error);
        return resolved.Entry is null ? (resolved.Source, resolved.ExpectedId) : (await LatestReleaseUrlAsync(resolved.Source) ?? resolved.Source, resolved.ExpectedId);
    }

    private async Task<string?> LatestReleaseUrlAsync(string gitUrl)
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

    private async Task UpdateServerPackagesAsync(IReadOnlyList<string> values)
    {
        var options = values.ToList();
        var force = TakeFlag(options, "--force");
        var gitRef = TakeValue(options, "--ref");
        if (options.Count > 1 || (gitRef is not null && options.Count == 0))
            throw new ArgumentException("Usage: server-update [package-id] [--ref <branch|tag|commit>] [--force]");
        var install = await LoadServerInstallAsync();
        var ids = options.Count == 1 ? options.ToArray() : _serverPackages.LoadManifest(install.RepoRoot).Dependencies.Keys.ToArray();
        if (ids.Length == 0)
        {
            WriteSuccess("No server packages are installed.");
            return;
        }
        var failures = 0;
        foreach (var id in ids)
        {
            ServerPackageResult result = null!;
            await RunOperationAsync(async ct => result = await _serverPackages.UpdateAsync(install.RepoRoot, id, gitRef, force, Console.WriteLine, ct));
            if (result.Ok) WriteSuccess(result.Message);
            else { WriteError(result.Message); failures++; }
        }
        if (failures > 0) throw new InvalidOperationException($"{failures} server package(s) could not be updated.");
    }

    private async Task RemoveServerPackageAsync(IReadOnlyList<string> values)
    {
        var options = values.ToList();
        var force = TakeFlag(options, "--force");
        if (options.Count != 1) throw new ArgumentException("Usage: server-remove <package-id> [--force]");
        var install = await LoadServerInstallAsync();
        Report(await _serverPackages.RemoveAsync(install.RepoRoot, options[0], force));
    }

    private async Task RestoreServerPackagesAsync(IReadOnlyList<string> values)
    {
        RequireCount(values, 0, "server-restore");
        var install = await LoadServerInstallAsync();
        ServerPackageResult result = null!;
        await RunOperationAsync(async ct => result = await _serverPackages.RestoreAsync(install.RepoRoot, Console.WriteLine, ct));
        Report(result);
    }

    private async Task LinkServerPackageAsync(IReadOnlyList<string> values)
    {
        RequireCount(values, 2, "server-link <package-id> <folder>");
        var install = await LoadServerInstallAsync();
        Report(await _serverPackages.LinkAsync(install.RepoRoot, values[0], Path.GetFullPath(values[1])));
    }

    private async Task UnlinkServerPackageAsync(IReadOnlyList<string> values)
    {
        RequireCount(values, 1, "server-unlink <package-id>");
        var install = await LoadServerInstallAsync();
        Report(await _serverPackages.UnlinkAsync(install.RepoRoot, values[0]));
    }

    private async Task BuildServerAsync(IReadOnlyList<string> values)
    {
        RequireCount(values, 0, "server-build");
        var install = await LoadInstallAsync();
        if (!_server.HasServerProject(install.RepoRoot)) throw new InvalidOperationException(BasisServerService.MissingProjectMessage);
        if (!await _server.HasRequiredDotNetSdkAsync()) throw new InvalidOperationException("The .NET 10 SDK is required to build the Basis server.");
        Console.WriteLine("Building the Basis server…");
        var result = (Success: false, Output: "");
        await RunOperationAsync(async ct => result = await _server.BuildAsync(install.RepoRoot, ct));
        if (!result.Success)
            throw new InvalidOperationException("The server build failed:\n" + string.Join('\n', result.Output.Split('\n').TakeLast(25)));
        WriteSuccess($"Built the Basis server into {_server.GetPaths(install.RepoRoot).RuntimeDirectory}.");
    }

    private async Task InstallServerSideAsync(BasisInstall install, CatalogPackageVersion entry, string url)
    {
        if (!ServerPackageService.SupportsPackages(install.RepoRoot)) return;
        if (_serverPackages.LoadManifest(install.RepoRoot).Dependencies.ContainsKey(entry.Name)) return;
        var mounted = MountedPackageRoot(install, entry.Name);
        ServerPackageResult result = null!;
        if (mounted is not null) result = await _serverPackages.InstallLinkedAsync(install.RepoRoot, url, mounted, entry.Name);
        else await RunOperationAsync(async ct => result = await _serverPackages.InstallAsync(install.RepoRoot, url, entry.Name, Console.WriteLine, ct));
        if (result.Ok) WriteSuccess(result.Message);
        else WriteWarning($"The Unity side is installed, but the Basis Server side was not: {result.Message}");
    }

    private string? MountedPackageRoot(BasisInstall install, string id)
    {
        if (_mountRegistry.Find(install.UnityProjectPath, id) is not { } mount || !Directory.Exists(mount.FolderPath)) return null;
        var root = UpmGitUrl.Parse(mount.OriginalManifestValue)?.Path is { Length: > 0 } sub && !File.Exists(Path.Combine(mount.FolderPath, "package.json"))
            ? Path.Combine(mount.FolderPath, sub)
            : mount.FolderPath;
        return File.Exists(Path.Combine(root, "package.json")) ? root : null;
    }

    private async Task<BasisInstall> LoadServerInstallAsync()
    {
        var install = await LoadInstallAsync();
        if (!ServerPackageService.HasServer(install.RepoRoot))
            throw new InvalidOperationException("This project has no Basis Server source (Basis Server/BasisNetworkCore).");
        return install;
    }

    private static void Report(ServerPackageResult result)
    {
        if (!result.Ok) throw new InvalidOperationException(result.Message);
        WriteSuccess(result.Message);
    }

    private static string DescribeServerStatus(ServerPackageStatus status) => status switch
    {
        ServerPackageStatus.Ready => "ready",
        ServerPackageStatus.NotRestored => "not restored",
        ServerPackageStatus.Changed => "changed",
        ServerPackageStatus.Missing => "missing",
        _ => "invalid",
    };

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
        _mountRegistry.Find(install.UnityProjectPath, id) is { } mount && Directory.Exists(mount.FolderPath)
        || _projects.ListEmbeddedPackages(install.UnityProjectPath)
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
  check-updates                        Check BasisVR/Basis for a newer version
  update-basis [--branch <name>]       Merge the latest BasisVR/Basis into the current branch
               [--yes] [--link] [--init-git]
  update-basis --continue | --abort    Finish or undo an update that is waiting on decisions
  conflicts                            List files an update needs a decision on
  resolve <path>|--all mine|basis|done Keep your version, take Basis's, or mark an edited file done
  basis-branch [branch]                Show or set the Basis branch this branch follows

Packages
  list-packages [search]               Show included, installed and available packages
  install-package <package-id>         Install an additional package; skip bundled ones
  list-package-lists                   List curated package lists
  install-list <list-id>               Add a package list; skip bundled packages

Basis Server
  server-packages                      List the packages built into this project's Basis Server
  server-install <id|git-url|file:path> Add a server package (registry id, git URL with
               [--link <folder>]       optional ?path= and #ref, or a local folder)
  server-update [id] [--ref <ref>]     Move git packages to the newest commit of their ref
               [--force]
  server-remove <id> [--force]         Remove a server package (--force discards local edits)
  server-restore                       Download missing packages and record packages-lock.props
  server-link <id> <folder>            Build a package from a local folder on this machine
  server-unlink <id>                   Go back to the package's own copy
  server-build                         Build the Basis server (needs the .NET 10 SDK)

Unity
  open-unity                           Open the selected project in its required editor

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
                else if (c == '\\' && i + 2 < input.Length && input[i + 1] == quote && !char.IsWhiteSpace(input[i + 2])) token.Append(input[++i]);
                else token.Append(c);
            }
            else if (c is '\'' or '"' && token.Length == 0) quote = c;
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
