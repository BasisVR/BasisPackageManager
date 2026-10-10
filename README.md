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
| Linux    | `BasisPackageManager.AppImage` (x64) or `*-linux-arm64.AppImage` (arm64) | `chmod +x` it, then run. (Needs FUSE, which most distros ship.) |
| macOS    | `*-osx-Setup.pkg` (Apple Silicon) or `*-osx-x64-Setup.pkg` (Intel) | Unsigned — **right-click → Open**, then **Open** the first time. |

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
  Basis branch, such as a long-term-support one, keeping your commits and edits.
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
- **Development clones (`.basisdev`)**: every package the manager clones for editing gets a
  sidecar file, `<Unity project>/.basisdev/<package-id>.json`, that records its upstream (git URL,
  branch or tag, sub-folder and the commit it was cloned at) and the `manifest.json` line it
  replaced. It lives next to the clones and is kept out of your repository's `git status`. The
  Packages tab uses it to show where each package really loads from (a folder tracked in the Basis
  repo, a `.basisdev` clone, git or the registry) and flags anything that doesn't line up: a clone
  hidden by the Basis repo's own copy, a clone `manifest.json` no longer points at, a missing clone,
  or a stale mount record, each with a fix. A clone you keep around on purpose while the Basis repo
  handles the package itself can be marked **Handled by Basis repo**, which stops it being flagged
  (**Flag again** undoes it).
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
- **Local Changes** — a `git status` of your install with a per-file unified diff, so you
  can see what you have modified in the Basis source.
- **Unity Editors** — detect installed editors and install the exact version Basis targets
  via Unity Hub, choosing the platform modules (Windows, Android, Linux, macOS, …).
- **Basis Server** — build and run the server included in the active checkout, edit every
  generated `config.xml` setting, manage default-library and startup content, and launch
  Basis Labs through Steam with an automatic server connection.
- **Server packages**: add packages to the Basis Server the same way you add them to Unity. Their
  server code is compiled straight into the server's own assemblies at build time (see
  `Basis Server/Packages/README.md` in Basis). A registry package marked as having server code
  installs on both sides at once, and the server builds it from the Unity project's copy when
  that copy is a local clone, so the client and the server always run the same code.
- **Settings** — default clone location, catalog URL, Unity Hub override, and detected
  tooling paths.

## Requirements

- .NET 9 SDK
- .NET 10 SDK to build the Basis server from the Server tab
- [Git](https://git-scm.com/) 2.28 or newer on your `PATH` (used for clone / update / status / diff)
- [Unity Hub](https://unity.com/download) for editor installs

## Console mode

Releases also include the `basispm` (`basispm.exe` on Windows) console application. Run it
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
writes missing sidecars and forgets stale mount records without touching package files or
`manifest.json`. `basisdev use <id>` points `manifest.json` back at a clone, `basisdev release <id>`
deletes a clone (it refuses while the clone holds uncommitted or unpushed work unless you add
`--force`), and `basisdev restore <id>` or `basisdev reclone <id>` repairs a clone that has gone
missing. `basisdev ignore <id>` stops flagging a clone that isn't used because the Basis repo
handles the package itself, and `basisdev unignore <id>` flags it again.

`contribute` lists what the project changed compared with the Basis version it's based on.
Pick changes with `--package <id>`, `--project-files`, `--repository-files`, `--path <path>`
(both repeatable) or `--all`, then add `--title "..."` (and optionally `--body`, `--branch`,
`--target <basis-branch>`) to open a pull request on `BasisVR/Basis`, or `--dry-run` to only
build the commit locally. It signs in with `GH_TOKEN`, `GITHUB_TOKEN` or the GitHub CLI.

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

For packages, `info <id>` and `versions <id>` show details and releases,
`install-package <id> --version <ref>` installs a specific release, `remove-package <id>` and
`update-packages [--dry-run]` handle the rest, `add-git <owner/repo>` adds a package straight from
GitHub, and `export-package-list` saves the packages you added as a list others can install with
`install-package-list <file>`. Packages that ship with Basis are never removed or replaced.

`unity` lists your editors and marks the one the project needs; `unity install` installs it
through Unity Hub (add modules with `--module android`), and `unity add <folder>` registers an
editor Unity Hub doesn't manage. `server-run` runs the Basis server in the terminal, where its
first-run setup wizard works too; `server-config` and `server-content` edit its settings and
startup content, and `connect` launches Basis Labs through Steam and joins it. `update-all` updates every saved
project that doesn't need a decision, `doctor` checks git, Unity, the .NET SDK and the project,
and `config` reads and changes the settings shared with the desktop app.

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
- **Real data, never faked** — stars / forks / last-updated are pulled from the GitHub and
  GitLab APIs at build time. Curation lives in `src/BasisPM.Server/seed/packages.json`
  (PR-editable); the "Submit a package" button opens a pre-filled GitHub issue.
- **Static export** for any static host (e.g. GitHub Pages):

  ```
  dotnet run --project src/BasisPM.Server -- generate "$PWD/dist"
  ```

  writes `index.html` + `packages.json` + `catalog.json` with the real stats baked in. Pass an
  absolute folder: `dotnet run` starts in `src/BasisPM.Server`, so a relative path lands there.
- `catalog.json` is **format-compatible with the desktop app** — point Settings →
  *Package Catalog URL* at `…/catalog.json` and the app's Packages tab serves from the registry.
- **Package images** — give a package a promo image on its card (like
  [Hangar](https://hangar.papermc.io/)) by dropping a square PNG **named after the package id**
  into `src/BasisPM.Server/wwwroot/icons/` and opening a PR — e.g. `icons/com.you.mypackage.png`.
  Nothing else to edit. Images are **self-hosted only** (never a remote URL, so there's no SSRF or
  tracking surface); a package with no image falls back to its emoji `icon`. On merge, CI
  ([`icons.yml`](.github/workflows/icons.yml)) auto-resizes to ≤256px and strips metadata so any
  reasonable image becomes a good size. PNG with transparency, ~256–512px square, looks best.

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
