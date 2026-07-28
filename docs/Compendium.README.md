# Compendium

**Compendium** is the desktop configuration GUI for Arcanum. It is a .NET 10 Avalonia application (`RetroDownfall.Compendium.Ux`) that reads, edits, and writes the `arcanum.json` configuration file.

## Launch from The Forge

**The Forge** can open Compendium via **View → Open Compendium**, The Anvil **Compendium** chip, the setup wizard, or disabled-feature banners. Discovery looks for an installed `RetroDownfall.Compendium.Ux` binary, then a development `dotnet run --project src/RetroDownfall.Compendium.Ux/...` path. If launch fails, The Forge shows the exact `arcanum.json` path (`~/.config/arcanum/arcanum.json`) so operators can edit configuration manually. Compendium itself does not need to change for this deep-link.

## Purpose and scope

Compendium is strictly a configuration editor. It does **not** run inference, manage the Arcanum daemon, open the Grimoire database, or execute MCP tools. Its only job is to give the operator a friendly, visual way to manage `arcanum.json`.

## Project location

- Project: `src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj`
- Added to: `RetroDownfall.Arcanum.slnx`
- Project-references: `RetroDownfall.Arcanum.Core` (reuses the existing `ArcanumSettings` model, JSON context, clamps, and validator)

## Target platforms

Avalonia desktop via `UsePlatformDetect()` on `net10.0`:

- Windows
- macOS
- Linux

`App.axaml` sets `Name="Compendium"` so the macOS menu bar shows **Compendium** (not “Avalonia Application”) during `dotnet run`. Bundled `.app` builds should also set matching `CFBundleName` / `CFBundleDisplayName` in `Info.plist`.

```bash
dotnet run --project src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj
```

## Configuration file location

Compendium reads and writes the same file Arcanum uses:

- macOS / Linux: `~/.config/arcanum/arcanum.json`
- Windows: `%USERPROFILE%\.config\arcanum\arcanum.json`

The path is resolved through `RetroDownfall.Arcanum.Core.Storage.ArcanumPaths.GrimoireDirectory`, which uses `Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)` and `Path.Combine`. No OS-specific slash literals are used in the service layer.

## Secret interop

Provider `apiKey` values and **`Host.Https.CertificatePassword`** are encrypted at rest with the same `Microsoft.AspNetCore.DataProtection` setup Arcanum uses:

- Application name: `ArcanumCore`
- Key ring directory: `~/.config/arcanum/keys/`
- Protector purpose: `Arcanum.Configuration.ProviderSecrets`
- Encrypted values are prefixed with `dp:v1:`.

Compendium decrypts secrets on read and re-encrypts them on save, so the file remains fully usable by both Arcanum and Compendium.

## Host HTTPS

The Host section includes optional HTTPS settings (`host.https.*`) and a **Generate local certificate** button:

- Creates a self-signed PFX under `~/.config/arcanum/certs/` (`arcanum-localhost-{timestamp}.pfx`), owner-only.
- SANs: `localhost`, `127.0.0.1`, `::1`. BasicConstraints CA=false; RSA 2048; SHA-256; valid up to 397 days.
- Populates Enabled, CertificatePath, CertificatePassword (random), clears PrivateKeyPath; preserves a valid HTTPS port.
- Does **not** install the certificate into the OS trust store — browsers/clients may warn until you trust it manually.
- Loopback SANs only: for ListenAny / remote access, HTTPS is required and you must supply a certificate whose SAN includes your hostname or IP (and trust it in the OS store — clients do not skip TLS validation).
- Password is stored encrypted (`dp:v1:`) on Save; Generate marks the section dirty but does not autosave.

## Architecture

