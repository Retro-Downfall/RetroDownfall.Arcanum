using System.Text.RegularExpressions;
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
        GuardrailsPipeline pipeline = CreatePipeline(
            ArcanumRuntimeDefaults.Guardrails with { Enabled = false, DetectPii = true });

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

        GuardrailsPipeline pipeline = CreatePipeline(
            ArcanumRuntimeDefaults.Guardrails with { Enabled = true, DetectPii = true },
            audit);

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
        GuardrailsPipeline pipeline = CreatePipeline(
            ArcanumRuntimeDefaults.Guardrails with { Enabled = true, DetectPii = true });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "My SSN is 123-45-6789.")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.PiiDetected, result.Error.Code);
    }

    [Fact]
    public async Task FilterInputAsync_DetectsCreditCard()
    {
        GuardrailsPipeline pipeline = CreatePipeline(
            ArcanumRuntimeDefaults.Guardrails with { Enabled = true, DetectPii = true });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "Card: 4111 1111 1111 1111")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.PiiDetected, result.Error.Code);
    }

    [Fact]
    public async Task FilterInputAsync_DetectsPhone()
    {
        GuardrailsPipeline pipeline = CreatePipeline(
            ArcanumRuntimeDefaults.Guardrails with { Enabled = true, DetectPii = true });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "Call me at (555) 123-4567 today.")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.PiiDetected, result.Error.Code);
    }

    [Fact]
    public async Task FilterInputAsync_CleanInput_IsAllowed()
    {
        GuardrailsPipeline pipeline = CreatePipeline(
            ArcanumRuntimeDefaults.Guardrails with { Enabled = true, DetectPii = true });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "What is the weather in Paris?")],
            CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task FilterInputAsync_ToxicityBlocklist_BlocksAndReturnsBlockedCode()
    {
        GuardrailsPipeline pipeline = CreatePipeline(ArcanumRuntimeDefaults.Guardrails with
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
        GuardrailsPipeline pipeline = CreatePipeline(ArcanumRuntimeDefaults.Guardrails with
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
        GuardrailsPipeline pipeline = CreatePipeline(ArcanumRuntimeDefaults.Guardrails with
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
        GuardrailsPipeline pipeline = CreatePipeline(ArcanumRuntimeDefaults.Guardrails with
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
        GuardrailsPipeline pipeline = CreatePipeline(ArcanumRuntimeDefaults.Guardrails with
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
        GuardrailsPipeline pipeline = CreatePipeline(ArcanumRuntimeDefaults.Guardrails with
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
        GuardrailsPipeline pipeline = CreatePipeline(ArcanumRuntimeDefaults.Guardrails with
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
    public async Task FilterOutputAsync_Blocked_ReportsOutputStageInErrorMessage()
    {
        GuardrailsPipeline pipeline = CreatePipeline(ArcanumRuntimeDefaults.Guardrails with
        {
            Enabled = true,
            DetectPii = false,
            BlockToxicity = true,
            ToxicityBlocklist = ["bad-word"],
        });

        Result<GuardrailsResult> outputResult = await pipeline.FilterOutputAsync(
            "The model says bad-word here.",
            CancellationToken.None);

        Assert.True(outputResult.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.Blocked, outputResult.Error.Code);

        Assert.DoesNotContain("Input rejected", outputResult.Error.Message, StringComparison.Ordinal);

        Assert.StartsWith("Response blocked", outputResult.Error.Message, StringComparison.Ordinal);

        Result<GuardrailsResult> inputResult = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "The user says bad-word here.")],
            CancellationToken.None);

        Assert.True(inputResult.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.Blocked, inputResult.Error.Code);

        Assert.StartsWith("Input rejected", inputResult.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilterOutputAsync_Pii_IsNotReScannedOnOutput()
    {
        // PII detection is an input-only gate; output is not re-scanned for PII.
        GuardrailsPipeline pipeline = CreatePipeline(ArcanumRuntimeDefaults.Guardrails with
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
            ArcanumRuntimeDefaults.Guardrails with { Enabled = true, DetectPii = true },
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
            ArcanumRuntimeDefaults.Guardrails with { Enabled = false, DetectPii = true },
            audit);

        await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "alice@example.com")],
            CancellationToken.None);

        Assert.Empty(audit.Records);
    }

    [Fact]
    public async Task FilterInputAsync_MultipleViolations_AuditsAllUpToCap()
    {
        FakeGuardrailAuditLogger audit = new();

        GuardrailsPipeline pipeline = CreatePipeline(
            ArcanumRuntimeDefaults.Guardrails with
            {
                Enabled = true,
                DetectPii = true,
                BlockToxicity = true,
                ToxicityBlocklist = ["bad-word"],
            },
            audit);

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "alice@example.com and bob@evil.org bad-word")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.True(audit.Records.Count >= 2);
    }

    [Fact]
    public async Task FilterInputAsync_PhoneWithMismatchedParens_StillDetectsPhoneNumber()
    {
        GuardrailsPipeline pipeline = CreatePipeline(
            ArcanumRuntimeDefaults.Guardrails with { Enabled = true, DetectPii = true });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "Call (555-555-5555 today.")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.PiiDetected, result.Error.Code);
    }

    [Fact]
    public async Task FilterInputAsync_PhoneWithBalancedParens_Matches()
    {
        GuardrailsPipeline pipeline = CreatePipeline(
            ArcanumRuntimeDefaults.Guardrails with { Enabled = true, DetectPii = true });

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "Call (555) 555-5555 today.")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.PiiDetected, result.Error.Code);
    }

    /// <summary>
    /// An operator pattern with an ambiguous quantifier backtracks exponentially on crafted input.
    /// The match budget then elapses, and a guardrail that reads "budget elapsed" as "no violation"
    /// lets the caller choose whether the block applies. Blocked topics must fail closed — and be
    /// audited — exactly like the allowed-topics arm already does.
    /// </summary>
    [Fact]
    public async Task FilterInputAsync_BlockedTopics_MatchTimeout_FailsClosedAndIsAudited()
    {
        FakeGuardrailAuditLogger audit = new();

        GuardrailsPipeline pipeline = CreatePipeline(
            ArcanumRuntimeDefaults.Guardrails with
            {
                Enabled = true,
                DetectPii = false,
                BlockedTopics = ["^(a+)+$"],
            },
            audit);

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", new string('a', 64) + "!")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.Blocked, result.Error.Code);

        Assert.NotEmpty(audit.Records);
    }

    /// <summary>
    /// The rejection is already decided when the audit record is written, so a request token that was
    /// cancelled in the meantime (client disconnect, shutdown) must not silently drop the only
    /// evidence that a guardrail fired.
    /// </summary>
    [Fact]
    public async Task Violation_AuditRecordWritten_EvenWhenRequestTokenAlreadyCancelled()
    {
        TokenHonouringGuardrailAuditLogger audit = new();

        GuardrailsPipeline pipeline = CreatePipeline(
            ArcanumRuntimeDefaults.Guardrails with { Enabled = true, DetectPii = true },
            audit);

        using CancellationTokenSource cancelled = new();

        await cancelled.CancelAsync();

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "Please reply to alice@example.com for details.")],
            cancelled.Token,
            new GuardrailAuditContext("session-1", "test-model"));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.PiiDetected, result.Error.Code);

        GuardrailAuditRecord record = Assert.Single(audit.Records);

        Assert.Equal("pii-email", record.ViolationType);
    }

    /// <summary>
    /// A long local part followed by an at sign and a domain that never reaches a top-level label
    /// makes the email pattern retry from every start position. The scan must finish inside its
    /// match budget (fail closed when it cannot, like the topic patterns) rather than pin the
    /// request thread. The current engine happens to resolve this input in linear time, so the
    /// timeout arm itself is pinned by the substituted-pattern test above.
    /// </summary>
    [Fact]
    public async Task Pii_scan_of_a_long_local_part_then_at_sign_completes_within_budget()
    {
        FakeGuardrailAuditLogger audit = new();

        GuardrailsPipeline pipeline = CreatePipeline(
            ArcanumRuntimeDefaults.Guardrails with { Enabled = true, DetectPii = true },
            audit);

        string hostile = new string('a', 200_000) + "@" + string.Concat(Enumerable.Repeat("a.", 100_000));

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", hostile)],
            CancellationToken.None);

        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"PII scan took {stopwatch.Elapsed.TotalSeconds:F1}s; the match budget did not bound it.");

        // Either answer is acceptable as long as one arrives in budget: no match (the engine proved
        // there is no address) or a fail-closed block (the budget elapsed first).
        Assert.True(result.IsSuccess || result.Error.Code == ErrorCodes.Guardrails.Blocked);

        Assert.DoesNotContain(audit.Records, record => record.ViolationType == "pii-email");
    }

    /// <summary>
    /// A pattern that cannot finish inside its match budget leaves the PII question unanswered.
    /// Reading that as "no PII" would let crafted input pick its own answer, so the scan fails
    /// closed and audits the undetermined evaluation, exactly like a blocked-topic timeout.
    /// </summary>
    [Fact]
    public async Task Pii_pattern_that_exceeds_its_match_budget_fails_closed_and_is_audited()
    {
        FakeGuardrailAuditLogger audit = new();

        GuardrailsPipeline pipeline = CreatePipeline(
            ArcanumRuntimeDefaults.Guardrails with { Enabled = true, DetectPii = true },
            audit,
            [
                new GuardrailsPipeline.PiiPattern(
                    "pii-email",
                    "Slow pattern.",
                    new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(50))),
            ]);

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", new string('a', 64) + "!")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guardrails.Blocked, result.Error.Code);

        GuardrailAuditRecord record = Assert.Single(audit.Records);

        Assert.Equal("pii-undetermined", record.ViolationType);

        Assert.Equal("***", record.MatchedTextRedacted);
    }

    [Fact]
    public void Default_pii_patterns_all_carry_a_finite_match_timeout()
    {
        GuardrailsPipeline pipeline = CreatePipeline(ArcanumRuntimeDefaults.Guardrails with { Enabled = true, DetectPii = true });

        Assert.Equal(4, pipeline.PiiPatterns.Count);

        foreach (GuardrailsPipeline.PiiPattern pattern in pipeline.PiiPatterns)
        {
            Assert.NotEqual(Regex.InfiniteMatchTimeout, pattern.Pattern.MatchTimeout);

            Assert.True(
                pattern.Pattern.MatchTimeout <= TimeSpan.FromSeconds(1),
                $"{pattern.Type} match budget {pattern.Pattern.MatchTimeout} is longer than the topic budget class.");
        }
    }

    [Fact]
    public async Task Pii_scan_reports_one_violation_per_kind_from_a_single_match()
    {
        FakeGuardrailAuditLogger audit = new();

        GuardrailsPipeline pipeline = CreatePipeline(
            ArcanumRuntimeDefaults.Guardrails with { Enabled = true, DetectPii = true },
            audit);

        Result<GuardrailsResult> result = await pipeline.FilterInputAsync(
            [new CoreChatMessage("user", "alice@example.com and bob@example.org, SSN 123-45-6789.")],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(["pii-email", "pii-ssn"], audit.Records.Select(record => record.ViolationType).Order(StringComparer.Ordinal).ToArray());
    }

    private static GuardrailsPipeline CreatePipeline(
        GuardrailsSettings guardrails,
        IGuardrailAuditLogger? audit = null,
        IReadOnlyList<GuardrailsPipeline.PiiPattern>? piiPatterns = null)
    {
        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Guardrails = guardrails.Enabled },
            Security = new SecuritySettings
            {
                Guardrails = new GuardrailsPolicySettings
                {
                    DetectPii = guardrails.DetectPii,
                    BlockToxicity = guardrails.BlockToxicity,
                    ToxicityBlocklist = guardrails.ToxicityBlocklist,
                    AllowedTopics = guardrails.AllowedTopics,
                    BlockedTopics = guardrails.BlockedTopics,
                },
            },
        };

        TestOptionsMonitor<ArcanumSettings> options = new(settings);

        audit ??= new FakeGuardrailAuditLogger();

        return piiPatterns is null
            ? new GuardrailsPipeline(options, audit, NullLogger<GuardrailsPipeline>.Instance)
            : new GuardrailsPipeline(options, audit, NullLogger<GuardrailsPipeline>.Instance) { PiiPatterns = piiPatterns };
    }

    private sealed class TokenHonouringGuardrailAuditLogger : IGuardrailAuditLogger
    {
        public List<GuardrailAuditRecord> Records { get; } = [];

        public Task LogAsync(GuardrailAuditRecord record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Records.Add(record);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GuardrailAuditRecord>> QueryAsync(
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? stage,
            string? violationType,
            string? sessionId,
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<AuditQueryPage<GuardrailAuditRecord>>> QueryPageAsync(
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? stage,
            string? violationType,
            string? sessionId,
            int limit,
            string? cursor,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
