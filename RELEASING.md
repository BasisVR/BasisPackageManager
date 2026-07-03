# Releasing Basis Package Manager

Releases are automated with [Velopack](https://velopack.io). Pushing a semver tag builds
installers for Windows, Linux and macOS, packs them (plus the update manifest) with `vpk`,
and publishes them to a single GitHub Release. The in-app updater reads that release, so a
new version promotes itself to existing users.

## Cut a release

```bash
git tag v0.1.0
git push origin v0.1.0
```

That's it. [`.github/workflows/release.yml`](.github/workflows/release.yml) runs the
`windows-latest` / `ubuntu-latest` / `macos-latest` matrix and, when it finishes, the
[Releases page](https://github.com/BasisVR/BasisPackageManager/releases) has:

| Platform | Asset |
|----------|-------|
| Windows  | `BasisPackageManager-win-Setup.exe` (+ portable zip) |
| Linux    | `BasisPackageManager-linux-*.AppImage` |
| macOS    | `BasisPackageManager-osx-*.pkg` |
| (all)    | `*-full.nupkg` + `releases.*.json` / `RELEASES` — the update manifest |

**Verify:** install the previous version, publish a higher tag, and confirm the in-app
banner offers the update and one click installs + restarts onto it.

## Versioning

The baseline version lives in [`Directory.Build.props`](Directory.Build.props), but the
**git tag is the source of truth** — CI passes `-p:Version=${TAG#v}` at build time, so
`v1.4.2` ships version `1.4.2` regardless of the props file. Use plain semver
(`vMAJOR.MINOR.PATCH`); Velopack rejects 4-part versions.

## Code signing

Releases are **unsigned by default**, which is why users see Windows SmartScreen and macOS
Gatekeeper prompts on first run. Signing removes those. The release workflow **auto-detects**
signing credentials: add the secrets below and the next tag signs automatically — no workflow
edits needed. Leave them unset and releases keep working, just unsigned.

Linux (AppImage) needs no signing for this purpose.

### Windows

> **Heads up:** the cheapest cloud option, Microsoft's
> [Azure Trusted Signing](https://azure.microsoft.com/products/artifact-signing) (~$10/mo),
> is **only available to the US, Canada and EU/UK — not Australia**. The two options below are
> internationally available and sign in GitHub Actions **without a USB hardware token**.

Since June 2023 ordinary OV certificates must live on a hardware token or a cloud HSM, so pick a
provider whose cloud service exposes the cert to `signtool`:

**[Certum](https://www.certum.eu/en/code-signing-certificates/) — cheapest (~€69 first year, ~€29
renewal).** A European CA trusted by Microsoft. Its **Open Source Developer** cert suits BasisVR
(MIT), and its SimplySign cloud presents the cert to `signtool` as a virtual smart card (no token).
It's OV, so SmartScreen reputation builds over the first downloads rather than instantly. Best if
you sign from your workstation or run Certum's SimplySign proxy on the runner.

**[SSL.com eSigner](https://www.ssl.com/how-to/cloud-code-signing-integration-with-github-actions/)
— cleanest for CI (~US$249/yr EV, cheaper OV).** Purpose-built for pipelines: an official GitHub
Action plus **eSigner CKA** (a `signtool` provider), no hardware. **EV gives instant SmartScreen
trust.**

Either way Velopack signs through `signtool`:

```
vpk pack … --signParams "/fd sha256 /td sha256 /tr <provider-timestamp-url>"
```

**To enable it:** add your provider's cert-setup step to the *Pack (Windows)* job in
[`release.yml`](.github/workflows/release.yml) (SSL.com's action, or Certum's SimplySign), and set
a repo secret **`WINDOWS_SIGN_PARAMS`** with the `signtool` arguments above. The workflow already
appends `--signParams "$WINDOWS_SIGN_PARAMS"` when that secret is present. (Ping me with your pick
and I'll wire the exact setup step.)

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
| Windows  | Certum (cheapest) — or SSL.com eSigner (CI-native) | ~€69/yr — or ~US$249/yr |
| macOS    | Apple Developer Program | US$99/yr |
| Linux    | — | free |

Windows-only signing is a fine place to start — Certum's open-source cert is inexpensive and
covers the most users. macOS signing is independent; add it whenever you want Mac users to skip
the Gatekeeper prompt.
