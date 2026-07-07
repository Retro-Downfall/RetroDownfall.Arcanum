using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence.Guardrails;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence.Guardrails;

public sealed class GuardrailsPipelineTests
{

    [Fact]
    public async Task FilterInputAsync_WhenDisabled_ReturnsAllowed()
    {

        GuardrailsPipeline pipeline = CreatePipeline(new GuardrailsSettings { Enabled = false, DetectPii = true });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "Email me at alice@example.com")],
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.True(result.Value!.IsAllowed);

    }

    [Fact]
    public async Task FilterInputAsync_DetectsEmailPii_AndRedactsMatchedText()
    {

        FakeGuardrailAuditLogger audit = new();

        GuardrailsPipeline pipeline = CreatePipeline(new GuardrailsSettings { Enabled = true, DetectPii = true }, audit);

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "Please reply to alice@example.com for details.")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.PiiDetected, result.Error.Code);

        GuardrailAuditRecord record = Assert.Single(audit.Records);

        Assert.Equal("pii-email", record.ViolationType);

        Assert.Equal("***@***.***", record.MatchedTextRedacted);

    }

    [Fact]
    public async Task FilterInputAsync_DetectsSsn()
    {

        GuardrailsPipeline pipeline = CreatePipeline(new GuardrailsSettings { Enabled = true, DetectPii = true });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "My SSN is 123-45-6789.")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.PiiDetected, result.Error.Code);

    }

    [Fact]
    public async Task FilterInputAsync_DetectsCreditCard()
    {

        GuardrailsPipeline pipeline = CreatePipeline(new GuardrailsSettings { Enabled = true, DetectPii = true });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "Card: 4111 1111 1111 1111")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.PiiDetected, result.Error.Code);

    }

    [Fact]
    public async Task FilterInputAsync_DetectsPhone()
    {

        GuardrailsPipeline pipeline = CreatePipeline(new GuardrailsSettings { Enabled = true, DetectPii = true });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "Call me at (555) 123-4567 today.")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.PiiDetected, result.Error.Code);

    }

    [Fact]
    public async Task FilterInputAsync_CleanInput_IsAllowed()
    {

        GuardrailsPipeline pipeline = CreatePipeline(new GuardrailsSettings { Enabled = true, DetectPii = true });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "What is the weather in Paris?")],
            CancellationToken.None);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task FilterInputAsync_ToxicityBlocklist_BlocksAndReturnsBlockedCode()
    {

        GuardrailsPipeline pipeline = CreatePipeline(new GuardrailsSettings
        {
            Enabled = true,
            DetectPii = false,
            BlockToxicity = true,
            ToxicityBlocklist = ["forbidden-term"],
        });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "This contains the FORBIDDEN-TERM word.")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.Blocked, result.Error.Code);

    }

    [Fact]
    public async Task FilterInputAsync_ToxicityBlocklistDisabled_DoesNotBlock()
    {

        GuardrailsPipeline pipeline = CreatePipeline(new GuardrailsSettings
        {
            Enabled = true,
            DetectPii = false,
            BlockToxicity = false,
            ToxicityBlocklist = ["forbidden-term"],
        });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "This contains the forbidden-term word.")],
            CancellationToken.None);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task FilterInputAsync_AllowedTopics_NonMatchingInput_IsBlocked()
    {

        GuardrailsPipeline pipeline = CreatePipeline(new GuardrailsSettings
        {
            Enabled = true,
            DetectPii = false,
            AllowedTopics = ["^weather", "^forecast"],
        });

        Result<GuardrailsResult> matching = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "weather forecast for today")],
            CancellationToken.None);

        Assert.True(matching.IsSuccess);

        Result<GuardrailsResult> nonMatching = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "how do I bake a cake?")],
            CancellationToken.None);

        Assert.True(nonMatching.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.Blocked, nonMatching.Error.Code);

    }

    [Fact]
    public async Task FilterInputAsync_AllowedTopicsEmpty_AllowsEverything()
    {

        GuardrailsPipeline pipeline = CreatePipeline(new GuardrailsSettings
        {
            Enabled = true,
            DetectPii = false,
            AllowedTopics = [],
        });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "anything goes here")],
            CancellationToken.None);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task FilterInputAsync_BlockedTopics_MatchingInput_IsBlocked()
    {

        GuardrailsPipeline pipeline = CreatePipeline(new GuardrailsSettings
        {
            Enabled = true,
            DetectPii = false,
            BlockedTopics = ["password\\s*=\\s*\\S+"],
        });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "The config has password = hunter2 in it.")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.Blocked, result.Error.Code);

    }

    [Fact]
    public async Task FilterInputAsync_BlockedTopics_InvalidRegex_IsSkippedNotThrown()
    {

        GuardrailsPipeline pipeline = CreatePipeline(new GuardrailsSettings
        {
            Enabled = true,
            DetectPii = false,
            BlockedTopics = ["(unbalanced"],
        });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "anything")],
            CancellationToken.None);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task FilterOutputAsync_Toxicity_Blocks()
    {

        GuardrailsPipeline pipeline = CreatePipeline(new GuardrailsSettings
        {
            Enabled = true,
            DetectPii = false,
            BlockToxicity = true,
            ToxicityBlocklist = ["bad-word"],
        });

        Result<GuardrailsResult> result = await pipeline.FilterOutputAsync(
            "The model says bad-word here.",
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.Blocked, result.Error.Code);

    }

    [Fact]
    public async Task FilterOutputAsync_Pii_IsNotReScannedOnOutput()
    {

        // PII detection is an input-only gate; output is not re-scanned for PII.
        GuardrailsPipeline pipeline = CreatePipeline(new GuardrailsSettings
        {
            Enabled = true,
            DetectPii = true,
        });

        Result<GuardrailsResult> result = await pipeline.FilterOutputAsync(
            "Echo: alice@example.com",
            CancellationToken.None);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task FilterInputAsync_Blocked_LogsAuditRecordWithRedactedText()
    {

        FakeGuardrailAuditLogger audit = new();

        GuardrailsPipeline pipeline = CreatePipeline(
            new GuardrailsSettings { Enabled = true, DetectPii = true },
            audit);

        await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "Reach me at alice@example.com")],
            CancellationToken.None,
            new GuardrailAuditContext("session-1", "test-model"));

        GuardrailAuditRecord record = Assert.Single(audit.Records);

        Assert.Equal("Input", record.Stage);

        Assert.Equal("pii-email", record.ViolationType);

        Assert.Equal("***@***.***", record.MatchedTextRedacted);

        Assert.Equal("session-1", record.SessionId);

        Assert.Equal("test-model", record.Model);

    }

    [Fact]
    public async Task FilterInputAsync_Disabled_DoesNotLogAuditRecord()
    {

        FakeGuardrailAuditLogger audit = new();

        GuardrailsPipeline pipeline = CreatePipeline(
            new GuardrailsSettings { Enabled = false, DetectPii = true },
            audit);

        await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "alice@example.com")],
            CancellationToken.None);

        Assert.Empty(audit.Records);

    }

    private static GuardrailsPipeline CreatePipeline(GuardrailsSettings guardrails, FakeGuardrailAuditLogger? audit = null)
    {

        ArcanumSettings settings = new() { Guardrails = guardrails };

        return new GuardrailsPipeline(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            audit ?? new FakeGuardrailAuditLogger(),
            NullLogger<GuardrailsPipeline>.Instance);

    }

}
