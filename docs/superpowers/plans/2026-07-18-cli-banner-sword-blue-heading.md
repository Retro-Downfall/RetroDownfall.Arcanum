# CLI Banner Sword + Blue UX Heading Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Figlet chat banner title with handle-left / tip-right sword-through-`ARCANUM` ASCII art, and change default CLI Heading colors to royal/soft blue while leaving Error reds alone.

**Architecture:** Banner title becomes a fixed multi-line string rendered via Spectre `Markup` + `Align.Center` inside the existing `Panel`. Theme defaults live in Core `ThemeSemanticColors` / `ThemeColors`; Compendium placeholders stay in sync. No Figlet at runtime.

**Tech Stack:** .NET, Spectre.Console, xUnit, Spectre.Console.Testing (`TestConsole`)

**Spec:** [docs/superpowers/specs/2026-07-18-cli-banner-sword-blue-heading-design.md](../specs/2026-07-18-cli-banner-sword-blue-heading-design.md)

## Global Constraints

- Sword orientation: handle left, tip right, blade through the mid row of the title art
- Art: ASCII / common box-drawing only (no emoji)
- No runtime Figlet; no Figlet string splice
- Light Heading default `#1E3A8A`; Dark Heading default `#60A5FA`
- Do not change `Error`, `Text`, `Highlight`, or `Muted` defaults
- Do not change hardcoded `[red]` status dots used with auth/unreachable failures
- Operator-configured `Arcanum:Cli:ThemeColors` overrides remain respected (defaults only)

## File structure

| File | Responsibility |
|------|----------------|
| `src/RetroDownfall.Arcanum.Cli/UX/ArcanumBannerRenderer.cs` | Swap Figlet for sword title art; keep panel/details |
| `tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumBannerRendererTests.cs` | Assert blade/tip + existing status/MCP cases |
| `src/RetroDownfall.Arcanum.Core/Configuration/ThemeSemanticColors.cs` | Light Heading default |
| `src/RetroDownfall.Arcanum.Core/Configuration/ThemeColors.cs` | Dark Heading default |
| `tests/RetroDownfall.Arcanum.Tests/Cli/ThemeDefaultColorsTests.cs` | Lock new Heading defaults |
| `src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs` | Compendium color placeholders |
| `docs/Arcanum.DESIGN.md` | Replace “Figlet banner” wording for chat |

---

