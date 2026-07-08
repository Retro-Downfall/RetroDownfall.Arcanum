# Compendium

**Compendium** is the desktop configuration GUI for Arcanum. It is a .NET 10 MAUI application (`RetroDownfall.Compendium.Ux`) that reads, edits, and writes the `arcanum.json` configuration file.

## Purpose and scope

Compendium is strictly a configuration editor. It does **not** run inference, manage the Arcanum daemon, open the Grimoire database, or execute MCP tools. Its only job is to give the operator a friendly, visual way to manage `arcanum.json`.

## Project location

- Project: `src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj`
- Added to: `RetroDownfall.Arcanum.slnx`
- Project-references: `RetroDownfall.Arcanum.Core` (reuses the existing `ArcanumSettings` model, JSON context, clamps, and validator)

## Target platforms

- Windows (`net10.0-windows10.0.19041.0`)
- MacCatalyst (`net10.0-maccatalyst`)
- A headless `net10.0` TFM is also included so the shared UI logic and services can be built and unit-tested without a full platform toolchain.

Linux UI support is not in v1, but the `Services/` layer is written with cross-platform path discipline so an Avalonia MAUI backend can be added later without changing service code.

## Configuration file location

Compendium reads and writes the same file Arcanum uses:

- macOS / Linux: `~/.config/arcanum/arcanum.json`
- Windows: `%USERPROFILE%\.config\arcanum\arcanum.json`

The path is resolved through `RetroDownfall.Arcanum.Core.Storage.ArcanumPaths.GrimoireDirectory`, which uses `Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)` and `Path.Combine`. No OS-specific slash literals are used in the service layer.

## Secret interop

Provider `apiKey` values are encrypted at rest with the same `Microsoft.AspNetCore.DataProtection` setup Arcanum uses:

- Application name: `ArcanumCore`
- Key ring directory: `~/.config/arcanum/keys/`
- Protector purpose: `Arcanum.Configuration.ProviderSecrets`
- Encrypted values are prefixed with `dp:v1:`.

Compendium decrypts keys on read and re-encrypts them on save, so the file remains fully usable by both Arcanum and Compendium.

## Architecture

- **MVVM** via `CommunityToolkit.Mvvm` source generators.
- **Models**: UI-only types — `ConfigSection`/`SectionDescriptor` (nav) and the `SettingDescriptor` metadata table (see below). The domain model is reused from `RetroDownfall.Arcanum.Core`.
- **Services**: `ArcanumConfigurationStore`, `ArcanumDataProtectionSecretProtector`, `DialogService`. All filesystem paths are composed with `Path.Combine` and `ArcanumPaths.*`.
- **ViewModels**: one root `ConfigurationViewModel` plus 14 `SectionViewModel` classes covering the config surface. Several sections group multiple config domains: **Storage** covers Grimoire + Sessions + EventBus + Logs + Workspaces; **Forge** covers Spells + Campaigns + Perception + Prompts + Codex; **Orchestration** covers Daemon + Apprentices + Conclave (top-level only — the nested `Conclave.A2A` sub-record round-trips untouched); **Security** covers Security + Ward. Core records are immutable `init`-only; the VMs expose mutable `[ObservableProperty]` fields and rebuild records via `with` expressions on save. `ProvidersSectionViewModel.ProviderViewModel.Models` is an `ObservableCollection<ModelEntryViewModel>` (name + Scrying `supportsVision` toggle per row), not a chips string — each `Arcanum:Providers[].models` entry is a `ModelEntry`, not a bare string. The nav rail (`ConfigSection` enum) has 17 entries, but `Pricing`, `Resilience`, and `Moderations` fall back to `HostPage` (no VM yet).

  **Settings with descriptors but no UI:** `SettingDescriptorCoverageTests` asserts every `ArcanumSettings` leaf property has a descriptor, but several recent config domains have descriptors and `ArcanumSettings` properties yet are not bound to any `SectionViewModel` — they round-trip via `_snapshot with { ... }` (preserved on save, not editable from the UI): `Embeddings` (all 5 RAG phases, including nested `Codebase` and `Saga` sub-records), `Guardrails` (including nested `AuditLog`), `Pricing` / `Budget`, `Cache`, `StructuredOutput`, `WebBrowsing`, `ClientToolForwarding`, `Resilience`, `Moderations`, `Metrics` (separate from `Host`), `Files`, `Batches`, and the nested `Conclave.A2A` sub-record. Operators must edit these in `arcanum.json` directly until their section VMs are implemented.
- **Views**: `AppShell` (side nav + content host + sticky `SaveBar`), one `ContentPage` per section, and reusable controls (`LabeledEntry`, `LabeledStepper`, `LabeledToggle`, `ChipsEditor`, `LabeledPicker`, `LabeledColorEntry`, `SaveBar`).

## SettingDescriptor metadata table

A single `SettingDescriptor` table (`src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs`) is the visual mirror of the `arcanum.json` configuration reference in [`docs/DESIGN.md`](DESIGN.md) §3.4. Each row pairs one setting with:

