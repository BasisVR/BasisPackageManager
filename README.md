# Basis Package Manager

A desktop app for cloning and living with [BasisVR/Basis](https://github.com/BasisVR/Basis):
clone the repo, add community and NuGet packages, keep the core and packages up to date,
see exactly what you have changed in the Basis source, and install the Unity version and
platforms Basis needs.

Built with [AvaloniaUI](https://avaloniaui.net/) (.NET 9), styled to match
[basisvr.org](https://basisvr.org/).

## Features

- **Installs** — clone `BasisVR/Basis` (choose folder + branch), or register an existing
  clone. Each install shows its branch, commit, required Unity version, and whether it is
  behind upstream or has local changes. One-click **Update Core** runs `git pull --ff-only`.
- **Packages** — install curated Basis packages, add any community UPM repo from GitHub,
  or search **NuGet.org** and add packages to `Assets/packages.config`
  (restored in Unity by [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity)).
- **Local Changes** — a `git status` of your install with a per-file unified diff, so you
  can see what you have modified in the Basis source.
- **Unity Editors** — detect installed editors and install the exact version Basis targets
  via Unity Hub, choosing the platform modules (Windows, Android, Linux, macOS, …).
- **Settings** — default clone location, catalog URL, Unity Hub override, NuGet prerelease
  toggle, and detected tooling paths.

## Requirements

- .NET 9 SDK
- [Git](https://git-scm.com/) on your `PATH` (used for clone / pull / status / diff)
- [Unity Hub](https://unity.com/download) for editor installs

## Package registry server

`src/BasisPM.Server` is an ASP.NET Core web app — a [Hangar](https://hangar.papermc.io/)-style
package registry where the community can browse and submit Basis-compatible packages, with a
JSON API the desktop app consumes.

```
dotnet run --project src/BasisPM.Server   # → http://localhost:5133
```

- **Browse UI** (`/`) — search, source/category filters, and package cards with install
  instructions, styled to match the desktop app.
- **API** — `GET /api/packages` (search/filter/sort), `/api/packages/{id}`, `/api/categories`,
  `POST /api/packages` (submit), and `GET /api/catalog`.
- `GET /api/catalog` is **format-compatible with the desktop app's catalog** — point
  Settings → *Package Catalog URL* at `http://<host>/api/catalog` and the app's Packages tab
  serves from the registry. Package data lives in `App_Data/registry.json` (seeded from code).

## Run

```
dotnet run --project src/BasisPM.App
```

## Layout

- `src/BasisPM.App` — Avalonia UI (MVVM: `ViewModels/`, `Views/`, `Styles/`)
- `src/BasisPM.Core` — services and models (`GitService`, `NuGetService`,
  `UnityHubService`, `BasisInstallService`, catalog + manifest handling)
- `src/BasisPM.Server` — ASP.NET Core package registry (browse UI + JSON API)
