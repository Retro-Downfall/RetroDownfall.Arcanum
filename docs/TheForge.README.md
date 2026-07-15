# The Forge

<!-- WAVE1 notes: API key paste prompt is deferred until MainWindow is ready; markdown image SSRF uses ConnectCallback + no auto-redirect; response compression retains Lexicon / RAG / Saga. -->

**The Forge** is the Inference IDE for Arcanum — a cross-platform Avalonia desktop application
(`RetroDownfall.TheForge.Ux`) that provides a full GUI over Arcanum's HTTP API: browsing and managing
campaigns, editing spells (including the **Spell Metadata Designer** for `SPELL.json` and **The
Mirror** for versions), chatting in sessions, running ephemeral **Proving Grounds** Trials,
orchestrating apprentices, approving wards, tracking budget, and more. See
[`docs/TheForge.DESIGN.md`](TheForge.DESIGN.md) for the full design document, naming metaphor, and
phased feature catalog.

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

`layoutState` holds a versioned JSON dock layout (`TheForgeDockLayoutDto`) when the operator has
rearranged tool windows; `null` means use the default shell layout.

## Window layout

Tool windows (Atelier, Gatehouse, Treasury, Arsenal, War Table, Output, Logs, Hearth) can be moved
between the left, right, and bottom dock regions via each tool’s header **context menu** (Move Left /
Move Right / Move Bottom / Hide) or restored from **View**. **View → Reset Window Layout** restores
the default shell and persists it. Layout is stored in `~/.config/arcanum/forge.json` as `layoutState`
and restored on next launch. The Workbench stays the central document host; The Anvil stays a fixed
status bar. Drag-and-drop docking is not required for this release — menu/context movement is the
supported path.

## Whispers (toast notifications)

**Whispers** is The Forge's non-blocking notification surface — short status toasts in the top-right
of the main window, above the dock. `IWhispersService` is registered as a singleton; the shell hosts
`WhispersHostView` as an overlay on `MainWindow`. Info/Success/Warning auto-dismiss after five seconds;
Error stays until dismissed manually. At most five active whispers are shown (oldest non-error dropped
first when at cap). Major actions in the Spell editor, Scriptorium, Archive, Reliquary, Workspace
Explorer, and Gatehouse call `Show` with short success/error messages; detailed server error text
stays on the Foundry Floor (and inline `LastError` / `StatusText` where those panels already use them).

## The Hearth terminal

The Hearth is The Forge's dockable terminal panel (View → **The Hearth**, default bottom dock). It
runs local shell commands from a working directory that starts at your user home profile. Use the
**Home** button to reset the working directory; use built-in `cd` (including `~`) to move around.

Initial Git integration is available through The Hearth terminal: use `git status`, `git diff`, etc.
directly until the dedicated Git UI (**The Ledger**) arrives.

The Hearth supports command output streaming, `cd`, Stop, and Clear. It is not a full
pseudo-terminal yet, so fully interactive terminal apps may not work correctly. Command execution is
local desktop functionality — it does not call the Arcanum API or go through Sanctum/Wards.

## Creating spells, prompts, and sessions

The Atelier supports creating spells, prompts, and sessions from campaign nodes. Campaign spell
creation writes to the campaign workspace. Top-level spell creation creates a workspace spell after
the operator chooses a workspace; built-in spells remain read-only. Global prompts can be created
from the **Global Prompts** root, and sessions can be created without a campaign from the
**Sessions** root. Right-click a campaign node (or the Global Spells / Global Prompts / Sessions
root) and choose the New command; fill the dialog and confirm. On success the relevant branch
refreshes and the new artifact opens in the Workbench — a spell in the Spell editor, a prompt in
The Scriptorium, and a session in The Tome. Creation success and failure are logged to the Foundry
Floor and shown as inline status on the node.

### Campaign management