- `Key` — the dot-path pointer that matches `ConfigurationValidationError.Pointer` (e.g. `host.port`, `mcp.requestTimeoutSeconds`, `cli.themeColors.light.text`), so validation errors route back to the offending field.
- `Section`, `Label`, `Description` — nav grouping, field label, and the help text shown under the field.
- `Kind` — `String`, `Int`, `Long`, `Float`, `Bool`, `Enum`, `StringArray`, `Path`, `Secret`, `Color`, or `Dictionary`. The kind selects the control (dropdown for `Enum`, live swatch for `Color`, chips for `StringArray`, masked entry for `Secret`).
- `Min` / `Max` / `Increment` — copied from the matching `ArcanumSettingClamps.*` method and locked in by `SettingDescriptorParityTests`.
- `EnumType` — the enum whose `Enum.GetValues()` populates the picker.
- `ClampName` — the `ArcanumSettingClamps` method name used by the parity test.

Two tests in `tests/RetroDownfall.Compendium.Tests` guard the table against drift:

- `SettingDescriptorParityTests` — every numeric descriptor's `Min`/`Max` equal the bounds of the corresponding `ArcanumSettingClamps.*` method (verified by invoking the clamp with extreme values).
- `SettingDescriptorCoverageTests` — reflects over `ArcanumSettings` and every nested record's `init` properties and asserts each has a matching descriptor; also asserts no orphaned descriptors and no duplicate keys.

## Reusable controls

| Control | Kind | Behaviour |
|---|---|---|
| `LabeledEntry` | String / Path / Secret | Label + Entry + description + validation hint; `IsPassword` for secrets. |
| `LabeledStepper` | Int / Long / Float | Label + Stepper bound to descriptor clamp `Min`/`Max`/`Increment` + live value label + description + validation hint. |
| `LabeledToggle` | Bool | Label + Switch + description. |
| `ChipsEditor` | StringArray | Add/remove chips for `string[]` fields (CORS origins, forbidden arts, allowed hosts, Scrying allowed MIME types). |
| `LabeledPicker` | Enum | Label + Picker whose `ItemsSource` is `Enum.GetValues(EnumType)`; `EnumToStringConverter` renders human-readable labels (e.g. `OpenAI Compatible`, `System Default`). |
| `LabeledColorEntry` | Color | Hex Entry + live `BoxView` swatch (via `HexToColorConverter`, parses `#RGB`/`#RRGGBB`/`#AARRGGBB`) + description + validation hint. Used for all 10 CLI theme colors. |
| `SaveBar` | — | Sticky strip: dirty dot, Save/Reload, last-saved timestamp. |

The `EnumToStringConverter` and `HexToColorConverter` are registered app-wide in `App.xaml`. `EnumToStringConverter` inserts a space before an uppercase letter only when the next character is lowercase, so acronyms stay together (`OpenAI Compatible`, `Llama Cpp Server`).

## Validation routing

When `ConfigurationValidator` rejects a save, `ConfigurationViewModel` builds an `IReadOnlyDictionary<string, string>` keyed by the validator's dot-path `Pointer` (e.g. `mcp.requestTimeoutSeconds`) and exposes it as `ValidationErrorsByPointer`. Each control binds its `ValidationErrors` to that dictionary and its `Key` to the descriptor key; the control looks up its key and shows the error detail in a red hint label under the field. The dictionary is cleared on successful save and on reload. `ValidationRoutingTests` asserts the validator's pointers match descriptor keys end-to-end.

## Dynamic theming

The UI supports both System Light and System Dark modes through MAUI `AppThemeBinding`. Raw Light/Dark colors live in `Resources/Styles/Colors.xaml`; control styles in `Resources/Styles/CompendiumTheme.xaml` consume them via `AppThemeBinding`. No page or control hardcodes a color.

## State synchronization

- On launch, the root VM loads `arcanum.json` once.
- A `FileSystemWatcher` watches the config directory and raises `ExternalChange` when the file is modified outside Compendium.
- The save bar shows a reload prompt when an external change is detected; if the user has local edits, a confirmation dialog is shown before discarding them.
- Save validates via `ConfigurationValidator`, atomically writes to a temp file, replaces `arcanum.json`, and applies owner-only permissions.

## Build and run

```bash
# Build the whole solution
dotnet build RetroDownfall.Arcanum.slnx

# Run the smoke test that verifies read/write round-trip with dp:v1: key interop
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj

# Run only the descriptor parity/coverage/converter tests
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj --filter "FullyQualifiedName~SettingDescriptor|FullyQualifiedName~Converter|FullyQualifiedName~ValidationRouting"

# Run the MacCatalyst app (requires Xcode on macOS)
dotnet run --project src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj -f net10.0-maccatalyst

# Run the Windows app (requires Windows SDK / Visual Studio)
dotnet run --project src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj -f net10.0-windows10.0.19041.0
```

## Note on headless testing

The full MacCatalyst / Windows app build requires the corresponding platform toolchain. A `net10.0` library TFM is intentionally included so the shared services and view models can be compiled and tested without Xcode or the Windows SDK. The UI XAML is only compiled for the platform TFMs.
