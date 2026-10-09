using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private async Task ProjectsAsync(List<string> values)
    {
        var options = values.ToList();
        var name = TakeValue(options, "--name");
        var addFound = TakeFlag(options, "--add");
        if (addFound && (options.Count == 0 || !options[0].Equals("find", StringComparison.OrdinalIgnoreCase))) throw new UsageException("--add goes with 'projects find'.");
        if (options.Count == 0)
        {
            if (name is not null) throw new UsageException("--name goes with 'projects add <path>'.");
            await ListProjectsAsync();
            return;
        }
        switch (options[0].ToLowerInvariant())
        {
            case "add":
                Expect(options, 2, 2, "the path of a Basis clone");
                await AddProjectAsync(options[1], name);
                break;
            case "remove" or "forget":
                Expect(options, 2, 2, "the name or number of a project");
                await RemoveProjectAsync(options[1]);
                break;
            case "rename":
                Expect(options, 3, 3, "a project and its new name");
                await RenameProjectAsync(options[1], options[2]);
                break;
            case "default":
                Expect(options, 1, 2, "a project name, or none");
                await DefaultProjectAsync(options.Count == 2 ? options[1] : null);
                break;
            case "find":
                Expect(options, 1, 2, "a folder to search");
                await FindProjectsAsync(options.Count == 2 ? options[1] : Environment.CurrentDirectory, addFound);
                break;
            default:
                throw new UsageException($"'projects {options[0]}' isn't something projects can do. Use add, remove, rename, default or find.");
        }
    }

    private async Task ListProjectsAsync()
    {
        var settings = await _settings.LoadAsync();
        string? current = null;
        try { current = ResolveQuietly(settings); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { DiagnosticLog.Write("Finding the current project", ex); }
        var saved = SavedProjects(settings);
        var chosen = DefaultProject(settings);
        var items = new List<ProjectItem>();
        for (var i = 0; i < saved.Count; i++)
        {
            var (root, name) = saved[i];
            if (!Directory.Exists(root))
            {
                items.Add(new ProjectItem(i + 1, name, root, false, false, null, null, null, null, null, Platform.PathsEqual(root, current), Platform.PathsEqual(root, chosen)));
                continue;
            }
            var install = await _installs.LoadAsync(root, AliasOf(settings, root));
            GitStatus? git = null;
            if (install.IsGitRepo)
            {
                try { git = await _git.GetStatusAsync(root); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException) { DiagnosticLog.Write($"Reading the git status of {root}", ex); }
            }
            items.Add(new ProjectItem(i + 1, name, root, true, install.IsBasisCheckout, install.HasUnityProject ? install.UnityProjectPath : null,
                install.UnityVersion, git?.Branch, git?.ShortCommit, git?.ChangeCount, Platform.PathsEqual(root, current), Platform.PathsEqual(root, chosen)));
        }
        if (current is not null && !saved.Any(p => Platform.PathsEqual(p.Root, current)))
        {
            var install = await _installs.LoadAsync(current);
            items.Add(new ProjectItem(0, install.DisplayName, current, true, install.IsBasisCheckout, install.UnityProjectPath, install.UnityVersion, null, null, null, true, false));
        }
        if (Out.JsonMode)
        {
            Out.Json(items);
            return;
        }
        if (items.Count == 0)
        {
            Out.Say("No projects yet.");
            Out.Note($"Clone Basis with '{Prefix}clone-basis <folder>', or add an existing clone with '{Prefix}projects add <path>'.");
            return;
        }
        var rows = items.Select(p => new Span[]
        {
            new(p.Current ? "*" : " ", Tone.Good), new(p.Number == 0 ? "-" : p.Number.ToString()), new(p.Name, p.Current ? Tone.Good : Tone.Plain),
            !p.Exists ? new("folder missing", Tone.Bad) : p.Branch is null ? new("not in git", Tone.Dim) : new($"{p.Branch} @ {p.Commit}" + (p.Changes > 0 ? $", {Plural(p.Changes.Value, "change")}" : "")),
            new(p.UnityVersion is null or "unknown" ? "" : "Unity " + p.UnityVersion, Tone.Dim), new(p.Default ? "default" : "", Tone.Accent), new(p.Path, Tone.Dim),
        }).ToList();
        Out.Table(rows, indent: 0);
        if (items.Any(p => p.Number == 0)) Out.Note($"'-' is the project in the current folder; save it with '{Prefix}projects add .'.");
        if (current is null && items.Count > 1) Out.Hint($"Pick one with --project <name>{(_interactive ? " or 'use <name>'" : "")}, or set a default with '{Prefix}projects default <name>'.");
    }

    private sealed record ProjectItem(int Number, string Name, string Path, bool Exists, bool IsBasisCheckout, string? UnityProject, string? UnityVersion,
        string? Branch, string? Commit, int? Changes, bool Current, bool Default);

    private string? ResolveQuietly(UserSettings settings)
    {
        if (_commandProject is not null) return PickProject(settings, _commandProject).Root;
        if (_sessionProject is not null) return _sessionProject;
        if (Environment.GetEnvironmentVariable("BASISPM_PROJECT") is { Length: > 0 } fromEnvironment) return PickProject(settings, fromEnvironment).Root;
        return FindProjectAt(settings, Environment.CurrentDirectory) ?? DefaultProject(settings) ?? OnlyProject(settings);
    }

    private async Task FindProjectsAsync(string folder, bool add)
    {
        var full = FullPath(folder);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"There's no folder {full}.");
        var found = new List<FoundProject>();
        await RunOperationAsync(ct => Task.Run(() =>
        {
            foreach (var project in _installs.FindProjects(full, searched => { if (searched % 50 == 0) Out.Progress($"Searched {searched:N0} folders" + Out.Ellipsis); }, ct))
            {
                found.Add(project);
                Out.Progress("Found " + project.RepoRoot);
            }
        }, ct), $"Looking for Basis projects in {full}" + Out.Ellipsis);
        var settings = await _settings.LoadAsync();
        var fresh = found.Where(p => !IsSaved(settings, p.RepoRoot)).ToList();
        if (add && fresh.Count > 0)
            await _settings.UpdateAsync(s =>
            {
                foreach (var p in fresh.Where(p => !IsSaved(s, p.RepoRoot)))
                {
                    s.Installs.Add(p.RepoRoot);
                    if (!string.Equals(p.Name, Path.GetFileName(p.RepoRoot), StringComparison.Ordinal)) SetAlias(s, p.RepoRoot, p.Name);
                }
            });
        string NameOf(FoundProject p) => IsSaved(settings, p.RepoRoot) ? ProjectName(settings, p.RepoRoot) : p.Name;
        if (Out.JsonMode)
        {
            Out.Json(found.Select(p => new { name = NameOf(p), path = p.RepoRoot, unityProject = p.UnityProjectPath, unityVersion = p.UnityVersion, saved = IsSaved(settings, p.RepoRoot) || add }));
            return;
        }
        if (found.Count == 0)
        {
            Out.Say($"No Basis projects in {full}.");
            return;
        }
        Out.Table(found.Select(p => new[]
        {
            IsSaved(settings, p.RepoRoot) ? new Span("saved", Tone.Dim) : new Span(add ? "added" : "new", Tone.Good), new Span(NameOf(p), Tone.Bold),
            new Span(p.UnityVersion is { Length: > 0 } and not "unknown" ? "Unity " + p.UnityVersion : "", Tone.Dim), new Span(p.RepoRoot, Tone.Dim),
        }).ToList(), indent: 0);
        if (add && fresh.Count > 0) Out.Success($"Added {Plural(fresh.Count, "project")} to your projects.");
        else if (fresh.Count > 0) Out.Hint($"Save {(fresh.Count == 1 ? "it" : "them")} with '{Prefix}projects find \"{folder}\" --add', or one at a time with '{Prefix}projects add <path>'.");
    }

    private async Task DefaultProjectAsync(string? value)
    {
        var settings = await _settings.LoadAsync();
        if (value is null)
        {
            var current = DefaultProject(settings);
            if (Out.JsonMode) Out.Json(new { defaultProject = current is null ? null : ProjectName(settings, current), path = current });
            else if (current is null) Out.Say($"No default project. Set one with '{Prefix}projects default <name>'.");
            else Out.Say($"{ProjectName(settings, current)} ({current})");
            return;
        }
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            SaveState(new ConsoleState());
            Out.Success("Cleared the default project.");
            return;
        }
        var (root, name) = FindSavedProject(settings, value);
        SaveState(new ConsoleState { DefaultProject = root });
        Out.Success($"Commands now use {name} unless you pass --project or run them inside another project.");
    }

    private async Task AddProjectAsync(string path, string? name)
    {
        var full = FullPath(path);
        var detection = _projects.Detect(full);
        if (!detection.IsValid) throw new DirectoryNotFoundException($"'{full}' is not a Basis/Unity project: {detection.Reason}");
        var settings = await _settings.LoadAsync();
        var root = RootOf(settings, full, detection.ResolvedPath!);
        var install = await _installs.LoadAsync(root);
        if (!install.IsBasisCheckout)
            throw new InvalidOperationException($"{root} isn't a Basis checkout: its Unity project has no Packages/com.basis.framework. Projects have to be complete Basis clones.");
        var alias = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (alias is not null && SavedProjects(settings).Any(p => !Platform.PathsEqual(p.Root, root) && p.Name.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Another project is already called {alias}. Pick a different --name.");
        var existed = IsSaved(settings, root);
        await _settings.UpdateAsync(s =>
        {
            if (!IsSaved(s, root)) s.Installs.Add(root);
            if (alias is not null) SetAlias(s, root, alias);
        });
        var display = alias ?? AliasOf(settings, root) ?? install.Name;
        if (existed) Out.Success(alias is null ? $"{display} is already in your projects." : $"{root} is now called {alias}.");
        else Out.Success($"Added {display} ({root}).");
        if (!install.IsGitRepo) Out.Note($"It isn't in git yet, so updates need '{Prefix}update-basis --init-git' first.");
    }

    private async Task RemoveProjectAsync(string value)
    {
        var settings = await _settings.LoadAsync();
        var (root, name) = FindSavedProject(settings, value);
        await _settings.UpdateAsync(s =>
        {
            s.Installs.RemoveAll(r => Platform.PathsEqual(r, root));
            foreach (var key in s.InstallAliases.Keys.Where(k => Platform.PathsEqual(k, root)).ToList()) s.InstallAliases.Remove(key);
        });
        if (Platform.PathsEqual(_sessionProject, root)) _sessionProject = null;
        if (Platform.PathsEqual(LoadState().DefaultProject, root)) SaveState(new ConsoleState());
        Out.Success($"Removed {name} from your projects.");
        if (Directory.Exists(root)) Out.Note($"Nothing was deleted; the files are still in {root}.");
    }

    private async Task RenameProjectAsync(string value, string newName)
    {
        var settings = await _settings.LoadAsync();
        var (root, name) = FindSavedProject(settings, value);
        var alias = newName.Trim();
        if (alias.Length > 0 && SavedProjects(settings).Any(p => !Platform.PathsEqual(p.Root, root) && p.Name.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Another project is already called {alias}.");
        await _settings.UpdateAsync(s => SetAlias(s, root, alias.Length == 0 ? null : alias));
        Out.Success(alias.Length == 0 ? $"{name} goes by its folder name again: {Path.GetFileName(root)}." : $"Renamed {name} to {alias}.");
    }

    private (string Root, string Name) FindSavedProject(UserSettings settings, string value)
    {
        var saved = SavedProjects(settings);
        var trimmed = value.Trim();
        if (int.TryParse(trimmed.TrimStart('#'), out var number) && number >= 1 && number <= saved.Count) return saved[number - 1];
        if (MatchSaved(saved, trimmed) is { } match) return match;
        var index = saved.FindIndex(p => Platform.PathsEqual(p.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        if (index >= 0) return saved[index];
        var suggestions = Suggest(trimmed, saved.Select(p => p.Name));
        throw new InvalidOperationException($"'{trimmed}' isn't one of your projects." + (suggestions.Count > 0 ? $" Did you mean {JoinOr(suggestions.Select(s => $"'{s}'"))}?" : $" Run '{Prefix}projects' to list them."));
    }

    private static void SetAlias(UserSettings settings, string root, string? alias)
    {
        foreach (var key in settings.InstallAliases.Keys.Where(k => Platform.PathsEqual(k, root)).ToList()) settings.InstallAliases.Remove(key);
        if (alias is not null) settings.InstallAliases[settings.Installs.FirstOrDefault(r => Platform.PathsEqual(r, root)) ?? root] = alias;
    }

    private async Task UseAsync(List<string> values)
    {
        Expect(values, 1, 1, "a project name, number or path");
        var settings = await _settings.LoadAsync();
        var (root, name) = PickProject(settings, values[0]);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"The folder of {name} is missing: {root}.");
        var install = await _installs.LoadAsync(root, AliasOf(settings, root));
        _sessionProject = root;
        Out.Success($"Using {name} ({root}).");
        if (!install.IsBasisCheckout) Out.Warning("This isn't a complete Basis checkout, so updates and package commands may not work.");
        if (!IsSaved(settings, root)) Out.Note($"Save it for next time with '{Prefix}projects add \"{root}\"'.");
        if (!_interactive) Out.Note($"'use' only lasts for the interactive console. For a single command, pass --project \"{values[0]}\".");
    }

    private async Task CloneBasisAsync(List<string> values)
    {
        var options = values.ToList();
        var name = TakeValue(options, "--name");
        Expect(options, 1, 2, "an empty folder to clone into");
        if (!_git.IsAvailable) throw new InvalidOperationException("Git was not found on PATH. Install it from https://git-scm.com/ and try again.");
        var destination = FullPath(options[0]);
        var branch = options.Count == 2 ? options[1] : BasisInstallService.DefaultBranch;
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidOperationException($"The destination is not empty: {destination}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destination);
        if (DiskSpace.ForPath(Path.GetDirectoryName(destination) ?? destination) is { } space && space.FreeBytes < 10L * 1024 * 1024 * 1024)
            Out.Warning($"Only {DiskSpace.Human(space.FreeBytes)} is free on {space.DriveName}. Basis plus the Unity Library it builds needs tens of GB.");

        await RunOperationAsync(async ct =>
        {
            var result = await _git.CloneAsync(BasisInstallService.BasisRepoUrl, destination, branch, Out.Progress, ct);
            if (!result.Ok) throw new InvalidOperationException($"Clone failed: {Tail(result.Output)}");
        }, $"Cloning Basis {branch}" + Out.Ellipsis);

        var install = await _installs.LoadAsync(destination);
        if (!install.HasUnityProject)
            throw new InvalidOperationException("The clone completed, but its Basis Unity project could not be found.");
        var alias = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        await _settings.UpdateAsync(s =>
        {
            if (!IsSaved(s, destination)) s.Installs.Add(destination);
            if (alias is not null) SetAlias(s, destination, alias);
        });
        if (_interactive) _sessionProject = destination;
        Out.Success($"Cloned Basis {branch} into {destination} and added it to your projects{(alias is null ? "" : " as " + alias)}.");
        Out.Say($"Unity project: {install.UnityProjectPath} (Unity {install.UnityVersion})");
        Out.Note($"Next: '{Prefix}doctor' checks this computer has what Basis needs, '{Prefix}open-unity' opens it.");
    }

    private async Task ShowStatusAsync(List<string> values)
    {
        Expect(values, 0, 0, "");
        var install = await LoadInstallAsync();
        var embedded = _projects.ListEmbeddedPackages(install.UnityProjectPath);
        var sources = await _basisDev.ScanAsync(install, compare: false);
        var clones = sources.Packages.Count(p => p.CloneExists);
        var inUse = sources.Packages.Count(p => p.CloneActive);
        var reconcile = sources.WithIssues.Count;
        GitStatus? git = null;
        BasisUpdateState? state = null;
        if (install.IsGitRepo)
        {
            git = await _git.GetStatusAsync(install.RepoRoot);
            state = await _updates.LoadStateAsync(install.RepoRoot);
        }
        var hasServer = _server.HasServerProject(install.RepoRoot);
        var serverBuilt = hasServer && File.Exists(_server.GetPaths(install.RepoRoot).ExecutablePath);
        var serverPackages = ServerPackageService.HasServer(install.RepoRoot) ? _serverPackages.LoadManifest(install.RepoRoot).Dependencies.Count : 0;
        var unityOpen = install.HasUnityProject && UnityProjectService.IsOpenInUnity(install.UnityProjectPath);
        if (Out.JsonMode)
        {
            Out.Json(new
            {
                name = install.DisplayName, path = install.RepoRoot, unityProject = install.UnityProjectPath, unityVersion = install.UnityVersion,
                isBasisCheckout = install.IsBasisCheckout, unityOpen,
                git = git is null ? null : new { branch = git.Branch, commit = git.ShortCommit, changes = git.ChangeCount, upstream = git.Upstream.Name, ahead = git.Upstream.Ahead, behind = git.Upstream.Behind },
                pending = state is null ? null : new { operation = state.Operation, basisBranch = state.BasisBranch, targetBranch = state.TargetBranch, phase = state.Phase },
                packages = new { bundled = embedded.Count, manifest = install.Manifest.Dependencies.Count, devClones = clones, devClonesInUse = inUse, toReconcile = reconcile },
                server = new { source = hasServer, built = serverBuilt, packages = serverPackages },
            });
            return;
        }
        Out.Say(new Span(install.DisplayName, Tone.Accent), new Span("  " + install.RepoRoot, Tone.Dim));
        var rows = new List<Span[]>
        {
            new Span[] { "Unity project", install.HasUnityProject ? $"{RelativeTo(install.RepoRoot, install.UnityProjectPath)} (Unity {install.UnityVersion}){(unityOpen ? ", open in Unity now" : "")}" : new Span("not found", Tone.Bad) },
        };
        if (git is not null)
        {
            var upstream = git.Upstream.HasUpstream ? $", {Describe(git.Upstream)}" : "";
            rows.Add(new Span[] { "Git", $"{git.Branch} @ {git.ShortCommit}{(git.ChangeCount > 0 ? $", {Plural(git.ChangeCount, "changed file")}" : ", no local changes")}{upstream}" });
        }
        else rows.Add(new Span[] { "Git", new Span($"not tracked by git ('{Prefix}update-basis --init-git' sets that up)", Tone.Warn) });
        if (state is not null)
            rows.Add(new Span[] { "Waiting", new Span(state.Operation == BasisOperation.BranchSwitch ? $"a switch to {state.TargetBranch} needs decisions: run '{Prefix}conflicts'" : $"a Basis {state.BasisBranch} update needs decisions: run '{Prefix}conflicts'", Tone.Warn) });
        rows.Add(new Span[] { "Packages", $"{Plural(embedded.Count, "bundled package")} in Packages, {Plural(install.Manifest.Dependencies.Count, "entry", "entries")} in manifest.json" });
        var cloneText = clones == 0 ? "none" : $"{clones} ({(inUse == 0 ? "none" : inUse.ToString())} in use)";
        rows.Add(new Span[] { "Dev clones", reconcile > 0 ? new Span($"{cloneText}, {Plural(reconcile, "package")} to reconcile: run '{Prefix}basisdev'", Tone.Warn) : cloneText });
        if (hasServer) rows.Add(new Span[] { "Server", $"Basis Server source found, {(serverBuilt ? "built" : "not built yet")}" + (serverPackages > 0 ? $", {Plural(serverPackages, "server package")}" : "") });
        Out.Table(rows);
        if (!install.IsBasisCheckout && install.HasUnityProject) Out.Warning("This Unity project has no Packages/com.basis.framework, so it isn't a complete Basis checkout.");
        Out.Note($"'{Prefix}check-updates' looks for a newer Basis; '{Prefix}doctor' checks git, Unity and the SDKs.");
    }

    private static string Describe(AheadBehind upstream) => (upstream.Ahead, upstream.Behind) switch
    {
        (0, 0) => $"in step with {upstream.Name}",
        (var ahead, 0) => $"{Plural(ahead, "commit")} to push to {upstream.Name}",
        (0, var behind) => $"{Plural(behind, "commit")} behind {upstream.Name}",
        var (ahead, behind) => $"{ahead} ahead and {behind} behind {upstream.Name}",
    };

    private static string RelativeTo(string root, string folder)
    {
        var relative = Path.GetRelativePath(root, folder).Replace('\\', '/');
        return relative == "." ? "(the repository root)" : relative;
    }

    private async Task BackupAsync(List<string> values)
    {
        var options = values.ToList();
        var to = TakeValue(options, "--to");
        Expect(options, 0, 0, "");
        var install = await LoadInstallAsync();
        var zip = await CreateBackupAsync(install, to);
        Out.Success($"Backed up {install.DisplayName} to {zip} ({DiskSpace.Human(new FileInfo(zip).Length)}).");
    }

    private async Task<string> CreateBackupAsync(BasisInstall install, string? folder)
    {
        if (!BackupService.LooksLikeUnityProject(install.UnityProjectPath)) throw new InvalidOperationException($"{install.DisplayName} has no Unity project to back up.");
        var destination = folder is null ? Path.Combine(Path.GetDirectoryName(install.RepoRoot) ?? install.RepoRoot, "BasisBackups") : FullPath(folder);
        var zip = "";
        await RunOperationAsync(async ct => zip = await BackupService.CreateBackupAsync(install.UnityProjectPath, destination, DateTime.Now.ToString("yyyyMMdd-HHmmss"), Out.Progress, ct),
            $"Backing up {install.DisplayName}" + Out.Ellipsis);
        return zip;
    }
}
