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
| Windows  | `*-win-Setup.exe` | Unsigned for now, so SmartScreen may warn — choose **More info → Run anyway**. |
| Linux    | `*.AppImage` | `chmod +x` it, then run. (Needs FUSE, which most distros ship.) |
| macOS    | `*.pkg` (Apple Silicon) | Unsigned — **right-click → Open**, then **Open** the first time. |

The app installs per-user (no admin required) and keeps itself up to date — see below.

## Updating

Basis Package Manager updates itself from GitHub Releases. When a newer version is
published, an **update banner** appears at the top of the window: click **Update now**
and it downloads the release and restarts onto it. You can also check on demand from
**Settings → About → Check for updates**.

Running from source (`dotnet run`) skips the in-app updater — update with `git pull`.

## Features

- **Installs** — clone `BasisVR/Basis` (choose folder + branch), or register an existing
  clone. Each install shows its branch, commit, required Unity version, and whether it is
  behind upstream or has local changes. One-click **Update Core** runs `git pull --ff-only`.
- **Packages** — install official Basis packages, or add any community UPM package from a
  **GitHub or GitLab** git URL. Discovery is powered by the registry (below).
- **Local Changes** — a `git status` of your install with a per-file unified diff, so you
  can see what you have modified in the Basis source.
- **Unity Editors** — detect installed editors and install the exact version Basis targets
  via Unity Hub, choosing the platform modules (Windows, Android, Linux, macOS, …).
- **Settings** — default clone location, catalog URL, Unity Hub override, and detected
  tooling paths.

## Requirements

- .NET 9 SDK
- [Git](https://git-scm.com/) on your `PATH` (used for clone / pull / status / diff)
- [Unity Hub](https://unity.com/download) for editor installs

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
  dotnet run --project src/BasisPM.Server -- generate ./dist
  ```

  writes `index.html` + `packages.json` + `catalog.json` with the real stats baked in.
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
