using RetroDownfall.Arcanum.Cli.UX;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class AssistantTranscriptSeparationTests
{

    [Fact]
    public void ToolDiagnosticLine_Create_truncates_and_never_embeds_full_payload()
    {

        string payload = new('x', 200);

        ToolDiagnosticLine line = ToolDiagnosticLine.Create(
            "spell_search",
            ToolDiagnosticOutcome.Succeeded,
            payload);

        Assert.Equal(80, line.TruncatedPreview.Length);

        Assert.EndsWith("…", line.TruncatedPreview, StringComparison.Ordinal);

        Assert.DoesNotContain(payload, line.TruncatedPreview, StringComparison.Ordinal);

        Assert.Equal(79, line.TruncatedPreview.Count(c => c == 'x'));

    }

    [Fact]
    public void ToolDiagnosticLine_Truncate_collapses_newlines()
    {

        string truncated = ToolDiagnosticLine.Truncate("line1\nline2\r\nline3");

        Assert.Equal("line1 line2 line3", truncated);

    }

    [Fact]
    public void ToolDiagnosticLine_Truncate_leaves_short_preview_unchanged()
    {

        string shortPreview = "ok — 3 results";

        Assert.Equal(shortPreview, ToolDiagnosticLine.Truncate(shortPreview));

    }

}
