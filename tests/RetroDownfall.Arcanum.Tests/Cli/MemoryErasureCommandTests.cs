using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Commands.Tower;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The three erase verbs, driven through the registered command tree against a recording host.
/// </summary>
/// <remarks>
/// One event list carries both the requests and the confirmation, so the order the contract depends
/// on — show, prepare, disclose, ask, apply — is asserted as an order rather than inferred from what
/// was eventually written. The prompt snapshots everything rendered before it was asked, which is the
/// only honest way to prove a disclosure preceded the decision it informs.
/// </remarks>
[Collection("GlobalConsole")]
public sealed class MemoryErasureCommandTests
{
    private const string SagaId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private const string ShownClaimVersion = "claim-version-7";

    private const string CovenantKeyName = "preference.builds";

    private const string DrainSentence =
        "Erasing drains in-flight Covenant turns first: it waits up to 30 seconds for them to finish, "
            + "turns that start meanwhile run without Covenant content, and backups, prepares and inventory "
            + "fail fast until it completes.";

    private const string ReclaimSentence =
        "This erase reclaims the key, so every outstanding Covenant preflight on this installation goes stale "
            + "and must be prepared again.";

    private const string ReclaimScopeSentence =
        "Reclaiming removes the key's pins and masks in every scope, not only this one; the key can be set "
            + "again later and starts with none.";

    private const string MayHaveApplied = "may have been applied";

    private static readonly string ShownHash = new('A', 64);

    private static readonly Guid CovenantEntryId = Guid.Parse("0195a0f0-0000-7000-8000-0000000000e1");

    private static readonly Guid ConfirmedVersion = Guid.Parse("0195a0f0-0000-7000-8000-0000000000c1");

    private static readonly Guid ProposedVersion = Guid.Parse("0195a0f0-0000-7000-8000-0000000000b2");

    private static readonly Guid CovenantCampaign = Guid.Parse("5b2e9c41-08d3-4a7f-b6e5-2c1908fa4d77");

