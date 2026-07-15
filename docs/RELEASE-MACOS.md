# macOS Apple Silicon release

Manual GitHub Actions workflow that builds, codesigns, notarizes, and publishes **osx-arm64** artifacts for Arcanum, Compendium, and The Forge.

This is **not** a Mac App Store release. Artifacts go to **GitHub** (workflow artifacts + a draft GitHub Release). Apple’s role is **Developer ID** code signing and notarization so Gatekeeper accepts downloads outside the App Store.

Workflow: [`.github/workflows/release-macos-arm64.yml`](../.github/workflows/release-macos-arm64.yml)

Packaging scripts: [`scripts/packaging/macos/`](../scripts/packaging/macos/)

---

## First run (precise checklist)

Do these steps in order once. Later releases only need [How to run](#how-to-run).

### 0. Land the workflow on GitHub

Commit and push the packaging scripts and `.github/workflows/release-macos-arm64.yml` to the branch you will run Actions from (usually `main`). Until that file exists on GitHub, **Release macOS arm64** will not appear under Actions.

### 1. Apple Developer Program + Developer ID certificate

1. Enroll (or use an existing team) at [developer.apple.com](https://developer.apple.com).
2. Create or download a **Developer ID Application** certificate — **not** “Apple Development”, **not** Mac App Store.
   - Xcode → Settings → Accounts → Manage Certificates → **+** → **Developer ID Application**, or  
   - Certificates, Identifiers & Profiles on the Apple Developer site.
3. On a Mac where that certificate is installed, list signing identities:

```bash
security find-identity -v -p codesigning
```

You need a line like:

```text
Developer ID Application: Your Name (TEAMID)
```

Copy that **entire** string. It becomes the `APPLE_SIGNING_IDENTITY` secret.

4. Export a password-protected `.p12` that includes the private key:
   - Open **Keychain Access** → **My Certificates**.
   - Find the **Developer ID Application** certificate → right-click → **Export…** → choose `.p12`.
   - Set a strong export password (this becomes `APPLE_CERTIFICATE_PASSWORD`).
5. Base64-encode the `.p12` as a single line (macOS):

```bash
base64 -i YourCert.p12 | pbcopy
```

The clipboard value becomes `APPLE_CERTIFICATE`.

### 2. Notarization credentials

1. `APPLE_ID` — Apple ID email for the developer team.
2. `APPLE_TEAM_ID` — 10-character Team ID from Membership details on developer.apple.com (also the `(TEAMID)` in the signing identity).
3. Create an **app-specific password** at [appleid.apple.com](https://appleid.apple.com) → Sign-In and Security → App-Specific Passwords (label e.g. `arcanum-notary`). That value is `APPLE_APP_SPECIFIC_PASSWORD`.

### 3. Add GitHub Actions secrets

In the repository: **Settings** → **Secrets and variables** → **Actions** → **New repository secret**. Add all six:

| Secret | Value |
|---|---|
| `APPLE_CERTIFICATE` | Base64 of the `.p12` (step 1.5) |
| `APPLE_CERTIFICATE_PASSWORD` | Password used when exporting the `.p12` |
| `APPLE_SIGNING_IDENTITY` | Exact `Developer ID Application: … (TEAMID)` string from step 1.3 |
| `APPLE_ID` | Apple ID email |
| `APPLE_TEAM_ID` | 10-character Team ID |
| `APPLE_APP_SPECIFIC_PASSWORD` | App-specific password from step 2.3 |

`APPLE_SIGNING_IDENTITY` **must** start with `Developer ID Application:`. Development or Mac App Store identities fail the workflow guard.

The workflow imports the cert into an ephemeral keychain and runs `security set-key-partition-list` so `codesign` can use the key non-interactively.

### 4. Confirm macOS Apple Silicon larger runners

The job uses `runs-on: macos-15-xlarge`.

1. The GitHub org/account must have **macOS larger runners** available (plan/billing as required by GitHub).
2. Confirm the label `macos-15-xlarge` can be used by this repository.
3. Standard `macos-15` is **not** sufficient as written: the workflow asserts `uname -m == arm64` and fails otherwise.

If `macos-15-xlarge` is unavailable, enable larger runners (or change the workflow label) before expecting a green run.

### 5. Run the first release

1. GitHub → **Actions** → **Release macOS arm64** → **Run workflow**.
2. Choose the branch that contains the workflow file.
3. Set **version** (examples below). Tag will be `v` + that value.
4. Wait for the job to finish.
5. On success, download the workflow artifacts and/or open the **draft** GitHub Release.
6. Spot-check on a Mac (Gatekeeper / open the apps / run `arcanum` from the zip).
7. On the draft release page, click **Publish release** when ready.

---

## Version input

| Allowed | Examples |
|---|---|
| Release | `0.1.0` |
| Prerelease | `0.1.0-beta`, `0.1.0-beta.1` |

**Rejected:** `0.1-beta` (missing patch), `0.1.0+build.42` (build metadata).

How the version is applied:

| Consumer | Value |
|---|---|
| GitHub draft release tag / title | `v${version}` (prerelease flag when version contains `-`) |
| .NET `-p:Version=` | Full SemVer (e.g. `0.1.0-beta.1`) |
| `CFBundleShortVersionString` | Numeric marketing version only (`0.1.0` — strip after `-`) |
| `CFBundleVersion` | `${{ github.run_number }}` (monotonic) |

Day-to-day project default for all three products is `0.1.0-beta` from [`Directory.Build.props`](../Directory.Build.props).

---

## Artifacts

| Asset | Contents |
|---|---|
| `arcanum-osx-arm64.zip` | Folder `arcanum-osx-arm64/arcanum` — signed Native AOT CLI. Zip is submitted to Apple notarization (**not stapled**). Extracted binary is validated with `codesign` / `spctl`. |
| `compendium-osx-arm64.dmg` | Signed, notarized, stapled `Compendium.app` (self-contained .NET Avalonia; **not** Native AOT). Default publish is **multi-file** so native libraries can be signed individually. |
| `the-forge-osx-arm64.dmg` | Same pattern for `The Forge.app`. |

Signing is **mandatory** in CI. Packaging scripts accept `--skip-sign` only for local structure smoke tests — never for release outputs.

---

## How to run

After [first-run setup](#first-run-precise-checklist) is done:

1. GitHub → Actions → **Release macOS arm64** → **Run workflow**.
2. Enter a version (e.g. `0.1.0-beta.1`).
3. When the job succeeds, download workflow artifacts and/or open the **draft** GitHub Release `v${version}`.
4. Spot-check on a clean Mac, then **publish** the draft release when ready.

Re-running the same version **replaces** release assets (`gh release upload --clobber`).

---

## First-run failure checklist

| Symptom | Likely cause |
|---|---|
| Workflow missing under Actions | Workflow file not pushed to the branch you selected |
| Missing secret error | One of the six `APPLE_*` secrets not set |
| Identity must be Developer ID Application | Wrong cert type or truncated `APPLE_SIGNING_IDENTITY` |
| Runner / `uname -m` assert | No `macos-15-xlarge`, or runner is not Apple Silicon |
| `codesign` / keychain errors | Bad `.p12`, wrong export password, or export missing the private key |
| `notarytool` auth failure | Wrong `APPLE_ID` / `APPLE_TEAM_ID` / app-specific password |
| Notarization rejected | Signing/entitlements issue — download Apple’s notarization log from the job output |

---

## Local packaging smoke (unsigned)

On an Apple Silicon Mac with the .NET 10 SDK:

```bash
# Structure-only (no Apple secrets) — do not distribute these outputs
./scripts/packaging/macos/build-arcanum.sh \
  --version 0.1.0-beta --output-dir /tmp/arcanum-dist --skip-sign

./scripts/packaging/macos/build-app-dmg.sh \
  --product compendium \
  --version 0.1.0-beta \
  --marketing-version 0.1.0 \
  --bundle-version 1 \
  --output-dir /tmp/arcanum-dist \
  --skip-sign
```

Pass `--single-file` to `build-app-dmg.sh` to exercise single-file publish instead of the multi-file default.
