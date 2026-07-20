# CLI Command Center (Terminal.Gui) — design notes

Supplement to `docs/Arcanum.DESIGN.md` §4.4 / §9 / §16.6. Locked product decisions for Command Center **v2** (2026-07-18 lineage).

## Entry

| Condition | Behavior | Exit |
|-----------|----------|------|
| `args.Length == 0` + interactive + `ARCANUM_NO_COMMAND_CENTER` unset | `ICommandCenterHost.RunAsync` | host |
| empty + non-interactive | usage/help | **0** |
| empty + `ARCANUM_NO_COMMAND_CENTER=1` | usage/help | **0** |
| too small after TG Init (≥80×12 floor; docs recommend ≥80×24) | message | **1** |
| TG init failure | message | **1** |
| `/exit` / `/quit` / Ctrl+Q (confirmed) | clean leave | **0** |

`NO_COLOR` / `ARCANUM_NO_COLOR` → Command Center theme only (not a TUI blocker).
`ARCANUM_NO_AUTO_SERVE=1` → skip auto-serve only.

## Hard rules while TUI active

No Spectre / `AnsiConsole` / `Console.WriteLine` / CAF recursion / `ChatCommand` instantiation.
`ShellCommandDispatcher` stays UI-agnostic (state + UI channel only).
All view mutations → Host / Window via `IApplication.Invoke`.

## Layout (v2)

- Header: model · session continuation line · API status · Generating…
- Left sessions list (UpdatedAt desc); collapses under 100 cols → Ctrl+O filterable overlay picker
- Center transcript (follow-tail when at bottom)
- Composer + focus-aware footer hints

## Sessions

- Startup: refresh recent active sessions; restore `CliSessionManager` last id when valid; stale/missing/archived → New Session + non-fatal hint (**does not** `ClearSession`)
- Resume is transactional: load ≤200 recent entries (API descending → chronological); failure keeps prior session/transcript; empty → empty-state message; `EntryCount > loaded` → `Older messages not loaded`
- Full-history paging and archived browsing: **deferred**
- New Session / switch: confirm discard if composer has text (Enter discard / Esc cancel)
- On `sessionBound`: persist via `CliSessionManager`, refresh sidebar highlight

## Streaming

`StreamingUiCoalescer`: flush ~50ms, on newline, before tool/status/error, final, cancel, dispose. Never drop final partial. Never mutate TG off main loop.

## Keyboard

F1 help · Ctrl+K palette · Ctrl+O sessions · Ctrl+N new · Ctrl+R/F5 refresh · Tab focus · Esc overlay/composer · Ctrl+C cancel/clear/quit-hint · Ctrl+Q quit

## Slash (v2)

Allow: `/help`, `/keys`, `/exit`, `/quit`, `/clear`, `/status`, `/doctor` (compact), `/mana`, `/tools`, `/model list`, `/provider list`, `/mcp`, `/arsenal`, `/campaign list`, `/session list|new|resume <id>`, `/attach <path>`, `/spell list`, `/ward list`.

Deny: `/serve`, all `/daemon …`, `/key …`.

`/attach <path>` and inline `@path` stage text attachments or Scrying image foci for the next turn (same semantics as `arcanum chat`; ephemeral foci, never Grimoire-persisted).

`/session resume <id>` loads bounded transcript via `SessionWorkspaceService` (no inference until next send).

## AOT

Package: Terminal.Gui **2.4.17** on Cli only. Gate: `./scripts/verify-aot-il-warnings.sh`. Method-level suppress only on `CommandCenterApp.CreateAndInit`.

**Deferred:** Spectre Command Center–lite fallback when the AOT/TG gate fails (same entry rules; no leftover spike mode). Until then, TG init / size-gate failure exits **1** with a message pointing at `arcanum chat` or another direct command.