Right-click the **Campaigns** root for **New Campaign** (name, path, type, description) or
**Refresh**. Right-click a campaign for **Edit / Properties**, **Delete Campaign**, **Export
Campaign**, and **Import into Campaign**, plus New Spell / Prompt / Session. Delete **unregisters**
the campaign from Arcanum only — disk files remain; the confirmation says so and defaults to Cancel.
Import targets an **existing** campaign (`POST /api/campaigns/{id}/import` with merge or replace):
register the campaign first, then import a previously exported JSON bundle. Export writes a
`CampaignExportDto` JSON file (spell payloads use `spellJson`; legacy `skillJson` is accepted when
reading older bundles). Path validation errors (`Campaign.InvalidPath`, `Campaign.PathNotAllowed`)
appear as short Whispers and detailed Foundry Floor lines. A full campaign Settings editor and
advanced import conflict wizards are not built yet.

## The Scriptorium

Prompts open in **The Scriptorium**, the Workbench prompt editor. Edit the template (AvaloniaEdit)
and the metadata supported by the prompt API (description, version, tags, model, provider,
Temperature / TopP / MaxOutputTokens, parameter-schema JSON, and default-parameters JSON), then
**Save**. Blank Temperature / TopP / MaxOutputTokens preserve the server value; empty or whitespace
JSON editors save as `{}`. Invalid JSON blocks Save with `LastError` and does not call the API.
**Render** previews the template with parameters you supply as `key=value` lines (one per
line; split on the first `=`). **Test** assembles the full system prompt with default parameters at
no LLM cost. **Run** executes the prompt via the live `execute-stream` (requires a non-empty user
message) and streams tokens into the results pane; optional run-only overrides (model, sampling,
seed, stop sequences, response format, penalties) are on the Run tab and are not saved. **Stop**
cancels the run. **Clone**, **Export**, **Import**, and **Delete** use existing Arcanum prompt routes
(export/import are persisted server artifacts; file-dialog cancel is a silent no-op). Import surfaces
`Prompt.DuplicateVersion` clearly. **Versions** lists prompt
versions by name and opens a selected version by its `Id`. Full prompt-version management beyond
list/open (diff/activate flows) and advanced import conflict wizards are deferred.

## Spell editor lifecycle

Workspace and campaign spells open with an explicit `?workspace=` context (DocumentKey identity is
normalized separately from the API workspace value). Same spell name in two workspaces opens two tabs
(with workspace path tooltips); reopening the same workspace focuses the existing tab. **Validate**,
**Clone**, **Export**, **Import**, and **Delete** call the matching Arcanum spell routes. Built-in spells are
read-only for Save/Delete; Clone-to-workspace, Export, and Import remain available. Export uses the server
export route (persisted artifact). Import reads a JSON file and posts `SpellImportRequest`;
`Spell.NameCollision` surfaces clearly. Successful delete closes the Workbench tab.

### Spell Metadata Designer (SPELL.json)

The Spell editor **SPELL.json** tab is a nested pair:

- **Spell Metadata Designer** — visual fields for `version`, read-only `activeVersion`, dependencies,
  declared tools, and input/output schema JSON. Add/remove dependencies and tools from the current
  catalog when available (manual entry always allowed). Advisory warnings appear when a dependency or
  tool is missing from the catalog; Arcanum remains authoritative on save.
- **Raw SPELL.json** — AvalonEdit for advanced edits of the same known metadata.

Save persists `Version`, `InputSchema`, `OutputSchema`, `DeclaredTools`, and `Dependencies` through
`UpdateSpellRequest` (plus the usual body/description/tags/tools fields). Built-in spells keep both
surfaces read-only. **Honest limit:** the raw editor round-trips **known** sidecar fields only —
unknown JSON properties are not preserved through the update API. Canonical on-disk sidecar name is
`SPELL.json` (Arcanum may still **read** legacy `SKILL.json`; new writes always use `SPELL.json`).

**Create Trial** opens The Proving Grounds (below) with this spell pre-selected when the singleton
tab is clean; a dirty Trial draft is not overwritten.

### The Mirror

