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

- Header: model · session continuation line · API status · **Thinking ⠋** (synthetic, while waiting for first token/tool) or Generating…
- Left sessions list (UpdatedAt desc); collapses under 100 cols → Ctrl+O filterable overlay picker
- **Transcript** (follow-tail when at bottom): user/assistant/status/command/error — **not** tool lines
- **Incantations** under Transcript (same width, ~⅓ body height, min frame **3**): CallId-keyed tool invocations; ToolCall creates, ToolResult/ToolError updates; tolerated failures stay here (not Transcript errors)
- Composer (multiline TextView): soft-wrap; grows **1–10** content rows upward into the body, then internal scroll; effective max respects header/footer + **minimum body 6** (Transcript 3 + Incantations 3) at the ≥80×12 floor
- Focus-aware footer hints
- Independent Transcript / Incantations viewport + follow-tail; rebuild restores by entry id / CallId

## Incantations formatting

- Structured ingest only (`CallId`, `ToolName`, args, result, error, state). Formatter never parses `Tool: …` display strings.
- Fail-closed heavy suppression (known heavy names + sensitive keys); summary keys limited to path/file/name/key/command/url/query-style metadata.
- ≤3 content lines per CallId (cell-width wrap); HR separator **between** blocks, outside the 3-line budget.
- Resume: parse tool-interaction fields; unparseable → generic safe summary (never raw text).

## Thinking spinner

- Synthetic display state (not a `SessionLogEntry`). TG UI-loop timer (~100ms); no full log rebuild per tick.
- Start when turn claims generation; stop on first non-empty assistant token or tool event; clear on result/error/cancel/finally. `SessionBound` alone does not clear. Suppress only the exact redundant server generating status string.
- Must not move a user-scrolled Transcript.

## Sessions

- Startup: refresh recent active sessions; restore `CliSessionManager` last id when valid; stale/missing/archived → New Session + non-fatal hint (**does not** `ClearSession`)
- Resume is transactional: load ≤200 recent entries (API descending → chronological); failure keeps prior session/transcript; empty → empty-state message; `EntryCount > loaded` → `Older messages not loaded`
- Full-history paging and archived browsing: **deferred**
- New Session / switch: confirm discard if composer has text (Enter discard / Esc cancel)
- On `sessionBound`: persist via `CliSessionManager`, refresh sidebar highlight

## Streaming

`StreamingUiCoalescer`: flush ~50ms, on newline, before tool/status/error, final, cancel, dispose. Never drop final partial. Never mutate TG off main loop.

## Keyboard

F1 help · Ctrl+K palette · Ctrl+O sessions · Ctrl+N new · Ctrl+R/F5 refresh · **Tab** / **Shift+Tab** cycle focus · **Enter** newline (composer) · **Ctrl+Enter** send · Esc overlay/composer · Ctrl+C cancel/clear/quit-hint · Ctrl+Q quit

**Tab cycle (single routing path):** wide `Composer → Sessions → Transcript → Incantations`; narrow `Composer → Transcript → Incantations`; overlay open ⇒ Tab **NoOp**. After each transition, `FocusRegion` matches the TG focused control. `FocusInput` does not hide overlays; use `CloseOverlay` / `CloseOverlayAndFocusInput`.

**Scroll:** Composer arrows = caret; Transcript / Incantations ↑↓ PgUp/PgDn Home/End scroll that pane only; Composer PgUp/PgDn may scroll Transcript. Sessions keep ↑↓/jk nav.

Composer send ownership: **Ctrl+Enter** maps to `Send` via composer `KeyDown`. Bare Enter falls through to TextView (`EnterKeyAddsLine` stays **true** — required for `WordWrap`; Terminal.Gui clears `Multiline`/`WordWrap` when it is set false). `TextView.Accepting` is a no-op so it cannot double-submit.

## Slash (v2)

Allow: `/help`, `/keys`, `/exit`, `/quit`, `/clear`, `/status`, `/doctor` (compact), `/mana`, `/tools`, `/model list`, `/provider list`, `/mcp`, `/arsenal`, `/campaign list`, `/session list|new|resume <id>`, `/attach <path>`, `/spell list`, `/ward list`.

Deny: `/serve`, all `/daemon …`, `/key …`.

`/attach <path>` and inline `@path` stage text attachments or Scrying image foci for the next turn (same semantics as `arcanum chat`; ephemeral foci, never Grimoire-persisted).

`/session resume <id>` loads bounded transcript via `SessionWorkspaceService` (no inference until next send).

## AOT

Package: Terminal.Gui **2.4.17** on Cli only. Gate: `./scripts/verify-aot-il-warnings.sh`. Method-level suppress only on `CommandCenterApp.CreateAndInit`.

**Deferred:** Spectre Command Center–lite fallback when the AOT/TG gate fails (same entry rules; no leftover spike mode). Until then, TG init / size-gate failure exits **1** with a message pointing at `arcanum chat` or another direct command.
