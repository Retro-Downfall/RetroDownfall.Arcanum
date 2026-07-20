# The Forge

**The Forge** is the Inference IDE for Arcanum — a cross-platform Avalonia desktop application
(`RetroDownfall.TheForge.Ux`) that provides a full GUI over Arcanum's HTTP API. See
[`docs/TheForge.DESIGN.md`](TheForge.DESIGN.md) for architecture, naming metaphor, and feature catalog.

> **Not** the server-side campaign/spell registry also called “The Forge” in [`Arcanum.DESIGN.md` §19](Arcanum.DESIGN.md#19-the-forge--campaign-spell-metadata-and-prompt-registry).

## What it is (and isn't)

The Forge is a pure API client. It never opens the Grimoire database, never runs inference itself,
and never duplicates Arcanum's business logic — every capability is a thin wrapper over an Arcanum
HTTP route. Running The Forge requires a running `arcanum serve` instance.

## Projects

| Project | Location |
|---|---|
| `RetroDownfall.TheForge.Core` | `src/RetroDownfall.TheForge.Core/` — models, JSON context, API key resolver; no Avalonia |
| `RetroDownfall.TheForge.Ux` | `src/RetroDownfall.TheForge.Ux/` — Avalonia desktop app |
| `RetroDownfall.TheForge.Tests` | `tests/RetroDownfall.TheForge.Tests/` — xUnit tests |

All three are part of `RetroDownfall.Arcanum.slnx`.

## Arcanum dependency

Start Arcanum first:

```bash
arcanum serve
```

Default listen: `http://localhost:5001` (loopback). With `ListenAny` / `ARCANUM_HOST_ANY`, Arcanum
binds **HTTPS-only** on `Arcanum:Host:Https:Port` (default 5443) — set The Forge `BaseUrl` to
`https://localhost:5443` (or your host + HTTPS port). Do not disable TLS certificate validation.

Connect/disconnect via **View → Connect to Arcanum** or The Anvil connection chip.

## Acquiring an API key

Every Arcanum `/api/*` route requires the `X-Arcanum-Key` header. The master key lives in the
**OS credential store** under `service=arcanum` / `account=master-api-key`. Arcanum creates it on
first `arcanum serve`; The Forge reads the same entry.

Resolution order:

1. OS credential store (`arcanum` / `master-api-key`).
2. Legacy plaintext `apiKey` in `~/.config/arcanum/the-forge.json` — migrated into the OS store, then stripped.
3. `THEFORGE_ARCANUM_KEY` environment variable (trimmed; empty = absent). **Never logged. Never persisted.** Private-beta / automation override only.
4. `arcanum key show` (stderr) — persisted into the OS store when possible.
5. Whispers paste dialog → OS store when available, else **process-only** with a warning. Declining skips re-prompts until The Anvil **Enter API key…** (also clears a bad env-key cache).

Do **not** keep the master key in `the-forge.json`. Rotate with `arcanum key set` (or update the OS
credential) and restart The Forge.

**Linux:** install `libsecret` and ensure a Secret Service (e.g. gnome-keyring) is running. The Forge
cannot share Arcanum's Data Protection `security.dat` fallback — use paste, `arcanum key set` on a
machine with a working keychain, or `THEFORGE_ARCANUM_KEY`. `arcanum doctor` reports master-key
presence.

## Settings file

`~/.config/arcanum/the-forge.json` — `reloadOnChange: true`. Legacy `forge.json` is renamed on first
launch when `the-forge.json` is absent.

```json
{
  "baseUrl": "http://localhost:5001",
  "apiKey": null,
  "theme": "light",
  "lastCampaignId": null,
  "layoutState": null,
  "autoConnect": true,
  "activeSessionId": null
}
```

- `apiKey` is obsolete (migrate-and-strip only); leave `null`.
- **Theme:** fresh installs default `"light"`; existing `"dark"` stays dark. Change live via **View → Theme**.
- `layoutState` holds the dock layout when rearranged; `null` = default shell.
- `lastCampaignId` tracks the active campaign for The Anvil and menu authoring.

**Campaigns** are solution-level containers (operator term stays **Campaign**). File / Campaign menus
and The Atelier create, open, edit, unregister, export, and import through Arcanum HTTP only — no
project-file discovery. Loopback may use a local folder picker; remote hosts need a path **on the
Arcanum host**.

## UI map

Full catalog and deferred list: [`TheForge.DESIGN.md` §6](TheForge.DESIGN.md#6-feature-catalog-phased).

| Surface | Where | Role |
|---|---|---|
| The Workbench | Center tabs | Spell editor, Tome, Scriptorium, Codex, Proving Grounds, Comparison, markdown |
| The Atelier | Left dock | Campaigns / workspaces / spells / prompts / sessions tree |
| The Gatehouse | Right dock | Ward approve / deny |
| The Treasury | Right dock | Read-only budget |
| The Arsenal | Right dock | MCP, Scrying Pool, Diagnostic MCP, Models & Providers |
| The War Table | Right dock | Apprentices + Chronicle |
| The Hearth | Bottom dock | Local shell (not a PTY; not Arcanum HTTP) |
| The Ledger | Hidden → View | Desktop-local Git UI (no push/pull/reset/rebase yet) |
| Lore / Archive / Divination / Workspace Explorer / Weave Inspector / Audit / Files & Batches | Hidden → View | Context, RAG, audit, OpenAI files/batches |
| The Anvil | Fixed status bar | Connection, campaign, spend, wards, apprentices, MCP |
| Whispers | Top-right toasts | Short success/error; detail on Foundry Floor |
| The Illumination | Spell / Codex / markdown tabs | Source · Split · Preview (Markdig; optional remote images) |

Tool windows move via header context menu or **View**; **View → Reset Window Layout** restores defaults.
Layout persists in `layoutState`.

### The Proving Grounds

Singleton Workbench tab (**Trial → Proving Grounds**). Build a Trial against a Spell, Prompt, or
Apprentice Goal; add Inquisitors (Regex, JsonSchema, Semantic); **Run** calls
`POST /api/proving-grounds/trials/run`. Suites persist locally in
`~/.config/arcanum/the-forge-trial-suites.json`. Shortcuts: Spell **Create Trial**, Scriptorium
**Open in Proving Grounds**.

## Build and run

```bash
dotnet build RetroDownfall.Arcanum.slnx

dotnet run --project src/RetroDownfall.TheForge.Ux/RetroDownfall.TheForge.Ux.csproj

dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj
```

`App.axaml` sets `Name="The Forge"` so the macOS menu bar shows **The Forge** during `dotnet run`.
Bundled `.app` builds should set matching `CFBundleName` / `CFBundleDisplayName` in `Info.plist`.

## macOS Apple Silicon release

Signed, notarized, stapled `the-forge-osx-arm64.dmg` containing `The Forge.app` (self-contained
Avalonia on .NET 10 — **not** Native AOT). See [`RELEASE-MACOS.md`](RELEASE-MACOS.md).

Windows/Linux private-beta archives: `scripts/packaging/windows/package-windows.ps1`,
`scripts/packaging/linux/package-linux.sh`, or the **Private beta release** workflow — see
[`PRIVATE-BETA-NOTES.md`](PRIVATE-BETA-NOTES.md).

## Status and gaps

Beta `0.1.0-beta` (from [`Directory.Build.props`](../Directory.Build.props)). Milestones A–H,
H1/H2, and inference IDE expansion through phase 10 are implemented — see
[`TheForge.DESIGN.md` §6](TheForge.DESIGN.md#6-feature-catalog-phased).

**Known gaps (short):**

- No PTY Hearth; no OS floating tool windows
- No provider / budget / pricing / model-metadata editing; no token-cost breakdown UI
- Guardrails settings panel deferred (edit `arcanum.json` or Compendium)
- Advanced import conflict wizards; full campaign Settings editor
- Ledger: no push / pull / reset / rebase
- Illumination: relative workspace images, Mermaid graphs, native math deferred
- Diagnostic MCP: Forbidden Arts / internal tools blocked server-side (by design)

Deferred detail and feature catalog: [`TheForge.DESIGN.md` §6](TheForge.DESIGN.md#6-feature-catalog-phased).