**The Mirror** (Spell editor tab) lists spell versions, fetches a selected version’s markdown body,
and shows a local LCS line diff against the **persisted active** SPELL.md body (not unsaved editor
edits — a passive warning appears when the editor is dirty). **Activate**, **Create Version**, and
**Update Version** are available for workspace spells (Create/Update send markdown body only);
built-in mutation stays disabled. Activate may note a preserved `PreviousVersion` sidecar. Short
Whispers toasts and Foundry Floor detail follow other Spell editor actions.

## The Proving Grounds

**The Proving Grounds** is a **singleton** Workbench document (menu **Trial → Proving Grounds**).
Build an ephemeral **Trial** against a Spell, Prompt, or Apprentice Goal; add key/value variables and
**Inquisitors** (Regex, JsonSchema, Semantic with bool `expectedAnswer`); **Run** calls
`POST /api/proving-grounds/trials/run`. Results show pass/fail, output, per-Inquisitor verdicts, and
usage. Whispers: Success / Warning / Error; Foundry Floor logs start and summary. Shortcuts: Spell
editor **Create Trial**, Scriptorium **Open in Proving Grounds**. Prefill is refused when the tab
already has a dirty draft (Reset requires confirmation). There is **no** Trial persistence or library
UI — that remains deferred.

## The Arsenal

**The Arsenal** (View → **The Arsenal**, default right dock) is the operational panel for inference
infrastructure — a tabbed panel whose tabs each back an Arcanum HTTP route. The **MCP Servers** tab
lists configured MCP servers (`GET /api/mcp`: state, transport, exposed tools) and runs **Start /
Stop / Restart** on the selected server plus **Reload** (`POST /api/mcp/{name}/start|stop|restart`,
`POST /api/mcp/reload`). The **Scrying Pool** tab lists built-in tools from
`POST /api/intelligence/arsenal` and invokes the selected one with JSON arguments via
`POST /api/tools/invoke` — external MCP direct invocation is not exposed by Arcanum yet, and the
panel says so. The **Models & Providers** and **Reliquary** tabs are described below. Every action
requires a running `arcanum serve` and surfaces busy/status/error inline (failures also go to the
Foundry Floor). Short Whispers toasts cover major success/failure outcomes; they do not replace
Floor detail.

## Models and Providers

The **Models & Providers** tab (inside The Arsenal) shows read-only lists of configured models
(`GET /api/models`) and providers (`GET /api/providers`; endpoint and API key are shown redacted by
the API), plus a provider connectivity test (`POST /api/providers/test`; only OpenAI-compatible
endpoints; credentials you enter are not stored). Provider configuration editing is not part of this
milestone.

## The Reliquary

The **Reliquary** tab (inside The Arsenal) manages local LlamaCpp: cached GGUF models and
llama-server status (`GET /api/llama/models`, `GET /api/llama/servers`), **Start / Stop / Warmup**
on the selected model, and **Pull** of a GGUF from a URL — download progress streams (NDJSON) into
the panel and the Foundry Floor, and **Cancel** stops an in-flight pull. Advanced cache pruning and
model-metadata editing are not exposed.

## The Treasury

**The Treasury** (View → **The Treasury**, default right dock) is a read-only budget dashboard over
`GET /api/budget`: enabled/disabled, daily limit, today's spend, remaining, spent percent (a Mana
bar), and alert threshold. When enforcement is disabled the banner names `Arcanum:Budget:Enabled` and
offers **Copy setting paths**. Budget/pricing editing is not part of this milestone.

## Lore Browser

**Lore Browser** (View → **Lore Browser**; hidden by default) edits Grimoire Lore key/value pairs
through `/api/lore/*` (list / save / delete). API failures surface inline and on the Foundry Floor.

## The Archive

**The Archive** (View → **The Archive**; hidden by default) browses Saga memories: list, stats,
delete-one, and Saga Divination. When Saga Divination is disabled the banner names
`Arcanum:Embeddings:Enabled` and `Arcanum:Embeddings:SagaEnabled` with **Copy setting paths**.
**Delete all Saga memories** is available behind an explicit confirmation dialog
(`DELETE /api/saga?confirm=true`); cancel is a no-op.

