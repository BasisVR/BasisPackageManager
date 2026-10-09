namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private const string ProjectsGroup = "Projects", UpdatesGroup = "Basis updates", PackagesGroup = "Packages", DevGroup = "Development clones (.basisdev)";
    private const string ContributeGroup = "Sending changes to Basis", ServerGroup = "Basis Server", UnityGroup = "Unity", ToolsGroup = "Settings and diagnostics", ConsoleGroup = "Interactive console";

    internal static readonly string[] GroupOrder = { ProjectsGroup, UpdatesGroup, PackagesGroup, DevGroup, ContributeGroup, ServerGroup, UnityGroup, ToolsGroup, ConsoleGroup };

    private static readonly CliOption Yes = new("--yes", "Don't ask for confirmation", Short: "-y");

    private IReadOnlyList<CliCommand> BuildCommands() => new List<CliCommand>
    {
        new()
        {
            Name = "projects", Group = ProjectsGroup, Aliases = new[] { "project" }, Json = true,
            Summary = "List your Basis projects; add, remove or rename one",
            Usage = new[] { "projects", "projects add <path> [--name <name>]", "projects remove <name>", "projects rename <name> <new-name>", "projects default [<name>|none]", "projects find [folder] [--add]" },
            Details = "Projects are shared with the desktop app's Projects tab. A command works on the project you pass with --project, the one you picked with 'use', "
                + "BASISPM_PROJECT, the project the current folder is in, your default project, or your only saved project, in that order. Removing a project only takes it "
                + "off the list; its files stay where they are.",
            Options = new[] { new CliOption("--name", "Display name for the project you add", "<name>"), new CliOption("--add", "Save the projects 'find' turns up") },
            Verbs = new() { ["add"] = new[] { ArgKind.Path }, ["remove"] = new[] { ArgKind.Project }, ["rename"] = new[] { ArgKind.Project }, ["default"] = new[] { ArgKind.Project }, ["find"] = new[] { ArgKind.Path } },
            Examples = new[]
            {
                new CliExample("projects", "List saved projects with their branch and Unity version"),
                new CliExample("projects add C:\\BasisVR\\Basis --name Main", "Save an existing clone as a project called Main"),
                new CliExample("projects rename Main \"Main LTS\"", "Change a project's display name"),
                new CliExample("projects default Main", "Use Main whenever no other project is picked"),
                new CliExample("projects find D:\\Projects --add", "Look for Basis clones in a folder and save them all"),
            },
            SeeAlso = new[] { "use", "clone-basis", "status" },
            Run = ProjectsAsync,
        },
        new()
        {
            Name = "use", Group = ProjectsGroup, Summary = "Pick the project the interactive console works on", Usage = new[] { "use <name|number|path>" },
            Details = "Takes a saved project's name or number (see 'projects'), or the path of a Basis clone. For a single command, pass --project instead.",
            Args = new[] { ArgKind.Project }, Run = UseAsync,
        },
        new()
        {
            Name = "clone-basis", Group = ProjectsGroup, Aliases = new[] { "clone" },
            Summary = "Clone BasisVR/Basis and add it to your projects",
            Usage = new[] { "clone-basis <empty-folder> [branch] [--name <name>]" },
            Details = "Downloads Basis with its full history (a few GB), checks out the branch (developer unless you name another) and saves the clone as a project, like New project in the app.",
            Options = new[] { new CliOption("--name", "Display name for the new project", "<name>") },
            Args = new[] { ArgKind.Path, ArgKind.BasisBranch },
            Examples = new[] { new CliExample("clone-basis C:\\BasisVR\\Basis", "Clone the developer branch"), new CliExample("clone-basis ~/Basis long-term-support-20260916 --name LTS", "Clone a long-term-support branch") },
            Run = CloneBasisAsync,
        },
        new()
        {
            Name = "status", Group = ProjectsGroup, Json = true, Summary = "Show the project's Unity, git, packages and server",
            Usage = new[] { "status" }, SeeAlso = new[] { "doctor", "check-updates" }, Run = ShowStatusAsync,
        },
        new()
        {
            Name = "backup", Group = ProjectsGroup, Summary = "Zip the project's Assets, Packages and settings",
            Usage = new[] { "backup [--to <folder>]" },
            Details = "Library and the other caches Unity rebuilds are left out, which keeps backups small. The zip goes into a BasisBackups folder next to the clone unless you pass --to.",
            Options = new[] { new CliOption("--to", "Folder to write the zip to", "<folder>", ValueKind: ArgKind.Path) },
            Run = BackupAsync,
        },
        new()
        {
            Name = "check-updates", Group = UpdatesGroup, Aliases = new[] { "check" }, Json = true,
            Summary = "Check BasisVR/Basis for new commits",
            Usage = new[] { "check-updates [--all]" },
            Options = new[] { new CliOption("--all", "Check every saved project") },
            SeeAlso = new[] { "update-basis", "update-all" }, Run = CheckUpdatesAsync,
        },
        new()
        {
            Name = "update-basis", Group = UpdatesGroup, Aliases = new[] { "update", "pull" },
            Summary = "Bring in the newest Basis, keeping your changes",
            Usage = new[] { "update-basis [--branch <name>] [--yes] [--backup] [--link] [--init-git]", "update-basis --continue | --abort" },
            Details = "Shows the incoming Basis commits and what happens to your own commits and uncommitted edits, then asks before changing anything. It works whatever git remote "
                + "the project uses. A copy kept in its own repository gets each update as one commit; --link merges Basis's history into it instead, after which every push also "
                + "uploads that history (over 2 GB). When files need a decision, run 'conflicts', settle each one with 'resolve', then 'update-basis --continue'.",
            Options = new[]
            {
                new CliOption("--branch", "Move onto another Basis branch, such as a long-term-support one", "<name>", ValueKind: ArgKind.BasisBranch), Yes,
                new CliOption("--backup", "Zip the project before updating (see 'backup')"),
                new CliOption("--link", "Link a copy's history to Basis instead of applying one commit"),
                new CliOption("--init-git", "Record a project that isn't in git in a new local repository first"),
                new CliOption("--continue", "Finish an update once every file is decided"),
                new CliOption("--abort", "Undo an update that is waiting on decisions"),
            },
            Examples = new[]
            {
                new CliExample("update-basis", "Show what's coming and ask before updating"),
                new CliExample("update-basis --branch long-term-support-20260916", "Move onto a long-term-support branch with your changes kept"),
                new CliExample("update-basis --yes --backup", "Back up, then update without asking"),
            },
            SeeAlso = new[] { "check-updates", "conflicts", "resolve", "basis-branch", "update-all" }, Run = UpdateBasisAsync,
        },
        new()
        {
            Name = "update-all", Group = UpdatesGroup, Summary = "Update every project that needs no decision",
            Usage = new[] { "update-all [--yes]" },
            Details = "Each project stays on the Basis branch it follows. A project that needs you (a predicted conflict, a move to another Basis branch, the first update of a copy, "
                + "a paused update, Unity open) is listed and left alone; update it on its own with update-basis.",
            Options = new[] { Yes }, SeeAlso = new[] { "update-basis", "check-updates" }, Run = UpdateAllAsync,
        },
        new()
        {
            Name = "conflicts", Group = UpdatesGroup, Json = true, Summary = "List files an update or switch needs you to decide",
            Usage = new[] { "conflicts" }, SeeAlso = new[] { "resolve" }, Run = ShowConflictsAsync,
        },
        new()
        {
            Name = "resolve", Group = UpdatesGroup, Summary = "Keep yours, take theirs, or mark a file done",
            Usage = new[] { "resolve <path> mine|basis|done", "resolve --all mine|basis" },
            Details = "'basis' takes the Basis version; during a branch switch 'theirs' (or 'basis') takes the other branch's version. 'done' marks a file you merged by hand once "
                + "its conflict markers are gone. Pick a whole version for Unity assets rather than editing them by hand.",
            Options = new[] { new CliOption("--all", "Settle every remaining file the same way") },
            Args = new[] { ArgKind.ConflictPath, ArgKind.ConflictChoice },
            Examples = new[] { new CliExample("resolve Basis/Packages/manifest.json mine", "Keep your version of one file"), new CliExample("resolve --all basis", "Take Basis's version of everything left") },
            Run = ResolveConflictAsync,
        },
        new()
        {
            Name = "basis-branch", Group = UpdatesGroup, Json = true, Summary = "Show or choose the Basis branch this branch follows",
            Usage = new[] { "basis-branch [branch]" },
            Details = "Without a branch it explains which Basis branch the project follows, how that was worked out, and which Basis commit it is built on. With one, the next update-basis moves onto it.",
            Args = new[] { ArgKind.BasisBranch }, Run = BasisBranchAsync,
        },
        new()
        {
            Name = "branches", Group = UpdatesGroup, Json = true, Summary = "List Basis's branches and your project's own branches",
            Usage = new[] { "branches" }, SeeAlso = new[] { "change-branch", "basis-branch" }, Run = ListBranchesAsync,
        },
        new()
        {
            Name = "change-branch", Group = UpdatesGroup, Aliases = new[] { "checkout" },
            Summary = "Switch branch, bringing uncommitted edits along",
            Usage = new[] { "change-branch <branch> [--yes]", "change-branch --continue | --abort" },
            Details = "Edited files that differ on the other branch are set aside and merged back after the switch. To move onto a Basis branch instead, use update-basis --branch.",
            Options = new[] { Yes, new CliOption("--continue", "Finish a switch once every file is decided"), new CliOption("--abort", "Go back to where you were") },
            Args = new[] { ArgKind.ProjectBranch }, Run = ChangeBranchAsync,
        },
        new()
        {
            Name = "list-packages", Group = PackagesGroup, Aliases = new[] { "packages", "ls" }, Json = true,
            Summary = "List packages you can add or have installed",
            Usage = new[] { "list-packages [search] [--installed] [--built-in]" },
            Details = "Packages that ship with Basis are hidden unless you pass --built-in. --installed shows only the packages in this project and the version each one is on.",
            Options = new[] { new CliOption("--installed", "Only packages in this project"), new CliOption("--built-in", "Packages that ship with Basis") },
            Examples = new[] { new CliExample("list-packages shader", "Search names, ids and categories"), new CliExample("list-packages --installed", "What this project added on top of Basis") },
            Run = ListPackagesAsync,
        },
        new()
        {
            Name = "info", Group = PackagesGroup, Aliases = new[] { "show" }, Json = true,
            Summary = "Show a package's details and install state",
            Usage = new[] { "info <package-id>" }, Args = new[] { ArgKind.Package }, SeeAlso = new[] { "versions" }, Run = PackageInfoAsync,
        },
        new()
        {
            Name = "versions", Group = PackagesGroup, Json = true, Summary = "List the releases a package can be installed at",
            Usage = new[] { "versions <package-id>" }, Args = new[] { ArgKind.Package }, SeeAlso = new[] { "install-package" }, Run = PackageVersionsAsync,
        },
        new()
        {
            Name = "install-package", Group = PackagesGroup, Aliases = new[] { "install", "add" },
            Summary = "Install registry packages and their dependencies",
            Usage = new[] { "install-package <package-id>... [--version <ref>] [--yes]" },
            Details = "Each package is cloned into the project as an editable working copy, pinned to its newest stable release, and its registry dependencies are added to "
                + "manifest.json. Without git it becomes a manifest dependency instead. Installing a package that is already there moves it to the newest release, or to the "
                + "--version you name. Packages that ship with Basis are skipped.",
            Options = new[]
            {
                new CliOption("--version", "A release tag, branch or commit; 'latest' is the newest release, 'default' the default branch", "<ref>"),
                new CliOption("--yes", "Replace a working copy that has local edits without asking", Short: "-y"),
            },
            Args = new[] { ArgKind.CatalogPackage }, RepeatLastArg = true,
            Examples = new[] { new CliExample("install-package com.example.tools", "Install the newest release"), new CliExample("install-package com.example.tools --version v2.1.0", "Install or switch to a specific release") },
            SeeAlso = new[] { "list-packages", "versions", "remove-package" }, Run = InstallPackageAsync,
        },
        new()
        {
            Name = "remove-package", Group = PackagesGroup, Aliases = new[] { "uninstall", "remove" },
            Summary = "Remove packages you added on top of Basis",
            Usage = new[] { "remove-package <package-id>... [--yes] [--force]" },
            Details = "Deletes the package's working copy and its manifest.json line, and its Basis Server side when it has one. Asks first when the working copy has uncommitted edits or unpushed commits.",
            Options = new[]
            {
                new CliOption("--yes", "Discard local edits in the working copy without asking", Short: "-y"),
                new CliOption("--force", "Remove a manifest.json line even when git history can't show whether Basis needs it"),
            },
            Args = new[] { ArgKind.InstalledPackage }, RepeatLastArg = true, Run = RemovePackageAsync,
        },
        new()
        {
            Name = "update-packages", Group = PackagesGroup, Aliases = new[] { "upgrade" },
            Summary = "Move installed packages to their newest release",
            Usage = new[] { "update-packages [package-id]... [--dry-run] [--yes]" },
            Details = "Without ids it updates every registry package in the project. Working copies with local edits are skipped, never discarded. --dry-run lists what would change.",
            Options = new[] { new CliOption("--dry-run", "List the updates without installing them"), Yes },
            Args = new[] { ArgKind.InstalledPackage }, RepeatLastArg = true, Run = UpdatePackagesAsync,
        },
        new()
        {
            Name = "add-git", Group = PackagesGroup, Summary = "Add a package straight from a GitHub repository",
            Usage = new[] { "add-git <github-url|owner/repo> [--force]" },
            Details = "Takes a repository URL or owner/repo, with an optional folder (?path=Packages/x, or a /tree/<branch>/<folder> URL) and #ref. It reads the package.json there, "
                + "adds the package to manifest.json and pulls in its registry dependencies. A package that ships with Basis is only replaced with --force.",
            Options = new[] { new CliOption("--force", "Replace a package that ships with Basis") },
            Examples = new[] { new CliExample("add-git owner/repo#v1.2.0", "Add the package at the repository root, pinned to a tag"), new CliExample("add-git \"https://github.com/owner/repo.git?path=Packages/com.owner.tool\"", "Add a package from a subfolder") },
            Run = AddGitAsync,
        },
        new()
        {
            Name = "list-package-lists", Group = PackagesGroup, Aliases = new[] { "list-lists", "package-lists" }, Json = true,
            Summary = "List the curated package lists in the registry", Usage = new[] { "list-package-lists" }, Run = ListPackageListsAsync,
        },
        new()
        {
            Name = "install-package-list", Group = PackagesGroup, Aliases = new[] { "install-list" },
            Summary = "Add every package in a package list or .json file",
            Usage = new[] { "install-package-list <list-id|file.json>" }, Args = new[] { ArgKind.PackageList }, Run = InstallPackageListAsync,
        },
        new()
        {
            Name = "export-package-list", Group = PackagesGroup, Aliases = new[] { "export-list" },
            Summary = "Save your added packages as a package list file",
            Usage = new[] { "export-package-list [file.json|-] [--name <name>] [--description <text>]" },
            Details = "Writes the git and registry packages this project adds on top of Basis, with the Basis branch, commit and Unity version it was made with, so others can "
                + "install the same set with install-package-list. '-' prints the list instead of writing a file.",
            Options = new[] { new CliOption("--name", "Name of the list (default: the project's name)", "<name>"), new CliOption("--description", "One line about what the list is for", "<text>") },
            Args = new[] { ArgKind.Path }, Run = ExportPackageListAsync,
        },
        new()
        {
            Name = "basisdev", Group = DevGroup, Aliases = new[] { "dev-packages" },
            Summary = "Show and reconcile the development clones",
            Usage = new[] { "basisdev [--all]", "basisdev reconcile", "basisdev record|forget|use|release|restore|reclone|ignore|unignore <package-id> [--force]" },
            Details = "Lists the development clones with their recorded upstream and anything that needs reconciling (--all lists every package). When the Basis repo has its own copy "
                + "of a cloned package, it compares the two. 'reconcile' records clones without a sidecar and forgets stale mount records. 'use' points manifest.json at a clone that "
                + "isn't in use; 'release' deletes a clone (the project's own copy or manifest source stays); 'restore' and 'reclone' fix a missing clone; 'ignore' stops flagging a "
                + "clone that isn't used because the project, such as the Basis repo, handles the package itself.",
            Options = new[] { new CliOption("--all", "List every package, not only clones and problems"), new CliOption("--force", "Go ahead even when that discards local work") },
            Verbs = new()
            {
                ["reconcile"] = Array.Empty<ArgKind>(), ["record"] = new[] { ArgKind.InstalledPackage }, ["forget"] = new[] { ArgKind.InstalledPackage }, ["use"] = new[] { ArgKind.InstalledPackage },
                ["release"] = new[] { ArgKind.InstalledPackage }, ["restore"] = new[] { ArgKind.InstalledPackage }, ["reclone"] = new[] { ArgKind.InstalledPackage },
                ["ignore"] = new[] { ArgKind.InstalledPackage }, ["unignore"] = new[] { ArgKind.InstalledPackage },
            },
            Run = BasisDevAsync,
        },
        new()
        {
            Name = "contribute", Group = ContributeGroup, Aliases = new[] { "send-to-basis" },
            Summary = "Send your changes to BasisVR/Basis as a pull request",
            Usage = new[]
            {
                "contribute",
                "contribute [--package <id>]... [--project-files] [--repository-files] [--path <path>]... [--all] --title <text> [--body <text>] [--branch <name>] [--target <basis-branch>] [--yes]",
                "contribute ... --dry-run",
            },
            Details = "Without a pick it lists what differs from the Basis version this project is based on: Basis packages, project files and repository files. Pick changes "
                + "(repeat --package and --path as needed) to open a pull request built on that Basis version. Your branch, history and working copy are left alone, and a fork "
                + "is made when you can't push to BasisVR/Basis. It signs in with the GitHub CLI (gh auth login) or GH_TOKEN / GITHUB_TOKEN. --dry-run only builds the commit locally.",
            Options = new[]
            {
                new CliOption("--package", "Changes in one Basis package (repeatable)", "<id>", ValueKind: ArgKind.InstalledPackage),
                new CliOption("--project-files", "Changes in the Unity project outside packages"),
                new CliOption("--repository-files", "Changes outside the Unity project"),
                new CliOption("--path", "Changes at or under a path (repeatable)", "<path>", ValueKind: ArgKind.Path),
                new CliOption("--all", "Everything that differs"),
                new CliOption("--title", "Pull request title", "<text>"),
                new CliOption("--body", "Pull request description", "<text>"),
                new CliOption("--branch", "Branch name for the pull request", "<name>"),
                new CliOption("--target", "Basis branch to open it against", "<basis-branch>", ValueKind: ArgKind.BasisBranch),
                Yes,
                new CliOption("--dry-run", "Build the commit locally and print its id; nothing is pushed"),
            },
            Examples = new[]
            {
                new CliExample("contribute", "See what differs from Basis"),
                new CliExample("contribute --package com.basis.framework --title \"Fix seat height\"", "Open a pull request with one package's changes"),
                new CliExample("contribute --path Basis/Assets/Scripts --dry-run", "Build the commit without pushing"),
            },
            Run = ContributeAsync,
        },
        new()
        {
            Name = "server-packages", Group = ServerGroup, Aliases = new[] { "server-list" }, Json = true,
            Summary = "List the packages built into the Basis Server", Usage = new[] { "server-packages" }, Run = ListServerPackagesAsync,
        },
        new()
        {
            Name = "server-install", Group = ServerGroup, Aliases = new[] { "server-add" }, Summary = "Add a server package (registry id, git URL, folder)",
            Usage = new[] { "server-install <package-id|git-url|file:path> [--link <folder>]" },
            Details = "Git URLs may carry ?path= and #ref. --link builds the package from a folder on this machine instead of its own copy.",
            Options = new[] { new CliOption("--link", "Build from a local folder", "<folder>", ValueKind: ArgKind.Path) },
            Args = new[] { ArgKind.CatalogPackage }, SeeAlso = new[] { "server-build" }, Run = InstallServerPackageAsync,
        },
        new()
        {
            Name = "server-update", Group = ServerGroup, Summary = "Move git server packages to their newest commit",
            Usage = new[] { "server-update [package-id] [--ref <branch|tag|commit>] [--force]" },
            Options = new[] { new CliOption("--ref", "Switch the package to another branch, tag or commit", "<ref>"), new CliOption("--force", "Discard local edits in the package") },
            Args = new[] { ArgKind.ServerPackage }, Run = UpdateServerPackagesAsync,
        },
        new()
        {
            Name = "server-remove", Group = ServerGroup, Aliases = new[] { "server-uninstall" }, Summary = "Remove a server package",
            Usage = new[] { "server-remove <package-id> [--force]" }, Options = new[] { new CliOption("--force", "Discard local edits in the package") },
            Args = new[] { ArgKind.ServerPackage }, Run = RemoveServerPackageAsync,
        },
        new()
        {
            Name = "server-restore", Group = ServerGroup, Summary = "Download missing server packages, write the lock",
            Usage = new[] { "server-restore" }, Run = RestoreServerPackagesAsync,
        },
        new()
        {
            Name = "server-link", Group = ServerGroup, Summary = "Build a server package from a local folder",
            Usage = new[] { "server-link <package-id> <folder>" }, Args = new[] { ArgKind.ServerPackage, ArgKind.Path }, Run = LinkServerPackageAsync,
        },
        new()
        {
            Name = "server-unlink", Group = ServerGroup, Summary = "Go back to building a server package from its own copy",
            Usage = new[] { "server-unlink <package-id>" }, Args = new[] { ArgKind.ServerPackage }, Run = UnlinkServerPackageAsync,
        },
        new()
        {
            Name = "server-build", Group = ServerGroup, Summary = "Build the Basis server (needs the .NET 10 SDK)", Usage = new[] { "server-build" },
            SeeAlso = new[] { "server-run" }, Run = BuildServerAsync,
        },
        new()
        {
            Name = "server-run", Group = ServerGroup, Aliases = new[] { "server-start" }, Summary = "Run the Basis server in this terminal",
            Usage = new[] { "server-run [--build] [--new-window]" },
            Details = "The server runs in this terminal until you press Ctrl+C. On its first run its setup wizard asks for the server's settings here. --new-window starts it in "
                + "its own console window instead, the way the app does.",
            Options = new[] { new CliOption("--build", "Build the server first when it hasn't been built"), new CliOption("--new-window", "Start it in a separate window and return") },
            SeeAlso = new[] { "server-build", "server-config", "connect" }, Run = RunServerAsync,
        },
        new()
        {
            Name = "server-config", Group = ServerGroup, Json = true, Summary = "Show or change the Basis server's config.xml",
            Usage = new[] { "server-config [--show-secrets]", "server-config <name>", "server-config <name> <value>" },
            Details = "config.xml is created by the server's first run. Changes reach a running server when it restarts.",
            Options = new[] { new CliOption("--show-secrets", "Show passwords and keys instead of hiding them") },
            Args = new[] { ArgKind.ServerConfigKey }, Examples = new[] { new CliExample("server-config Password \"a new password\"", "Change one setting") },
            Run = ServerConfigAsync,
        },
        new()
        {
            Name = "server-content", Group = ServerGroup, Json = true, Summary = "List, add or remove the server's content",
            Usage = new[] { "server-content", "server-content add <url> [--mode avatar|world|prop] [--password <password>] [--startup]", "server-content remove <file>" },
            Details = "Added content goes into the server's default library unless --startup is given, which loads it into the world when the server starts.",
            Options = new[]
            {
                new CliOption("--mode", "What the content is: avatar, world or prop (default avatar)", "<mode>", ValueKind: ArgKind.ContentMode),
                new CliOption("--password", "Unlock password of the content bundle", "<password>"),
                new CliOption("--startup", "Load it into the world at startup instead of the default library"),
            },
            Verbs = new() { ["add"] = Array.Empty<ArgKind>(), ["remove"] = new[] { ArgKind.ServerContent } },
            Run = ServerContentAsync,
        },
        new()
        {
            Name = "connect", Group = ServerGroup, Summary = "Launch Basis Labs through Steam and join a server",
            Usage = new[] { "connect [host[:port]] [--password <password>] [--wait]" },
            Details = "Defaults to this project's local server at 127.0.0.1:4296. A local server's own password is used when you don't give one. "
                + "--wait waits for a local server that is still starting (for example in another terminal with server-run).",
            Options = new[] { new CliOption("--password", "Server password", "<password>"), new CliOption("--wait", "Wait until the local server is ready") },
            Examples = new[] { new CliExample("connect", "Join the local server"), new CliExample("connect example.org:4296 --password hunter2", "Join a remote server") },
            Run = ConnectAsync,
        },
        new()
        {
            Name = "unity", Group = UnityGroup, Aliases = new[] { "editors" }, Json = true,
            Summary = "List, find and install Unity editors and modules",
            Usage = new[]
            {
                "unity", "unity releases [--stream lts|supported|tech|beta|alpha] [--search <text>] [--all]", "unity install [version] [--module <id>]...",
                "unity modules <version> <module>...", "unity add <editor-folder>", "unity forget <version|folder>", "unity hub",
            },
            Details = "Lists the editors Unity Hub knows about plus any you added by folder, and marks the version this project needs. Installs go through Unity Hub; "
                + "'unity install' with no version installs the one this project needs. 'unity add' registers an editor that Unity Hub doesn't manage.",
            Options = new[]
            {
                new CliOption("--stream", "Only releases from one stream", "<stream>", ValueKind: ArgKind.UnityStream),
                new CliOption("--search", "Only releases whose version contains this text", "<text>"),
                new CliOption("--all", "Show every release, not just the newest 25"),
                new CliOption("--module", "A build-support module to add, such as android or linux-il2cpp (repeatable)", "<id>", ValueKind: ArgKind.UnityModule),
            },
            Verbs = new()
            {
                ["releases"] = Array.Empty<ArgKind>(), ["install"] = new[] { ArgKind.UnityVersion }, ["modules"] = new[] { ArgKind.UnityVersion, ArgKind.UnityModule },
                ["add"] = new[] { ArgKind.Path }, ["forget"] = new[] { ArgKind.UnityVersion }, ["hub"] = Array.Empty<ArgKind>(), ["open"] = Array.Empty<ArgKind>(),
            },
            RepeatLastArg = true,
            Examples = new[] { new CliExample("unity install --module windows-il2cpp --module android", "Install the editor this project needs with two modules"), new CliExample("unity releases --stream lts", "Long-term-support releases") },
            SeeAlso = new[] { "open-unity" }, Run = UnityAsync,
        },
        new()
        {
            Name = "open-unity", Group = UnityGroup, Aliases = new[] { "open" }, Summary = "Open the project in the Unity editor it needs",
            Usage = new[] { "open-unity" }, Details = "Opens Unity Hub instead when that editor isn't installed.", Run = OpenUnityAsync,
        },
        new()
        {
            Name = "config", Group = ToolsGroup, Aliases = new[] { "settings" }, Json = true, Summary = "Show or change settings shared with the desktop app",
            Usage = new[] { "config", "config get <key>", "config set <key> <value>", "config unset <key>", "config path" },
            Details = "Keys: " + string.Join(", ", SettingKeys.Select(k => k.Key)) + ".",
            Verbs = new() { ["get"] = new[] { ArgKind.ConfigKey }, ["set"] = new[] { ArgKind.ConfigKey }, ["unset"] = new[] { ArgKind.ConfigKey }, ["path"] = Array.Empty<ArgKind>() },
            Examples = new[] { new CliExample("config set unity-hub-path \"D:\\Unity Hub\\Unity Hub.exe\"", "Use a Unity Hub that isn't in the usual place"), new CliExample("config unset catalog-url", "Go back to the Basis registry") },
            Run = ConfigAsync,
        },
        new()
        {
            Name = "doctor", Group = ToolsGroup, Json = true, Summary = "Check this computer and the project for problems",
            Usage = new[] { "doctor" }, Details = "Exits with code 1 when a check fails, so scripts can rely on it.", Run = DoctorAsync,
        },
        new()
        {
            Name = "logs", Group = ToolsGroup, Summary = "Show the diagnostic log of handled errors",
            Usage = new[] { "logs [--lines <count>] [--path]" },
            Options = new[] { new CliOption("--lines", "How many lines to show from the end (default 40)", "<count>"), new CliOption("--path", "Print only the log file's path") },
            Run = LogsAsync,
        },
        new()
        {
            Name = "news", Group = ToolsGroup, Aliases = new[] { "announcements" }, Json = true, Summary = "Show the latest Basis Package Manager announcements",
            Usage = new[] { "news [--all]" }, Options = new[] { new CliOption("--all", "Show every announcement, not only the latest five") }, Run = NewsAsync,
        },
        new()
        {
            Name = "completion", Group = ToolsGroup, Summary = "Print a tab-completion script for your shell",
            Usage = new[] { "completion [powershell|bash|zsh|fish]" },
            Details = "Without a shell it picks PowerShell on Windows and your $SHELL elsewhere.\n"
                + "PowerShell: add 'basispm completion powershell | Out-String | Invoke-Expression' to your $PROFILE.\n"
                + "bash: add 'eval \"$(basispm completion bash)\"' to ~/.bashrc.\n"
                + "zsh: add 'eval \"$(basispm completion zsh)\"' to ~/.zshrc, after compinit.\n"
                + "fish: run 'basispm completion fish > ~/.config/fish/completions/basispm.fish'.",
            Args = new[] { ArgKind.Shell }, Run = CompletionAsync,
        },
        new()
        {
            Name = "version", Group = ToolsGroup, Json = true, Summary = "Show the version and the git, .NET and OS it runs with",
            Usage = new[] { "version" }, Run = VersionAsync,
        },
        new()
        {
            Name = "help", Group = ToolsGroup, Aliases = new[] { "?" }, Summary = "Show every command, or one command in detail",
            Usage = new[] { "help [command]" }, Args = new[] { ArgKind.Command }, Run = HelpAsync,
        },
        new()
        {
            Name = "clear", Group = ConsoleGroup, Aliases = new[] { "cls" }, Interactive = true, Summary = "Clear the screen",
            Usage = new[] { "clear" }, Run = ClearAsync,
        },
        new()
        {
            Name = "exit", Group = ConsoleGroup, Aliases = new[] { "quit" }, Interactive = true, Summary = "Leave the interactive console (or press Ctrl+D)",
            Usage = new[] { "exit" }, Run = _ => Task.CompletedTask,
        },
        new()
        {
            Name = "__complete", Group = ToolsGroup, Hidden = true, Summary = "Print completions (used by the completion scripts)",
            Usage = new[] { "__complete [--shell <name>] <line>" }, Options = new[] { new CliOption("--shell", "Shell the candidates are for", "<name>") }, Run = CompleteAsync,
        },
    };
}
