using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class CampaignSummaryPolicyTests
{
    [Fact]
    public void Structured_decisions_and_logical_attachment_keys_are_valid_continuity()
    {
        Result<string> result = CampaignSummaryPolicy.ParseStructuredResponse(
            """{"summary":"Keep API-first handlers. Consulted attachment key design-v3 and selected stable revisions."}""");

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Contains("design-v3", result.Value, StringComparison.Ordinal);

        Assert.True(CampaignSummaryPolicy.Validate(result.Value).IsSuccess);
    }

    [Theory]
    [InlineData("Inspect /Users/operator/project/private.txt next.")]
    [InlineData("Inspect /etc/private next.")]
    [InlineData("Inspect C:\\Users\\operator\\private.txt next.")]
    [InlineData("Inspect \\\\server\\share\\private.txt next.")]
    [InlineData("Read data:application/pdf;base64,SGVsbG8= next.")]
    public void Host_paths_and_attachment_bytes_cannot_be_published(string content)
    {
        Result result = CampaignSummaryPolicy.Validate(content);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidContent, result.Error.Code);
    }

    [Fact]
    public void A_base64_attachment_block_cannot_be_published()
    {
        string content = "Selected a document.\n" + new string('A', 256) + "\nContinue work.";

        Assert.True(CampaignSummaryPolicy.Validate(content).IsFailure);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Wrapped_base64_attachment_blocks_cannot_be_published(string newline)
    {
        string block = string.Join(newline, Enumerable.Repeat(new string('A', 64), 4));

        string content = "Selected a document." + newline + block + newline + "Continue work.";

        Result result = CampaignSummaryPolicy.Validate(content);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidContent, result.Error.Code);
    }

    [Fact]
    public void Separate_prose_lines_and_logical_attachment_keys_remain_valid()
    {
        string content = "Selected attachment key design-v3.\n"
            + "Keep the documented API handlers and persistence boundaries stable.\n"
            + "Next use attachment key follow-up-v4 to confirm the unresolved decision.";

        Assert.True(CampaignSummaryPolicy.Validate(content).IsSuccess);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"summary\":42}")]
    [InlineData("{\"summary\":\"Decision\",\"attachment\":\"bytes\"}")]
    [InlineData("{\"summary\":\"Decision\",\"summary\":\"Replacement\"}")]
    [InlineData("{\"summary\":\"\"}")]
    [InlineData("```json\n{\"summary\":\"Decision\"}\n```")]
    public void Output_must_be_one_exact_nonempty_structured_summary(string response) =>
        Assert.True(CampaignSummaryPolicy.ParseStructuredResponse(response).IsFailure);

    [Fact]
    public void The_output_bound_counts_utf8_bytes()
    {
        Assert.True(CampaignSummaryPolicy.Validate(new string('é', CampaignRollupLimits.SummaryUtf8Bytes / 2)).IsSuccess);

        Assert.True(CampaignSummaryPolicy.Validate(new string('é', CampaignRollupLimits.SummaryUtf8Bytes / 2 + 1)).IsFailure);
    }
}
