using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed partial class DataRetentionCommandTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Refusal_contract_stops_a_blocked_reset_before_confirmation(bool invalidInput, bool json, bool yes)
    {
        const string reason = "The selected store cannot be reset for this Campaign.";

        DataRetentionPlan plan = CreatePlan() with
        {
            Blockers = [new(RetentionDataClass.SessionEntryEmbeddings, "target", invalidInput ? ErrorCodes.Data.InvalidRequest : "Data.SessionHold", reason)],
        };

        RecordingHandler handler = new(request => request.Path == "/api/data/memory/reset/plan"
            ? SuccessResponse(plan, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan)
            : SuccessResponse(CreateApplyResult(plan.PlanId), ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult));

        RecordingConfirmationPrompt prompt = new(confirmed: false);

        List<string> args = ["data", "reset-memory", "--scope", "entry", "--campaign", Guid.NewGuid().ToString("D"), json ? "--json" : "--plain"];

        if (yes) args.Add("--yes");

        var result = RunCommand(handler, [.. args], prompt);

        Assert.Equal(invalidInput ? (int)CliExitCode.ConfigurationError : (int)CliExitCode.GenericError, result.ExitCode);

        Assert.Empty(prompt.Question);

        Assert.Equal("/api/data/memory/reset/plan", Assert.Single(handler.Requests).Path);

        Assert.Contains(invalidInput ? ErrorCodes.Data.InvalidRequest : ErrorCodes.Data.Blocked, result.Error, StringComparison.Ordinal);

        Assert.Contains(reason, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusal_contract_stops_a_conflicting_reset_before_confirmation()
    {
        const string reason = "A durable data operation already owns this reset.";

        DataRetentionPlan plan = CreatePlan() with
        {
            Conflicts = [new(ErrorCodes.Data.Conflict, "target", reason)],
        };

        RecordingHandler handler = new(_ => SuccessResponse(plan, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan));

        RecordingConfirmationPrompt prompt = new(confirmed: false);

        var result = RunCommand(handler, ["data", "reset-memory", "--scope", "saga"], prompt);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Empty(prompt.Question);

        Assert.Equal("/api/data/memory/reset/plan", Assert.Single(handler.Requests).Path);

        Assert.Contains(ErrorCodes.Data.Conflict, result.Error, StringComparison.Ordinal);

        Assert.Contains(reason, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Targeted_Covenant_reset_cli_refuses_before_http_and_confirmation(bool yes, bool json)
    {
        DataRetentionPlan plan = CreatePlan();

        RecordingHandler handler = new(request => request.Path == "/api/data/memory/reset/plan"
            ? SuccessResponse(plan, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan)
            : SuccessResponse(CreateApplyResult(plan.PlanId), ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult));

        RecordingConfirmationPrompt prompt = new(confirmed: true);

        List<string> args = ["data", "reset-memory", "--scope", "covenant", "--campaign", Guid.NewGuid().ToString("D"), json ? "--json" : "--plain"];

        if (yes) args.Add("--yes");

        var result = RunCommand(handler, [.. args], prompt);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Empty(prompt.Question);

        Assert.Contains(ErrorCodes.Data.InvalidRequest, result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("External disclosures", result.Output, StringComparison.OrdinalIgnoreCase);
    }
}