- **MVVM** via `CommunityToolkit.Mvvm` source generators; DI via `Microsoft.Extensions.DependencyInjection` (`Program` → `ServiceCollectionConfigurator` → `App`).
- **Shell B (Visual Studio–like):** menu bar + left section list + center document tabs + sticky `SaveBar`. Theme tokens are copied from TheForge (VS 2026 Fluent Dark/Light) and selected via Avalonia `ThemeDictionaries` so the chrome follows the OS light/dark preference (`RequestedThemeVariant=Default`).
- **Models**: UI-only types — `ConfigSection`/`SectionDescriptor` (nav) and the `SettingDescriptor` metadata table (see below). The domain model is reused from `RetroDownfall.Arcanum.Core`.
- **Services**: `ArcanumConfigurationStore`, `ArcanumDataProtectionSecretProtector`, `DialogService` (via `IMainWindowProvider`), `IUiDispatcher`. All filesystem paths are composed with `Path.Combine` and `ArcanumPaths.*`.
- **ViewModels**: one root `ConfigurationViewModel` plus 14 polished `SectionViewModel` classes. Several sections group multiple config domains: **Storage** covers Grimoire + Sessions + EventBus + Logs + Workspaces; **Forge** covers Spells + Campaigns + Perception + Prompts + Codex; **Orchestration** covers Daemon + Apprentices + Conclave (top-level only — the nested `Conclave.A2A` sub-record round-trips untouched); **Security** covers Security + Ward. Core settings types are `record` POCOs with **`{ get; set; }`** (required for the configuration binding generator — not `init`-only). Compendium VMs expose mutable `[ObservableProperty]` fields and rebuild settings snapshots via `with` expressions on save.
- **Provider model capabilities:** the polished `ProvidersPage` rows edit only model name and vision support. Existing optional reasoning, tokenization, and provider/model prompt-caching profile objects are retained as opaque metadata and round-trip unchanged; legacy string entries do not gain invented capabilities. Operators author those nested objects in raw `arcanum.json`. The `providers(.models).tokenization.*`, `providers.models.reasoning.*`, and `providers(.models).promptCaching.*` descriptors provide schema/help and parity metadata only; they are not a generic editor surface for provider rows.
- **Token-accounting defaults:** `IntelligencePage` exposes the calibrated/unknown-profile safety margin and unknown-image reserve beside the fallback tokenizer encoding. Exact model/provider overrides remain in the typed nested profile above.
- **Coding tools:** `ConfigSection.CodingTools` is a generic descriptor page for every numeric/bool/path bound under `codingTools.search`, `codingTools.patch`, and `codingTools.workspaceCheck`. `workspaceCheck.customProfiles` remains one opaque dictionary descriptor: Compendium preserves it unchanged but does not expose executable IDs, fixed arguments, parsers, or option renderings as child controls. Author/review those security-sensitive values in raw `arcanum.json`.
- **Generic descriptor editor:** domains without a polished hand-authored view (CodingTools, Resilience, Pricing, Budget, StructuredOutput, WebBrowsing, ClientToolForwarding, Guardrails, Embeddings, Metrics, Files, Attachments, Batches) open `GenericSettingsSectionView`, which renders fields from `SettingDescriptors` grouped by subdomain. Edits live in `GenericSettingFieldViewModel` instances; `_snapshot` stays the last loaded baseline until Save/Reload. On save, `BuildSettings()` applies polished `with` edits then a reflection-based key updater (`GenericSettingsUpdater`) for generic fields. Compendium is a desktop editor and is not Native AOT-shipped.
- **Views**: `MainWindow` + one Avalonia `UserControl` per polished section + `GenericSettingsSectionView`, and reusable controls (`LabeledEntry`, `LabeledStepper`/`NumericUpDown`, `LabeledToggle`, `ChipsEditor`, `LabeledPicker`, `LabeledColorEntry`, `SaveBar`).

## SettingDescriptor metadata table

A single `SettingDescriptor` table (`src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs`) is the visual mirror of the `arcanum.json` configuration reference in [`docs/Arcanum.DESIGN.md`](Arcanum.DESIGN.md) §3.4. Each row pairs one setting with:

For polished sections such as `ProvidersPage`, descriptor presence documents schema/help and supports coverage/parity checks; it does not imply that the generic editor renders that field.

- `Key` — the dot-path pointer that matches `ConfigurationValidationError.Pointer` (e.g. `host.port`, `security.allowUnsandboxedToolChildren`, `mcp.requestTimeoutSeconds`, `cli.themeColors.light.text`), so validation errors route back to the offending field.
- `Section`, `Label`, `Description` — nav grouping, field label, and the help text shown under the field.
- `Group` — optional UI-only subgroup for the generic editor (otherwise derived from the key’s second segment).
- `Kind` — `String`, `Int`, `Long`, `Float`, `Bool`, `Enum`, `StringArray`, `Path`, `Secret`, `Color`, or `Dictionary`. The kind selects the control (dropdown for `Enum`, live swatch for `Color`, chips for `StringArray`, masked entry for `Secret`).
- `Min` / `Max` / `Increment` — copied from the matching `ArcanumSettingClamps.*` method and locked in by `SettingDescriptorParityTests`.
- `EnumType` — the enum whose `Enum.GetValues()` populates the picker.
- `ClampName` — the `ArcanumSettingClamps` method name used by the parity test.
- `AllowUnset` — distinguishes an absent nullable value from an explicit zero; used by `pricing.defaultPricing.reasoningPer1M`, where null falls back to the output rate and zero means free reasoning.

Two tests in `tests/RetroDownfall.Compendium.Tests` guard the table against drift:

- `SettingDescriptorParityTests` — every numeric descriptor's `Min`/`Max` equal the bounds of the corresponding `ArcanumSettingClamps.*` method (verified by invoking the clamp with extreme values).
- `SettingDescriptorCoverageTests` — reflects over `ArcanumSettings` and nested settings records' public settable properties and asserts each has a matching descriptor; also asserts no orphaned descriptors and no duplicate keys.

`GenericSettingsPreservationTests` asserts polished edits do not drop generic-domain values, and that generic field edits apply through `BuildSettings()`.

