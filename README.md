# Basis Package Manager

A desktop app for cloning and living with [BasisVR/Basis](https://github.com/BasisVR/Basis):
clone the repo, add community packages from GitHub or GitLab, keep the core and packages up
to date, see exactly what you have changed in the Basis source, and install the Unity version
and platforms Basis needs.

Built with [AvaloniaUI](https://avaloniaui.net/) (.NET 9), styled to match
[basisvr.org](https://basisvr.org/).

## Features

- **Installs** — clone `BasisVR/Basis` (choose folder + branch), or register an existing
  clone. Each install shows its branch, commit, required Unity version, and whether it is
  behind upstream or has local changes. One-click **Update Core** runs `git pull --ff-only`.
- **Packages** — install curated Basis packages, or add any community UPM package from a
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

## Run

```
dotnet run --project src/BasisPM.App
```

## Layout

- `src/BasisPM.App` — Avalonia UI (MVVM: `ViewModels/`, `Views/`, `Styles/`)
- `src/BasisPM.Core` — services and models (`GitService`, `UnityHubService`,
  `BasisInstallService`, catalog + manifest handling)
- `src/BasisPM.Server` — package registry: browse UI + JSON API + static-site generator
