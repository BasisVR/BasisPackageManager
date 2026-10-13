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
            Usage = new[] { "clone-basis <empty-folder> [branch] [--name <name>] [--without <part>[,<part>]]" },
            Details = "Downloads Basis with its full history (a few GB), checks out the branch (developer unless you name another) and saves the clone as a project, like New project in the app. "
                + "--without leaves parts out of the folder (server: the Basis Server folder, images: Basis/Images). Git still has them, so updates keep working, and 'parts add' brings them back.",
            Options = new[] { new CliOption("--name", "Display name for the new project", "<name>"), new CliOption("--without", "Parts to leave out: server, images", "<parts>", ValueKind: ArgKind.Part) },
            Args = new[] { ArgKind.Path, ArgKind.BasisBranch },
            Examples = new[]
            {
                new CliExample("clone-basis C:\\BasisVR\\Basis", "Clone the developer branch"),
                new CliExample("clone-basis ~/Basis long-term-support-20260916 --name LTS", "Clone a long-term-support branch"),
                new CliExample("clone-basis D:\\Worlds\\Basis --without server,images", "Clone without the Basis Server and the readme images"),
            },
            SeeAlso = new[] { "parts" },
            Run = CloneBasisAsync,
        },
        new()
        {
            Name = "parts", Group = ProjectsGroup, Aliases = new[] { "part" }, Json = true,
            Summary = "Leave the Basis Server or images out of the project",
            Usage = new[] { "parts", "parts remove <part>... [--keep-ignored] [--yes]", "parts add <part>..." },
            Details = "Parts are folders a project can do without: server (Basis Server) and images (Basis/Images). 'remove' takes a part out of the project folder with git's sparse "
                + "checkout and 'add' brings it back. Git keeps the files either way, so Basis updates, branch switches and your commits are unaffected. Removing a part also deletes "
                + "the files git ignores in it, such as build output and the server's config; it asks first unless you pass --yes, and --keep-ignored leaves them. A part with "
                + "uncommitted changes isn't removed.",
            Options = new[] { new CliOption("--keep-ignored", "Leave the files git ignores where they are"), Yes },
            Verbs = new() { ["add"] = new[] { ArgKind.Part }, ["remove"] = new[] { ArgKind.Part } },
            RepeatLastArg = true,
            Examples = new[]
            {
                new CliExample("parts", "Show which parts the project has and which are left out"),
                new CliExample("parts remove server", "Leave the Basis Server out of the project"),
                new CliExample("parts add server images", "Bring both parts back"),
            },
            SeeAlso = new[] { "clone-basis", "status" },
            Run = PartsAsync,
        },
        new()
        {
            Name = "status", Group = ProjectsGroup, Json = true, Summary = "Show the project's Unity, git, packages and server",
            Usage = new[] { "status" }, SeeAlso = new[] { "doctor", "check-updates" }, Run = ShowStatusAsync,
        },
        new()
        {
            Name = "sync", Group = ProjectsGroup,
            Summary = "Pull your team's commits, then restore packages",
            Usage = new[] { "sync [--yes]" },
            Details = "Pulls the current branch from its own remote (fast-forward only, so your commits and edits are never rewritten), then clones the packages "
                + "Packages/basispackages.json lists that are missing and moves clones to the listed versions, like 'restore'. Basis itself isn't touched; use 'update-basis' for that.",
            Options = new[] { new CliOption("--yes", "Install listed packages that need review without asking", Short: "-y") },
            SeeAlso = new[] { "restore", "update-basis" }, Run = SyncProjectAsync,
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
            Name = "storage", Group = ProjectsGroup, Aliases = new[] { "disk" }, Json = true,
            Summary = "Show how much disk space the project uses",
            Usage = new[] { "storage" },
            Details = "Measures the whole project and splits out Unity's Library (and its PackageCache), Temp, Logs and obj folders, the Basis Server build output "
                + "and each .basisdev clone, and says which clones Unity doesn't load. It also shows the shared download cache.",
            SeeAlso = new[] { "clean" }, Run = StorageAsync,
        },
        new()
        {
            Name = "clean", Group = ProjectsGroup,
            Summary = "Free disk space used by Library, Temp, logs or clones",
            Usage = new[] { "clean library|temp|logs|clones|cache [--yes]" },
            Details = "library deletes Unity's Library folder; Unity rebuilds it the next time it opens the project, which can take a long time. temp deletes Unity's Temp folder. "
                + "Both refuse while Unity has the project open. logs deletes the project's log files, leaving any that are in use. clones removes .basisdev clones Unity "
                + "doesn't load, as long as they have no uncommitted or unpushed work and aren't listed in Packages/basispackages.json. cache empties the shared download "
                + "cache that makes new clones faster; it isn't tied to a project.",
            Verbs = new() { ["library"] = Array.Empty<ArgKind>(), ["temp"] = Array.Empty<ArgKind>(), ["logs"] = Array.Empty<ArgKind>(), ["clones"] = Array.Empty<ArgKind>(), ["cache"] = Array.Empty<ArgKind>() },
            Options = new[] { Yes },
            Examples = new[] { new CliExample("clean library --yes", "Delete Library so Unity rebuilds it"), new CliExample("clean clones", "Remove clones Unity doesn't use") },
            SeeAlso = new[] { "storage" }, Run = CleanAsync,
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
            Usage = new[] { "install-package <package-id>... [--version <ref>] [--no-deps] [--yes]" },
            Details = "Each package is cloned into the project as an editable working copy, pinned to its newest stable release that works with the project's Unity and Basis, "
                + "and its registry dependencies are added to manifest.json. Packages its package.json needs (dependencies, vpmDependencies or gitDependencies) that the project "
                + "doesn't have are installed after it unless you pass --no-deps. Without git it becomes a manifest dependency instead. Installing a package that is already there "
                + "moves its working copy to the newest release, or to the --version you name, in place: your edits are kept when they don't clash. Packages that ship with Basis are skipped. A package from a catalog you added, or a version the Basis registry hasn't checked, is "
                + "checked for risky code and shown to you before Unity can load it. Packages the registry has pulled are refused.",
            Options = new[]
            {
                new CliOption("--version", "A release tag, branch or commit; 'latest' is the newest release, 'default' the default branch", "<ref>"),
                new CliOption("--no-deps", "Don't install the packages its package.json needs"),
                new CliOption("--yes", "Replace a working copy whose edits clash with the update, or install a package that needs review, without asking", Short: "-y"),
            },
            Args = new[] { ArgKind.CatalogPackage }, RepeatLastArg = true,
            Examples = new[] { new CliExample("install-package com.example.tools", "Install the newest release"), new CliExample("install-package com.example.tools --version v2.1.0", "Install or switch to a specific release") },
            SeeAlso = new[] { "list-packages", "versions", "remove-package" }, Run = InstallPackageAsync,
        },
        new()
        {
            Name = "verify", Group = PackagesGroup, Aliases = new[] { "verify-packages" }, Json = true,
            Summary = "Check packages for conflicts and compile the project",
            Usage = new[] { "verify [<package-id>...] [--no-compile] [--batch|--open-editor] [--upgrade-apis] [--timeout <minutes>] [--record <file>]" },
            Details = "Looks for problems Unity hits with the project's packages: types, namespaces, assemblies, DLLs or GUIDs that clash, package.json mistakes, "
                + "scripts Unity won't compile and dependencies it can't find. Then it compiles the project with its own Unity version: in batch mode when Unity is "
                + "closed, or by reading the result of the open editor's next recompile. Naming packages limits the report to them and the problems they cause. "
                + "Results are saved, so the app shows them too and the next run marks anything new. Exits with code 1 when it finds an error or can't compile. "
                + "--record adds the results to a registry checks file under the project's Basis branch.",
            Options = new[]
            {
                new CliOption("--no-compile", "Only look for conflicts and package problems"),
                new CliOption("--batch", "Compile in batch mode (Unity must be closed)"),
                new CliOption("--open-editor", "Read the result of the open editor's next recompile"),
                new CliOption("--upgrade-apis", "Rename obsolete Unity APIs in .basisdev clones, then compile again"),
                new CliOption("--timeout", "Minutes to wait for Unity (default 60)", "<minutes>"),
                new CliOption("--record", "Add the results to a registry checks file", "<file>", ValueKind: ArgKind.Path),
            },
            Args = new[] { ArgKind.InstalledPackage }, RepeatLastArg = true,
            Examples = new[]
            {
                new CliExample("verify", "Check every package, then compile the project"),
                new CliExample("verify com.basis.steamlobby", "Report only on one package"),
                new CliExample("verify --no-compile", "Look for conflicts without starting Unity"),
            },
            SeeAlso = new[] { "check-package", "check-registry", "install-package" },
            Run = VerifyAsync,
        },
        new()
        {
            Name = "check-package", Group = PackagesGroup, Json = true,
            Summary = "Check a package folder for Unity package problems",
            Usage = new[] { "check-package <folder>" },
            Details = "For package authors: checks package.json, assembly definitions, namespaces and the folder layout, and, when a project is picked, conflicts "
                + "with the packages already in it. Nothing is installed or compiled. Exits with code 1 when it finds an error.",
            Args = new[] { ArgKind.Path },
            SeeAlso = new[] { "verify" },
            Run = CheckPackageAsync,
        },
        new()
        {
            Name = "check-registry", Group = PackagesGroup,
            Summary = "Install and check every registry package in turn",
            Usage = new[] { "check-registry [<package-id>...] [--record <file>] [--no-compile] [--yes]" },
            Details = "For registry maintainers, on a spare clone of Basis: installs each registry package (or the ones you name) into the project, checks it like "
                + "'verify' does, then puts the project's package files back. --record saves each result in a registry checks file under the project's Basis "
                + "branch, which is how basisvr.org/packages/checks.json is made. Exits with code 1 when a package couldn't be checked.",
            Options = new[]
            {
                new CliOption("--record", "Add the results to a registry checks file", "<file>", ValueKind: ArgKind.Path),
                new CliOption("--no-compile", "Only look for conflicts and package problems"),
                Yes,
            },
            Args = new[] { ArgKind.CatalogPackage }, RepeatLastArg = true,
            SeeAlso = new[] { "verify" },
            Run = CheckRegistryAsync,
        },
        new()
        {
            Name = "remove-package", Group = PackagesGroup, Aliases = new[] { "uninstall", "remove" },
            Summary = "Remove packages you added on top of Basis",
            Usage = new[] { "remove-package <package-id>... [--yes] [--force]" },
            Details = "Deletes the package's working copy and its manifest.json line, and its Basis Server side when it has one. Asks first when other packages in the project need it, "
                + "and when the working copy has uncommitted edits or unpushed commits.",
            Options = new[]
            {
                new CliOption("--yes", "Discard local edits in the working copy without asking", Short: "-y"),
                new CliOption("--force", "Remove it even when other packages need it, or when git history can't show whether Basis needs its manifest.json line"),
            },
            Args = new[] { ArgKind.InstalledPackage }, RepeatLastArg = true, Run = RemovePackageAsync,
        },
        new()
        {
            Name = "update-packages", Group = PackagesGroup, Aliases = new[] { "upgrade" },
            Summary = "Move installed packages to their newest release",
            Usage = new[] { "update-packages [package-id]... [--dry-run] [--notes] [--yes]" },
            Details = "Without ids it checks every git package in the project. A package on a release moves to the newest release that works with the project's Unity and "
                + "Basis; one on a branch picks up that branch's new commits. Working copies are moved in place, so edits and commits are kept; when they clash with the update "
                + "the package is left as it was and the clashing files are listed. --dry-run lists what would change and --notes adds each update's release notes.",
            Options = new[] { new CliOption("--dry-run", "List the updates without installing them"), new CliOption("--notes", "Show the release notes of each update"), Yes },
            Args = new[] { ArgKind.InstalledPackage }, RepeatLastArg = true, Run = UpdatePackagesAsync,
        },
        new()
        {
            Name = "release-notes", Group = PackagesGroup, Aliases = new[] { "whats-new" }, Json = true,
            Summary = "Show what changed in a package's next update",
            Usage = new[] { "release-notes <package-id> [--to <ref>]" },
            Details = "Lists the GitHub release notes and the commits between the installed version and the update that's available, or the --to version you name.",
            Options = new[] { new CliOption("--to", "Show the changes up to this release, branch or commit", "<ref>") },
            Args = new[] { ArgKind.InstalledPackage },
            Examples = new[] { new CliExample("release-notes com.example.tools", "What the next update brings"), new CliExample("release-notes com.example.tools --to v2.0.0", "Everything up to a release") },
            SeeAlso = new[] { "update-packages", "versions" }, Run = ReleaseNotesAsync,
        },
        new()
        {
            Name = "restore", Group = PackagesGroup, Aliases = new[] { "restore-packages" },
            Summary = "Clone the packages basispackages.json lists",
            Usage = new[] { "restore [--no-switch] [--yes]" },
            Details = "Clones every package Packages/basispackages.json lists that's missing from .basisdev, at the commit it lists, and moves existing clones to the listed "
                + "version when a teammate changed it (clones with clashing edits are left alone). Run it after cloning a project or in CI. Exits with code 1 when anything couldn't be restored.",
            Options = new[] { new CliOption("--no-switch", "Only clone missing packages; leave existing clones where they are"), new CliOption("--yes", "Install listed packages that need review without asking", Short: "-y") },
            SeeAlso = new[] { "sync", "basisdev" }, Run = RestorePackagesAsync,
        },
        new()
        {
            Name = "add-git", Group = PackagesGroup, Summary = "Add a package straight from a GitHub repository",
            Usage = new[] { "add-git <github-url|owner/repo> [--force] [--yes]" },
            Details = "Takes a repository URL or owner/repo, with an optional folder (?path=Packages/x, or a /tree/<branch>/<folder> URL) and #ref. It reads the package.json there, "
                + "clones the package into .basisdev and pulls in its registry dependencies. A package that isn't in the Basis registry is checked for risky code and "
                + "shown to you before manifest.json points at it. A package that ships with Basis is only replaced with --force.",
            Options = new[] { new CliOption("--force", "Replace a package that ships with Basis"), new CliOption("--yes", "Install without asking, even when the package needs review", Short: "-y") },
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
            Usage = new[] { "install-package-list <list-id|file.json> [--yes]" }, Args = new[] { ArgKind.PackageList }, Run = InstallPackageListAsync,
            Details = "Registry packages are added from the registry's own repository, whatever URL the list gives. A listed package that isn't in the registry is cloned, "
                + "checked for risky code and shown to you before Unity can load it.",
            Options = new[] { new CliOption("--yes", "Install packages that need review without asking", Short: "-y") },
        },
        new()
        {
            Name = "scan", Group = PackagesGroup, Json = true,
            Summary = "Check a package folder or git URL for risky code",
            Usage = new[] { "scan <folder|git-url> [--strict]" },
            Details = "Looks through a package without running it: editor code that runs by itself, programs it starts, code it loads at run time, compiled and native "
                + "libraries, saved-login paths and chat webhooks. A git URL is downloaded to a temporary folder first. It finds common warning signs; it can't prove a "
                + "package is safe.",
            Options = new[] { new CliOption("--strict", "Exit with code 1 when anything high-risk is found") },
            Args = new[] { ArgKind.Path },
            Examples = new[] { new CliExample("scan https://github.com/owner/repo.git#v1.2.0", "Check a release before adding it to the registry"), new CliExample("scan ./Packages/com.example.tool --strict", "Check a local package and fail on anything high-risk") },
            SeeAlso = new[] { "add-git", "install-package" }, Run = ScanPackageAsync,
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
            Name = "licenses", Group = PackagesGroup, Aliases = new[] { "notices" }, Json = true,
            Summary = "List package licenses and save third-party notices",
            Usage = new[] { "licenses [--output <file>] [--include-unity]" },
            Details = "Lists every package Unity loads in the project with its license, and flags packages that state no license or use a copyleft license "
                + "(GPL, LGPL, AGPL, MPL, EPL or CC-BY-SA). --output writes third-party notices in Markdown with each package's full license text, ready to ship with a build.",
            Options = new[] { new CliOption("--output", "Write the notices (Markdown) to this file", "<file>", ValueKind: ArgKind.Path), new CliOption("--include-unity", "Also list Unity's own com.unity.* packages") },
            Examples = new[] { new CliExample("licenses --output THIRD-PARTY-NOTICES.md", "Save notices to ship with a build") },
            Run = LicensesAsync,
        },
        new()
        {
            Name = "basisdev", Group = DevGroup, Aliases = new[] { "dev-packages" },
            Summary = "Show and reconcile the development clones",
            Usage = new[] { "basisdev [--all]", "basisdev reconcile", "basisdev record|forget|use|release|restore|reclone|ignore|unignore|move|switch <package-id> [--force]", "basisdev share <package-id> [--branch <name>] [--message <text>]" },
            Details = "Lists the development clones with their recorded upstream and anything that needs reconciling (--all lists every package). Installed packages are cloned into "
                + ".basisdev and listed in Packages/basispackages.json, which the project can commit even when .basisdev is ignored. When the Basis repo has its own copy "
                + "of a cloned package, it compares the two. 'reconcile' records clones without a sidecar or basispackages.json entry and forgets stale mount records. 'use' points manifest.json at a clone that "
                + "isn't in use; 'release' deletes a clone (the project's own copy or manifest source stays); 'restore' and 'reclone' fix a missing clone, such as one listed in "
                + "basispackages.json on a fresh checkout; 'ignore' stops flagging a clone that isn't used because the project, such as the Basis repo, handles the package itself; "
                + "'move' moves an older clone from Packages into .basisdev; 'switch' moves a clone to the version basispackages.json lists when a teammate changed it; "
                + "'share' commits your edits, pushes them to your fork on GitHub (or a branch you can push to) and points basispackages.json at that branch so teammates get them.",
            Options = new[]
            {
                new CliOption("--all", "List every package, not only clones and problems"), new CliOption("--force", "Go ahead even when that discards local work"),
                new CliOption("--branch", "Branch to push shared edits to", "<name>"), new CliOption("--message", "Commit message for shared edits", "<text>"),
            },
            Verbs = new()
            {
                ["reconcile"] = Array.Empty<ArgKind>(), ["record"] = new[] { ArgKind.InstalledPackage }, ["forget"] = new[] { ArgKind.InstalledPackage }, ["use"] = new[] { ArgKind.InstalledPackage },
                ["release"] = new[] { ArgKind.InstalledPackage }, ["restore"] = new[] { ArgKind.InstalledPackage }, ["reclone"] = new[] { ArgKind.InstalledPackage },
                ["ignore"] = new[] { ArgKind.InstalledPackage }, ["unignore"] = new[] { ArgKind.InstalledPackage }, ["move"] = new[] { ArgKind.InstalledPackage },
                ["switch"] = new[] { ArgKind.InstalledPackage }, ["share"] = new[] { ArgKind.InstalledPackage },
            },
            Run = BasisDevAsync,
        },
        new()
        {
            Name = "new-package", Group = DevGroup, Aliases = new[] { "create-package" },
            Summary = "Create a package of your own in .basisdev",
            Usage = new[] { "new-package <id> [--name <text>] [--description <text>] [--author <name>] [--license mit|apache-2.0|none] [--reference <assembly>]... [--no-runtime] [--no-editor] [--server] [--samples] [--github [--private]]" },
            Details = "Creates the package in .basisdev/<id> as its own git repository with package.json, README, CHANGELOG, a license, Runtime and Editor assembly definitions "
                + "that reference Basis (Basis Framework, BasisSDK and BasisCommon unless you pick with --reference), and optionally a Server~ part for the Basis Server and "
                + "Samples~. manifest.json loads it with a file: line. package.json asks for this project's Unity version and, when it can be worked out, the Basis version the "
                + "project is based on. --github creates a repository under your GitHub account and pushes the package there.",
            Options = new[]
            {
                new CliOption("--name", "Display name (default: from the id)", "<text>"),
                new CliOption("--description", "One line about what the package does", "<text>"),
                new CliOption("--author", "Author name (default: your git user.name)", "<name>"),
                new CliOption("--license", "mit (default), apache-2.0 or none", "<license>"),
                new CliOption("--reference", "Basis assembly the code uses (repeatable)", "<assembly>"),
                new CliOption("--no-runtime", "Leave out the Runtime folder"),
                new CliOption("--no-editor", "Leave out the Editor folder"),
                new CliOption("--server", "Add a Server~ part for the Basis Server"),
                new CliOption("--samples", "Add a Samples~ folder"),
                new CliOption("--github", "Create a GitHub repository and push the package"),
                new CliOption("--private", "Make that repository private"),
            },
            Examples = new[]
            {
                new CliExample("new-package com.mystudio.seats --name \"Seats\"", "Create a package with runtime and editor code"),
                new CliExample("new-package com.mystudio.chat --server --github", "Include server code and put it on GitHub"),
            },
            Run = NewPackageAsync,
        },
        new()
        {
            Name = "publish-package", Group = DevGroup, Aliases = new[] { "release-package" }, Json = true,
            Summary = "Release a new version of a package you work on",
            Usage = new[] { "publish-package <id> [--version <x.y.z> | --bump patch|minor|major] [--notes <text>] [--no-release] [--submit [--category <name>] [--tag <tag>]...] [--dry-run] [--yes]" },
            Details = "Checks the package, writes the new version into package.json, adds the release notes to CHANGELOG.md, commits, tags the commit (in the style the "
                + "repository already uses, vX.Y.Z by default), pushes the branch and the tag together and creates a GitHub release. Uncommitted changes in the clone are "
                + "part of the release commit. The notes default to the commit subjects since the last tag. --submit then opens a pull request that adds the package "
                + "to the Basis registry. --dry-run only shows what would happen.",
            Options = new[]
            {
                new CliOption("--version", "Version to release", "<x.y.z>"),
                new CliOption("--bump", "patch, minor or major", "<part>"),
                new CliOption("--notes", "Release notes (default: commit subjects since the last tag)", "<text>"),
                new CliOption("--no-release", "Push the tag without creating a GitHub release"),
                new CliOption("--submit", "Open a pull request that adds the package to the Basis registry"),
                new CliOption("--category", "Registry category for --submit", "<name>"),
                new CliOption("--tag", "Registry search tag for --submit (repeatable)", "<tag>"),
                new CliOption("--dry-run", "Show the plan and checks without changing anything"),
                Yes,
            },
            Examples = new[]
            {
                new CliExample("publish-package com.mystudio.seats --dry-run", "See the version, tag, notes and checks"),
                new CliExample("publish-package com.mystudio.seats --bump minor --yes", "Release the next minor version"),
            },
            Args = new[] { ArgKind.InstalledPackage }, Run = PublishPackageAsync,
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
            Examples = new[] { new CliExample("unity install --module windows-coreclr --module android", "Install the editor this project needs with two modules"), new CliExample("unity releases --stream lts", "Long-term-support releases") },
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
