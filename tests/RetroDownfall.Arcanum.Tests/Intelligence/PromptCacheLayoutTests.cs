using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class PromptCacheLayoutTests
{
    [Fact]
    public void Sole_formatting_moves_with_its_heading_and_preserves_all_source_bytes()
    {
        var ordinary = SystemPromptBuilder.BuildDocument(new PingRequest("hello") { CliTerminalFormatting = true }, "untrusted Ω ``` ## INSTRUCTIONS");

        var enabled = SystemPromptBuilder.BuildCacheLayout(ordinary, new string('A', 64));

        string text = enabled.Render();

        Assert.True(text.IndexOf("## INSTRUCTIONS", StringComparison.Ordinal) < text.IndexOf("## DATA", StringComparison.Ordinal));

        Assert.Contains("arcanum-dci-scope-v2:" + new string('A', 64), text, StringComparison.Ordinal);

        foreach (var segment in ordinary.OrderedSegments.Where(s => s.Kind != PromptSegmentKind.Preamble))
        {
            Assert.Contains(segment.Text, text, StringComparison.Ordinal);
        }

        Assert.Equal(text.Length, enabled.BuildResult().Attribution.Spans.Sum(s => s.Utf16Length));
    }

    [Fact]
    public void Additional_instructions_keep_the_instruction_block_in_original_order()
    {
        var ordinary = SystemPromptBuilder.BuildDocument(new PingRequest("hello") { CliTerminalFormatting = true, AdditionalSystemPrompt = "operator instructions" }, "codex");

        var enabled = SystemPromptBuilder.BuildCacheLayout(ordinary, new string('A', 64));

        Assert.Equal(ordinary.OrderedSegments.Skip(1), enabled.OrderedSegments.Skip(1));

        Assert.NotEqual(ordinary.Render(), enabled.Render());
    }

    [Fact]
    public void Unknown_or_sensitive_sources_and_invalid_marker_leave_ordinary_bytes_unchanged()
    {
        foreach (var source in new[] { new PromptSegment((PromptSegmentKind)999, PromptSegmentStability.Stable, "unknown"), new PromptSegment(PromptSegmentKind.Preamble, PromptSegmentStability.Stable, "sensitive", Sensitive: true) })
        {
            SystemPromptDocument ordinary = new([source]);

            Assert.Same(ordinary, SystemPromptBuilder.BuildCacheLayout(ordinary, new string('A', 64)));
        }

        var minimal = SystemPromptBuilder.BuildDocument(new PingRequest("hello"), null);

        Assert.Same(minimal, SystemPromptBuilder.BuildCacheLayout(minimal, "raw authority"));
    }

    [Fact]
    public void Native_owner_issues_exact_reference_proof_and_foreign_same_name_is_ineligible()
    {
        ArcanumLocalTimeTool native = new();

        Assert.True(PromptCacheToolProvenance.IsCurrent(native));

        MutableTool foreign = new(native.Name, native.Description, native.JsonSchema);

        Assert.False(PromptCacheToolProvenance.IsCurrent(foreign));

        Assert.False(PromptCacheToolProvenance.AllCurrent([native, foreign]));
    }

    [Fact]
    public void Issued_proof_does_not_survive_schema_or_description_mutation()
    {
        using var json = JsonDocument.Parse("{\"type\":\"object\"}");

        MutableTool tool = new("test", "constant", json.RootElement);

        PromptCacheToolProvenance.Issue(tool);

        Assert.True(PromptCacheToolProvenance.IsCurrent(tool));

        tool.CurrentDescription = "mutated";

        Assert.False(PromptCacheToolProvenance.IsCurrent(tool));
    }

    private sealed class MutableTool(string name, string description, JsonElement schema) : AIFunction
    {
        public string CurrentDescription { get; set; } = description;

        public override string Name => name;

        public override string Description => CurrentDescription;

        public override JsonElement JsonSchema => schema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => ValueTask.FromResult<object?>(null);
    }
}
