# Basis Package Manager

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
