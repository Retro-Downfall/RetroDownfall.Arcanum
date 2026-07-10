# The Forge

**The Forge** is the Inference IDE for Arcanum — a cross-platform Avalonia desktop application
(`RetroDownfall.TheForge.Ux`) that provides a full GUI over Arcanum's HTTP API: browsing campaigns
and spells, editing and casting/executing spells, chatting in sessions, orchestrating apprentices,
approving wards, tracking budget, and more. See [`docs/TheForge.DESIGN.md`](TheForge.DESIGN.md) for
the full design document, naming metaphor, and phased feature catalog.

## What it is (and isn't)

The Forge is a pure API client. It never opens the Grimoire database, never runs inference itself,
and never duplicates Arcanum's business logic — every capability is a thin wrapper over an Arcanum
HTTP route, authenticated the same way any external client would be. Running The Forge requires a
running `arcanum serve` instance to talk to.

## Projects

| Project | Location |
|---|---|
| `RetroDownfall.TheForge.Core` | `src/RetroDownfall.TheForge.Core/` — models, JSON context, API key resolver; no Avalonia dependency |
| `RetroDownfall.TheForge.Ux` | `src/RetroDownfall.TheForge.Ux/` — the Avalonia desktop app |
| `RetroDownfall.TheForge.Tests` | `tests/RetroDownfall.TheForge.Tests/` — xUnit tests |

All three are part of the existing `RetroDownfall.Arcanum.slnx` solution.

## Arcanum dependency

The Forge requires a reachable Arcanum instance. Start one first:

```bash
arcanum serve
```

By default Arcanum listens on `http://localhost:5001` (loopback). The Forge's `forge.json`
(`BaseUrl`) must point at whatever host/port your instance actually binds to.

## Acquiring an API key

Every Arcanum `/api/*` route requires the `X-Arcanum-Key` header. The master key is stored in the
**OS credential store** under the shared identity `service=arcanum` / `account=master-api-key`
(macOS Keychain, Windows Credential Manager, Linux Secret Service). Arcanum creates it on first
`arcanum serve`; The Forge reads the same entry.

The Forge resolves a key in this order:

1. OS credential store (`arcanum` / `master-api-key`).
2. Legacy plaintext `apiKey` in `~/.config/arcanum/forge.json` — migrated into the OS store, then stripped.
3. Shelling out to `arcanum key show` (stderr) — result persisted into the OS store.
4. Otherwise, a Whispers paste dialog; the pasted key is stored in the OS credential store.

Do **not** keep the master key in `forge.json` going forward. To rotate, run `arcanum key set` (or
update the OS credential) and restart The Forge.

**Linux:** install `libsecret` and ensure a Secret Service (e.g. gnome-keyring) is running. If the
OS store is unavailable, Arcanum can still fall back to Data Protection `security.dat`, but The Forge
cannot share that fallback — paste or `arcanum key set` on a machine with a working keychain.

## Settings file

`~/.config/arcanum/forge.json` — loaded with `reloadOnChange: true`, so most settings apply without
restarting The Forge:

```json
{
  "baseUrl": "http://localhost:5001",
  "apiKey": null,
  "theme": "dark",
  "lastCampaignId": null,
  "layoutState": null,
  "autoConnect": true,
  "activeSessionId": null
}
```

`apiKey` is obsolete (legacy migrate-and-strip only); leave it `null`.

`layoutState` holds a versioned JSON dock layout (`ForgeDockLayoutDto`) when the operator has
rearranged tool windows; `null` means use the default shell layout.

## Window layout

Tool windows (Atelier, Gatehouse, Treasury, Arsenal, War Table, Output, Logs, Hearth) can be moved
between the left, right, and bottom dock regions via each tool’s header **context menu** (Move Left /
Move Right / Move Bottom / Hide) or restored from **View**. **View → Reset Window Layout** restores
the default shell and persists it. Layout is stored in `~/.config/arcanum/forge.json` as `layoutState`
and restored on next launch. The Workbench stays the central document host; The Anvil stays a fixed
status bar. Drag-and-drop docking is not required for this release — menu/context movement is the
supported path.

## The Hearth terminal

The Hearth is The Forge's dockable terminal panel (View → **The Hearth**, default bottom dock). It
runs local shell commands from a working directory that starts at your user home profile. Use the
**Home** button to reset the working directory; use built-in `cd` (including `~`) to move around.

Initial Git integration is available through The Hearth terminal: use `git status`, `git diff`, etc.
directly until the dedicated Git UI (**The Ledger**) arrives.

The Hearth supports command output streaming, `cd`, Stop, and Clear. It is not a full
pseudo-terminal yet, so fully interactive terminal apps may not work correctly. Command execution is
local desktop functionality — it does not call the Arcanum API or go through Sanctum/Wards.

## Build and run

From the repository root:

```bash
# Build the whole solution, including The Forge
dotnet build RetroDownfall.Arcanum.slnx

# Run The Forge (requires `arcanum serve` running separately)
dotnet run --project src/RetroDownfall.TheForge.Ux/RetroDownfall.TheForge.Ux.csproj

# Run The Forge's test suite
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj
```

## Status

The Forge is in **alpha** (`0.1.0-alpha`). **Milestones A–E are complete** (Phases 1–10): Avalonia shell, Atelier, Spell editor, Tome, War Table, Gatehouse, Anvil, and Visual Studio 2026 Fluent-inspired theming (Cascadia Mono / Segoe UI Variable, Dark/Light resource dictionaries, ManaBar, Icons, `forge.json` `Theme` swap). **The Hearth** local terminal is also available (see above). See [`docs/TheForge.DESIGN.md`](TheForge.DESIGN.md) §5.7, §5.8, and §6.

**Known gaps (honest UI):** Treasury and Arsenal are placeholders (“not implemented yet”). Campaign **New Spell / New Prompt / New Session** are disabled until create flows ship. Dedicated Git UI (The Ledger) is not built yet — use The Hearth for `git` commands. Connect via **View → Connect to Arcanum** or the Anvil connection chip; disconnect from the View menu. Tool windows rearrange via context menu / View menu; OS floating windows are not implemented yet.

