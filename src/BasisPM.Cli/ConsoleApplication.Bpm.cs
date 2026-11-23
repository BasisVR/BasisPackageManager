using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private const string BpmGroup = "Project files (bpm-manifest.json)";

    private BpmService Bpm => new(_git);

    private IEnumerable<CliCommand> BpmCommands() => new List<CliCommand>
    {
        new()
        {
            Name = "init-project", Group = BpmGroup, Aliases = new[] { "init", "create-project" },
            Summary = "Start a project that takes Basis from .basisdev",
            Usage = new[] { "init-project <folder> [--content|--game] [--at <commit|branch>] [--packages <id>[,<id>]] [--copy-template] [--basis <git-url>]", "init-project <folder> --contributor [--at <branch>] [--basis <git-url>]" },
            Details = "Writes bpm-manifest.json with Basis pinned to a commit hash, clones Basis into .basisdev as a bare repository with a sparse worktree, locks the packages and mounts them "
                + "with file: lines in Packages/manifest.json. --content (avatars, worlds, props) copies the Basis Assets and ProjectSettings and keeps them following Basis; every Basis package is listed. "
                + "--game leaves Assets and ProjectSettings to you: list the Basis packages you want with --packages and their dependencies come along. In an existing project it says which layers and tags "
                + "Basis expects instead of touching ProjectSettings. --copy-template copies the template once for a new game. --contributor makes a full clone of Basis (or your fork) instead, the project you send PRs from. "
                + "Commit bpm-manifest.json, bpm-lock.json and Packages/manifest.json. .basisdev is added to .gitignore, Plastic's ignore.conf or .p4ignore when the project uses one.",
            Options = new[]
            {
                new CliOption("--content", "Assets, ProjectSettings and the Unity version follow Basis"), new CliOption("--game", "The project owns Assets and ProjectSettings (the default)"),
                new CliOption("--at", "Basis commit or branch to pin (developer by default)", "<rev>"), new CliOption("--packages", "Basis packages to use", "<ids>"),
                new CliOption("--copy-template", "Copy the Basis Assets and ProjectSettings once"), new CliOption("--basis", "Basis repository to use", "<git-url>"),
                new CliOption("--contributor", "Clone all of Basis to work on Basis itself"),
            },
            Args = new[] { ArgKind.Path },
            Examples = new[]
            {
                new CliExample("init-project D:\\Avatars\\Mine --content", "An avatar project that follows Basis"),
                new CliExample("init-project D:\\Game --game --packages com.basis.framework,com.basis.sdk", "Use two Basis packages in your own game"),
                new CliExample("init-project D:\\Basis --contributor", "Clone Basis to work on it"),
            },
            SeeAlso = new[] { "lock", "restore", "pin" },
            Run = InitProjectAsync,
        },
        new()
        {
            Name = "lock", Group = BpmGroup, Summary = "Write bpm-lock.json from bpm-manifest.json",
            Usage = new[] { "lock [--refresh]" },
            Details = "Resolves every enabled package in bpm-manifest.json and the packages they need into bpm-lock.json: checkouts by commit, git sources by commit hash, with their depth and dependencies. "
                + "Dependencies come from bpmDependencies in each package.json (vpmDependencies is still read while packages move over), plus gitDependencies. A dependency that isn't listed comes from the "
                + "default checkout. Git sources keep their locked commit until --refresh, so a lock only moves when you ask.",
            Options = new[] { new CliOption("--refresh", "Look up branches and tags again instead of keeping locked commits") },
            SeeAlso = new[] { "restore", "mount", "manifest" },
            Run = LockAsync,
        },
        new()
        {
            Name = "mount", Group = BpmGroup, Summary = "Point Packages/manifest.json at the locked packages",
            Usage = new[] { "mount" },
            Details = "Writes a file: line in Packages/manifest.json for every package in bpm-lock.json, relative to Packages, however deep the project is. Local overrides win on this machine. "
                + "Lines BPM wrote for packages that are no longer locked are removed; lines BPM didn't write are left alone. Mount lines are never symlinks.",
            SeeAlso = new[] { "lock", "override" },
            Run = MountAsync,
        },
        new()
        {
            Name = "pin", Group = BpmGroup, Aliases = new[] { "update-project" }, Summary = "Move a pinned checkout to another commit",
            Usage = new[] { "pin [checkout] <commit|branch|tag> [--yes]" },
            Details = "Fetches the checkout's repository, pins bpm-manifest.json to the commit hash, locks, restores and mounts, and lists which packages changed so you know what to retest. "
                + "When Assets and ProjectSettings follow the checkout, it shows what changes in them first and asks: files you haven't touched are updated, added or removed, files you changed are left for you. "
                + "Without a checkout name it moves the default one (Basis). Afterwards it checks the Assets for duplicate GUIDs and orphaned .meta files. For a fork held as a working tree, rebase each fork branch onto the new pin yourself; 'manifest' says which fork is behind.",
            Options = new[] { Yes },
            Examples = new[] { new CliExample("pin Basis developer", "Move Basis to the newest developer commit"), new CliExample("pin developer", "The same, for the default checkout") },
            SeeAlso = new[] { "manifest", "lock" },
            Run = PinAsync,
        },
        new()
        {
            Name = "override", Group = BpmGroup, Json = true, Summary = "Load a package from a local folder on this machine",
            Usage = new[] { "override", "override <package-id> <folder>", "override <package-id> --clear" },
            Details = "A local override mounts a package from a folder on this computer, such as your own clone of an add-on, instead of what bpm-lock.json says. It's kept in .basisdev/bpm-local.json "
                + "and never committed, the Unity side of server-link. Packages/manifest.json does change on this machine, so don't commit those lines. Before a release, install the add-on into a clean project from its git URL.",
            Options = new[] { new CliOption("--clear", "Remove the override") },
            Args = new[] { ArgKind.Package, ArgKind.Path },
            SeeAlso = new[] { "mount", "server-link" },
            Run = OverrideAsync,
        },
        new()
        {
            Name = "enable-package", Group = BpmGroup, Summary = "Mount a package bpm-manifest.json turned off",
            Usage = new[] { "enable-package <package-id>" },
            Details = "Sets enabled back to yes in bpm-manifest.json, then locks and mounts again.",
            Args = new[] { ArgKind.Package }, SeeAlso = new[] { "disable-package" }, Run = values => SetEnabledAsync(values, true),
        },
        new()
        {
            Name = "disable-package", Group = BpmGroup, Summary = "Keep a package's checkout but stop mounting it",
            Usage = new[] { "disable-package <package-id>" },
            Details = "Sets enabled to no in bpm-manifest.json, then locks and mounts again. The checkout stays, so turning it back on doesn't download anything; useful while troubleshooting or when two repositories provide the same package.",
            Args = new[] { ArgKind.Package }, SeeAlso = new[] { "enable-package" }, Run = values => SetEnabledAsync(values, false),
        },
        new()
        {
            Name = "embed-package", Group = BpmGroup, Aliases = new[] { "patch-package" }, Summary = "Copy a locked package into Packages/ to patch it",
            Usage = new[] { "embed-package <package-id>" },
            Details = "For a short-lived patch to a Basis package in a project that owns its Packages folder. Copies the package from its checkout into Packages/, where your version control records it, "
                + "points bpm-manifest.json at that folder and notes the commit it was copied from, so the copy can be diffed against upstream later and sent back. Its mount line is removed. "
                + "Edits made in a pinned checkout never reach a teammate, so patch a copy instead. For long-lived patches, use a fork held as a working tree.",
            Args = new[] { ArgKind.Package }, SeeAlso = new[] { "lock", "contribute" }, Run = EmbedAsync,
        },
        new()
        {
            Name = "app-config", Group = BpmGroup, Json = true, Aliases = new[] { "app" }, Summary = "App name, id and Basis features (basis.config.json)",
            Usage = new[] { "app-config", "app-config init [--name <name>] [--company <name>] [--identifier <id>] [--version <x.y.z>]", "app-config get <key>", "app-config set <key> <value>", "app-config unset <key>", "app-config apply" },
            Details = "basis.config.json sits next to bpm-manifest.json and holds what makes the project its own app: productName, companyName, identifier (one reverse-domain id for every platform) and version, "
                + "plus features (Basis parts to turn on or off, such as \"mainMenu\": false) and settings (the starting value of a Basis setting, by its key). Commit it. "
                + "Without arguments it shows the file and what's wrong with it, such as an identifier still set to Basis's own, which makes two apps install over each other and share saved settings. "
                + "apply writes the name, company, identifier and version into ProjectSettings with Unity closed; with Unity open, Basis applies the file itself when it changes. "
                + "When ProjectSettings follows the Basis template, template updates keep your values instead of Basis's.",
            Options = new[]
            {
                new CliOption("--name", "productName for init", "<name>"), new CliOption("--company", "companyName for init", "<name>"),
                new CliOption("--identifier", "identifier for init", "<id>"), new CliOption("--version", "version for init", "<x.y.z>"),
            },
            Examples = new[]
            {
                new CliExample("app-config init --name MyApp --identifier com.myapp.app", "Give the project its own name and id"),
                new CliExample("app-config set features.mainMenu off", "Ship your own UI instead of the Basis menu"),
                new CliExample("app-config set settings.mainvolume 50", "Start the main volume at 50"),
                new CliExample("app-config apply", "Write the name and id into ProjectSettings"),
            },
            SeeAlso = new[] { "manifest", "init-project" },
            Run = AppConfigAsync,
        },
        new()
        {
            Name = "manifest", Group = BpmGroup, Json = true, Summary = "Show checkouts, mounts and what's out of date",
            Usage = new[] { "manifest" },
            Details = "Lists the checkouts in .basisdev with how each is held (pinned, working tree or inline) and where it is, the line each locked package is mounted with, and anything that needs doing: "
                + "a stale lock, a checkout off its pin, edits in a pinned checkout, a fork that isn't rebased onto the pin, a client and Basis Server lock that disagree, or active local overrides.",
            SeeAlso = new[] { "lock", "restore", "mount" },
            Run = ManifestStatusAsync,
        },
    };

    private async Task<string> BpmProjectAsync()
    {
        if (_commandProject is { } raw && Directory.Exists(FullPath(raw))) return UnityFolderOf(FullPath(raw));
        if (_commandProject is null && _sessionProject is null && Environment.GetEnvironmentVariable("BASISPM_PROJECT") is not { Length: > 0 })
            for (var folder = new DirectoryInfo(Environment.CurrentDirectory); folder is not null; folder = folder.Parent)
                if (File.Exists(Path.Combine(folder.FullName, BpmStore.ManifestFileName))) return folder.FullName;
        return (await LoadInstallAsync()).UnityProjectPath;
    }

    private static string UnityFolderOf(string folder) =>
        File.Exists(Path.Combine(folder, BpmStore.ManifestFileName)) || File.Exists(Path.Combine(folder, "Packages", "manifest.json")) || !Directory.Exists(Path.Combine(folder, "Basis", "Packages"))
            ? folder : Path.Combine(folder, "Basis");

    private Task<BasisInstall?> BpmInstallAsync(string project)
    {
        var path = Path.Combine(project, "Packages", "manifest.json");
        if (!File.Exists(path)) return Task.FromResult<BasisInstall?>(null);
        PackageManifest? manifest;
        try { manifest = System.Text.Json.JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(path), new System.Text.Json.JsonSerializerOptions { ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (System.Text.Json.JsonException) { return Task.FromResult<BasisInstall?>(null); }
        if (manifest is null) return Task.FromResult<BasisInstall?>(null);
        manifest.Dependencies ??= new Dictionary<string, string>();
        var repo = FindRepoRoot(project, project);
        return Task.FromResult<BasisInstall?>(new BasisInstall { RepoRoot = repo, UnityProjectPath = project, Name = Path.GetFileName(project), IsGitRepo = _git.IsGitRepo(repo), HasUnityProject = true, Manifest = manifest });
    }

    private static void Report(BpmResult result)
    {
        foreach (var line in result.Done.Where(d => d.Length > 0)) Out.Success(line);
        Report(result.Issues);
    }

    private static void Report(IEnumerable<BpmIssue> issues)
    {
        foreach (var issue in issues)
        {
            switch (issue.Level)
            {
                case BpmIssueLevel.Error: Out.Error(issue.Message); break;
                case BpmIssueLevel.Warning: Out.Warning(issue.Message); break;
                default: Out.Hint(issue.Message); break;
            }
        }
    }

    private static void ThrowIfFailed(BpmResult result)
    {
        if (!result.Ok) throw new InvalidOperationException("Some steps didn't finish; see the messages above.");
    }

    private async Task InitProjectAsync(List<string> values)
    {
        var options = values.ToList();
        var content = TakeFlag(options, "--content");
        var game = TakeFlag(options, "--game");
        var copy = TakeFlag(options, "--copy-template");
        var at = TakeValue(options, "--at");
        var url = TakeValue(options, "--basis") ?? BpmService.BasisUrl;
        var packages = TakeValue(options, "--packages")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var contributor = TakeFlag(options, "--contributor") | TakeFlag(options, "--framework-developer");
        Expect(options, 1, 1, "the project folder");
        if (contributor)
        {
            if (content || game || copy || packages is not null) throw new UsageException("--contributor clones all of Basis, so it takes only --at and --basis.");
            var clone = new List<string> { options[0] };
            if (at is not null) clone.Add(at);
            if (url != BpmService.BasisUrl) clone.AddRange(new[] { "--basis", url });
            await CloneBasisAsync(clone);
            return;
        }
        if (content && game) throw new UsageException("Pick --content or --game, not both.");
        if (content && copy) throw new UsageException("--content already copies the template.");
        BpmResult result = null!;
        await RunOperationAsync(async ct => result = await Bpm.InitAsync(FullPath(options[0]), content, url, at, packages, copy, Out.Progress, ct), "Setting up the project" + Out.Ellipsis);
        Report(result);
        ThrowIfFailed(result);
        Out.Hint($"Commit {BpmStore.ManifestFileName}, {BpmStore.LockFileName}, {BasisConfigService.FileName} and Packages/manifest.json. Never commit {BasisDevStore.FolderName}.");
    }

    private async Task LockAsync(List<string> values)
    {
        var options = values.ToList();
        var refresh = TakeFlag(options, "--refresh");
        Expect(options, 0, 0, "");
        var project = await BpmProjectAsync();
        var catalog = await LoadCatalogAsync();
        BpmResult result = null!;
        await RunOperationAsync(async ct => result = await Bpm.ResolveAsync(project, catalog, refresh, Out.Progress, ct), "Locking" + Out.Ellipsis);
        Report(result);
        ThrowIfFailed(result);
    }

    private async Task MountAsync(List<string> values)
    {
        Expect(values, 0, 0, "");
        var project = await BpmProjectAsync();
        BpmResult result = null!;
        await RunOperationAsync(async ct => result = await Bpm.MountAsync(project, ct));
        Report(result);
        ThrowIfFailed(result);
    }

    private async Task RestoreProjectAsync(bool noSwitch, bool assumeYes)
    {
        var project = await BpmProjectAsync();
        var hasManifest = BpmStore.Exists(project);
        var steps = new List<BpmResult>();
        if (hasManifest)
        {
            BpmResult checkouts = null!;
            await RunOperationAsync(async ct => checkouts = await Bpm.RestoreAsync(project, Out.Progress, ct), "Restoring the checkouts" + Out.Ellipsis);
            Report(checkouts);
            steps.Add(checkouts);
        }
        if (await BpmInstallAsync(project) is { } install)
        {
            var catalog = await LoadCatalogAsync();
            BasisDevResult clones = null!;
            _basisDev.ApproveUpdate = ListedReview(catalog, assumeYes);
            try { await RunOperationAsync(async ct => clones = await _basisDev.RestoreAllAsync(install, !noSwitch, Out.Progress, ct), "Restoring the listed packages" + Out.Ellipsis); }
            finally { _basisDev.ApproveUpdate = null; }
            if (clones.Ok) Out.Success(clones.Message);
            else Out.Error(clones.Message);
            steps.Add(new BpmResult(clones.Ok, Array.Empty<BpmIssue>(), Array.Empty<string>()));
        }
        if (hasManifest && File.Exists(Path.Combine(project, "Packages", "manifest.json")))
        {
            BpmResult mounted = null!;
            await RunOperationAsync(async ct => mounted = await Bpm.MountAsync(project, ct));
            Report(mounted);
            steps.Add(mounted);
        }
        if (steps.Any(s => !s.Ok)) throw new InvalidOperationException("Some packages couldn't be restored; see the messages above.");
    }

    private async Task PinAsync(List<string> values)
    {
        var options = values.ToList();
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        Expect(options, 1, 2, "a commit, branch or tag");
        var project = await BpmProjectAsync();
        var (name, rev) = options.Count == 2 ? (options[0], options[1])
            : (BpmService.DefaultCheckout(BpmStore.ReadManifest(project)) ?? throw new UsageException($"{BpmStore.ManifestFileName} has no default checkout; name the checkout to move."), options[0]);
        if (!GitUrlPolicy.IsSafeRef(rev)) throw new UsageException($"'{rev}' isn't a valid git ref.");
        var before = BpmStore.ReadManifest(project).Checkouts?.GetValueOrDefault(name)?.Commit;
        BpmResult moved = null!;
        await RunOperationAsync(async ct => moved = await Bpm.MovePinAsync(project, name, rev, Out.Progress, ct), $"Moving {name}" + Out.Ellipsis);
        Report(moved);
        ThrowIfFailed(moved);
        var after = BpmStore.ReadManifest(project).Checkouts![name].Commit!;
        BpmTemplatePlan plan = BpmTemplatePlan.None;
        await RunOperationAsync(async ct => plan = await Bpm.PlanTemplateAsync(project, name, after, ct), "Comparing the template" + Out.Ellipsis);
        if (!plan.IsEmpty)
        {
            Out.Heading($"Assets and ProjectSettings follow {name}. Moving the pin changes:");
            Out.Table(plan.Changes.Select(c => new[] { new Span(c.Kind.ToString(), c.Kind == BpmTemplateChangeKind.Conflict ? Tone.Warn : Tone.Dim), new Span(c.Path), new Span(c.Reason, Tone.Dim) }).ToList());
            bool apply;
            try { apply = assumeYes || Confirm("Apply these changes?"); }
            catch
            {
                BpmStore.Update(project, (manifest, _) => manifest.Checkouts![name].Commit = before);
                throw;
            }
            if (!apply)
            {
                BpmStore.Update(project, (manifest, _) => manifest.Checkouts![name].Commit = before);
                Out.Hint($"Nothing changed; {name} stays at {BpmService.Short(before)}.");
                return;
            }
        }
        var catalog = await LoadCatalogAsync();
        BpmResult locked = null!, restored = null!, mounted = null!;
        await RunOperationAsync(async ct => locked = await Bpm.ResolveAsync(project, catalog, false, Out.Progress, ct), "Locking" + Out.Ellipsis);
        Report(locked);
        ThrowIfFailed(locked);
        await RunOperationAsync(async ct => restored = await Bpm.RestoreAsync(project, Out.Progress, ct), "Restoring" + Out.Ellipsis);
        Report(restored);
        ThrowIfFailed(restored);
        if (!plan.IsEmpty)
        {
            var manifest = BpmStore.ReadManifest(project);
            var checkout = manifest.Checkouts![name];
            var folder = BpmService.CheckoutFolder(project, name, checkout);
            var projectFolder = BpmService.TemplateFolders(checkout)[0] is var assets && assets.Contains('/') ? assets[..assets.LastIndexOf('/')] : "";
            var applied = BpmService.ApplyTemplate(project, plan, folder, projectFolder);
            Report(applied);
        }
        await RunOperationAsync(async ct => mounted = await Bpm.MountAsync(project, ct));
        Report(mounted);
        ThrowIfFailed(mounted);
    }

    private async Task OverrideAsync(List<string> values)
    {
        var options = values.ToList();
        var clear = TakeFlag(options, "--clear");
        var project = await BpmProjectAsync();
        if (options.Count == 0 && !clear)
        {
            var root = BpmService.BasisDevRoot(project);
            var overrides = BpmStore.LocalProject(BpmStore.ReadLocal(root), root, project).Overrides ?? new Dictionary<string, string>();
            if (Out.JsonMode) { Out.Json(overrides); return; }
            if (overrides.Count == 0) { Out.Say(new Span("No local overrides.", Tone.Dim)); return; }
            Out.Table(overrides.Select(o => new[] { new Span(o.Key, Tone.Bold), new Span(o.Value) }).ToList());
            return;
        }
        Expect(options, clear ? 1 : 2, clear ? 1 : 2, clear ? "a package id" : "a package id and a folder");
        Bpm.SetOverride(project, options[0], clear ? null : FullPath(options[1]));
        Out.Success(clear ? $"{options[0]} is mounted from the lock again." : $"{options[0]} loads from {FullPath(options[1])} on this machine.");
        BpmResult mounted = null!;
        await RunOperationAsync(async ct => mounted = await Bpm.MountAsync(project, ct));
        Report(mounted);
        ThrowIfFailed(mounted);
    }

    private async Task SetEnabledAsync(List<string> values, bool enabled)
    {
        Expect(values, 1, 1, "a package id");
        var project = await BpmProjectAsync();
        BpmService.SetEnabled(project, values[0], enabled);
        Out.Success($"{values[0]} is turned {(enabled ? "on" : "off")} in {BpmStore.ManifestFileName}.");
        var catalog = await LoadCatalogAsync();
        BpmResult locked = null!, mounted = null!;
        await RunOperationAsync(async ct => locked = await Bpm.ResolveAsync(project, catalog, false, Out.Progress, ct), "Locking" + Out.Ellipsis);
        Report(locked);
        ThrowIfFailed(locked);
        if (enabled)
        {
            BpmResult restored = null!;
            await RunOperationAsync(async ct => restored = await Bpm.RestoreAsync(project, Out.Progress, ct), "Restoring" + Out.Ellipsis);
            Report(restored);
            ThrowIfFailed(restored);
        }
        await RunOperationAsync(async ct => mounted = await Bpm.MountAsync(project, ct));
        Report(mounted);
        ThrowIfFailed(mounted);
    }

    private async Task EmbedAsync(List<string> values)
    {
        Expect(values, 1, 1, "a package id");
        var project = await BpmProjectAsync();
        var embedded = BpmService.Embed(project, values[0]);
        Report(embedded);
        ThrowIfFailed(embedded);
        var catalog = await LoadCatalogAsync();
        BpmResult locked = null!, restored = null!, mounted = null!;
        await RunOperationAsync(async ct => locked = await Bpm.ResolveAsync(project, catalog, false, Out.Progress, ct), "Locking" + Out.Ellipsis);
        Report(locked);
        ThrowIfFailed(locked);
        await RunOperationAsync(async ct => restored = await Bpm.RestoreAsync(project, Out.Progress, ct), "Restoring" + Out.Ellipsis);
        Report(restored);
        ThrowIfFailed(restored);
        await RunOperationAsync(async ct => mounted = await Bpm.MountAsync(project, ct));
        Report(mounted);
        ThrowIfFailed(mounted);
    }

    private async Task ManifestStatusAsync(List<string> values)
    {
        Expect(values, 0, 0, "");
        var project = await BpmProjectAsync();
        BpmReport report = null!;
        await RunOperationAsync(async ct => report = await Bpm.StatusAsync(project, ct), "Checking" + Out.Ellipsis);
        if (Out.JsonMode)
        {
            Out.Json(new
            {
                project = report.ProjectPath,
                basisdev = report.BasisDevRoot,
                checkouts = report.Checkouts.Select(c => new { name = c.Name, held = c.Held.ToString(), folder = c.Folder, exists = c.Exists, head = c.Head, branch = c.Branch, locked = c.Locked, changes = c.Changes }),
                mounts = report.Mounts.Select(m => new { id = m.Id, line = m.Line, source = m.Source, @override = m.Override }),
                issues = report.Issues.Select(i => new { level = i.Level.ToString(), subject = i.Subject, message = i.Message }),
            });
            return;
        }
        Out.Heading($"{report.ProjectPath}  ({BasisDevStore.FolderName}: {report.BasisDevRoot})");
        if (report.Checkouts.Count > 0)
        {
            Out.Heading("Checkouts");
            Out.Table(report.Checkouts.Select(c => new[]
            {
                new Span(c.Name, Tone.Bold),
                new Span(c.Held switch { BpmHeld.Pinned => "pinned", BpmHeld.WorkingTree => "working tree", _ => "inline" }, Tone.Dim),
                new Span(!c.Exists ? "missing" : c.Branch is { } b ? $"{b} @ {BpmService.Short(c.Head)}" : BpmService.Short(c.Head), c.Exists ? Tone.Plain : Tone.Warn),
                new Span(c.Changes > 0 ? $"{c.Changes} changed" : "", Tone.Warn),
                new Span(c.Folder, Tone.Dim),
            }).ToList());
        }
        if (report.Mounts.Count > 0)
        {
            Out.Heading("Mounted packages");
            Out.Table(report.Mounts.Select(m => new[] { new Span(m.Id, Tone.Bold), new Span(m.Line ?? "(embedded)"), new Span(m.Source, m.Override ? Tone.Warn : Tone.Dim) }).ToList());
        }
        if (report.Issues.Count == 0) Out.Success("Everything matches bpm-lock.json.");
        Report(report.Issues);
    }

    private async Task AppConfigAsync(List<string> values)
    {
        var options = values.ToList();
        var name = TakeValue(options, "--name");
        var company = TakeValue(options, "--company");
        var identifier = TakeValue(options, "--identifier");
        var version = TakeValue(options, "--version");
        var verb = options.Count == 0 ? "show" : options[0].ToLowerInvariant();
        if (verb != "init" && (name ?? company ?? identifier ?? version) is not null) throw new UsageException("--name, --company, --identifier and --version go with 'app-config init'; use 'app-config set' to change one value.");
        var project = await BpmProjectAsync();
        switch (verb)
        {
            case "show":
                Expect(options, 0, 0, "");
                ShowAppConfig(project);
                return;
            case "init":
            {
                Expect(options, 1, 1, "");
                if (BasisConfigService.Exists(project)) throw new UsageException($"{BasisConfigService.FileName} already exists; change it with 'app-config set'.");
                var config = BasisConfigService.Starter(project, name);
                if (company is not null) config.CompanyName = company.Trim();
                config.Identifier = identifier?.Trim() ?? (company is not null ? BasisConfigService.SuggestIdentifier(company, config.ProductName ?? "app") : config.Identifier);
                if (version is not null) config.Version = version.Trim();
                BasisConfigService.Write(project, config);
                Out.Success($"Wrote {BasisConfigService.ConfigPath(project)}.");
                ShowAppConfig(project);
                return;
            }
            case "get":
            {
                Expect(options, 2, 2, "a key");
                var config = BasisConfigService.Read(project) ?? throw new UsageException($"{project} has no {BasisConfigService.FileName}. Run 'app-config init' first.");
                string? value;
                try { value = BasisConfigService.Get(config, options[1]); }
                catch (ArgumentException ex) { throw new UsageException(ex.Message); }
                if (Out.JsonMode) Out.Json(new { key = options[1], value });
                else if (value is null) Out.Hint($"{options[1]} isn't set.");
                else Console.WriteLine(value);
                return;
            }
            case "set" or "unset":
            {
                var unset = verb == "unset";
                Expect(options, unset ? 2 : 3, unset ? 2 : 3, unset ? "a key" : "a key and a value");
                try { BasisConfigService.Update(project, c => BasisConfigService.Set(c, options[1], unset ? null : options[2])); }
                catch (ArgumentException ex) { throw new UsageException(ex.Message); }
                Out.Success(unset ? $"Removed {options[1]} from {BasisConfigService.FileName}." : $"Set {options[1]} to {options[2]} in {BasisConfigService.FileName}.");
                Report(BasisConfigService.Check(project).Where(i => i.Subject != "server"));
                return;
            }
            case "apply":
            {
                Expect(options, 1, 1, "");
                var result = BasisConfigService.ApplyToProject(project, UnityOpenProbe.IsOpen(project));
                Report(result);
                ThrowIfFailed(result);
                Report(BasisConfigService.Check(project).Where(i => i.Level != BpmIssueLevel.Info));
                return;
            }
            default: throw new UsageException($"\"{options[0]}\" isn't an app-config action. Use init, get, set, unset or apply, or nothing to show the file.");
        }
    }

    private static void ShowAppConfig(string project)
    {
        var config = BasisConfigService.Read(project);
        var issues = BasisConfigService.Check(project);
        if (Out.JsonMode)
        {
            Out.Json(new
            {
                path = BasisConfigService.ConfigPath(project),
                exists = config is not null,
                productName = config?.ProductName, companyName = config?.CompanyName, identifier = config?.Identifier, version = config?.Version,
                features = config?.Features, settings = config?.Settings,
                issues = issues.Select(i => new { level = i.Level.ToString(), subject = i.Subject, message = i.Message }),
            });
            return;
        }
        if (config is null)
        {
            Out.Hint($"{project} has no {BasisConfigService.FileName}. 'app-config init' writes one from ProjectSettings.");
            return;
        }
        Out.Heading(BasisConfigService.ConfigPath(project));
        Out.Table(new List<Span[]>
        {
            new[] { new Span("productName", Tone.Bold), new Span(config.ProductName ?? "(not set)") },
            new[] { new Span("companyName", Tone.Bold), new Span(config.CompanyName ?? "(not set)") },
            new[] { new Span("identifier", Tone.Bold), new Span(config.Identifier ?? "(not set)") },
            new[] { new Span("version", Tone.Bold), new Span(config.Version ?? "(not set)") },
        });
        if (config.Features is { Count: > 0 } features)
        {
            Out.Heading("Features");
            Out.Table(features.Select(f => new[] { new Span(f.Key, Tone.Bold), new Span(f.Value ? "on" : "off", f.Value ? Tone.Plain : Tone.Warn) }).ToList());
        }
        if (config.Settings is { Count: > 0 } settings)
        {
            Out.Heading("Setting defaults");
            Out.Table(settings.Select(s => new[] { new Span(s.Key, Tone.Bold), new Span(s.Value.ValueKind == System.Text.Json.JsonValueKind.String ? s.Value.GetString() ?? "" : s.Value.GetRawText()) }).ToList());
        }
        if (issues.Count == 0) Out.Success("Nothing to fix.");
        Report(issues);
    }
}
