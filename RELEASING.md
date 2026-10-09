# Releasing Basis Package Manager

Releases are automated with [Velopack](https://velopack.io). Pushing a semver tag builds
installers for Windows, Linux and macOS — each in x64 **and** ARM flavours — packs them
(plus the update manifests) with `vpk`, and publishes them to a single GitHub Release. The
in-app updater reads that release, so a new version promotes itself to existing users.

## Cut a release

```bash
git tag v0.1.0
git push origin v0.1.0
```

That's it. [`.github/workflows/release.yml`](.github/workflows/release.yml) runs the
`windows-latest` / `ubuntu-latest` / `macos-latest` matrix and, when it finishes, the
[Releases page](https://github.com/BasisVR/BasisPackageManager/releases) has:

| Platform | Architecture | Asset | Update channel |
|----------|--------------|-------|----------------|
| Windows  | x64 | `BasisPackageManager-win-Setup.exe` (+ portable zip) | `win` |
| Windows  | arm64 | `BasisPackageManager-win-arm64-Setup.exe` (+ portable zip) | `win-arm64` |
| Linux    | x64 | `BasisPackageManager.AppImage` | `linux` |
| Linux    | arm64 | `BasisPackageManager-linux-arm64.AppImage` | `linux-arm64` |
| macOS    | Apple Silicon | `BasisPackageManager-osx-Setup.pkg` (+ portable zip) | `osx` |
| macOS    | Intel | `BasisPackageManager-osx-x64-Setup.pkg` (+ portable zip) | `osx-x64` |
| (all)    | | `*-full.nupkg` + `releases.*.json` / `RELEASES` — the update manifests | |

**Verify:** install the previous version, publish a higher tag, and confirm the in-app
banner offers the update and one click installs + restarts onto it.

Every published application directory also contains the companion `basispm` console executable
(`basispm.exe` on Windows). The release workflow publishes the GUI and CLI into the same RID output
before Velopack creates installers and portable archives.

## Architectures

Every architecture that both .NET 9 and Velopack support is built: the three OS jobs
cross-compile and cross-pack their second architecture with `vpk pack --runtime <rid>`,
so no ARM runners are involved. The setup binary checks the machine architecture at
install time, so a user who grabs the wrong file gets a clear refusal, not a broken install.

Update channels are how an install finds the right binaries: the channel name is baked
into each package, and the app updates from that same channel forever. The three original
channels predate multi-arch support and keep their historical meaning — `win` and `linux`
are x64 and `osx` is Apple Silicon — while every newer architecture uses its RID as the
channel name. **Never re-point an existing channel at a different architecture**: every
install on that channel would self-update onto binaries its CPU can't run.

Deliberately not shipped:

- **win-x86** — 32-bit-only Windows machines can't run Unity, so the app is pointless there
  (and Windows 10 32-bit is out of support).
- **linux-arm (32-bit, armhf)** — Velopack 1.2.0 ships no 32-bit ARM update/AppImage stubs.
- **riscv64 / loongarch64** — no official .NET runtime, so no self-contained publish.

To add an architecture later, add its RID to `targets` in the release matrix (it gets a
RID-named channel automatically) and to the CI publish-smoke list.

## Versioning

The baseline version lives in [`Directory.Build.props`](Directory.Build.props), but the
**git tag is the source of truth** — CI passes `-p:Version=${TAG#v}` at build time, so
`v1.4.2` ships version `1.4.2` regardless of the props file. Use plain semver
(`vMAJOR.MINOR.PATCH`); Velopack rejects 4-part versions.

## Release channels (stable vs prerelease)

Two update channels:

- **Stable** — a plain tag like `v0.2.0`. Everyone receives it.
- **Prerelease** — a tag with a prerelease suffix like `v0.2.0-beta.1` (anything containing a `-`).
  The workflow marks that GitHub release as a **pre-release**, so only users who opt in via
  **Settings → "Receive prerelease updates"** get it. Use this for the frequent, may-be-broken
  builds; stable users stay on the last full release until you cut a plain `vX.Y.Z`.

```bash
git tag v0.2.0-beta.1        # prerelease channel
git push origin v0.2.0-beta.1
```

Prerelease versions sort *below* the matching stable (`0.2.0-beta.1` < `0.2.0`), so cutting the
stable `v0.2.0` later promotes prerelease testers up to it automatically.

## Code signing

Releases are **unsigned by default**, which is why users see Windows SmartScreen and macOS
Gatekeeper prompts on first run. Signing removes those. The release workflow **auto-detects**
signing credentials: add the secrets below (for Windows, also the `SIGNPATH_RELEASE_SIGNING`
variable) and the next tag signs automatically, with no workflow edits needed. Leave them unset
and releases keep working, just unsigned.

Linux (AppImage) needs no signing for this purpose.

### Windows: SignPath

Windows releases are signed through the free [SignPath Foundation](https://signpath.org/) program
for open source projects. The certificate belongs to SignPath Foundation and its key never leaves
their HSM: the release workflow uploads the files as GitHub Actions artifacts, SignPath checks that
they came from this repository's workflow run, and signs them once a maintainer approves.

Every release sends two signing requests, and the Windows job waits up to an hour for each approval:

1. **App binaries**, before `vpk pack`: the five files built from this repository, for each Windows
   architecture (`BasisPM.App.exe`, `BasisPM.App.dll`, `BasisPM.Core.dll`, `basispm.exe`,
   `basispm.dll`). Velopack copies them unchanged into the update package, the portable zip and the
   installer.
2. **Installers**, after `vpk pack`: `BasisPackageManager-win-Setup.exe` and
   `BasisPackageManager-win-arm64-Setup.exe`, which carry the signed binaries inside.

Everything else ships as its publisher built it. The .NET runtime keeps Microsoft's signature, while
Avalonia, Velopack's `Update.exe` and the portable zip's launcher stay unsigned: SignPath Foundation
signs only code built from this repository, and allows unsigned upstream files inside a signed package.

SignPath checks both requests against
[`build/signpath/artifact-configuration.xml`](build/signpath/artifact-configuration.xml). It accepts
only those files, and only when their product name is `Basis Package Manager` and their product
version equals the release version, which the workflow passes as the `version` parameter. The
release build sets `IncludeSourceRevisionInInformationalVersion=false` so the product version is
exactly the tag, without a `+commit` suffix.

**SignPath setup.** The signing runs through the `Basis` project of the `basisvr [OSS]` organization
(`12263251-c5d9-4535-9600-ee8bed1543dd`), which SignPath Foundation manages and shares with the
Basis repository. Already in place:

- the **GitHub.com** trusted build system, linked to the project;
- the `basis-package-manager` artifact configuration, with the contents of
  `build/signpath/artifact-configuration.xml` (the `Basis` project's default configuration stays
  Basis's own);
- the `CI builds` CI user as a submitter on `release-signing` and `test-signing`, with dooly123
  approving.

Only SignPath Foundation can add `https://github.com/BasisVR/BasisPackageManager.git` to the project's
repository URLs; origin verification rejects builds from any other repository. They order the
production certificate (`Release certificate 2026`, still CSR PENDING) after reviewing a successful
signing with the test certificate.

**Turning it on**, once the repository URL is added:

1. Create an API token for the `CI builds` user in SignPath and store it as the `SIGNPATH_API_TOKEN`
   repository secret:

   ```bash
   gh secret set SIGNPATH_API_TOKEN --repo BasisVR/BasisPackageManager
   ```

2. **Test signing.** Run the Release workflow by hand (*Actions → Release → Run workflow*). A manual
   run is a dry run: it builds every package as version `0.0.1-dryrun.<run number>`, signs Windows
   through the `test-signing` policy with SignPath's self-signed test certificate, and keeps the
   packages as workflow artifacts for a week instead of publishing a release. Tag builds stay
   unsigned at this stage. Once it works, tell SignPath Foundation so they can review the setup and
   import the production certificate.
3. **Release signing.** After the production certificate is in place, set the
   `SIGNPATH_RELEASE_SIGNING` repository variable to `true`. From then on every tag signs through
   `release-signing`:

   ```bash
   gh variable set SIGNPATH_RELEASE_SIGNING --repo BasisVR/BasisPackageManager --body true
   ```

Without `SIGNPATH_API_TOKEN`, or for tags without `SIGNPATH_RELEASE_SIGNING`, the release still
builds, unsigned.

SignPath Foundation's [conditions](https://signpath.org/terms) also apply: everyone on the team uses
MFA on GitHub and SignPath, a team member approves every release, and the README keeps its
[code signing policy](README.md#code-signing-policy) section.

### macOS — Apple Developer ID + notarization

macOS refuses to launch unsigned, un-notarized apps for normal users. You need the
**Apple Developer Program** and two "Developer ID" certificates.

- **Cost:** **$99/year** (Apple Developer Program).

**Setup:**

1. Join the [Apple Developer Program](https://developer.apple.com/programs/).
2. In *Certificates, Identifiers & Profiles* create a **Developer ID Application** certificate
   (signs the app) and a **Developer ID Installer** certificate (signs the `.pkg`). Export each
   from Keychain Access as a password-protected `.p12`.
3. Create an **app-specific password** for your Apple ID (appleid.apple.com → Sign-In & Security).
4. Base64-encode each `.p12` (`base64 -i cert.p12 | pbcopy`) and add these secrets:

   | Secret | Value |
   |--------|-------|
   | `MAC_CERTIFICATE_BASE64` | base64 of the Developer ID **Application** `.p12` |
   | `MAC_INSTALLER_CERTIFICATE_BASE64` | base64 of the Developer ID **Installer** `.p12` |
   | `MAC_CERTIFICATE_PASSWORD` | the `.p12` export password (used for both) |
   | `MAC_KEYCHAIN_PASSWORD` | any string — password for the temporary CI keychain |
   | `MAC_APP_IDENTITY` | e.g. `Developer ID Application: Your Name (TEAMID)` |
   | `MAC_INSTALLER_IDENTITY` | e.g. `Developer ID Installer: Your Name (TEAMID)` |
   | `APPLE_ID` | your Apple ID email |
   | `APPLE_APP_PASSWORD` | the app-specific password from step 3 |
   | `APPLE_TEAM_ID` | your 10-character Team ID |

The workflow imports the certs into a temporary keychain, stores notarytool credentials, and
runs `vpk pack … --signAppIdentity … --signInstallIdentity … --notaryProfile … --signEntitlements build/entitlements.plist`.
Velopack signs, notarizes and staples automatically. The hardened-runtime entitlements a .NET
app needs are in [`build/entitlements.plist`](build/entitlements.plist).

## Cost summary

| Platform | What | Cost |
|----------|------|------|
| Windows  | **SignPath Foundation (open source)** | **free** |
| macOS    | Apple Developer Program (annual — no one-time or lifetime option) | US$99/yr |
| Linux    | — | free |

Windows is signed through SignPath at no cost. macOS can keep shipping unsigned for now (Mac users
right-click → Open the first time); add Apple's $99/yr once Mac matters enough to remove that prompt.
