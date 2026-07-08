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

Every Arcanum `/api/*` route requires the `X-Arcanum-Key` header. The Forge resolves a key in this
order:

1. A previously-resolved key cached in `~/.config/arcanum/forge.json`.
2. Shelling out to `arcanum key show` (the Arcanum CLI writes the raw key to **stderr**).
3. Otherwise, The Forge prompts you to paste one manually.

Once resolved, the key is cached back into `forge.json` (file mode `0600` on Unix) so subsequent
launches don't need to re-resolve it. If you rotate the key, delete `apiKey` from `forge.json` (or
delete the file entirely) to force re-resolution.

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

The Forge is in **alpha** (`0.1.0-alpha`, overriding the solution-wide `0.1.0-beta` in
`Directory.Build.props`). **Milestone C is complete** (Phases 1–6): the solution scaffold, Core
models, the full HTTP/SSE/NDJSON client stack, DI wiring, the Phase 3 Avalonia shell, the Phase 4
live Atelier tree, the Phase 5 Spell editor, and the Phase 6 Tome are in place. The Atelier lists
campaign, workspace, global spell, and recent-session roots with lazy campaign children and
spell/session/prompt Workbench navigation. The Spell editor loads spell detail and versions, edits
SPELL.md/SKILL.json, saves, casts dry-run previews, estimates Mana, activates versions, and opens a
Session tab on `sessionBound`. The Tome streams NDJSON ping-stream chat (all `IntelligenceEvent`
types, tool cards, mana bar, manual entry, fork/export) and observes live session SSE. War Table,
Gatehouse, Anvil aggregation, and theme polish remain in later milestones. See
[`docs/TheForge.DESIGN.md`](TheForge.DESIGN.md) §6 for the full milestone breakdown.