## Divination

**Divination** (View → **Divination**; hidden by default) runs semantic search over sessions,
workspace files, and Saga. Each tab names the exact embedding paths when disabled (Sessions:
`Arcanum:Embeddings:Enabled` + `Arcanum:Embeddings:SessionSearchEnabled`; Workspace:
`Arcanum:Embeddings:Enabled` + `Arcanum:Embeddings:CodebaseRetrievalEnabled`; Saga:
`Arcanum:Embeddings:Enabled` + `Arcanum:Embeddings:SagaEnabled`) and offers **Copy setting paths**
per tab. Session hits open The Tome. The Forge never embeds or searches client-side.

## Workspace Explorer

**Workspace Explorer** (View → **Workspace Explorer**; hidden by default; also opened from an
Atelier workspace node) browses registered workspaces and files through Arcanum's workspace file
APIs — never local disk. **Index** triggers the **server's** workspace indexing endpoint (not
client-side indexing). When indexing/Divination is disabled the banner names
`Arcanum:Embeddings:Enabled` and `Arcanum:Embeddings:CodebaseRetrievalEnabled`. Optional Save /
Delete / Create directory are server-gated by `Arcanum:Workspaces:EnableFileWrite` and may return
`403 Workspace.FileWriteDisabled`; the write banner names that path. Each disabled banner offers
**Copy setting paths**. Deletes ask for confirmation first.

## Session memory controls

In **The Tome**, Refresh / Pin / Unpin / Delete entry / Compact manage session memory when
`Arcanum:Sessions:AllowMemoryManagement` is enabled. When disabled, the banner names that path and
offers **Copy setting paths**; the server returns `Session.MemoryManagementDisabled` and those
controls turn off (chat still works). Pinning past the server pin limit surfaces
`Session.TooManyPinned`. Divination focuses the Divination dock tool.

## The Codex

**The Codex** is a Workbench document for `CODEX.md`. Open a campaign Codex from the Atelier campaign
tree, or View → **The Codex** for the Grimoire-global Codex. Load / save / delete go through
`/api/campaigns/{id}/codex` or `/api/codex` — no workspace disk reads from The Forge.

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

`App.axaml` sets `Name="The Forge"` so the macOS menu bar shows **The Forge** (not “Avalonia Application”) during `dotnet run`. Bundled `.app` builds should also set matching `CFBundleName` / `CFBundleDisplayName` in `Info.plist`.

## macOS Apple Silicon release

The Forge ships as a signed, notarized, stapled `the-forge-osx-arm64.dmg` containing `The Forge.app` (self-contained Avalonia on .NET 10 — **not** Native AOT). Packaging defaults to **multi-file** publish so native libraries can be codesigned individually. See [`RELEASE-MACOS.md`](RELEASE-MACOS.md) for the manual workflow, required **Developer ID Application** secrets, SemVer vs `CFBundle*` versioning, and draft-release steps.

## Status

The Forge is in **beta** (`0.1.0-beta`, inherited from [`Directory.Build.props`](../Directory.Build.props)). **Milestones A–H are complete**, plus **H1/H2 (The
Illumination)**, and Phases **5–7 polish** (Spell Metadata Designer, Proving Grounds UI, campaign
CRUD + import/export): Avalonia shell, Atelier, Spell editor, Tome, War Table,
Gatehouse, Anvil, Visual Studio 2026 Fluent-inspired theming (Cascadia Mono / Segoe UI Variable,
Dark/Light resource dictionaries, ManaBar, Icons, `forge.json` `Theme` swap), Atelier artifact
creation and **campaign New / Edit / Delete (unregister) / Export / Import**, The Scriptorium prompt
editor, Milestone G operational panels — The Arsenal (MCP servers, Scrying Pool, Models & Providers,
Reliquary) and The Treasury — **The Hearth** local terminal, **Milestone H Context and Memory** (Lore
Browser, The Archive, Divination, Workspace Explorer, Tome session memory controls, The Codex),
**The Illumination** Markdig-backed markdown preview (Spell editor / The Codex Source·Split·Preview,
Workspace Explorer Open Preview, standalone markdown tabs), **The Mirror** and **Spell Metadata
Designer** in the Spell editor, and **The Proving Grounds** singleton Trial Workbench tab. See
[`docs/TheForge.DESIGN.md`](TheForge.DESIGN.md) §5.7–§5.18 and §6.

