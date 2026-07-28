# CLI banner sword + blue UX heading

**Date:** 2026-07-18  
**Status:** Approved; implementation plan next

## Goal

1. Make the `arcanum chat` startup banner show a sword piercing the large `ARCANUM` title (handle left, tip right).
2. Change default CLI UX heading color from wine/cyan to royal blue; keep error/warning reds.

## Non-goals

- Custom operator `Arcanum:Cli:ThemeColors` overrides (unchanged if set).
- Changing `Error`, highlight green, text, or muted roles.
- Changing hardcoded Spectre `[red]` dots used only for failed/auth server status.
- Redesigning the details table, subtitle, or panel chrome beyond title art + theme defaults.

## Banner

### Behavior

- [`ArcanumBannerRenderer.Render`](../../../src/RetroDownfall.Arcanum.Cli/UX/ArcanumBannerRenderer.cs) stops using Spectre `FigletText` for `"ARCANUM"`.
- It renders a fixed multi-line ASCII art block where:
  - Left side: pommel / grip / crossguard
  - Middle: letter forms for `ARCANUM` with the mid row occupied by a continuous blade (`═` or equivalent box-drawing)
  - Right side: blade continuation ending in a tip (`>`)
- Exact glyph layout is chosen at implementation time; must read as handle-left / tip-right pierce through the word.
- The art is colored with `ctx.Theme.Heading` and remains centered above the existing subtitle and details table inside the same `Panel`.

### Implementation shape

- Prefer a `private const string` (or small helper returning `Markup` / `Text`) of the finished art inside `ArcanumBannerRenderer` (or an adjacent internal static helper in the same UX folder if the string is large).
- No runtime Figlet generation; no line-by-line splice of Figlet output.
- Art must use ASCII / common box-drawing only (no emoji), so it survives typical terminals and `TestConsole` assertions.

### Tests

- Extend [`ArcanumBannerRendererTests`](../../../tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumBannerRendererTests.cs):
  - Keep existing status / MCP assertions.
  - Assert output contains a tip marker and blade character(s) (e.g. `>` and `═`), and still contains `ARCANUM` (case-insensitive as today) or unmistakable letter fragments from the custom art if the word is split across rows.

## Theme (UX blue)

### Defaults

| Role | Light (was → new) | Dark (was → new) |
|------|-------------------|------------------|
| Heading | `#8B1538` → `#1E3A8A` | `#00FFD5` → `#60A5FA` |
| Error | unchanged (`#C41E3A` / `#FF6B6B`) | unchanged |

Files:

- [`ThemeSemanticColors.cs`](../../../src/RetroDownfall.Arcanum.Core/Configuration/ThemeSemanticColors.cs) — Light Heading default
- [`ThemeColors.cs`](../../../src/RetroDownfall.Arcanum.Core/Configuration/ThemeColors.cs) — Dark Heading default
- [`SettingDescriptors.cs`](../../../src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs) — Compendium placeholders for light/dark heading

### Unchanged

- `Text`, `Highlight`, `Muted`, `Error` defaults
- Banner / status bar `[red]` markers paired with `ErrorMarkup` for auth failure and unreachable server

## Docs

- Touch design/README only if they hardcode the old heading hex or describe Figlet specifically for the chat banner; otherwise skip doc churn.

## Success criteria

- Interactive `arcanum chat` banner shows handle-left / tip-right sword through the title.
- Fresh installs / unset theme colors use royal blue headings in light and soft blue in dark; errors remain red.
- Existing banner renderer tests pass with new art assertions.
