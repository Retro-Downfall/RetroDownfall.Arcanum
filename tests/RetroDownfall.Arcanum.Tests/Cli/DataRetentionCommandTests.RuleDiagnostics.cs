using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed partial class DataRetentionCommandTests
{
    [Theory]
    [InlineData("batch-input-files", "uploaded-files", "uploaded-files, batch-input-files, batch-output-files, batch-error-files")]
    [InlineData("attachment-chunks", "attachments", "attachment-versions, attachment-bytes, attachment-chunks, attachment-embeddings")]
    [InlineData("workspace-embeddings", "workspace-indexes", "workspace-chunks, workspace-embeddings")]
    [InlineData("cost-adjustments", "accounting", "inference-runs, billable-operations, budget-reservations, cost-adjustments")]
    public void Retention_confirmation_names_the_shared_rule_and_every_affected_class(string selector, string rule, string affected)
    {
        RecordingHandler handler = new(_ => ErrorResponse());

        RecordingConfirmationPrompt prompt = new(confirmed: false);

        _ = RunCommand(handler, ["data", "retention", "set", selector, "disabled"], prompt);

        Assert.Contains(rule + " retention rule", prompt.Question, StringComparison.Ordinal);

        Assert.Contains(affected, prompt.Question, StringComparison.Ordinal);

        Assert.Contains("disabled", prompt.Question, StringComparison.Ordinal);

        if (rule == "accounting") Assert.Contains("budget-alert history", prompt.Question, StringComparison.Ordinal);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Retention_success_names_the_shared_scope_in_its_human_response()
    {
        RecordingHandler handler = new(_ => SuccessResponse(CreateSettings(), ArcanumJsonContext.Default.ApiResponseRetentionSettings));

        var result = RunCommand(handler, ["--yes", "data", "retention", "set", "batch-input-files", "30"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("uploaded-files, batch-input-files, batch-output-files, batch-error-files", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-rule", "30", "missing-rule")]
    [InlineData("archived-sessions", "tomorrow", "tomorrow")]
    public void Invalid_retention_input_is_named_before_confirmation_or_http(string selector, string value, string rejected)
    {
        RecordingHandler handler = new(_ => ErrorResponse());

        RecordingConfirmationPrompt prompt = new(confirmed: true);

        var result = RunCommand(handler, ["data", "retention", "set", selector, value], prompt);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains(rejected, result.Error, StringComparison.Ordinal);

        Assert.Empty(prompt.Question);

        Assert.Empty(handler.Requests);

        if (selector == "missing-rule")
        {
            Assert.Contains("archived-sessions", result.Error, StringComparison.Ordinal);

            Assert.Contains("accounting", result.Error, StringComparison.Ordinal);
        }
    }
}
