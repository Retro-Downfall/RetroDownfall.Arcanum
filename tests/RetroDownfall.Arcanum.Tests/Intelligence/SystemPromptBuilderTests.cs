using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Infrastructure.Workspace;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class SystemPromptBuilderTests
{

    [Fact]
    public void Build_MinimalRequest_IncludesPersonaAndNonePlaceholders()
    {

        string prompt = SystemPromptBuilder.Build(new PingRequest("hello"), codexContent: null);

        Assert.Contains("autonomous developer assistant", prompt, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("## DATA", prompt, StringComparison.Ordinal);

        Assert.Contains("## CONTEXT", prompt, StringComparison.Ordinal);

        Assert.Contains("## INSTRUCTIONS", prompt, StringComparison.Ordinal);

        Assert.Contains("[None]", prompt, StringComparison.Ordinal);

    }

    [Fact]
    public void Build_WithAttachedFiles_IncludesFileBlocks()
    {

        List<AttachedFileDto> files =
        [
            new("src/App.cs", "class App {}"),
            new("notes.txt", "remember this"),
        ];

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello"),
            codexContent: null,
            attachedFiles: files);

        Assert.Contains("### Attached Files for this Turn", prompt, StringComparison.Ordinal);

        Assert.Contains("#### src/App.cs", prompt, StringComparison.Ordinal);

        Assert.Contains("class App {}", prompt, StringComparison.Ordinal);

        Assert.Contains("#### notes.txt", prompt, StringComparison.Ordinal);

        Assert.DoesNotContain("[None]", prompt.Split("## CONTEXT")[0], StringComparison.Ordinal);

    }

    [Fact]
    public void Build_WithDataStreams_IncludesStreamSections()
    {

        List<DataStreamPayload> streams =
        [
            new("metrics", "text/plain", "cpu=42%"),
        ];

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello") { DataStreams = streams },
            codexContent: null);

        Assert.Contains("### Data Stream: metrics", prompt, StringComparison.Ordinal);

        Assert.Contains("cpu=42%", prompt, StringComparison.Ordinal);

    }

    [Fact]
    public void Build_WithContextSnapshot_IncludesWorkspaceContextAndThreads()
    {

        PatternSnapshot snapshot = new(
            DomainType.SoftwareEngineering,
            "/workspace",
            ["Solution: App.sln", "  ", "Project: App.csproj"]);

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello") { ContextSnapshot = snapshot },
            codexContent: null);

        Assert.Contains("### Workspace Context", prompt, StringComparison.Ordinal);

        Assert.Contains("Domain: SoftwareEngineering", prompt, StringComparison.Ordinal);

        Assert.Contains("RootPath: /workspace", prompt, StringComparison.Ordinal);

        Assert.Contains("### Table of Contents", prompt, StringComparison.Ordinal);

        Assert.Contains("- Solution: App.sln", prompt, StringComparison.Ordinal);

        Assert.Contains("- Project: App.csproj", prompt, StringComparison.Ordinal);

        Assert.DoesNotContain("-   ", prompt, StringComparison.Ordinal);

    }

    [Fact]
    public void Build_WithCodex_IncludesMasterCodexSection()
    {

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello"),
            codexContent: "# Codex rules\nBe helpful.");

        Assert.Contains("### Master Codex (CODEX.md)", prompt, StringComparison.Ordinal);

        Assert.Contains("# Codex rules", prompt, StringComparison.Ordinal);

    }

    [Fact]
    public void Build_WithCampaignSummary_IncludesCompressedContextSection()
    {

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello"),
            codexContent: null,
            campaignSummary: "  earlier plot points  ");

        Assert.Contains("### Campaign Summary (compressed context)", prompt, StringComparison.Ordinal);

        Assert.Contains("earlier plot points", prompt, StringComparison.Ordinal);

    }

    [Fact]
    public void Build_WithActiveSpell_IncludesSpellBodyAndScripts()
    {

        ParsedSpell spell = new(
            "Heal",
            "healing",
            "/spells/heal/SPELL.md",
            "---\nname: Heal\n---\nfull",
            "/spells/heal",
            ["cast.sh"])
        {
            Body = "heal instructions",
        };

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello"),
            codexContent: null,
            activeSpell: spell);

        Assert.Contains("### Active Operational Spell (Heal)", prompt, StringComparison.Ordinal);

        Assert.Contains("full", prompt, StringComparison.Ordinal);

        Assert.Contains("#### Available Spell Scripts", prompt, StringComparison.Ordinal);

        Assert.Contains("cast.sh", prompt, StringComparison.Ordinal);

        Assert.Contains("run_spell_script", prompt, StringComparison.Ordinal);

    }

    [Fact]
    public void Build_WithCliTerminalFormatting_IncludesOutputDirective()
    {

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello") { CliTerminalFormatting = true },
            codexContent: null);

        Assert.Contains("### Output Formatting Directive", prompt, StringComparison.Ordinal);

        Assert.Contains("raw CLI terminal", prompt, StringComparison.Ordinal);

    }

    [Fact]
    public void Build_WithAdditionalSystemPrompt_IncludesExtraInstructions()
    {

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello") { AdditionalSystemPrompt = "  stay concise  " },
            codexContent: null);

        Assert.Contains("### Additional Instructions", prompt, StringComparison.Ordinal);

        Assert.Contains("stay concise", prompt, StringComparison.Ordinal);

    }

    [Fact]
    public void Build_WithChronosyncDomainChange_IncludesTemporalDelta()
    {

        PatternSnapshot snapshot = new(DomainType.Research, "/workspace", []);

        ChronosyncReport delta = new(
            DateTimeOffset.UtcNow.AddHours(-1),
            [],
            [],
            DomainChanged: true,
            PreviousDomain: DomainType.SoftwareEngineering);

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello")
            {
                ContextSnapshot = snapshot,
                ChronosyncDelta = delta,
            },
            codexContent: null);

        Assert.Contains("### Chronosync Report (Temporal Delta)", prompt, StringComparison.Ordinal);

        Assert.Contains("SoftwareEngineering", prompt, StringComparison.Ordinal);

        Assert.Contains("Research", prompt, StringComparison.Ordinal);

    }

    [Fact]
    public void Build_WithChronosyncThreadChanges_ListsNewAndMissingThreads()
    {

        ChronosyncReport delta = new(
            DateTimeOffset.UtcNow.AddHours(-1),
            ["Note: README.md"],
            ["Project: Old.csproj"],
            DomainChanged: false);

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello") { ChronosyncDelta = delta },
            codexContent: null);

        Assert.Contains("New threads (added since last sync):", prompt, StringComparison.Ordinal);

        Assert.Contains("- Note: README.md", prompt, StringComparison.Ordinal);

        Assert.Contains("Missing threads (removed since last sync):", prompt, StringComparison.Ordinal);

        Assert.Contains("- Project: Old.csproj", prompt, StringComparison.Ordinal);

    }

    [Fact]
    public void Build_WithChronosyncNoPreviousSnapshot_OmitsTemporalDelta()
    {

        ChronosyncReport delta = new(
            null,
            ["Note: README.md"],
            [],
            DomainChanged: true);

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello") { ChronosyncDelta = delta },
            codexContent: null);

        Assert.DoesNotContain("### Chronosync Report (Temporal Delta)", prompt, StringComparison.Ordinal);

    }

    [Fact]
    public void Build_SpellWithoutScripts_OmitsScriptsSection()
    {

        ParsedSpell spell = new(
            "Plain",
            "plain",
            "/spells/plain/SPELL.md",
            "---\nname: Plain\n---\nfull",
            "/spells/plain",
            [])
        {
            Body = "plain body",
        };

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello"),
            codexContent: null,
            activeSpell: spell);

        Assert.DoesNotContain("#### Available Spell Scripts", prompt, StringComparison.Ordinal);

    }

}