### Task 1: Sword-through banner title

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Cli/UX/ArcanumBannerRenderer.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumBannerRendererTests.cs`
- Modify: `docs/Arcanum.DESIGN.md` (Figlet mention only)

**Interfaces:**
- Consumes: `BannerContext`, `IThemePalette.HeadingBoldMarkup`, existing `Render` / `BuildDetailsTable`
- Produces: `ArcanumBannerRenderer.Render` still returns `IRenderable`; title is sword ASCII (no public API change)

- [ ] **Step 1: Write the failing sword assertion**

Add this fact to `tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumBannerRendererTests.cs` (keep existing tests unchanged):

```csharp
[Fact]
public void Render_includes_sword_through_title()
{
    TestConsole console = new();

    console.Write(ArcanumBannerRenderer.Render(CreateContext(ServeLaunchStatus.AlreadyRunning)));

    Assert.Contains('═', console.Output);
    Assert.Contains('>', console.Output);
    Assert.Contains('╪', console.Output);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run (outside sandbox per project rule for .NET):

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ArcanumBannerRendererTests.Render_includes_sword_through_title"
```

Expected: FAIL — output has Figlet letters, not `╪` / continuous blade.

- [ ] **Step 3: Replace Figlet with sword title art**

In `src/RetroDownfall.Arcanum.Cli/UX/ArcanumBannerRenderer.cs`:

1. Add this constant next to `TipText` (raw string; backslashes literal):

```csharp
private const string TitleArt =
    """
     _    ____   ____    _    _   _ _   _ __  __
    / \  |  _ \ / ___|  / \  | \ | | | | |  \/  |
    ○═╪/═_═\═|═|_)═|═|═════/═_═\═|══\|═|═|═|═|═|\/|═|══>
    / ___ \|  _ <| |___ / ___ \| |\  | |_| | |  | |
    /_/   \_\_| \_\\____/_/   \_\_| \_|\___/|_|  |_|
    """;
```

2. Replace the `FigletText` construction with centered heading markup:

```csharp
Markup title = new(
    ctx.Theme.HeadingBoldMarkup(Markup.Escape(TitleArt.Trim('\r', '\n'))));

IRenderable centeredTitle = Align.Center(title);
```

3. Use `centeredTitle` in the `Rows` content instead of `title` (Figlet):

```csharp
Rows content = new(centeredTitle, subtitle, table);
```

4. Remove unused Figlet usage (no `FigletText` / `FigletFont` references left in this file).

If visual balance looks off when manually previewed, adjust only whitespace / blade length in `TitleArt` — keep handle left (`○═╪`), tip right (`>`), and mid-row blade (`═`).

- [ ] **Step 4: Run banner tests**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ArcanumBannerRendererTests"
```

Expected: all PASS (including sword + status + MCP facts).

- [ ] **Step 5: Update DESIGN Figlet wording**

In `docs/Arcanum.DESIGN.md`, find the `chat` row text that says `Figlet banner (\`ArcanumBannerRenderer\`)` and change it to:

`sword-through-title banner (\`ArcanumBannerRenderer\`)`

Do not rewrite the rest of that cell.

- [ ] **Step 6: Commit**

```bash
git add \
  src/RetroDownfall.Arcanum.Cli/UX/ArcanumBannerRenderer.cs \
  tests/RetroDownfall.Arcanum.Tests/Cli/ArcanumBannerRendererTests.cs \
  docs/Arcanum.DESIGN.md
git commit -m "$(cat <<'EOF'
Add sword-through ARCANUM art to the chat startup banner.

EOF
)"
```

---

### Task 2: Royal blue Heading defaults

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Configuration/ThemeSemanticColors.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Configuration/ThemeColors.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Cli/ThemeDefaultColorsTests.cs`
- Modify: `src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs`

**Interfaces:**
- Consumes: existing `ThemeSemanticColors` / `ThemeColors` records
- Produces: Light `Heading` `#1E3A8A`, Dark `Heading` `#60A5FA`; Error defaults unchanged

- [ ] **Step 1: Write failing default-color tests**

Create `tests/RetroDownfall.Arcanum.Tests/Cli/ThemeDefaultColorsTests.cs`:

```csharp
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ThemeDefaultColorsTests
{
    [Fact]
    public void Light_heading_default_is_royal_blue()
    {
        Assert.Equal("#1E3A8A", new ThemeSemanticColors().Heading);
    }

    [Fact]
    public void Dark_heading_default_is_soft_blue()
    {
        Assert.Equal("#60A5FA", new ThemeColors().Dark.Heading);
    }

    [Fact]
    public void Error_defaults_remain_red()
    {
        Assert.Equal("#C41E3A", new ThemeSemanticColors().Error);
        Assert.Equal("#FF6B6B", new ThemeColors().Dark.Error);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ThemeDefaultColorsTests"
```

Expected: FAIL on heading asserts (still `#8B1538` / `#00FFD5`); error asserts may already pass.

- [ ] **Step 3: Update Core defaults**

In `src/RetroDownfall.Arcanum.Core/Configuration/ThemeSemanticColors.cs`:

```csharp
public string Heading { get; init; } = "#1E3A8A";
```

In `src/RetroDownfall.Arcanum.Core/Configuration/ThemeColors.cs` Dark initializer:

```csharp
Heading = "#60A5FA",
```

Leave `Error`, `Text`, `Highlight`, and `Muted` exactly as they are.

- [ ] **Step 4: Sync Compendium placeholders**

In `src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs`:

- `cli.themeColors.light.heading` Placeholder: `"#1E3A8A"`
- `cli.themeColors.dark.heading` Placeholder: `"#60A5FA"`

- [ ] **Step 5: Run theme + palette tests**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ThemeDefaultColorsTests|FullyQualifiedName~ConfiguredThemePaletteTests|FullyQualifiedName~ArcanumBannerRendererTests"
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add \
  src/RetroDownfall.Arcanum.Core/Configuration/ThemeSemanticColors.cs \
  src/RetroDownfall.Arcanum.Core/Configuration/ThemeColors.cs \
  tests/RetroDownfall.Arcanum.Tests/Cli/ThemeDefaultColorsTests.cs \
  src/RetroDownfall.Compendium.Ux/Models/SettingDescriptors.cs
git commit -m "$(cat <<'EOF'
Use royal blue defaults for CLI UX headings.

EOF
)"
```

---

### Task 3: Smoke verification

**Files:** none new (verification only)

- [ ] **Step 1: Run focused regression suite**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~ArcanumBannerRendererTests|FullyQualifiedName~ThemeDefaultColorsTests|FullyQualifiedName~ConfiguredThemePaletteTests"
```

Expected: all PASS.

- [ ] **Step 2: Optional visual check**

If a local Arcanum build is available:

```bash
dotnet run --project src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -- chat --help
```

Or start `arcanum chat` briefly and confirm: blue heading chrome, sword handle left / tip right, red only on real errors.

No commit unless Step 1 required a fix (then commit the fix with a clear message).

---

## Spec coverage check

| Spec requirement | Task |
|------------------|------|
| Sword handle left / tip right through title | Task 1 |
| No Figlet / fixed ASCII art | Task 1 |
| Banner tests for blade/tip | Task 1 |
| Light Heading `#1E3A8A` | Task 2 |
| Dark Heading `#60A5FA` | Task 2 |
| Error reds unchanged | Task 2 (explicit asserts) |
| Compendium placeholders | Task 2 |
| DESIGN Figlet wording | Task 1 |
| Hardcoded `[red]` status dots untouched | (non-goal; no task edits those lines) |