    [Fact]
    public async Task Saga_erase_shows_prepares_discloses_prompts_then_applies()
    {
        ErasureHandler handler = new();

        RecordingPrompt prompt = new(handler, answer: true);

        CliTestResult result = await RunAsync(handler, ["memory", "saga", "erase", SagaId], prompt);

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Equal(
            [$"GET /api/memory/saga/{SagaId}", "POST /api/memory/saga/erase/prepare", "prompt", "POST /api/memory/saga/erase"],
            handler.Events);

        using JsonDocument prepare = JsonDocument.Parse(handler.Body("/api/memory/saga/erase/prepare"));

        JsonElement prepared = prepare.RootElement;

        Assert.Equal(SagaId, prepared.GetProperty("memoryId").GetString());

        Assert.Equal(ShownHash, prepared.GetProperty("expectedContentHash").GetString());

        Assert.Equal(ShownClaimVersion, prepared.GetProperty("expectedClaimVersionId").GetString());

        Guid mutationId = prepared.GetProperty("mutationId").GetGuid();

        Assert.NotEqual(Guid.Empty, mutationId);

        using JsonDocument apply = JsonDocument.Parse(handler.Body("/api/memory/saga/erase"));

        JsonElement applied = apply.RootElement;

        Assert.Equal(SagaId, applied.GetProperty("memoryId").GetString());

        Assert.Equal(ShownHash, applied.GetProperty("expectedContentHash").GetString());

        Assert.Equal(ShownClaimVersion, applied.GetProperty("expectedClaimVersionId").GetString());

        Assert.Equal(mutationId, applied.GetProperty("mutationId").GetGuid());

        Assert.Equal("token-1", applied.GetProperty("preflightToken").GetString());

        int disclosure = prompt.BeforePrompt.IndexOf(CovenantExternalRetentionDisclosure.DestructiveOperationText, StringComparison.Ordinal);

        int embedding = prompt.BeforePrompt.IndexOf("Embedding provider: known", StringComparison.Ordinal);

        int guidance = prompt.BeforePrompt.IndexOf("Retention guidance", StringComparison.Ordinal);

        Assert.True(disclosure >= 0, prompt.BeforePrompt);

        Assert.True(embedding > disclosure, prompt.BeforePrompt);

        Assert.True(guidance > embedding, prompt.BeforePrompt);

        Assert.Contains(mutationId.ToString("D"), result.Output, StringComparison.Ordinal);

        Assert.Contains("No other memory store was touched.", result.Output, StringComparison.Ordinal);

        // The show response is how the target is learned, never something printed: the memory's text
        // must not reach either stream.
        Assert.DoesNotContain(ErasureHandler.SagaContent, result.Output + result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain(CovenantExternalRetentionDisclosure.DestructiveOperationText, result.Output, StringComparison.Ordinal);

        // A confirmed erase is not an uncertain one.
        Assert.DoesNotContain(MayHaveApplied, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saga_erase_sends_an_explicit_expected_content_hash_instead_of_the_shown_one()
    {
        string explicitHash = new string('0', 63) + "A";

        ErasureHandler handler = new();

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "saga", "erase", SagaId, "--expected-content-hash", explicitHash],
            new RecordingPrompt(handler, answer: true));

        Assert.True(result.ExitCode == 0, result.Error);

        foreach (string path in new[] { "/api/memory/saga/erase/prepare", "/api/memory/saga/erase" })
        {
            string body = handler.Body(path);

            using JsonDocument document = JsonDocument.Parse(body);

            Assert.Equal(explicitHash, document.RootElement.GetProperty("expectedContentHash").GetString());

            Assert.DoesNotContain(ShownHash, body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(3, "Erase 3 Saga memories with identical content in this scope?")]
    [InlineData(1, "Erase this Saga memory in this scope?")]
    public async Task Saga_erase_prompt_names_the_twin_count(int erasedItemCount, string opening)
    {
        ErasureHandler handler = new()
        {
            Preflight = ErasureHandler.DefaultPreflight with
            {
                Plan = ErasureHandler.DefaultPreflight.Plan with { ErasedItemCount = erasedItemCount },
            },
        };

        RecordingPrompt prompt = new(handler, answer: false);

        CliTestResult result = await RunAsync(handler, ["memory", "saga", "erase", SagaId], prompt);

        Assert.Equal(0, result.ExitCode);

        Assert.NotNull(prompt.Question);

        Assert.StartsWith(opening, prompt.Question, StringComparison.Ordinal);

        Assert.EndsWith("This cannot be undone.", prompt.Question, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("12345678-1234-1234-1234-123456789abc")]
    public async Task Lexicon_erase_forwards_the_shown_target_byte_for_byte(string? campaign)
    {
        ErasureHandler handler = new();

        string[] args = campaign is null
            ? ["memory", "lexicon", "erase", "Operator"]
            : ["memory", "lexicon", "erase", "Operator", "--campaign", campaign];

        RecordingPrompt prompt = new(handler, answer: true);

        CliTestResult result = await RunAsync(handler, args, prompt);

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Equal(
            ["POST /api/memory/lexicon/show", "POST /api/memory/lexicon/erase/prepare", "prompt", "POST /api/memory/lexicon/erase"],
            handler.Events);

        AssertDisclosedBeforeThePrompt(prompt);

        Assert.Equal(MutationIdIn(handler, "/api/memory/lexicon/erase/prepare"), MutationIdIn(handler, "/api/memory/lexicon/erase"));

        // Neither the entry's facts nor the name the host stores for it are printed: the plan names
        // counts, and the question names what the operator typed.
        foreach (string shownText in LexiconCliFixture.Detail.Entry.Facts.Append(LexiconCliFixture.Detail.Entry.Name))
        {
            Assert.DoesNotContain(shownText, result.Output + result.Error, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(MayHaveApplied, result.Error, StringComparison.Ordinal);

        using JsonDocument show = JsonDocument.Parse(handler.Body("/api/memory/lexicon/show"));

        JsonElement scope = show.RootElement.GetProperty("scope");

        Assert.Equal(campaign is null ? "Global" : "Campaign", scope.GetProperty("kind").GetString());

        if (campaign is not null)
        {
            Assert.Equal(Guid.Parse(campaign), scope.GetProperty("campaignId").GetGuid());
        }

        // The target the show route answered with, as a client that deserialized and re-serialized it
        // through the shared context would hold it: the erase must carry exactly that.
        using JsonDocument shown = JsonDocument.Parse(handler.Responses["/api/memory/lexicon/show"]);

        LexiconCurationTarget roundTripped = JsonSerializer.Deserialize(
            shown.RootElement.GetProperty("data").GetProperty("target").GetRawText(),
            ArcanumJsonContext.Default.LexiconCurationTarget)!;

        string expectedTarget = JsonSerializer.Serialize(roundTripped, ArcanumJsonContext.Default.LexiconCurationTarget);

        Assert.Equal(shown.RootElement.GetProperty("data").GetProperty("target").GetRawText(), expectedTarget);

        foreach (string path in new[] { "/api/memory/lexicon/erase/prepare", "/api/memory/lexicon/erase" })
        {
            using JsonDocument body = JsonDocument.Parse(handler.Body(path));

            Assert.Equal(expectedTarget, body.RootElement.GetProperty("target").GetRawText());
        }

        using JsonDocument apply = JsonDocument.Parse(handler.Body("/api/memory/lexicon/erase"));

        Assert.Equal("token-1", apply.RootElement.GetProperty("preflightToken").GetString());
    }

    [Fact]
    public async Task Lexicon_erase_refuses_an_empty_campaign_before_any_request()
    {
        ErasureHandler handler = new();

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "lexicon", "erase", "Operator", "--campaign", Guid.Empty.ToString()],
            new RecordingPrompt(handler, answer: true));

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Events);
    }

    [Fact]
    public async Task Covenant_erase_forwards_the_entry_and_both_head_expectations()
    {
        ErasureHandler handler = new();

        RecordingPrompt prompt = new(handler, answer: true);

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "covenant", "erase", CovenantKeyName, "--campaign", CovenantCampaign.ToString()],
            prompt);

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Equal(
            ["POST /api/memory/covenant/detail", "POST /api/memory/covenant/erase/prepare", "prompt", "POST /api/memory/covenant/erase"],
            handler.Events);

        AssertDisclosedBeforeThePrompt(prompt);

        // Nothing the detail route reported about the entry's content — its authored and rendered
        // digests, its provenance digest — is printed.
        foreach (string shownValue in new[] { ErasureHandler.AuthoredHashMarker, ErasureHandler.RenderedHashMarker, ErasureHandler.ProvenanceDigestMarker })
        {
            Assert.DoesNotContain(shownValue, result.Output + result.Error, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(MayHaveApplied, result.Error, StringComparison.Ordinal);

        using JsonDocument detail = JsonDocument.Parse(handler.Body("/api/memory/covenant/detail"));

        Assert.Equal("Campaign", detail.RootElement.GetProperty("scope").GetString());

        Assert.Equal(CovenantCampaign, detail.RootElement.GetProperty("campaignId").GetGuid());

        Guid? mutationId = null;

        foreach (string path in new[] { "/api/memory/covenant/erase/prepare", "/api/memory/covenant/erase" })
        {
            using JsonDocument body = JsonDocument.Parse(handler.Body(path));

            JsonElement root = body.RootElement;

            Assert.Equal("Campaign", root.GetProperty("scope").GetString());

            Assert.Equal(CovenantCampaign, root.GetProperty("campaignId").GetGuid());

            Assert.Equal(CovenantKeyName, root.GetProperty("key").GetString());

            Assert.Equal(CovenantEntryId, root.GetProperty("entryId").GetGuid());

            Assert.Equal(ConfirmedVersion, root.GetProperty("confirmed").GetProperty("versionId").GetGuid());

            Assert.Equal(3, root.GetProperty("confirmed").GetProperty("laneRevision").GetInt64());

            Assert.Equal(ProposedVersion, root.GetProperty("proposed").GetProperty("versionId").GetGuid());

            Assert.Equal(1, root.GetProperty("proposed").GetProperty("laneRevision").GetInt64());

            Guid sent = root.GetProperty("mutationId").GetGuid();

            Assert.Equal(mutationId ?? sent, sent);

            mutationId = sent;
        }

        using JsonDocument apply = JsonDocument.Parse(handler.Body("/api/memory/covenant/erase"));

        Assert.Equal("token-1", apply.RootElement.GetProperty("preflightToken").GetString());
    }

    [Fact]
    public async Task Covenant_erase_of_a_key_with_no_entry_stops_before_prepare()
    {
        ErasureHandler handler = new()
        {
            CovenantDetail = ErasureHandler.DefaultCovenantDetail with
            {
                EntryId = null,
                Confirmed = null,
                Proposed = null,
            },
        };

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "covenant", "erase", CovenantKeyName],
            new RecordingPrompt(handler, answer: true));

        Assert.Equal(1, result.ExitCode);

        Assert.Equal(["POST /api/memory/covenant/detail"], handler.Events);

        Assert.Contains("has no entry to erase", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Covenant_erase_prompt_states_the_drain_cost()
    {
        ErasureHandler handler = new()
        {
            Preflight = ErasureHandler.DefaultPreflight with
            {
                Notes = [MemoryErasureNote.OtherScopesUnaffected, MemoryErasureNote.CovenantDrainsInFlightTurns],
            },
        };

        RecordingPrompt prompt = new(handler, answer: false);

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "covenant", "erase", CovenantKeyName, "--campaign", CovenantCampaign.ToString()],
            prompt);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains(DrainSentence, prompt.BeforePrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Covenant_erase_warns_that_reclaiming_the_key_stales_outstanding_preflights(bool reclaims, bool json)
    {
        ErasureHandler handler = new()
        {
            Preflight = ErasureHandler.DefaultPreflight with
            {
                Plan = ErasureHandler.DefaultPreflight.Plan with
                {
                    Covenant = ErasureHandler.CovenantFacts with { ReclaimsKey = reclaims },
                },
                Notes = reclaims
                    ? [MemoryErasureNote.CovenantDrainsInFlightTurns]
                    : [MemoryErasureNote.OtherScopesUnaffected, MemoryErasureNote.CovenantDrainsInFlightTurns],
            },
        };

        RecordingPrompt prompt = new(handler, answer: true);

        string[] args = json
            ? ["memory", "covenant", "erase", CovenantKeyName, "--campaign", CovenantCampaign.ToString(), "--json"]
            : ["memory", "covenant", "erase", CovenantKeyName, "--campaign", CovenantCampaign.ToString()];

        CliTestResult result = await RunAsync(handler, args, prompt);

        Assert.True(result.ExitCode == 0, result.Error);

        foreach (string sentence in new[] { ReclaimSentence, ReclaimScopeSentence })
        {
            // Before the question, on the diagnostic stream, in every mode — and only when the plan says so.
            Assert.Equal(reclaims, prompt.BeforePrompt.Contains(sentence, StringComparison.Ordinal));

            Assert.Equal(reclaims, result.Error.Contains(sentence, StringComparison.Ordinal));

            Assert.DoesNotContain(sentence, result.Output, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(DrainSentence, result.Output, StringComparison.Ordinal);

        if (json)
        {
            using JsonDocument document = JsonDocument.Parse(result.Output);

            Assert.Equal(handler.PreparedMutationId, document.RootElement.GetProperty("mutationId").GetGuid());
        }
    }

    /// <summary>
    /// The drain cost is a warning weighed before answering, so it reaches the diagnostic stream in
    /// every mode, and under <c>--json</c> stdout stays the one result document.
    /// </summary>
    /// <remarks>
    /// The fake's Covenant preflight carries <see cref="MemoryErasureNote.CovenantDrainsInFlightTurns"/>
    /// by default, because the host always sends it for a Covenant erase.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Covenant_drain_sentence_reaches_stderr_only_in_every_mode(bool json)
    {
        ErasureHandler handler = new();

        string[] args = json
            ? ["memory", "covenant", "erase", CovenantKeyName, "--campaign", CovenantCampaign.ToString(), "--yes", "--json"]
            : ["memory", "covenant", "erase", CovenantKeyName, "--campaign", CovenantCampaign.ToString(), "--yes"];

        CliTestResult result = await RunAsync(handler, args, new RecordingPrompt(handler, answer: false));

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Contains(DrainSentence, result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain(DrainSentence, result.Output, StringComparison.Ordinal);

        if (json)
        {
            using JsonDocument document = JsonDocument.Parse(result.Output);

            Assert.Equal(handler.PreparedMutationId, document.RootElement.GetProperty("mutationId").GetGuid());
        }
    }

    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task Decline_writes_one_cancellation_document_and_exits_zero(string store)
    {
        ErasureHandler handler = new();

        CliTestResult result = await RunAsync(handler, [.. EraseArgs(store), "--json"], new RecordingPrompt(handler, answer: false));

        Assert.Equal(0, result.ExitCode);

        MemoryErasureCancellationPayload payload =
            JsonSerializer.Deserialize(result.Output, CliJsonContext.Default.MemoryErasureCancellationPayload)!;

        Assert.Equal(("erase", Store(store), true), (payload.Operation, payload.Store, payload.Cancelled));

        Assert.Equal(handler.PreparedMutationId, payload.MutationId);

        Assert.Contains("prompt", handler.Events);

        Assert.DoesNotContain(handler.Events, e => e.EndsWith("/erase", StringComparison.Ordinal));

        Assert.Contains($"{Store(store)} erasure cancelled.", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task Yes_with_json_writes_exactly_one_result_document(string store)
    {
        ErasureHandler handler = new();

        CliTestResult result = await RunAsync(
            handler,
            [.. EraseArgs(store), "--json", "--yes"],
            new RecordingPrompt(handler, answer: false));

        Assert.True(result.ExitCode == 0, result.Error);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(handler.PreparedMutationId, document.RootElement.GetProperty("mutationId").GetGuid());

        Assert.Equal(store, document.RootElement.GetProperty("store").GetString(), ignoreCase: true);

        Assert.DoesNotContain("prompt", handler.Events);

        Assert.Contains(CovenantExternalRetentionDisclosure.DestructiveOperationText, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task Non_interactive_without_yes_exits_two_and_never_applies(string store)
    {
        ErasureHandler handler = new();

        ServiceCollection services = Services(handler);

        // The production prompt, seeing redirected standard input: the refusal is the typed one that
        // names --yes, never a cancellation reported as success.
        services.AddSingleton<IConfirmationPrompt>(provider => new ConfirmationPrompt(
            provider.GetRequiredService<IConsoleDispatcher>(),
            new CliInvocationOptions(Json: false, Plain: false, Yes: false),
            TextReader.Null,
            isOutputRedirected: static () => false,
            isInputRedirected: static () => true));

        CliTestResult result = await CliTestHarness.RunAsync(services, EraseArgs(store));

        Assert.Equal(2, result.ExitCode);

        Assert.DoesNotContain(handler.Events, e => e.EndsWith("/erase", StringComparison.Ordinal));

        // The disclosure was still written: the refusal comes from asking, which comes after it.
        Assert.Contains(CovenantExternalRetentionDisclosure.DestructiveOperationText, result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shipped prompt treats <c>--json</c> as non-interactive by declaration, so a structured run
    /// without <c>--yes</c> is refused rather than declined — the contract the Command Reference states.
    /// </summary>
    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task A_json_run_without_yes_is_refused_by_the_shipped_prompt_and_never_applies(string store)
    {
        ErasureHandler handler = new();

        CliTestResult result = await CliTestHarness.RunAsync(Services(handler), [.. EraseArgs(store), "--json"]);

        Assert.Equal(2, result.ExitCode);

        Assert.DoesNotContain(handler.Events, e => e.EndsWith("/erase", StringComparison.Ordinal));

        Assert.Contains(CovenantExternalRetentionDisclosure.DestructiveOperationText, result.Error, StringComparison.Ordinal);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(2, document.RootElement.GetProperty("exitCode").GetInt32());

        Assert.False(document.RootElement.TryGetProperty("cancelled", out _));
    }

    [Fact]
    public async Task Scrub_pending_result_names_the_reasons_and_the_scrub_verb()
    {
        ErasureHandler handler = new()
        {
            Result = ErasureHandler.DefaultResult with
            {
                Local = ErasureHandler.DefaultResult.Local with
                {
                    Outcome = MemoryLocalErasureOutcome.RowsRemovedScrubPending,
                    PendingReasons = [MemoryErasureScrubPendingReason.WalCheckpointPending],
                    WalCheckpointAttempt = MemoryErasureWalCheckpointAttempt.Busy,
                },
            },
        };

        CliTestResult result = await RunAsync(handler, ["memory", "saga", "erase", SagaId], new RecordingPrompt(handler, answer: true));

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Contains("scrub pending (WAL checkpoint pending)", result.Output, StringComparison.Ordinal);

        Assert.Contains("Run 'arcanum memory erasure scrub' to finish.", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reason_no_scrub_can_clear_is_not_sent_to_the_scrub_verb()
    {
        ErasureHandler handler = new()
        {
            Result = ErasureHandler.DefaultResult with
            {
                Local = ErasureHandler.DefaultResult.Local with
                {
                    Outcome = MemoryLocalErasureOutcome.RowsRemovedScrubPending,
                    PendingReasons = [MemoryErasureScrubPendingReason.FullTextSecureDeleteUnverified],
                },
            },
        };

        CliTestResult result = await RunAsync(handler, ["memory", "lexicon", "erase", "Operator"], new RecordingPrompt(handler, answer: true));

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Contains("scrub pending (full-text secure delete unverified)", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("arcanum memory erasure scrub", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_refusal_exits_one_and_an_unreachable_host_exits_three()
    {
        ErasureHandler refusing = new()
        {
            Failures =
            {
                ["/api/memory/saga/erase"] = (HttpStatusCode.Conflict, new Error(ErrorCodes.MemoryErasure.StalePlan, "What this erase would remove changed after it was prepared.")),
            },
        };

        CliTestResult refused = await RunAsync(refusing, ["memory", "saga", "erase", SagaId], new RecordingPrompt(refusing, answer: true));

        Assert.Equal(1, refused.ExitCode);

        Assert.Contains("What this erase would remove changed after it was prepared.", refused.Error, StringComparison.Ordinal);

        ErasureHandler unreachable = new()
        {
            Exceptions = { ["/api/memory/saga/erase/prepare"] = new HttpRequestException("offline") },
        };

        CliTestResult offline = await RunAsync(unreachable, ["memory", "saga", "erase", SagaId], new RecordingPrompt(unreachable, answer: true));

        Assert.Equal(3, offline.ExitCode);

        Assert.DoesNotContain("prompt", unreachable.Events);
    }

    public static TheoryData<string, string, int> RefusalsAtApply
    {
        get
        {
            (string Code, int Status)[] refusals =
            [
                (ErrorCodes.MemoryErasure.SubjectErased, 410),
                (ErrorCodes.MemoryErasure.StalePlan, 409),
                (ErrorCodes.MemoryErasure.KeyLost, 409),
                (ErrorCodes.MemoryErasure.KeyUnavailable, 503),
                (ErrorCodes.MemoryErasure.ErasureIncomplete, 500),
                (ErrorCodes.MemoryErasure.InvalidPreflight, 400),
                (ErrorCodes.MemoryErasure.Unavailable, 503),
                (ErrorCodes.Covenant.ManualRecoveryRequired, 503),
                (ErrorCodes.Covenant.ForbiddenAuthority, 403),
                (ErrorCodes.Hub.Unhandled, 500),
            ];

            TheoryData<string, string, int> data = [];

            foreach (string store in new[] { "saga", "lexicon", "covenant" })
            {
                foreach ((string code, int status) in refusals)
                {
                    data.Add(store, code, status);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RefusalsAtApply))]
    public async Task Every_erase_refusal_exits_one_with_the_hosts_own_message(string store, string code, int status)
    {
        string message = $"The host refused this erase with {code}.";

        ErasureHandler handler = new()
        {
            Failures = { [ApplyPath(store)] = ((HttpStatusCode)status, new Error(code, message)) },
        };

        CliTestResult result = await RunAsync(handler, EraseArgs(store), new RecordingPrompt(handler, answer: true));

        Assert.Equal(1, result.ExitCode);

        Assert.Contains(message, result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("No other memory store was touched.", result.Output, StringComparison.Ordinal);

        // A typed refusal proves the erase rolled back. Two answers do not: the Covenant saying the
        // commit's outcome could not be read back, and the host's catch-all for an exception nothing
        // classified — which is how a Saga or Lexicon commit that failed and could not be read back
        // reaches the wire. Only those may say the erase may have happened.
        bool uncertain = code is ErrorCodes.Covenant.ManualRecoveryRequired or ErrorCodes.Hub.Unhandled;

        Assert.Equal(uncertain, result.Error.Contains(MayHaveApplied, StringComparison.Ordinal));

        Assert.Equal(uncertain, result.Error.Contains(handler.PreparedMutationId.ToString("D"), StringComparison.Ordinal));
    }

    public static TheoryData<string, string> UnreadableApplyAnswers
    {
        get
        {
            TheoryData<string, string> data = [];

            foreach (string store in new[] { "saga", "lexicon", "covenant" })
            {
                foreach (string answer in new[] { "undecodable", "success-without-data", "error-without-envelope" })
                {
                    data.Add(store, answer);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// An apply whose answer cannot be read is an apply whose outcome is unknown.
    /// </summary>
    /// <remarks>
    /// The host processed the request — headers came back — but the CLI cannot tell a committed
    /// erase from a refused one: a 2xx body that is not an envelope, a success envelope with no
    /// result, or an error status with no envelope at all.
    /// </remarks>
    [Theory]
    [MemberData(nameof(UnreadableApplyAnswers))]
    public async Task An_apply_answer_that_cannot_be_read_names_the_mutation(string store, string answer)
    {
        ErasureHandler handler = new()
        {
            RawResponses =
            {
                [ApplyPath(store)] = answer switch
                {
                    "undecodable" => (HttpStatusCode.OK, "this is not an envelope"),
                    "success-without-data" => (HttpStatusCode.OK, "{\"isSuccess\":true}"),
                    _ => (HttpStatusCode.InternalServerError, ""),
                },
            },
        };

        CliTestResult result = await RunAsync(handler, EraseArgs(store), new RecordingPrompt(handler, answer: true));

        Assert.Equal(1, result.ExitCode);

        Assert.Contains(handler.PreparedMutationId.ToString("D"), result.Error, StringComparison.Ordinal);

        Assert.Contains(MayHaveApplied, result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("No other memory store was touched.", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ctrl-C after the apply was sent still cancels, and says the erase may have happened.
    /// </summary>
    /// <remarks>
    /// Driven at the handler, because only the command tree's own token is a real Ctrl-C: the
    /// cancellation must be the caller's for the client to rethrow it rather than report a timeout.
    /// The exception propagating is what the tree maps to exit 130.
    /// </remarks>
    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task A_cancellation_after_the_apply_was_sent_names_the_mutation_and_still_cancels(string store)
    {
        using CancellationTokenSource cancellation = new();

        ErasureHandler handler = new() { CancelOnApply = cancellation };

        StringWriter error = new();

        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => EraseDirectlyAsync(handler, store, error, prompt: null, cancellation.Token));

        Assert.Equal(CliExitCode.Cancelled, CliFailureMapper.Map(cancelled).ExitCode);

        Assert.Contains($"POST {ApplyPath(store)}", handler.Events);

        Assert.Contains(handler.PreparedMutationId.ToString("D"), error.ToString(), StringComparison.Ordinal);

        Assert.Contains(MayHaveApplied, error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A cancellation that lands after the question was answered but before the apply went out
    /// cancels an erase that never started, so it sends nothing and claims nothing.
    /// </summary>
    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task A_cancellation_before_the_apply_is_sent_sends_nothing_and_claims_nothing(string store)
    {
        using CancellationTokenSource cancellation = new();

        ErasureHandler handler = new();

        StringWriter error = new();

        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => EraseDirectlyAsync(handler, store, error, new CancellingApproval(cancellation), cancellation.Token));

        Assert.Equal(CliExitCode.Cancelled, CliFailureMapper.Map(cancelled).ExitCode);

        Assert.DoesNotContain(handler.Events, e => e.EndsWith("/erase", StringComparison.Ordinal));

        Assert.DoesNotContain(MayHaveApplied, error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs one erase handler with the caller's own cancellation token, which only a direct call can
    /// supply: through the command tree the token is System.CommandLine's.
    /// </summary>
    /// <remarks>
    /// With no prompt the run approves through <c>--yes</c>; with one, the prompt is asked.
    /// </remarks>
    private static async Task<int> EraseDirectlyAsync(
        ErasureHandler handler,
        string store,
        StringWriter error,
        IConfirmationPrompt? prompt,
        CancellationToken cancellationToken)
    {
        CliInvocationOptions options = new(Json: false, Plain: false, Yes: prompt is null);

        ServiceCollection services = Services(handler);

        services.AddSingleton<IConsoleDispatcher>(new ConsoleDispatcher(new StringWriter(), error, options));

        if (prompt is not null)
        {
            services.AddSingleton(prompt);
        }

        await using ServiceProvider provider = services.BuildServiceProvider();

        using IDisposable invocation = CliInvocationContext.Push(options);

        return store switch
        {
            "saga" => await provider.GetRequiredService<MemoryCommands>().SagaErase(SagaId, null, cancellationToken),
            "lexicon" => await provider.GetRequiredService<MemoryCommands>().LexiconErase("Operator", null, cancellationToken),
            _ => await provider.GetRequiredService<CovenantCommands>().Erase(CovenantKeyName, CovenantCampaign, cancellationToken),
        };
    }

    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task A_cancellation_at_the_question_sends_no_apply_and_claims_nothing(string store)
    {
        ErasureHandler handler = new();

        CliTestResult result = await RunAsync(handler, EraseArgs(store), new RecordingPrompt(handler, answer: true) { Cancel = true });

        Assert.Equal(130, result.ExitCode);

        Assert.DoesNotContain(handler.Events, e => e.EndsWith("/erase", StringComparison.Ordinal));

        Assert.DoesNotContain(MayHaveApplied, result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal under <c>--json --yes</c> writes the CLI error envelope as the one stdout document,
    /// whichever store refused, with the message also on the diagnostic stream for a human reader.
    /// </summary>
    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task A_refusal_under_json_writes_one_error_envelope(string store)
    {
        ErasureHandler handler = new()
        {
            Failures =
            {
                [ApplyPath(store)] = (HttpStatusCode.Conflict, new Error(ErrorCodes.MemoryErasure.StalePlan, "What this erase would remove changed after it was prepared.")),
            },
        };

        CliTestResult result = await RunAsync(handler, [.. EraseArgs(store), "--json", "--yes"], new RecordingPrompt(handler, answer: false));

        Assert.Equal(1, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal("What this erase would remove changed after it was prepared.", document.RootElement.GetProperty("error").GetString());

        Assert.Equal(1, document.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task A_subject_already_erased_at_prepare_is_refused_before_the_question(string store)
    {
        ErasureHandler handler = new()
        {
            Failures =
            {
                [PreparePath(store)] = (HttpStatusCode.Gone, new Error(ErrorCodes.MemoryErasure.SubjectErased, "This item was already erased.")),
            },
        };

        CliTestResult result = await RunAsync(handler, EraseArgs(store), new RecordingPrompt(handler, answer: true));

        Assert.Equal(1, result.ExitCode);

        Assert.Contains("This item was already erased.", result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("prompt", handler.Events);
    }

    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task An_apply_the_host_never_answered_names_the_mutation_and_exits_three(string store)
    {
        ErasureHandler handler = new()
        {
            Exceptions = { [ApplyPath(store)] = new HttpRequestException("offline") },
        };

        CliTestResult result = await RunAsync(handler, EraseArgs(store), new RecordingPrompt(handler, answer: true));

        Assert.Equal(3, result.ExitCode);

        // The erase may have committed before the connection failed, so the identity a later inspection
        // needs is not lost with the answer.
        Assert.Contains(handler.PreparedMutationId.ToString("D"), result.Error, StringComparison.Ordinal);

        Assert.Contains("may have been applied", result.Error, StringComparison.Ordinal);
    }

    private static string[] EraseArgs(string store) => store switch
    {
        "saga" => ["memory", "saga", "erase", SagaId],
        "lexicon" => ["memory", "lexicon", "erase", "Operator"],
        "covenant" => ["memory", "covenant", "erase", CovenantKeyName, "--campaign", CovenantCampaign.ToString()],
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    private static string PreparePath(string store) => $"/api/memory/{store}/erase/prepare";

    private static Guid MutationIdIn(ErasureHandler handler, string path)
    {
        using JsonDocument body = JsonDocument.Parse(handler.Body(path));

        return body.RootElement.GetProperty("mutationId").GetGuid();
    }

    /// <summary>The shared sentence, a channel line and the help targets, in that order, all before the question.</summary>
    private static void AssertDisclosedBeforeThePrompt(RecordingPrompt prompt)
    {
        int disclosure = prompt.BeforePrompt.IndexOf(CovenantExternalRetentionDisclosure.DestructiveOperationText, StringComparison.Ordinal);

        int channel = prompt.BeforePrompt.IndexOf("  Encrypted backups: ", StringComparison.Ordinal);

        int guidance = prompt.BeforePrompt.IndexOf("Retention guidance", StringComparison.Ordinal);

        Assert.True(disclosure >= 0, prompt.BeforePrompt);

        Assert.True(channel > disclosure, prompt.BeforePrompt);

        Assert.True(guidance > channel, prompt.BeforePrompt);
    }

    private static string ApplyPath(string store) => $"/api/memory/{store}/erase";

    private static MemoryReviewStore Store(string store) => store switch
    {
        "saga" => MemoryReviewStore.Saga,
        "lexicon" => MemoryReviewStore.Lexicon,
        "covenant" => MemoryReviewStore.Covenant,
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    private static Task<CliTestResult> RunAsync(ErasureHandler handler, string[] args, RecordingPrompt prompt)
    {
        ServiceCollection services = Services(handler);

        services.AddSingleton<IConfirmationPrompt>(prompt);

        services.AddSingleton<IConsoleDispatcher>(provider => new ObservingDispatcher(
            new ConsoleDispatcher(provider.GetRequiredService<ICliInvocationContext>()),
            prompt));

        return CliTestHarness.RunAsync(services, args);
    }

    private static ServiceCollection Services(ErasureHandler handler)
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        services.AddSingleton<IHttpClientFactory>(new LexiconCliFixture.Factory(handler));

        services.AddSingleton<ISecretStore>(new LexiconCliFixture.Secrets());

        CliTestHarness.AddKeyedArcanumResponder(services, LexiconCliFixture.Key);

        // An erase names its own scope. Saved and active context must never choose it.
        services.AddSingleton<ICliContextStore>(new ForbiddenContext());

        return services;
    }

    /// <summary>
    /// A host that answers every erase route from fixed values, echoing the caller's mutation identity.
    /// </summary>
    internal sealed class ErasureHandler : HttpMessageHandler
    {
        internal const string SagaContent = "The operator prefers terse release notes.";

        internal const string AuthoredHashMarker = "authored-digest-marker";

        internal const string RenderedHashMarker = "rendered-digest-marker";

        internal const string ProvenanceDigestMarker = "provenance-digest-marker";

        internal static readonly MemoryErasureExternalExposureDto External = new(
            MemoryExternalRevocation.NotPerformed,
            [
                new(MemoryExternalChannel.InferenceProviderAuthorship, MemoryExternalEvidence.Known),
                new(MemoryExternalChannel.InferenceProviderContext, MemoryExternalEvidence.NotRecorded),
                new(MemoryExternalChannel.EmbeddingProvider, MemoryExternalEvidence.Known),
                new(MemoryExternalChannel.EncryptedBackup, MemoryExternalEvidence.ReceiptWindow),
                new(MemoryExternalChannel.OtherExternal, MemoryExternalEvidence.NotRecorded),
            ]);

        internal static readonly CovenantErasurePlanFacts CovenantFacts = new(
            ConfirmedVersions: 2,
            ProposedVersions: 1,
            ProvenanceLeaves: 0,
            MutationReceipts: 3,
            CurationRows: 0,
            OutboxRows: 0,
            SearchDocuments: 2,
            ReclaimsKey: false,
            RetainsCampaignMask: false,
            GlobalConfirmedResurfaces: false,
            IsPinned: false,
            AffectedCampaigns: 1);

        internal static readonly MemoryErasurePreflightDto DefaultPreflight = new(
            MemoryReviewStore.Saga,
            Guid.Empty,
            new string('1', 64),
            new string('2', 64),
            new MemoryErasurePlanDto(1, 7, 0, 1, Pinned: false, Lexicon: null, Covenant: null),
            External,
            [MemoryRetainedLocalCopy.SessionTranscripts, MemoryRetainedLocalCopy.OtherLocalState],
            [MemoryErasureNote.OtherScopesUnaffected],
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 12, 5, 0, TimeSpan.Zero),
            "token-1");

        internal static readonly MemoryErasureResultDto DefaultResult = new(
            MemoryReviewStore.Saga,
            Guid.Empty,
            Replayed: false,
            new string('2', 64),
            new MemoryErasureLocalResultDto(
                MemoryLocalErasureOutcome.Verified,
                [],
                MemoryErasureWalCheckpointAttempt.Truncated,
                1,
                7,
                0,
                1,
                SuppressionFingerprintRecorded: true),
            External,
            [MemoryRetainedLocalCopy.SessionTranscripts, MemoryRetainedLocalCopy.OtherLocalState],
            [MemoryErasureNote.OtherScopesUnaffected]);

        internal static readonly CovenantDetailDto DefaultCovenantDetail = new(
            CovenantScope.Campaign,
            CovenantCampaign,
            CovenantKeyName,
            CovenantEntryId,
            Head(ConfirmedVersion, CovenantLane.Confirmed, 3),
            Head(ProposedVersion, CovenantLane.Proposed, 1),
            KeyEpoch: 1,
            ConfirmedSources: null,
            ProposedSources: null);

        internal List<string> Events { get; } = [];

        internal Dictionary<string, string> Bodies { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, string> Responses { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, (HttpStatusCode Status, Error Error)> Failures { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, Exception> Exceptions { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, (HttpStatusCode Status, string Body)> RawResponses { get; } = new(StringComparer.Ordinal);

        /// <summary>Cancels the caller's token when the apply arrives, as Ctrl-C after sending would.</summary>
        internal CancellationTokenSource? CancelOnApply { get; init; }

        internal MemoryErasurePreflightDto Preflight { get; init; } = DefaultPreflight;

        internal MemoryErasureResultDto Result { get; init; } = DefaultResult;

        internal CovenantDetailDto CovenantDetail { get; init; } = DefaultCovenantDetail;

        internal Guid PreparedMutationId { get; private set; }

        internal string Body(string path) =>
            Bodies.TryGetValue(path, out string? body)
                ? body
                : throw new Xunit.Sdk.XunitException($"No request reached {path}. Events: {string.Join(", ", Events)}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;

            Events.Add($"{request.Method} {path}");

            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            Bodies[path] = body;

            if (Exceptions.TryGetValue(path, out Exception? exception))
            {
                throw exception;
            }

            if (path.EndsWith("/erase/prepare", StringComparison.Ordinal))
            {
                PreparedMutationId = MutationIdOf(body);
            }

            if (path.EndsWith("/erase", StringComparison.Ordinal) && CancelOnApply is { } cancel)
            {
                await cancel.CancelAsync();

                cancellationToken.ThrowIfCancellationRequested();
            }

            if (RawResponses.TryGetValue(path, out (HttpStatusCode Status, string Body) raw))
            {
                return Respond(path, raw.Status, raw.Body);
            }

            if (Failures.TryGetValue(path, out (HttpStatusCode Status, Error Error) failure))
            {
                return Respond(path, failure.Status, JsonSerializer.Serialize(
                    new ApiResponse<MemoryErasurePreflightDto>(null, false, failure.Error),
                    ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto));
            }

            return path switch
            {
                $"/api/memory/saga/{SagaId}" => Respond(path, Envelope(SagaDetail, ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail)),
                "/api/memory/lexicon/show" => Respond(path, Envelope(LexiconCliFixture.Detail, ArcanumJsonContext.Default.ApiResponseLexiconEntryDetail)),
                "/api/memory/covenant/detail" => Respond(path, Envelope(CovenantDetail, ArcanumJsonContext.Default.ApiResponseCovenantDetailDto)),
                _ when path.EndsWith("/erase/prepare", StringComparison.Ordinal) => Respond(path, Envelope(
                    Preflight with { Store = StoreOf(path), MutationId = MutationIdOf(body), Notes = HostNotes(path, Preflight.Notes) },
                    ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto)),
                _ when path.EndsWith("/erase", StringComparison.Ordinal) => Respond(path, Envelope(
                    Result with { Store = StoreOf(path), MutationId = MutationIdOf(body), Notes = HostNotes(path, Result.Notes) },
                    ArcanumJsonContext.Default.ApiResponseMemoryErasureResultDto)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static SagaMemoryDetail SagaDetail { get; } = new(
            new SagaMemoryDto(SagaId, SagaContent, DateTimeOffset.UnixEpoch, null, null, null),
            ShownHash,
            new SagaMemoryLifecycle(null, null),
            SagaRetrievalEligibility.Eligible,
            new AnnalClaimHead("claim-1", AnnalSubjectStore.Saga, SagaId, ShownClaimVersion, 2, AnnalOperation.Correct, DateTimeOffset.UnixEpoch),
            []);

        private static CovenantHeadDto Head(Guid versionId, CovenantLane lane, long revision) =>
            new(
                CovenantEntryId,
                versionId,
                CovenantScope.Campaign,
                CovenantCampaign,
                CovenantKeyName,
                lane,
                revision,
                CovenantLifecycle.Set,
                CovenantOrigin.Operator,
                AuthoredHashMarker,
                RenderedHashMarker,
                64,
                0,
                ProvenanceDigestMarker,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                CovenantEffectiveShadowState.NotEvaluated,
                CovenantEffectiveMaterialization.NotEvaluated);

        /// <summary>
        /// The host always states the drain cost on a Covenant erase, so the fake does too unless a test
        /// already put it there.
        /// </summary>
        private static MemoryErasureNote[] HostNotes(string path, MemoryErasureNote[] notes) =>
            StoreOf(path) is MemoryReviewStore.Covenant && !notes.Contains(MemoryErasureNote.CovenantDrainsInFlightTurns)
                ? [.. notes, MemoryErasureNote.CovenantDrainsInFlightTurns]
                : notes;

        private static Guid MutationIdOf(string body)
        {
            using JsonDocument document = JsonDocument.Parse(body);

            return document.RootElement.GetProperty("mutationId").GetGuid();
        }

        private static MemoryReviewStore StoreOf(string path) =>
            path.StartsWith("/api/memory/saga/", StringComparison.Ordinal) ? MemoryReviewStore.Saga
            : path.StartsWith("/api/memory/lexicon/", StringComparison.Ordinal) ? MemoryReviewStore.Lexicon
            : MemoryReviewStore.Covenant;

        private static string Envelope<T>(T value, JsonTypeInfo<ApiResponse<T>> typeInfo) =>
            JsonSerializer.Serialize(new ApiResponse<T>(value, true, null), typeInfo);

        private HttpResponseMessage Respond(string path, string json) => Respond(path, HttpStatusCode.OK, json);

        private HttpResponseMessage Respond(string path, HttpStatusCode status, string json)
        {
            Responses[path] = json;

            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>
    /// Answers the question, having written it to the diagnostic stream exactly as the shipped prompt
    /// does, so every assertion over what reached a stream covers the question as well.
    /// </summary>
    /// <remarks>
    /// <see cref="BeforePrompt"/> is everything the operator saw before answering, the question included.
    /// </remarks>
    private sealed class RecordingPrompt(ErasureHandler handler, bool answer) : IConfirmationPrompt
    {
        internal StringBuilder Rendered { get; } = new();

        internal string BeforePrompt { get; private set; } = "";

        internal string? Question { get; private set; }

        /// <summary>Answers by cancelling, as Ctrl-C at the question would.</summary>
        internal bool Cancel { get; init; }

        /// <summary>The dispatcher the run writes through, set when the run composes it.</summary>
        internal IConsoleDispatcher? Dispatcher { get; set; }

        public Task<bool> PromptForConfirmationAsync(string question, CancellationToken cancellationToken)
        {
            Dispatcher?.WriteDiagnostic($"{question} [y/N]");

            BeforePrompt = Rendered.ToString();

            Question = question;

            handler.Events.Add("prompt");

            return Cancel ? throw new OperationCanceledException() : Task.FromResult(answer);
        }
    }

    /// <summary>Approves the question, then cancels — Ctrl-C between the answer and the apply.</summary>
    private sealed class CancellingApproval(CancellationTokenSource cancellation) : IConfirmationPrompt
    {
        public async Task<bool> PromptForConfirmationAsync(string question, CancellationToken cancellationToken)
        {
            await cancellation.CancelAsync();

            return true;
        }
    }

    private sealed class ObservingDispatcher : IConsoleDispatcher
    {
        private readonly IConsoleDispatcher inner;

        private readonly RecordingPrompt prompt;

        internal ObservingDispatcher(IConsoleDispatcher inner, RecordingPrompt prompt)
        {
            this.inner = inner;

            this.prompt = prompt;

            prompt.Dispatcher = this;
        }

        public void WritePayload(string value)
        {
            prompt.Rendered.Append(value).Append('\n');

            inner.WritePayload(value);
        }

        public void WriteDiagnostic(string value)
        {
            prompt.Rendered.Append(value).Append('\n');

            inner.WriteDiagnostic(value);
        }

        public void WriteVerbose(string value) => inner.WriteVerbose(value);

        public void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo) => inner.WriteJson(value, typeInfo);

        public void WriteJson(JsonElement value) => inner.WriteJson(value);

        public void BeginJsonStream() => inner.BeginJsonStream();
    }

    private sealed class ForbiddenContext : ICliContextStore
    {
        public string FilePath => "unused";

        public CliContextDocument Load() => throw new InvalidOperationException("An erase must not consult saved context.");
    }
}