### Markdown preview (The Illumination)

- In the **Spell editor** and **The Codex**, use **Source** / **Split** / **The Illumination** to
  toggle AvaloniaEdit/TextBox editing vs rendered preview (defaults: Source). Standalone markdown
  tabs default to Preview.
- Optional **Remote images** toggle (default off). When on, http(s) images load through a dedicated
  SSRF-safe loader (no Arcanum credentials): `AllowAutoRedirect` is off, redirects are followed
  manually with per-hop host checks, and TCP connect uses DNS-rebind IP pinning (`ConnectCallback`).
  Localhost/private/metadata hosts are blocked. Relative workspace images stay placeholders
  (workspace file API is text-only). Data URIs (raster MIME) are allowed under size caps. SVG is
  never rendered.
- **Sync scroll** (Spell + standalone markdown; AvaloniaEdit): approximate caret ↔ preview block
  sync in Split. The Codex exposes the same toggle (defaults off) because its TextBox source editor
  only supports best-effort sync.
- Fenced code uses ColorCode highlighting with Forge theme brushes. Footnotes render. Math and
  Mermaid appear as labeled source blocks (no equation engine / no Mermaid graphs / no WebView).
- Links open in the system browser **only on click**, and only for `http` / `https` / `mailto`.
- Raw HTML is omitted (`[HTML omitted]`), never executed. Large documents may show **Preview
  truncated** (256 KiB cap).
- In **Workspace Explorer**, select a `.md` / `.markdown` file, wait for contents to load, then
  **Open Preview** to open a Workbench tab (preview-first).
- The Scriptorium’s **Render** button remains server-side template render — not markdown preview.

**Known gaps (honest UI):** The Arsenal exposes only built-in tool invocation (`POST /api/tools/invoke`); external MCP direct invocation is not exposed by Arcanum yet. No provider/config editing, budget/pricing editing, advanced Llama cache pruning, or model-metadata editing; no model/session token/cost breakdown. Campaign **New / Edit / Delete (unregister only) / Export / Import** and **New Spell / New Prompt / New Session** (plus top-level New Workspace Spell / New Prompt / New Session) are available. **The Mirror** (spell version list / fetch body / LCS compare / activate / create / update) and the **Spell Metadata Designer** (visual SPELL.json: version, dependencies, declared tools, schemas + raw editor) ship in the Spell editor — raw round-trip covers known metadata fields only; unknown JSON properties are not preserved through `UpdateSpellRequest`. **The Proving Grounds** is a singleton Workbench tab (Trial → Proving Grounds; Spell **Create Trial**; Scriptorium **Open in Proving Grounds**) for ephemeral Trials — Spell / Prompt / ApprenticeGoal targets with Regex, JsonSchema, and Semantic Inquisitors; no persistent Trial libraries yet. Full prompt-version management beyond list/open, advanced import conflict wizards, full campaign Settings editing, and dedicated Git UI (The Ledger) are not built yet — use The Hearth for `git` commands. No client-side embeddings; disabled banners name exact `Arcanum:*` paths with **Copy setting paths** (Open Compendium deep-link deferred; Guardrails panel deferred). Advanced file diff/merge is not exposed. The Illumination: relative workspace images unresolved until a binary API; Mermaid graphs and native math deferred; Codex scroll sync incomplete; SVG/intranet remotes blocked. A true PTY Hearth remains a gap. Connect via **View → Connect to Arcanum** or the Anvil connection chip; disconnect from the View menu. Tool windows rearrange via context menu / View menu; OS floating windows are not implemented yet.

