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
| Windows  | `*-win-Setup.exe` (x64) or `*-win-arm64-Setup.exe` (Windows on ARM) | Unsigned for now, so SmartScreen may warn — choose **More info → Run anyway**. |
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

- **Installs** — clone the complete `BasisVR/Basis` repository (the `developer` branch), or
  register an existing clone and switch branches afterward. Each install shows its branch,
  commit, required Unity version, and whether it is
  behind upstream or has local changes. **Update Basis** brings in the latest Basis (below).
- **Basis updates**: whatever git setup a project has (a clone of `BasisVR/Basis`, a fork,
  your own repository, or no git at all), **Update Basis** fetches the latest Basis straight
  from GitHub and merges it into your current branch. You see the incoming commits first.
  Uncommitted edits to files Basis also changes are set aside and merged back afterwards;
  any file changed on both sides is listed so you can keep yours, take Basis's, or combine
  them, and **Undo update** puts everything back. Projects are checked for Basis updates at
  startup and every two hours, with a banner and a badge on **Projects** when one is ready.
- **Packages** — packages bundled in the Basis checkout are detected automatically; install
  additional official packages, or add any community UPM package from a
  **GitHub or GitLab** git URL. Discovery is powered by the registry (below).
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
without arguments for an interactive prompt, or use `--project` for scripts and one-shot commands:

```text
basispm clone-basis C:\BasisVR\Basis
basispm --project C:\BasisVR\Basis status
basispm --project C:\BasisVR\Basis check-updates
basispm --project C:\BasisVR\Basis update-basis
basispm --project C:\BasisVR\Basis list-packages
basispm --project C:\BasisVR\Basis install-package com.example.package
basispm --project C:\BasisVR\Basis server-install com.example.transport
basispm --project C:\BasisVR\Basis server-build
```

The console follows the same project model as the desktop app: it clones `BasisVR/Basis` first,
detects packages already bundled under `Basis/Packages`, and only installs additional packages.
Converting an arbitrary Unity project into Basis is not supported; use a complete Basis checkout.
Run `basispm help` for branch, package-list, update, and Unity commands.

`update-basis` shows what is coming and asks before merging (`--yes` skips the prompt,
`--branch <name>` follows another Basis branch such as a long-term-support one). If files need a
decision, run `conflicts`, settle each with `resolve <path> mine|basis|done`, then
`update-basis --continue`; `update-basis --abort` puts the project back as it was. A project
whose history isn't connected to Basis is linked with `update-basis --link`, and one without git
is recorded first with `update-basis --init-git`.

Server packages live in `Basis Server/Packages`: `server-install` takes a registry id, a git URL
(with optional `?path=` and `#ref`) or a `file:` path, `server-update [id] [--ref <ref>]` moves git
packages forward, `server-remove <id>` takes them out again, and `server-restore` downloads anything
missing and records the exact commits in `packages-lock.props`. `server-link <id> <folder>` builds a
package from a working copy on this machine without touching the committed files. `server-build`
compiles the server; `dotnet build` and `dotnet publish` restore missing server packages by
themselves, so CI and Docker builds need only git.

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
