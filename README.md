# Basis Package Manager

> ⚠️ **Work in progress — not ready for release.** This is an early preview under active
> development. Expect bugs, breaking changes, and missing features. Not recommended for general
> use yet — try it at your own risk.

A desktop app for cloning and living with [BasisVR/Basis](https://github.com/BasisVR/Basis):
clone the repo, add community packages from GitHub or GitLab, keep the core and packages up
to date, see exactly what you have changed in the Basis source, and install the Unity version
and platforms Basis needs.

Built with [AvaloniaUI](https://avaloniaui.net/) (.NET 9), styled to match
[basisvr.org](https://basisvr.org/).

## Download

Grab the latest installer for your platform from the
[**Releases**](https://github.com/BasisVR/BasisPackageManager/releases) page:

| Platform | File | First run |
|----------|------|-----------|
| Windows  | `*-win-Setup.exe` (x64) or `*-win-arm64-Setup.exe` (Windows on ARM) | Signed by SignPath Foundation (see the [code signing policy](#code-signing-policy)). If SmartScreen still warns, choose **More info → Run anyway**. |
| Linux    | `BasisPackageManager.AppImage` (x64) or `*-linux-arm64.AppImage` (arm64) | `chmod +x` it, then run. If it says it can't mount, run it with `--appimage-extract-and-run`. The first launch offers to add it to your applications menu. |
| macOS    | `*-osx-Setup.pkg` (Apple Silicon) or `*-osx-x64-Setup.pkg` (Intel) | Unsigned: open it once, then choose **Open Anyway** under System Settings → Privacy & Security. On macOS 14 and older, **right-click → Open** also works. |

Not sure which architecture you have? Windows: Settings → System → About → *System type*.
Mac: Apple menu → About This Mac (an *Intel* processor means `osx-x64`). Pick the matching
file — the installer refuses a package built for a different processor.

The app installs per-user (no admin required) and keeps itself up to date — see below.

## Updating

Basis Package Manager updates itself from GitHub Releases. When a newer version is
published, an **update banner** appears at the top of the window: click **Update now**
and it downloads the release and restarts onto it. You can also check on demand from
**Settings → About → Check for updates**.

Running from source (`dotnet run`) skips the in-app updater — update with `git pull`.

## Features

- **Installs**: clone the complete `BasisVR/Basis` repository (the `developer` branch), or
  register an existing project, including a copy of Basis kept in your own git repository. Each
  install shows its branch, commit, required Unity version, the Basis branch it follows, and
  whether it is behind its own remote or has local changes. **Change branch** either switches to
  another branch of your project (uncommitted edits come along) or moves the project onto another
  Basis branch, such as a long-term-support one, keeping your commits and edits. When a project is
  behind its own remote, **Pull and sync** pulls your team's commits (fast-forward only, so nothing
  of yours is rewritten) and then clones or moves packages to match `Packages/basispackages.json`.
- **Parts**: when cloning, untick **Basis Server** (the `Basis Server` folder) or **Images**
  (`Basis/Images`, the readme pictures) to leave them out of the project; the choice is remembered
  for the next clone, and **Parts** on a project adds or removes them later. Left-out parts use
  git's sparse checkout, so git still has their files: Basis updates, branch switches and your
  commits work as before, and ticking a part again brings it back. A part with uncommitted changes
  isn't removed, and the files git ignores in it (build output, the server's config) are deleted
  with it unless you choose to keep them.
- **Basis updates**: whatever git setup a project has (a clone of `BasisVR/Basis`, a fork, a copy
  pushed to your own repository, or no git at all), **Update Basis** fetches the latest Basis
  straight from GitHub; your own remotes are never used or changed. A project that shares history
  with Basis gets a normal merge. A project copied without Basis's history keeps it that way:
  BasisPM compares your files with Basis's history (a project made from Basis's `Basis` Unity folder
  alone works too) to find the version you started from, then brings the Basis changes in as one
  commit that records the Basis version and branch as `Basis-Commit:` / `Basis-Branch:` trailers,
  so your repository and your pushes stay small and teammates who pull see the same Basis version.
  A project that never recorded its Basis branch has it worked out from its history, so a project
  built on a long-term-support branch keeps following it. You see the incoming commits first.
  Uncommitted edits to files Basis also changes are set aside and merged back afterwards; any
  file changed on both sides is listed so you can keep yours, take Basis's, or combine them, and
  **Undo update** puts everything back. Moving to an older Basis branch takes the newer Basis
  changes out again while keeping yours. Projects are checked for Basis updates at startup and
  every two hours, with a banner and a badge on **Projects** when one is ready.
- **Packages** — packages bundled in the Basis checkout are detected automatically; install
  additional official packages, or add any community UPM package from a
  **GitHub or GitLab** git URL. Discovery is powered by the registry (below).
- **Package updates**: installed packages are checked in the background. A package on a release shows
  the newer release that's available, a package on a branch shows that the branch has new commits,
  and the Packages tab shows how many updates are waiting; **Update all** only touches packages that
  have one. **Update** first shows the release notes and commits, then moves the package's clone to
  the new version in place, so your edits and commits are kept. When they clash with the new
  version nothing is changed, and you choose whether to replace your copy with a clean one.
- **Compatibility and dependencies**: installs and updates pick the newest release that works with
  the project's Unity version and Basis version (see [Package compatibility](#package-compatibility));
  a newer release that doesn't fit is named on the package so you know why it isn't offered. After an
  install or update, packages its `package.json` needs (`dependencies`, `vpmDependencies` or
  `gitDependencies`) that the project doesn't have are offered, and a package with missing
  dependencies is flagged with **Install missing**. **Remove** warns when other packages in the
  project need the one you're removing, then takes the package out of the whole project: its
  `.basisdev` working copy, its `manifest.json` line, its entry in `Packages/basispackages.json` (so
  teammates don't get it back on their next sync) and its `packages-lock.json` entry.
- **Package checks**: after packages are installed, the manager checks that they fit into the
  project and that it still compiles, and says which package broke what. It first looks for the
  problems Unity hits without saying why: two packages declaring the same public type (a `CS0433`
  error in any code that uses it), a public type in the global namespace that hides a Unity one (a
  package's own `Debug` class breaks every `Debug.Log` in your scripts), assembly definitions with
  the same name, a `using` of a namespace the project doesn't have, a reference to an assembly that
  isn't there (Unity drops it silently), the same DLL in two packages, clashing `.meta` GUIDs, and
  `package.json` mistakes such as a git URL or a package Unity can't find in `dependencies` (Unity
  then stops loading every package). It also flags package habits that cause trouble later: scripts
  outside an assembly definition (Unity skips them), editor code in a runtime assembly (player
  builds fail), samples outside `Samples~`, and test assemblies without `UNITY_INCLUDE_TESTS`. The
  same checks appear in the review shown before an install. Then it compiles the project with its
  own Unity version: in batch mode when Unity is closed, or, when Unity has the project open, by
  reading the result of the editor's next recompile. Results are kept per project together with the
  Basis branch and commit they were made on, so a Basis update marks them as needing another look,
  and the installed packages are checked again after every Basis update.
- **Basis branches**: each package shows whether it works with the Basis branch the project is on
  (developer, a long-term-support branch or experimental), from the project's own checks or, before
  you install it, from the registry's `checks.json`, which the **Package checks** workflow fills in by
  compiling every registry package on each Basis branch whenever that branch moves.
- **Development clones (`.basisdev`)**: every package the manager installs is cloned into
  `<Unity project>/.basisdev/<package-id>` as an editable working copy, and `manifest.json` points at
  it with a `file:` line. Each clone gets a sidecar file, `.basisdev/<package-id>.json`, that records
  its upstream (git URL, branch or tag, sub-folder and the commit it was cloned at) and the
  `manifest.json` line it replaced. `.basisdev` is kept out of your repository's `git status`, so the
  same details (version, git URL, branch or tag, sub-folder and commit) are also listed in
  `Packages/basispackages.json`, next to `manifest.json`, which you can commit: on a fresh checkout
  the manager reads it and offers to clone each listed package again. The Packages tab shows where
  each package really loads from (a folder tracked in the Basis repo, a `.basisdev` clone, git or
  the registry) and flags anything that doesn't line up: a clone hidden by the Basis repo's own copy,
  a clone `manifest.json` no longer points at, a missing clone, a clone missing from
  `basispackages.json`, a clone an older version left in `Packages/` (**Move to .basisdev**), or a
  stale mount record, each with a fix. A clone you keep around on purpose while the Basis repo
  handles the package itself can be marked **Handled by Basis repo**, which stops it being flagged
  (**Flag again** undoes it). When a teammate moves a package to another version in
  `basispackages.json`, your clone is flagged **Different version** with **Use the listed version**,
  and **Restore all** clones every missing package and moves clones to the listed versions in one go.
  **Share with team** on a clone with edits commits them, pushes them to your fork on GitHub (or to
  the package's own repository when you can push there) and points `basispackages.json` at that
  branch, so teammates get your version the next time they sync.
- **Send changes to Basis**: **Changes vs Basis** on a project (or **Send to Basis** on a package
  that ships with Basis) opens a window listing everything the project changed compared with the
  Basis version it's based on, grouped into Basis packages, project files and repository files,
  with a diff for each file. Tick the changes to send, give them a title, and the manager opens a
  pull request on `BasisVR/Basis` (from your fork when you can't push there). The pull request is
  built from that Basis version plus only the files you picked, so it works whatever git the
  project itself uses: a clone, a fork, a copy in your own repository on GitHub, GitLab or
  anywhere else, or a Unity-folder-only copy. Your branch, history, working copy and remotes are
  never touched. `.meta` files go along with their assets, and files Unity rewrites by itself
  (TMP font assets, `packages-lock.json`, `ProjectVersion.txt`, addressables settings) start
  unticked. Sign-in uses the GitHub CLI (`gh auth login`) or a personal access token.
- **Making packages**: **New package** on the Packages tab creates a package of your own in
  `.basisdev/<id>` as its own git repository: `package.json` (asking for the project's Unity version
  and, when it can be worked out, the Basis version the project is based on), a README, a changelog,
  a license, Runtime and Editor assembly definitions that reference the Basis assemblies you pick,
  and optionally a `Server~` part for the Basis Server and `Samples~`. `manifest.json` loads it with a
  `file:` line, and it can be put on GitHub straight away. **Publish** on a package you can push to
  checks it, sets the new version (patch, minor, major or your own), adds the release notes to
  `CHANGELOG.md` (filled in from the commits since the last release), commits, tags the commit in
  the style the repository already uses, pushes the branch and the tag together and creates a
  GitHub release. If the push fails, nothing is left half done. Publish can also open a pull request
  that adds the package to the Basis registry.
- **Local Changes** — a `git status` of your install with a per-file unified diff, so you
  can see what you have modified in the Basis source.
- **Unity Editors** — detect installed editors and install the exact version Basis targets
  via Unity Hub, choosing the platform modules (Windows, Android, Linux, macOS, …). Only the
  modules that release has for your OS are offered, with the CoreCLR ones first and ticked.
- **Basis Server**: build and run the server included in the active checkout, edit every
  generated `config.xml` setting, manage default-library and startup content, and launch
  Basis Labs through Steam with an automatic server connection. The server runs inside the app:
  its output appears in a console on the Server tab, with a command line for server commands
  such as `/status` or `/perm`. **Stop server** asks it to shut down cleanly, **Restart server**
  applies `config.xml` changes, and closing the app stops it (after asking). The server's own
  setup wizard needs a terminal, so a first run in the app starts with the default settings; an
  admin player UUID entered on the Server tab is added to the server's admin group on that start.
  **Use the setup wizard instead** runs it in its own console window: Terminal on macOS, and
  your terminal app on Linux.
- **Server packages**: add packages to the Basis Server the same way you add them to Unity. Their
  server code is compiled straight into the server's own assemblies at build time (see
  `Basis Server/Packages/README.md` in Basis). A registry package marked as having server code
  installs on both sides at once, and the server builds it from the Unity project's copy when
  that copy is a local clone, so the client and the server always run the same code.
- **Package safety**: Unity runs a package's editor scripts as soon as it loads it, with your full
  access, so the manager checks what it can before that happens. Every install is cloned into
  `.basisdev` first, and `manifest.json` only points at it afterwards. Packages from the Basis
  registry install straight away. Anything else (a catalog you added, a git URL, a package list
  entry the registry doesn't list, a website link to an unlisted package, or a registry package at
  a version the registry hasn't checked) opens a review first, showing where it comes from and what
  its files can do. A package the registry pulls is flagged on the Packages tab of any project that
  has it, with a Remove button, and can't be installed again. See [Package safety](#package-safety).
- **Licenses**: **Licenses** on the Packages tab lists every package Unity loads in the project
  with the license it ships under, flags packages that state no license or use a copyleft license
  (GPL, LGPL, AGPL, MPL, EPL, CC-BY-SA), and saves third-party notices in Markdown with each
  package's full license text, ready to ship with a build. Unity's own packages can be included too.
- **Storage**: **Storage** on a project shows how much disk space it uses: Unity's Library (and its
  PackageCache), Temp, Logs and obj folders, the Basis Server build output, each `.basisdev` clone
  and everything else. Library, Temp and Logs can be cleaned from there (Library and Temp only while
  Unity has the project closed; Unity rebuilds them), and development clones Unity doesn't load can
  be removed when they have no uncommitted or unpushed work and aren't listed in
  `Packages/basispackages.json`.
- **Download cache**: every repository the manager downloads (Basis itself and every package) is
  kept as a bare copy in a shared cache, so cloning it again for another project only downloads what
  changed. Each clone still gets its own complete copy of its history, so clearing the cache never
  breaks a project. It is on by default; **Settings > Download cache** turns it off, shows its size
  and clears it.
- **Settings** — default clone location, catalog URL, Unity Hub override, and detected
  tooling paths.

## Requirements

- .NET 9 SDK
- .NET 10 SDK to build the Basis server from the Server tab
- [Git](https://git-scm.com/) 2.28 or newer on your `PATH` (used for clone / update / status / diff).
  On macOS run `xcode-select --install` or `brew install git`; on Linux use your package manager.
- [Unity Hub](https://unity.com/download) for editor installs
- Linux: a terminal app for the server's setup wizard, and a desktop session for Unity Hub installs

## macOS and Linux

- **Tools outside the app's `PATH`.** Apps opened from Finder, the Dock or a desktop menu get a
  short `PATH`, so the app also looks in the Homebrew and MacPorts folders, `/etc/paths.d`,
  `~/.dotnet`, `~/.local/bin`, and the Flatpak, Snap, Linuxbrew and Nix folders for git, dotnet,
  gh and Unity Hub.
- **Git on macOS.** `/usr/bin/git` counts as missing until Apple's command line tools are
  installed, because until then it only offers to install them.
- **Unity editors.** The Unity tab reads Unity Hub's own files (its editor folders, your custom
  install folder and editors you located), so it lists editors without starting the Hub, and on
  Linux without a display. Unity Hub is found in `/Applications` and `~/Applications` on macOS,
  and in the deb, AppImage and Flathub (`com.unity.UnityHub`) locations on Linux. On Apple
  silicon, editors install as Apple silicon builds, and when a version is installed for both
  Intel and Apple silicon, the native one opens.
- **Unity has the project open.** Branch switches, Basis updates and cleanups ask you to close
  Unity first, as on Windows. A lock file left behind by a crash doesn't count.
- **Server setup wizard.** **Use the setup wizard instead** opens the server in Terminal on
  macOS. On Linux it tries `xdg-terminal-exec`, `$TERMINAL`, `x-terminal-emulator`, then your
  desktop's terminal app. **Stop server** sends it Ctrl+C.
- **Links.** `basispm://` links from the registry website open in the app on macOS too.
- **Command line.** The console app ships inside the app:
  `/Applications/Basis Package Manager.app/Contents/MacOS/basispm` on macOS, and inside the
  AppImage on Linux. **Settings → Advanced → Detected tools → Add basispm to PATH** links it into
  `/usr/local/bin` or `~/.local/bin`. On Linux the link points at the AppImage, which starts the
  console app when it's run as `basispm`.
- **Applications menu (Linux).** The AppImage's first launch offers to add it to your
  applications menu with the Basis icon, and running the AppImage from a new place updates that
  entry.

## Console mode

Releases also include the `basispm` (`basispm.exe` on Windows) console application (on macOS and
Linux, see [Command line](#macos-and-linux) for putting it on your `PATH`). Run it
without arguments for an interactive console with Tab completion and command history, or pass a
command for scripts and one-shot use:

```text
basispm clone-basis C:\BasisVR\Basis
basispm status
basispm doctor
basispm check-updates
basispm update-basis
basispm list-packages
basispm install-package com.example.package
basispm server-run --build
basispm --project "Basis Avatar" basisdev
basispm contribute --package com.basis.framework --title "Fix crouch"
```

Projects are the ones the desktop app shows on its Projects tab: `projects` lists them and
`projects add <path>` saves another. A command works on the project you name with
`--project <name|number|path>`, otherwise on the project the current folder is in, then your
default project (`projects default <name>`), then your only project. `BASISPM_PROJECT` sets one
for a whole shell session.

The console follows the same project model as the desktop app: it clones `BasisVR/Basis` first,
detects packages already bundled under `Basis/Packages`, and only installs additional packages.
Converting an arbitrary Unity project into Basis is not supported; use a complete Basis checkout.

`clone-basis <folder> --without server,images` leaves parts out of a new clone. `parts` shows which
parts a project has, `parts remove server` takes the `Basis Server` folder out (it asks before
deleting the files git ignores there, such as build output; `--keep-ignored` leaves them), and
`parts add server` brings it back.

`basispm help` lists every command, and `basispm help <command>` (or `<command> --help`) shows its
options and examples. A mistyped command or option gets a suggestion. Commands that list or show
things take `--json` for scripts, and tables print as tab-separated text when the output is piped.
Colors follow `NO_COLOR`, `FORCE_COLOR` and `--no-color`. Exit codes are 0 for done, 1 for failed,
2 for wrong usage and 130 for cancelled. `basispm completion powershell|bash|zsh|fish` prints a
Tab-completion script for your shell; `basispm help completion` shows where to add it.

`update-basis` shows what is coming and asks before changing anything (`--yes` skips the prompt).
`--branch <name>` moves the project onto another Basis branch, such as a long-term-support one,
keeping your commits and edits; `basis-branch` shows which Basis branch the project follows and how
that was worked out. If files need a decision, run `conflicts`, settle each with
`resolve <path> mine|basis|done`, then `update-basis --continue`; `update-basis --abort` puts the
project back as it was. A copy kept in its own repository without Basis's history gets each update
as a single commit; `update-basis --link` merges Basis's history into it instead, after which every
push also uploads that history (over 2 GB). A project without git is recorded first with
`update-basis --init-git`. `change-branch <name>` switches to another of your own branches and
brings uncommitted edits along; if any overlap, settle them the same way and finish with
`change-branch --continue` (or `--abort`).

`basisdev` lists the development clones with their recorded upstream and anything that needs
reconciling. When the Basis repo has its own copy of a cloned package, it compares the two: how
many files differ and which upstream commit the Basis copy matches, if any. `basisdev reconcile`
writes missing sidecars and `Packages/basispackages.json` entries and forgets stale mount records
without touching package files or `manifest.json`. `basisdev use <id>` points `manifest.json` back
at a clone, `basisdev release <id>` deletes a clone (it refuses while the clone holds uncommitted or
unpushed work unless you add `--force`), and `basisdev restore <id>` or `basisdev reclone <id>`
repairs a clone that has gone missing, such as one listed in `basispackages.json` on a fresh
checkout. `basisdev move <id>` moves a clone an older version made in `Packages/` into `.basisdev`.
`basisdev ignore <id>` stops flagging a clone that isn't used because the Basis repo handles the
package itself, and `basisdev unignore <id>` flags it again.

`contribute` lists what the project changed compared with the Basis version it's based on.
Pick changes with `--package <id>`, `--project-files`, `--repository-files`, `--path <path>`
(both repeatable) or `--all`, then add `--title "..."` (and optionally `--body`, `--branch`,
`--target <basis-branch>`) to open a pull request on `BasisVR/Basis`, or `--dry-run` to only
build the commit locally. It signs in with `GH_TOKEN`, `GITHUB_TOKEN` or the GitHub CLI.

`new-package <id>` creates a package in `.basisdev` (`--name`, `--description`, `--author`,
`--license mit|apache-2.0|none`, `--reference <assembly>` for the Basis assemblies its code uses,
`--no-runtime`, `--no-editor`, `--server`, `--samples`, and `--github [--private]` to put it on
GitHub). `publish-package <id>` releases the next version (`--version <x.y.z>` or
`--bump patch|minor|major`, `--notes`, `--no-release` to skip the GitHub release, `--submit` to open
a registry pull request, `--dry-run` to only show the plan and the checks).

Server packages live in `Basis Server/Packages`: `server-install` takes a registry id, a git URL
(with optional `?path=` and `#ref`) or a `file:` path, `server-update [id] [--ref <ref>]` moves git
packages forward, `server-remove <id>` takes them out again, and `server-restore` downloads anything
missing and records the exact commits in `packages-lock.props`. `server-link <id> <folder>` builds a
package from a working copy on this machine without touching the committed files. `server-build`
compiles the server; `dotnet build` and `dotnet publish` restore missing server packages by
themselves, so CI and Docker builds need only git. Packages can also ship prebuilt .NET assemblies
and native libraries for chosen platforms (`win`, `linux`, `osx`, optionally with `-x64`, `-x86`,
`-arm64` or `-arm`); the build picks the ones for the runtime identifier it targets. Publishing for
another operating system (for example `-r linux-x64` on Windows) also needs
`-p:BasisServerPackageRid=linux-x64` (or a `BasisServerPackageRid` environment variable), because the
server's libraries build without a runtime identifier.

`verify` checks the project's packages: conflicts between them, package problems Unity would hit,
then a compile with the project's Unity version (batch mode when Unity is closed; when Unity has the
project open it reads the result of the editor's next recompile, and `--open-editor` or `--batch`
picks one). Name packages (`verify com.example.package`) to report only on them, `--no-compile`
skips Unity, and it exits with code 1 when something is wrong. The results are saved, so the
desktop app shows them too and the next run marks anything new. `check-package <folder>` runs the
package checks on a package you're writing, against the current project when there is one.
`check-registry` installs each registry package into the project in turn, checks it and puts the
project's package files back; with `--record checks.json` it saves each result under the project's
Basis branch. Use it on a spare clone of Basis.

For packages, `info <id>` and `versions <id>` show details and releases,
`install-package <id> --version <ref>` installs a specific release, `remove-package <id>` and
`update-packages [--dry-run]` handle the rest, `add-git <owner/repo>` adds a package straight from
GitHub, and `export-package-list` saves the packages you added as a list others can install with
`install-package-list <file>`. Packages that ship with Basis are never removed or replaced.
`update-packages` moves working copies in place and keeps your edits (a package whose edits clash
with its update is left as it was); `--notes` adds each update's release notes, and
`release-notes <id> [--to <ref>]` shows them for one package. `install-package` and `add-git` also
install what the package's `package.json` needs unless you pass `--no-deps`, and `remove-package`
asks first when other packages need the one you're removing (`--force` skips that). `restore` clones
the packages `Packages/basispackages.json` lists that are missing and moves clones to the listed
versions, so it's the command to run after cloning a project or in CI; `sync` pulls the project's
own remote first. `basisdev switch <id>` moves one clone to its listed version, and
`basisdev share <id> [--branch <name>] [--message <text>]` pushes a clone's edits to your fork and
lists that branch for your team.

`unity` lists your editors and marks the one the project needs; `unity install` installs it
through Unity Hub (add modules with `--module android`), and `unity add <folder>` registers an
editor Unity Hub doesn't manage. `server-run` runs the Basis server in the terminal, where its
first-run setup wizard works too; `server-config` and `server-content` edit its settings and
startup content, and `connect` launches Basis Labs through Steam and joins it. `update-all` updates every saved
project that doesn't need a decision, `doctor` checks git, Unity, the .NET SDK and the project,
and `config` reads and changes the settings shared with the desktop app.

`storage` shows how much disk space the project uses (Library, Temp, Logs, obj, the Basis Server
build output and each `.basisdev` clone) and the size of the shared download cache.
`clean library`, `clean temp` and `clean logs` delete those folders (the first two refuse while
Unity has the project open), `clean clones` removes `.basisdev` clones Unity doesn't load that have
no local work and aren't listed in `Packages/basispackages.json`, and `clean cache` empties the
download cache; each asks first unless you add `--yes`. `config set clone-cache false` stops new
clones from using the cache. `licenses` lists every package's license and flags missing and
copyleft ones; `licenses --output THIRD-PARTY-NOTICES.md` saves the notices with the full license
texts, and `--include-unity` adds Unity's own packages.

## Package compatibility

A package can say which Unity and Basis versions it works with, and the manager installs or updates
to the newest release that fits the project. `unity` and `unityRelease` in `package.json` are Unity's
own minimum-version fields. `basis` gives the Basis versions as dates, because Basis has no version
numbers of its own: a project's Basis version is the date of the Basis commit it's based on, and the
long-term-support branches are named after theirs.

```json
"basis": { "from": "2026-09-16", "before": "2027-01-01", "channels": ["developer", "lts"] }
```

`from` is the first Basis it works with and `before` the first it doesn't; both are optional, and a
plain string (`"basis": "2026-09-16"`) means `from`. Dates can also be written `20260916` or as a
branch name such as `long-term-support-20260916`. `channels` limits a package to Basis branches:
`developer`, `lts`, `experimental` or an exact branch name. A field that's left out doesn't limit
anything. The registry reads these from every release's `package.json`, so the app knows which
release fits before it downloads anything.

`gitDependencies` lists packages Unity can't fetch by itself, as package id to git URL (with
`?path=` and `#ref` like a `manifest.json` line). They're cloned into `.basisdev` like any other
install, and reviewed first when the URL isn't the Basis registry's.

## Package registry server

`src/BasisPM.Server` is a [Hangar](https://hangar.papermc.io/)-style package registry where the
community can browse and submit Basis-compatible **git packages (GitHub or GitLab)**. It runs as
an ASP.NET Core app for development, and exports a static site for hosting.

Run it live:

```
dotnet run --project src/BasisPM.Server   # → http://localhost:5133
```

- **Browse UI** — search, source/category filters, and package cards with copy-paste install
  instructions, styled like the desktop app.
- **Real data, never faked**: stars, forks and last-updated dates come from the GitHub and GitLab
  APIs when the site is generated.
- **One file per package**: the curated registry is `src/BasisPM.Server/seed/packages/<id>.json`,
  one package per file, named after the package id. Stars, forks, dates, `source` and `versions`
  are filled in when the site is generated, so they don't belong in a seed file. A legacy
  `seed/packages.json` list is still read when it exists (a file in `seed/packages/` wins over a
  list entry with the same id), so older forks keep working. Package lists live in
  `seed/packagelists.json`.
- **Submitting a package**: the website's **Submit a package** reads the package's `package.json`,
  then opens GitHub with `src/BasisPM.Server/seed/packages/<id>.json` filled in. Committing it opens
  a pull request, and GitHub makes a fork for you when you can't push here. Adding the file in a
  pull request by hand works the same way; an issue is still offered as a fallback.
- **Every release**: `generate` lists each package's semver tags (through the GitHub or GitLab API,
  or `git ls-remote` when the API isn't available), keeps the newest 20 and reads `package.json` at
  each one. Every tag that holds the package becomes a version in `catalog.json` with its `ref`,
  `commit`, `unity` and `unityRelease`, `basis` compatibility, dependencies (`dependencies` plus
  `vpmDependencies`, keeping only ranges the app understands) and `gitDependencies`. The newest
  version keeps the plain git URL, so older app builds still pick the latest release themselves;
  older versions are pinned with `#<tag>`. A package without usable tags, or whose git URL pins a
  ref, keeps one version read from that ref or its default branch. `packages.json` carries the same
  list as `versions`, which the website shows as **Releases** in a package's details.
- **Package checks** (`.github/workflows/package-checks.yml`): a scheduled workflow, on a
  self-hosted runner labelled `unity` that has Unity Hub, the Unity versions the Basis branches use
  and an activated license, that runs `build/check-registry.ps1`. For developer, the newest
  long-term-support branch and `experimental-branch-major-changes` (or the branches you name), it
  clones Basis, skips any branch whose head hasn't moved since the last run, runs `check-registry`,
  and commits the results to `src/BasisPM.Server/seed/checks.json`, which is published next to
  `catalog.json`.
- **Static export** for any static host (e.g. GitHub Pages):

  ```
  dotnet run --project src/BasisPM.Server -- generate "$PWD/dist"
  ```

  writes `index.html`, `packages.json`, `catalog.json`, `packagelists.json` (plus the legacy
  `bundles.json`) and the icons. Pass an absolute folder: `dotnet run` starts in
  `src/BasisPM.Server`, so a relative path lands there. `--check` checks the seed first and stops on
  errors, `--no-versions` skips reading tags, and `--offline` skips every network check and tag read.
  When `seed/checks.json` exists it is copied unchanged next to `catalog.json`. Set `GITHUB_TOKEN`
  to lift the GitHub API rate limit, and `BASISPM_SEED_DIR` to use another seed folder.
- **Checking the seed**: `dotnet run --project src/BasisPM.Server -- check` checks every seed file:
  required fields, a lowercase package id such as `com.company.package` that matches the file name,
  an https git URL on github.com or gitlab.com with a safe `?path=`, http(s) links, the version and
  dependency ranges, and an `image` that exists in `wwwroot/icons` or in the package's own
  repository. Online, it also checks that the repository exists, that `package.json` at the git
  URL's path has the same name, and that a license is set in the seed or in `package.json`. It
  prints a report and exits with 1 on any error; `--offline` skips the online part.
- **CI**: [`registry.yml`](.github/workflows/registry.yml) runs the check on every pull request that
  touches `src/BasisPM.Server/seed/`. Once a day, or when run by hand, it checks the seed, generates
  the site and uploads it as the `registry-site` artifact. Deploying to the website is off by
  default: set the repository variable `REGISTRY_DEPLOY` to `true` and add a `WEBSITE_DEPLOY_TOKEN`
  secret that can push to the website repository (`REGISTRY_WEBSITE_REPO`, default
  `BasisVR/basisvr.github.io`), and each run copies the site into its `packages/` folder.
- **The page** (`wwwroot/index.html`) is the file the website serves. On the website it uses the
  site's compiled Tailwind stylesheet; under `dotnet run`, where that stylesheet is missing, it
  loads Tailwind from its CDN instead.
- `catalog.json` is **format-compatible with the desktop app** — point Settings →
  *Package Catalog URL* at `…/catalog.json` and the app's Packages tab serves from the registry.
- **Package images** — give a package a promo image on its card (like
  [Hangar](https://hangar.papermc.io/)) by dropping a square PNG **named after the package id**
  into `src/BasisPM.Server/wwwroot/icons/` and opening a PR — e.g. `icons/com.you.mypackage.png`.
  Nothing else to edit. Images are **self-hosted only** (never a remote URL, so there's no SSRF or
  tracking surface); a package with no image falls back to its emoji `icon`. On merge, CI
  ([`icons.yml`](.github/workflows/icons.yml)) auto-resizes to ≤256px and strips metadata so any
  reasonable image becomes a good size. PNG with transparency, ~256–512px square, looks best.

## Package safety

A Unity package is code that runs on the computer of everyone who installs it, and Unity has no
sandbox: editor scripts marked `[InitializeOnLoad]`, asset postprocessors, compiler plugins and
native plugins all run as soon as the project loads them. The manager can't make an untrusted
package safe, but it makes sure nobody runs one by accident, that what the registry checked is what
gets installed, and that a bad package can be pulled from everyone quickly.

- **Review before Unity sees it.** Installs clone into `.basisdev/<id>`, which Unity ignores until
  `manifest.json` points at it. When the source isn't the Basis registry, or the version isn't the
  one the registry checked, the clone is scanned and a review window lists what the package can
  do: editor code that runs by itself, programs it starts, code it loads at run time, compiled and
  native libraries (their imports are read too), compiler plugins, saved-login, browser and wallet
  paths, chat webhooks, large encoded blobs and programs disguised as other file types. Comments
  and plain strings don't count as code; code hidden in string interpolation, unicode escapes or
  line breaks does. Declining deletes the clone and leaves `manifest.json` untouched. Without git
  nothing can be checked, so you are asked before Unity downloads it directly.
- **Install links only trust the registry.** A `basispm://install` link for a registry package
  installs the registry's own repository, whatever URL the link carries. A link to anything else
  goes through the review, which also points out when the name on the link doesn't match the
  package. Package lists work the same way.
- **Pulled packages.** The registry publishes `advisories.json` next to `catalog.json`. The app
  checks it whenever it loads the catalog and remembers the last copy for offline starts; the CLI
  checks it on install and in `doctor`. A matching package gets a red PULLED pill and a banner with
  Remove on the Packages tab of any project that has it, and installing it is refused.
- **Lockfile clones.** **Clone again** from `Packages/basispackages.json` checks out the recorded
  commit, not whatever the tag points at today, and refuses when that commit has been rewritten away.
- **CLI.** `basispm scan <folder|git-url> [--strict] [--json]` runs the same checks without
  installing anything, for reviewing a submission or in CI.
- **Automatic API upgrades.** When the project's Unity version turns an API a package uses into a
  compile error and Unity's message names the replacement (`(UnityUpgradable) -> X`, or "Use X
  instead"), the package check renames that call in the package's `.basisdev` clone, checks again,
  and puts back any change that still doesn't compile. Unity's own updater only handles the first
  kind, and never touches packages. Only the failing call sites change; everything else in the file,
  including encoding and line endings, stays byte for byte the same. The package doesn't show as
  edited: its details say the automatic API upgrade was applied, and the change is recorded in the
  clone's `.git` folder so updates can undo it first.

These checks look for common warning signs. A determined author can hide code from any scanner,
so the review says that a clean result doesn't prove a package is safe.

### For registry maintainers

Pull a package by adding it to `advisories.json` (`src/BasisPM.Server/seed/advisories.json`, which
`generate` copies; on the live site it's `packages/advisories.json`):

```json
[
  {
    "id": "BPM-2026-001",
    "package": "com.example.tool",
    "repository": "https://github.com/example/tool",
    "commits": ["<sha of each bad commit>"],
    "severity": "malware",
    "title": "Uploads saved Discord logins",
    "details": "Versions from 1.4.0 on. Remove it and change your passwords.",
    "published": "2026-10-14"
  }
]
```

`repository` and `commits` are optional: leave them out to pull the package id wherever it comes
from. `severity` is `malware` (the default: installs are refused) or `warning` (an amber notice;
installs still go through the review). Remove the package from the catalog as well.

A package's seed file (`src/BasisPM.Server/seed/packages/<id>.json`) can carry `reviewedCommit`, the
full commit a curator actually read; the catalog puts it on the version built from that commit. Once
any version of a package has one, only clones at a reviewed commit install without a prompt, so a
moved tag or a release the crawl picked up since opens the review instead. Registry dependencies are
written to `manifest.json` pinned to their version's reviewed commit. Only set it after reviewing the
code (`basispm scan <git-url>#<tag>` is a good start); the crawl never fills it.

## Run

```
dotnet run --project src/BasisPM.App
```

## Releasing (maintainers)

Push a semver tag and CI builds + publishes installers for Windows, Linux and macOS to a
single GitHub Release; the in-app updater promotes it to existing users automatically:

```
git tag v0.1.0
git push origin v0.1.0
```

See **[RELEASING.md](RELEASING.md)** for the full process, versioning, and how to set up
code-signing certificates (Windows + macOS). Every push/PR to `main` is compile-checked by
[`.github/workflows/ci.yml`](.github/workflows/ci.yml).

## Layout

- `src/BasisPM.App` — Avalonia UI (MVVM: `ViewModels/`, `Views/`, `Styles/`)
- `src/BasisPM.Core` — services and models (`GitService`, `UnityHubService`,
  `BasisInstallService`, catalog + manifest handling)
- `src/BasisPM.Server` — package registry: browse UI + JSON API + static-site generator

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

- Committers and reviewers: [dooly123](https://github.com/dooly123), [TheButlah](https://github.com/TheButlah)
- Approvers: [dooly123](https://github.com/dooly123)

Windows releases are built from this repository by GitHub Actions
([`release.yml`](.github/workflows/release.yml)), and a team member approves every signing
request. Only the binaries built from this repository and the Windows installers are signed; see
[RELEASING.md](RELEASING.md#windows-signpath) for the details.

### Privacy policy

Basis Package Manager collects no telemetry. It connects to GitHub to check for app and Basis
updates and to download Basis and packages, to basisvr.org for the package catalog and
announcements, to Unity's release service for Unity editor versions, and to the git servers of any
packages you add. Error and crash reports stay on your computer unless you choose to file them as a
GitHub issue. GitHub sign-in, needed only to open pull requests, uses the GitHub CLI or a token you
provide, and that token is only sent to GitHub.

## License

Distributed under the MIT License. See [LICENSE](LICENSE) for more information.

## Third-Party Licenses

The packages below ship in every release build (desktop app and `basispm` console).

### MIT

- [Avalonia](https://github.com/AvaloniaUI/Avalonia) - Copyright (c) AvaloniaUI OÜ. The UI
  framework: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`
  and the platform backends they pull in.
- [SkiaSharp and HarfBuzzSharp](https://github.com/mono/SkiaSharp) - Copyright (c) 2015-2016
  Xamarin, Inc., Copyright (c) 2017-2018 Microsoft Corporation. Rendering and text shaping
  for Avalonia. The native libraries they bundle are listed in their
  [third-party notices](https://github.com/mono/SkiaSharp/blob/v2.88.9/External-Dependency-Info.txt).
- [MicroCom](https://github.com/kekekeks/MicroCom) - Copyright (c) 2021 Nikita Tsukanov.
  `MicroCom.Runtime`, used by Avalonia.
- [Tmds.DBus](https://github.com/tmds/Tmds.DBus) - Copyright 2006 Alp Toker, 2016 Tom Deseyn
  and contributors. `Tmds.DBus.Protocol`, used by Avalonia on Linux.
- [Velopack](https://github.com/velopack/velopack) - Copyright 2021 Caelan Sayler, 2024
  Velopack Ltd. Installers and in-app updates.
- [.NET](https://github.com/dotnet/runtime) - Copyright (c) .NET Foundation and Contributors.
  The runtime bundled into every release, plus `System.IO.Pipelines`.
- [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) - Copyright (c)
  2020 Microsoft Corporation. Icon shapes in `src/BasisPM.App/Styles/Icons.axaml`.

### BSD-3-Clause

- [Skia](https://github.com/google/skia) - Copyright (c) 2011 Google Inc. The native graphics
  library inside SkiaSharp.
- [ANGLE](https://github.com/google/angle) - Copyright 2018 The ANGLE Project Authors.
  `Avalonia.Angle.Windows.Natives`, Windows only.

### Old MIT

- [HarfBuzz](https://github.com/harfbuzz/harfbuzz) - Copyright 1998-2012 Google, Red Hat,
  Mozilla Foundation and other contributors. The native text shaping library inside
  HarfBuzzSharp.

### SIL Open Font License 1.1

- [Inter](https://github.com/rsms/inter) - Copyright 2020 The Inter Project Authors. The UI
  font, bundled through `Avalonia.Fonts.Inter`.

### Package registry page

`src/BasisPM.Server/wwwroot/index.html` loads these from a CDN at runtime; they are not bundled:

- [Tailwind CSS](https://github.com/tailwindlabs/tailwindcss) (MIT) - Copyright (c) Tailwind
  Labs, Inc.
- [Inter](https://github.com/rsms/inter) (OFL 1.1), served by [Bunny Fonts](https://fonts.bunny.net).

### Development only

Used to build and test the project; not shipped:

- [xUnit](https://github.com/xunit/xunit) (Apache-2.0): `xunit`, `xunit.runner.visualstudio`
- [VSTest](https://github.com/microsoft/vstest) (MIT): `Microsoft.NET.Test.Sdk`
- [coverlet](https://github.com/coverlet-coverage/coverlet) (MIT): `coverlet.collector`
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) (MIT): `Avalonia.Headless.XUnit`
- [ASP.NET Core](https://github.com/dotnet/aspnetcore) (MIT): the registry server and
  `Microsoft.AspNetCore.Mvc.Testing`