The Coding Tools descriptors mirror the exact defaults/clamps in [Arcanum.DESIGN.md §3.4](Arcanum.DESIGN.md#34-configuration-reference-arcanumsettings). `workspaceCheck.enabled=true` is only an operator preference: runtime advertisement still requires macOS Seatbelt, trusted native `dotnet`, a trusted selected SDK/runtime, and a trusted launch chain. Linux/Windows remain unavailable. Compendium must not imply that closed profiles are safe to run against an untrusted repository: MSBuild tasks, generators, analyzers, and tests execute arbitrary code, network remains open, and `workspace_check` always requires a Ward while Wards are enabled.

Changing Coding Tools settings changes no Grimoire schema and requires no database migration or reinstall.

## Reusable controls

`LabeledEntry`, `LabeledStepper`, `LabeledToggle`, `ChipsEditor`, `LabeledPicker`, `LabeledColorEntry`, `SaveBar` — descriptor-driven controls with validation hints. Converters: `EnumToStringConverter`, `HexToBrushConverter` / `HexColorParser` (unit-tested without Avalonia app init).

## Validation routing

When `ConfigurationValidator` rejects a save, `ConfigurationViewModel` builds an `IReadOnlyDictionary<string, string>` keyed by the validator's dot-path `Pointer` (e.g. `mcp.requestTimeoutSeconds`) and exposes it as `ValidationErrorsByPointer`. Each control binds its `ValidationErrors` to that dictionary and its `Key` to the descriptor key; the control looks up its key and shows the error detail in a red hint label under the field. The dictionary is cleared on successful save and on reload. `ValidationRoutingTests` asserts the validator's pointers match descriptor keys end-to-end.

## Dynamic theming

The UI uses Avalonia FluentTheme plus TheForge-inspired Dark/Light brush sets registered as `ResourceDictionary.ThemeDictionaries` (`Themes/DarkTheme.axaml`, `Themes/LightTheme.axaml`) with shared typography in `Themes/Typography.axaml` (14px UI / 32px control height). `App.axaml` sets `VerticalContentAlignment=Center` on `Button`, `TextBox`, `ComboBox`, and `NumericUpDown` so text stays vertically centered when those controls are forced to the 32px anvil height (Avalonia’s default content alignment is top). `RequestedThemeVariant` is `Default`, so Avalonia follows the OS and picks the matching dictionary via `ActualThemeVariant`. Views bind colors via `{DynamicResource Forge*Brush}` only — no hardcoded page colors.

OS light/dark following is best-effort on Linux; some desktop environments don't report a theme preference and Avalonia falls back to dark.

## State synchronization

- On launch, the root VM loads `arcanum.json` once. Corrupt or unparseable JSON shows an alert with
  the file path and parse error (and sets the status bar); the load does not leave an unobserved task
  exception.
- Edits mark dirty through `MarkDirty()` (polished section `PropertyChanged`, nested provider/job/model property changes, collection add/remove, and every `GenericSettingFieldViewModel` change in the generic descriptor editor). `MarkDirty` and `IsDirty`/`IsSaving`/`HasExternalChange` changes notify `SaveCommand` and `CancelCommand` so the Save/Cancel buttons enable correctly.
- **Cancel** discards unsaved edits by re-applying the in-memory snapshot from the last successful load/save (no disk read, no confirm dialog).
- A `FileSystemWatcher` watches the config directory and raises `ExternalChange` when the file is modified outside Compendium.
- The watcher callback marshals to the UI thread and sets `HasExternalChange` only (no modal from the watcher).
- The save bar shows a reload prompt when an external change is detected; if the user has local edits, a confirmation dialog is shown when they click Reload.
- Save validates via `ConfigurationValidator`, atomically writes to a temp file, replaces `arcanum.json`, and applies owner-only permissions.

## Build and run

```bash
# Build the whole solution
dotnet build RetroDownfall.Arcanum.slnx

# Run Compendium (Avalonia desktop)
dotnet run --project src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj

# Run the smoke / descriptor / generic preservation tests
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj

# Run only the descriptor parity/coverage/converter tests
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj --filter "FullyQualifiedName~SettingDescriptor|FullyQualifiedName~Converter|FullyQualifiedName~ValidationRouting|FullyQualifiedName~GenericSettings"
```

## macOS Apple Silicon release

Compendium ships as a signed, notarized, stapled `compendium-osx-arm64.dmg` containing `Compendium.app` (self-contained Avalonia on .NET 10 — **not** Native AOT). Packaging defaults to **multi-file** publish so native libraries can be codesigned individually. See [`RELEASE-MACOS.md`](RELEASE-MACOS.md) for the manual workflow, required **Developer ID Application** secrets, SemVer vs `CFBundle*` versioning, and draft-release steps.

## Windows x64 packaging

Unsigned `compendium-win-x64.zip` (self-contained Avalonia folder publish, **not** Native AOT) is produced with Arcanum via:

- Local: `.\scripts\packaging\windows\package-windows.ps1 -Version <semver> -OutputDir .\dist -SkipForge`
- GitHub Actions: [`.github/workflows/build-windows-x64.yml`](../.github/workflows/build-windows-x64.yml) (`workflow_dispatch`)

See [`PRIVATE-BETA-NOTES.md`](PRIVATE-BETA-NOTES.md) for archive layout and SmartScreen notes.

## Note on headless testing

Unit tests target `net10.0` and do not require a desktop session or Avalonia window. UI XAML compiles as part of the Avalonia desktop project; CI exercises services, ViewModels, converters, and descriptor guards without an X server.
