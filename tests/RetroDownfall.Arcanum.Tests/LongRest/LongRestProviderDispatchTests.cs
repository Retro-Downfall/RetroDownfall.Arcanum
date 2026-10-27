using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

[Collection("Grimoire")]
public sealed class LongRestProviderDispatchTests
{
    [Theory]
    [InlineData(LongRestProviderPayloadKind.Summary, HttpStatusCode.BadRequest)]
    [InlineData(LongRestProviderPayloadKind.Summary, HttpStatusCode.TooManyRequests)]
    [InlineData(LongRestProviderPayloadKind.Summary, HttpStatusCode.InternalServerError)]
    [InlineData(LongRestProviderPayloadKind.Adjudication, HttpStatusCode.BadRequest)]
    [InlineData(LongRestProviderPayloadKind.Adjudication, HttpStatusCode.InternalServerError)]
    [InlineData(LongRestProviderPayloadKind.Embedding, HttpStatusCode.BadRequest)]
    [InlineData(LongRestProviderPayloadKind.Embedding, HttpStatusCode.TooManyRequests)]
    [InlineData(LongRestProviderPayloadKind.Embedding, HttpStatusCode.InternalServerError)]
    public async Task Each_explicit_attempt_sends_once_with_its_own_committed_disclosure(LongRestProviderPayloadKind kind, HttpStatusCode status)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync(kind);

        f.Handler.Status = status;

        List<CovenantDigest> receipts = [];

        List<Guid> attempts = [];

        for (int correction = 0; correction < 2; correction++)
        {
            LongRestDispatchAttempt attempt = await f.PrepareAsync();

            (long State, long Ordinal, long Receipts)? observed = null;

            f.Handler.InsideSend = async () =>
            {
                string where = " WHERE WorkDigest = X'" + Convert.ToHexString(LongRestDiscoveryHash.Work(f.Payload.Work).Value.Bytes) + "';";

                observed = ((long)(await f.H.ScalarAsync("SELECT StateCode FROM long_rest_provider_effects" + where))!,
                    (long)(await f.H.ScalarAsync("SELECT PhysicalOrdinal FROM long_rest_provider_effects" + where))!,
                    (long)(await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"))!);
            };

            Result<LongRestProviderResult> result = await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
                f.Callback(attempt), f.Admission(), CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(correction + 1, f.Handler.SendCount);

            Assert.Equal((3L, (long)attempt.PhysicalOrdinal, (long)correction + 1), observed);

            LongRestDispatchAttempt sent = f.CurrentAttempt!;

            receipts.Add(sent.DisclosureReceiptDigest!.Value);

            attempts.Add(sent.AttemptId);

            LongRestSweepHarness.Require(await f.Attempts.CompleteAttemptAsync(sent,
                new(LongRestDispatchAttemptState.Failed, new(0, 0, 0, 0, 0), null, "LongRest.TransportFailure"), CancellationToken.None));
        }

        Assert.NotEqual(receipts[0], receipts[1]);

        Assert.NotEqual(attempts[0], attempts[1]);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        using JsonDocument body = JsonDocument.Parse(f.Handler.LastBody!);
        Assert.Equal(kind == LongRestProviderPayloadKind.Embedding ? "embedding" : "model", body.RootElement.GetProperty("model").GetString());

        if (kind == LongRestProviderPayloadKind.Embedding)
        {
            Assert.Equal(64, body.RootElement.GetProperty("dimensions").GetInt32());

            Assert.Equal("Accepted summary.\n", body.RootElement.GetProperty("input")[0].GetString());
        }
        else
        {
            Assert.False(body.RootElement.TryGetProperty("tools", out _));

            Assert.False(body.RootElement.GetProperty("response_format").GetProperty("json_schema").TryGetProperty("strict", out _));

            LongRestChatRequest request = LongRestProviderCallFreezer.RenderChat(f.Payload).Value;

            Assert.Equal(request.SystemPrompt, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());

            Assert.Equal(request.UserPrompt, body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        }
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("source-before")]
    [InlineData("source-after-journal")]
    [InlineData("claim-before")]
    [InlineData("claim-after-journal")]
    [InlineData("lease-before")]
    [InlineData("lease-after-journal")]
    [InlineData("config-after-journal")]
    public async Task A_refused_or_stale_callback_never_reaches_HTTP(string fence)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync();

        LongRestDispatchAttempt attempt = await f.PrepareAsync();

        Func<CancellationToken, Task> callback = f.Callback(attempt, fence);

        LongRestBeforeSendRefusal refusal = await Assert.ThrowsAsync<LongRestBeforeSendRefusal>(() =>
            f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance, callback, f.Admission(), CancellationToken.None));

        // These fixture callbacks throw untyped exceptions. Their text is not a trusted code;
        // only the independently observed epoch change has a more specific diagnosis.
        Assert.Equal(fence == "config-after-journal" ? ErrorCodes.LongRest.ConfigurationChanged : ErrorCodes.Hub.Error, refusal.Error.Code);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT FoldGroup FROM long_rest_discovery_generations;"));
    }

    [Fact]
    public async Task Source_mutation_while_disclosure_acknowledgement_is_outstanding_refuses_the_send()
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync();

        LongRestDispatchAttempt attempt = await f.PrepareAsync();

        TaskCompletionSource committed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        f.JournalBarrier = () => { committed.SetResult(); return release.Task; };

        Task<Result<LongRestProviderResult>> dispatch = f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            f.Callback(attempt), f.Admission(), CancellationToken.None);
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));

        await f.MutateSourceAsync();

        release.SetResult();

        LongRestBeforeSendRefusal refusal = await Assert.ThrowsAsync<LongRestBeforeSendRefusal>(async () => await dispatch);

        Assert.Equal(ErrorCodes.Hub.Error, refusal.Error.Code);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
    }

    [Theory]
    [InlineData(LongRestProviderPayloadKind.Summary, 400, "request limits")]
    [InlineData(LongRestProviderPayloadKind.Summary, 401, "credential")]
    [InlineData(LongRestProviderPayloadKind.Embedding, 401, "credential")]
    [InlineData(LongRestProviderPayloadKind.Embedding, 404, "model and endpoint")]
    [InlineData(LongRestProviderPayloadKind.Summary, 429, "rate limit")]
    [InlineData(LongRestProviderPayloadKind.Embedding, 503, "service")]
    public async Task Provider_failure_identifies_status_remedy_and_safe_structured_context(LongRestProviderPayloadKind kind, int status, string remedy)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync(kind);

        f.Handler.Status = (HttpStatusCode)status;

        Result<LongRestProviderResult> result = await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            f.Callback(await f.PrepareAsync()), f.Admission(), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(status is 429 or >= 500 ? ErrorCodes.LongRest.ProviderUnavailable : ErrorCodes.LongRest.ProviderRejected, result.Error.Code);

        Assert.Contains($"HTTP {status}", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains(remedy, result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("protected-test-body", result.Error.Message, StringComparison.Ordinal);

        Dictionary<string, object?> fields = Assert.Single(f.Log.Fields);

        Assert.Equal(LogLevel.Warning, Assert.Single(f.Log.Levels));

        Assert.Equal("provider", fields["Provider"]);

        Assert.Equal(kind == LongRestProviderPayloadKind.Embedding ? "embedding" : "model", fields["Model"]);

        Assert.Equal(f.Payload.Work.OperationId, fields["OperationId"]);

        Assert.Equal(status, fields["HttpStatus"]);

        Assert.Equal(result.Error.Code, fields["Code"]);

        Assert.Equal(1, f.Handler.SendCount);

        Assert.DoesNotContain("protected-test-body", Assert.Single(f.Log.Messages), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HTTP 401 (private-fault-body)")]
    [InlineData("private-fault-body Status: 404")]
    public async Task Unknown_transport_status_does_not_infer_status_from_exception_text(string body)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync();

        f.Handler.TransportFailure = new System.ClientModel.ClientResultException(body, null!);

        Result<LongRestProviderResult> result = await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            f.Callback(await f.PrepareAsync()), f.Admission(), CancellationToken.None);

        Assert.Equal(ErrorCodes.LongRest.ProviderUnavailable, result.Error.Code);

        Assert.Contains("status is unknown", result.Error.Message, StringComparison.Ordinal);

        Assert.Null(Assert.Single(f.Log.Fields)["HttpStatus"]);

        Assert.DoesNotContain("private-fault-body", Assert.Single(f.Log.Messages), StringComparison.Ordinal);

        Assert.Equal(1, f.Handler.SendCount);
    }

    [Fact]
    public async Task Provider_error_bodies_never_enter_the_public_failure_or_executor_log()
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync();

        f.Handler.Status = HttpStatusCode.BadRequest;

        Result<LongRestProviderResult> result = await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            f.Callback(await f.PrepareAsync()), f.Admission(), CancellationToken.None);
        Assert.True(result.IsFailure);

        Assert.DoesNotContain("protected-test-body", result.Error.Message, StringComparison.Ordinal);

        string logged = Assert.Single(f.Log.Messages);

        Assert.DoesNotContain("protected-test-body", logged, StringComparison.Ordinal);

        Assert.DoesNotContain("Alpha observation", logged, StringComparison.Ordinal);

        Assert.Contains("ClientResultException", logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reusing_one_child_callback_never_sends_a_second_request()
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync();

        LongRestDispatchAttempt attempt = await f.PrepareAsync();

        Func<CancellationToken, Task> callback = f.Callback(attempt);

        Assert.True((await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance, callback, f.Admission(), CancellationToken.None)).IsSuccess);

        LongRestBeforeSendRefusal refusal = await Assert.ThrowsAsync<LongRestBeforeSendRefusal>(() =>
            f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance, callback, f.Admission(), CancellationToken.None));

        Assert.Equal(ErrorCodes.Hub.Error, refusal.Error.Code);

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("source")]
    [InlineData("owner")]
    [InlineData("config")]
    [InlineData("gate")]
    [InlineData("model")]
    [InlineData("covenant")]
    [InlineData("cancellation")]
    public async Task Paid_return_keeps_durable_spend_when_output_or_live_publication_is_invalid(string invalidation)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync();

        LongRestDispatchAttempt attempt = await f.PrepareAsync();

        using CancellationTokenSource cancel = new();
        f.Handler.ChatText = invalidation switch
        {
            "malformed" => "not-json",
            "oversized" => "{\"summary\":\"" + new string('x', 8193) + "\"}",
            _ => "{\"summary\":\"Accepted summary.\"}",
        };

        f.Handler.InsideSend = async () =>
        {
            if (invalidation == "source") await f.MutateSourceAsync();

            if (invalidation == "owner") await f.InvalidateOwnerAsync();

            if (invalidation is "config" or "gate" or "model" or "covenant") f.Aba(invalidation == "gate", invalidation);
        };

        if (invalidation == "cancellation") f.PaidRecorded = cancel.Cancel;

        Result<LongRestProviderResult>? response = null;

        Exception? exception = await Record.ExceptionAsync(async () => response = await f.Provider.ExecuteAsync(f.Payload,
            UnrestrictedTurnBudget.Instance, f.Callback(attempt), f.Admission(), cancel.Token));

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(17L, await f.H.ScalarAsync("SELECT InputTokens FROM BillableOperations;"));

        Assert.Equal(3L, await f.H.ScalarAsync("SELECT OutputTokens FROM BillableOperations;"));

        Assert.Equal("long-rest-summary", await f.H.ScalarAsync("SELECT Purpose FROM BillableOperations;"));

        Assert.Equal("0.000043", await f.H.ScalarAsync("SELECT ActualCostUsd FROM BillableOperations;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT FoldGroup FROM long_rest_discovery_generations;"));

        Assert.Null(exception);

        if (invalidation is "malformed" or "oversized" or "config" or "gate" or "model" or "covenant" or "cancellation")
        {
            Assert.True(response!.IsSuccess);

            Assert.Equal(LongRestProviderResultDisposition.Invalid, response.Value.Disposition);

            Assert.Null(response.Value.Summary);

            Assert.Equal(17, response.Value.Usage.InputTokens);
        }
        else if (invalidation is "source" or "owner")
        {
            Assert.True((await f.Attempts.CompleteAttemptAsync(f.CurrentAttempt!,
                new(LongRestDispatchAttemptState.Accepted, response!.Value.Usage, 0.000043m, null), CancellationToken.None)).IsFailure);
            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE CompletionJson IS NOT NULL;"));
        }
    }

    [Fact]
    public async Task Exact_long_unicode_embedding_input_is_sent_once_without_Weave_truncation()
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync(LongRestProviderPayloadKind.Embedding);

        string input = "  " + new string('x', 1200) + " café 👩🏽‍💻\n";

        f.Payload = LongRestProviderPayload.Embedding(f.Payload.Work, input).Value;

        Result<LongRestProviderResult> result = await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            f.Callback(await f.PrepareAsync()), f.Admission(), CancellationToken.None);
        Assert.True(result.IsSuccess);

        Assert.Equal(LongRestProviderResultDisposition.Valid, result.Value.Disposition);

        Assert.Equal(64, result.Value.Vector.Length);

        Assert.Equal(1f, result.Value.Vector[0]);

        Assert.Equal(64f, result.Value.Vector[^1]);

        using JsonDocument body = JsonDocument.Parse(f.Handler.LastBody!);
        Assert.Equal(input, body.RootElement.GetProperty("input")[0].GetString());

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(17L, await f.H.ScalarAsync("SELECT InputTokens FROM BillableOperations;"));
    }

    [Fact]
    public async Task Closed_adjudication_preserves_NoChange_and_separate_paid_purpose()
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync(LongRestProviderPayloadKind.Adjudication);

        f.Handler.ChatText = LongRestScriptedVerdicts.NoMatches(2);

        Result<LongRestProviderResult> result = await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            f.Callback(await f.PrepareAsync()), f.Admission(), CancellationToken.None);
        Assert.True(result.IsSuccess);

        Assert.Equal(LongRestProviderResultDisposition.Valid, result.Value.Disposition);

        Assert.Equal(new LongRestAdjudicationDecision(LongRestAdjudicationKind.NoChange, null), result.Value.Decision);

        Assert.Equal("long-rest-adjudication", await f.H.ScalarAsync("SELECT Purpose FROM BillableOperations;"));

        Assert.Equal(17, result.Value.Usage.InputTokens);

        Assert.Equal(1, f.Handler.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_input_reservation_is_reconciled_to_reported_paid_spend(bool embedding)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync(embedding ? LongRestProviderPayloadKind.Embedding : LongRestProviderPayloadKind.Summary);

        f.UseDurableBudget();

        LongRestProviderAdmission admission = f.Admission();

        int input = embedding ? ((LongRestProviderAdmission.Embedding)admission).ReservedInputTokens
            : ((LongRestProviderAdmission.Chat)admission).Context.PrecomputedBreakdown!.InputTokens;
        Result<LongRestProviderResult> result = await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            f.Callback(await f.PrepareAsync()), admission, CancellationToken.None);
        Assert.True(result.IsSuccess);

        decimal expected = input * 2m / 1000000m + (embedding ? 0m : 2048 * 3m / 1000000m);

        Assert.Equal(expected, decimal.Parse((string)(await f.H.ScalarAsync("SELECT ReservedUsd FROM BudgetReservations;"))!, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(embedding ? "0.000034" : "0.000043", await f.H.ScalarAsync("SELECT ReconciledUsd FROM BudgetReservations;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT Status FROM BudgetReservations;"));

        InferenceAuditRecord audit = Assert.Single(f.Audit.Records);

        Assert.Equal(embedding ? "long-rest-embedding" : "long-rest-summary", audit.RequestType);

        Assert.Equal(17, audit.PromptTokens);

        Assert.Equal(0, audit.ToolCalls);

        Assert.Empty(audit.ToolNames);

        Assert.Null(audit.ToolArgumentsJson);
    }

    [Fact]
    public async Task Accounting_failure_after_paid_return_refuses_result_and_completes_the_run_as_failed()
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync();

        f.PaidRecorded = () => throw new InvalidOperationException("protected-accounting-body");

        Result<LongRestProviderResult> result = await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            f.Callback(await f.PrepareAsync()), f.Admission(), CancellationToken.None);
        Assert.True(result.IsSuccess);

        Assert.Equal(LongRestProviderResultDisposition.Invalid, result.Value.Disposition);

        Assert.Equal(17, result.Value.Usage.InputTokens);

        Assert.Equal("LongRest.AccountingRefused", result.Value.InvalidReason);

        Assert.Null(result.Value.Summary);

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT Status FROM InferenceRuns;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT FoldGroup FROM long_rest_discovery_generations;"));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("dimensions")]
    [InlineData("nonfinite")]
    public async Task Invalid_paid_vectors_keep_exact_input_usage_and_cannot_publish(string invalid)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync(LongRestProviderPayloadKind.Embedding);

        f.Handler.InvalidVector = invalid;

        Result<LongRestProviderResult> result = await f.Provider.ExecuteAsync(f.Payload, UnrestrictedTurnBudget.Instance,
            f.Callback(await f.PrepareAsync()), f.Admission(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);

        Assert.Equal(LongRestProviderResultDisposition.Invalid, result.Value.Disposition);

        Assert.Empty(result.Value.Vector);

        Assert.Equal(17, result.Value.Usage.InputTokens);

        Assert.Equal(17L, await f.H.ScalarAsync("SELECT InputTokens FROM BillableOperations;"));

        Assert.Equal("long-rest-embedding", await f.H.ScalarAsync("SELECT Purpose FROM BillableOperations;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT FoldGroup FROM long_rest_discovery_generations;"));
    }
}

internal sealed class LongRestProviderFixture : IAsyncDisposable
{
    internal LongRestSweepHarness H = null!;

    internal LongRestConfigurationEpoch Epoch = null!;

    internal LongRestConfigurationEpochTests.NotifyingOptions Options = null!;

    internal LongRestProviderPayload Payload = null!;

    internal LongRestMaintenanceCapability Capability = null!;

    internal CovenantTurnLease Authority = null!;

    internal LongRestAttemptStore Attempts = null!;

    internal LongRestDispatchAttempt? CurrentAttempt;

    internal CountingProvider Handler = new();

    internal LongRestProviderClientFactory ClientFactory = null!;

    internal LongRestProvider Provider = null!;

    internal Action? PaidRecorded;

    internal RecordingAudit Audit = new();

    internal SafeLog Log = new();

    internal Func<Task>? JournalBarrier;

    internal ModelTokenEstimator Estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

    internal CovenantDigest Destination = new(new byte[32]);

    internal GenerationProvenance Provenance = null!;

    internal Guid SourceSession;

    internal string SourceId = Guid.NewGuid().ToString("D");

    internal static async Task<LongRestProviderFixture> CreateAsync(LongRestProviderPayloadKind kind = LongRestProviderPayloadKind.Summary, string? content = null,
        bool reasoningModel = false)
    {
        LongRestProviderFixture f = new();

        f.H = await LongRestSweepHarness.CreateAsync();

        ArcanumSettings settings = LongRestConfigurationEpochTests.Settings();

        // A chat model that declares reasoning answers an adjudication with reasoning off.
        if (reasoningModel) settings.Providers[0].Models = [new ModelEntry("model", Reasoning: new ModelReasoningSettings { WireDialect = ReasoningWireDialect.Standard }), "embedding"];

        f.Options = new(settings);

        f.Epoch = new(f.Options, Guid.NewGuid());

        f.SourceSession = await f.H.SessionAsync();

        await f.H.SeedAsync(f.SourceId, content ?? "Alpha observation.", f.SourceSession);

        Guid other = Guid.NewGuid();

        await f.H.SeedAsync(other.ToString("D"), "Beta observation.");

        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

        f.Provenance = provenance;

        CovenantDigest partition = await f.H.ProtectAsync(Guid.Parse(f.SourceId), provenance);

        await f.H.ProtectAsync(other, provenance);

        f.Authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None)).Value;

        Guid operation = Guid.NewGuid();

        await f.H.ExecuteAsync("INSERT INTO LongRunningOperations (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary) VALUES ($id, 'long-rest-sweep', 1, 1, '2026-10-10T00:00:00.0000000Z', 'sweep');", ("$id", operation.ToString("N")));

        long highwater = (long)(await f.H.ScalarAsync("SELECT max(InsertionSequence) FROM long_rest_sources;"))!;

        LongRestSweepScope scope = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null), partition).Value;

        CovenantOperationLeaseSnapshot authority = f.Authority.Snapshot;

        LongRestSweepFence fence = new(CanonicalCampaignContext.GlobalOnly, highwater, authority.DatasetGeneration!.Value, 1,
            authority.RuntimeAuthorityGeneration, authority.CapabilityGeneration, f.Epoch.Capture().Stamp);
        LongRestRecipeIdentity recipe = new(1, 1, "provider", "model", f.Destination, "provider", "embedding", f.Destination, 64);

        LongRestDiscoveryGeneration generation = LongRestSweepHarness.Require(await f.H.Store.StartOrResumeAsync(operation, scope, fence, recipe, CancellationToken.None));

        Guid session = await f.H.SessionAsync();

        SessionTurnClaimLease claim = LongRestSweepHarness.Require(await new SessionTurnClaimStore(new FixedCovenantConnectionSource(f.H.Connection), new CovenantQuotaGuard(), fence.Configuration.BootId)
            .AcquireAsync(new(Guid.NewGuid(), 1, Guid.NewGuid(), session, SessionTurnSurface.Intelligence, fence.Configuration.ValueDigest,
                fence.Configuration.ValueDigest, null, 0, 0), CancellationToken.None));
        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CanonicalCampaignContext.GlobalOnly, InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools,
            CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), authority.RuntimeAuthorityGeneration, authority.AuthorityEpoch)).Value;
        f.Capability = LongRestSweepHarness.Require(await LongRestMaintenanceCapability.CreateAsync(operation, invocation, claim,
            CanonicalCampaignContext.GlobalOnly, f.Authority, fence.Configuration, CancellationToken.None));
        Assert.True((await f.Capability.ConsumeAsync(operation, scope, fence.Configuration, CancellationToken.None)).IsSuccess);

        LongRestSourcePage page = LongRestSweepHarness.Require(await f.H.Store.CaptureNextSourcePageAsync(generation.GenerationId, generation.Revision, f.Capability, CancellationToken.None));

        LongRestHierarchyWork work = LongRestSweepHarness.Require(await f.H.Store.ReadNextFoldAsync(generation.GenerationId, f.Capability, CancellationToken.None))!;

        f.Payload = kind == LongRestProviderPayloadKind.Embedding
            ? LongRestProviderPayload.Embedding(work.Fold.Work, "Accepted summary.\n").Value
            : LongRestProviderPayload.Summary(work.Fold, LongRestDiscoveryLimits.ProviderInputUtf8Bytes).Value;

        if (kind == LongRestProviderPayloadKind.Adjudication)
        {
            LongRestPaidEvidence paid = await f.H.PayAsync(work.Fold.Work, LongRestProviderPayloadKind.Summary, new(new byte[32]), 1);

            LongRestAcceptedHierarchySummary summary = new(generation.GenerationId, Guid.NewGuid(), work.Fold, work.Fold.Work.RecipeDigest,
                paid.Attempt.PayloadDigest, "tainttoken summary", DerivedArtifactContentDigest.ForText("tainttoken summary"),
                work.Fold.Children[0].Sensitivity, provenance, [.. work.Fold.Children.SelectMany(c => c.LabelDigests).Distinct()], paid);
            summary = LongRestSweepHarness.Require(await f.H.Store.CommitAcceptedHierarchySummaryAsync(summary, f.Capability, CancellationToken.None));

            LongRestHierarchyPublication publication = await f.H.EmbeddingAsync(work, summary);

            generation = LongRestSweepHarness.Require(await f.H.Store.CommitFoldAsync(publication, f.Capability, CancellationToken.None));

            Assert.Null(LongRestSweepHarness.Require(await f.H.Store.ReadNextFoldAsync(generation.GenerationId, f.Capability, CancellationToken.None)));

            generation = LongRestSweepHarness.Require(await f.H.Store.PublishAsync(generation.GenerationId, (long)(await f.H.ScalarAsync("SELECT Revision FROM long_rest_discovery_generations;"))!, fence, f.Capability, CancellationToken.None));

            LongRestSweepStore sweeps = new(f.H.Saga.Context, TimeProvider.System);

            LongRestSweep sweep = LongRestSweepHarness.Require(await sweeps.BeginOrResumeAsync(operation, scope, fence.Configuration, CancellationToken.None));

            LongRestSweepWork next = LongRestSweepHarness.Require(await sweeps.ReadNextWorkAsync(sweep, f.Capability, CancellationToken.None))!;

            f.Payload = LongRestProviderPayload.Adjudication(next.Identity, next.Inputs, LongRestDiscoveryLimits.ProviderInputUtf8Bytes).Value;
        }

        _ = page;

        f.Attempts = new(f.H.Saga.Context, TimeProvider.System);

        f.ClientFactory = new(new NoKeys(), f.Handler, NullLogger<LongRestProviderClientFactory>.Instance);

        f.Provider = new(f.ClientFactory, new ModelCallExecutor(f.Estimator, f.Options, f.Log), f.Epoch,
            new PaidRunWriter(new TurnRunWriter(f.H.Saga.Context), () => f.PaidRecorded?.Invoke()),
            new BudgetReservationService(f.H.Saga.Context, f.Options), audit: f.Audit);

        return f;
    }

    internal void UseDurableBudget()
    {
        Options.CurrentValue.Cost.Budget = new() { Enabled = true, DailyLimitUsd = 100m };

        Provider = new(ClientFactory, new ModelCallExecutor(Estimator, Options, Log), Epoch,
            new PaidRunWriter(new TurnRunWriter(H.Saga.Context), () => PaidRecorded?.Invoke()), new BudgetReservationService(H.Saga.Context, Options), Audit);
    }

    internal LongRestProviderAdmission Admission()
    {
        if (Payload.Kind == LongRestProviderPayloadKind.Embedding)
        {
            LongRestProviderSelection selected = Epoch.Capture().ResolveEmbedding().Value;

            return new LongRestProviderAdmission.Embedding(selected.Provider, selected.Model, selected.Dimensions,
                Estimator.EstimateLongRestEmbedding(selected.Provider, selected.Model, Payload).TokenCount,
                LongRestEmbeddingPayloadHash.Create(selected.Provider.Name, selected.Model, selected.Dimensions, Payload.EmbeddingText!).Value);
        }

        LongRestProviderSelection chat = Epoch.Capture().ResolveChat().Value;

        return new LongRestProviderAdmission.Chat(new(chat.Provider, chat.Model, 2048, 0,
            Estimator.EstimateLongRestChat(chat.Provider, chat.Model, Payload)));
    }

    internal async Task<LongRestDispatchAttempt> PrepareAsync() => LongRestSweepHarness.Require(await Attempts.PrepareAttemptAsync(Payload.Work,
        Payload.Kind, LongRestProviderCallFreezer.PayloadDigest(Payload, Epoch.Capture().ResolveChat().Value.Provider,
            Payload.Kind == LongRestProviderPayloadKind.Embedding ? "embedding" : "model", Payload.Kind == LongRestProviderPayloadKind.Embedding ? 64 : 0).Value,
        Destination, CancellationToken.None));

    internal Func<CancellationToken, Task> Callback(LongRestDispatchAttempt attempt, string? failure = null)
    {
        int used = 0;

        return async token =>
        {
            if (Interlocked.Exchange(ref used, 1) != 0) throw new InvalidOperationException("LongRest.CallbackReused");

            if (failure?.EndsWith("-before", StringComparison.Ordinal) is true) await InvalidateAsync(failure);

            await ValidateAsync(attempt, token);

            LongRestDispatchCapability child = LongRestSweepHarness.Require(await Capability.CreateDispatchAsync(Payload.Work,
                attempt.PayloadDigest, Destination, attempt.PhysicalOrdinal, Epoch.Capture().Stamp, token));
            if (failure == "receipt") await H.ExecuteAsync("CREATE TEMP TRIGGER long_rest_fixture_receipt_failure BEFORE INSERT ON external_disclosure_receipts BEGIN SELECT RAISE(ABORT, 'LongRest.ReceiptRefused'); END;");

            GenerationProvenance provenance = Payload.Kind == LongRestProviderPayloadKind.Adjudication ? Payload.Inputs[0].Provenance : Payload.Fold?.Children[0].Provenance ?? Provenance;

            ProviderCallSensitivity sensitivity = new(ContentSensitivity.CovenantDerived, provenance,
                CovenantDigests.Sensitivity(new(ContentSensitivity.CovenantDerived, provenance.Mode, provenance.ExactGenerationIds, provenance.BloomBits)));
            CovenantDisclosureDraft draft = new(Capability.Claim.Claim.OriginInstallationId, CovenantDisclosureSubjectKind.Turn,
                Capability.Claim.Claim.ClaimId, CovenantDigests.MaintenanceDispatchEffect(new(Capability.Claim.Claim.ClaimId,
                    CovenantMaintenanceStep.LongRest, attempt.PhysicalOrdinal, attempt.PayloadDigest, Destination)),
                CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, Destination, sensitivity.Digest,
                null, null, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            async Task<CovenantDisclosureReceipt> AcknowledgeAsync()
            {
                CovenantDisclosureReceipt durable = LongRestSweepHarness.Require(await new CovenantDisclosureTransactionWriter(Epoch.Capture().Stamp.BootId)
                    .AcknowledgeAsync(H.Connection, draft, CovenantDisclosureEffectCategory.MaintenanceAttempt, sensitivity, token));
                if (JournalBarrier is not null) await JournalBarrier();

                return durable;
            }

            CovenantDisclosureReceipt receipt = await AcknowledgeAsync();

            if (failure?.EndsWith("-after-journal", StringComparison.Ordinal) is true) await InvalidateAsync(failure);

            attempt = LongRestSweepHarness.Require(await Attempts.RecordDisclosureAsync(attempt, receipt.Digest, token));

            await ValidateAsync(attempt, token);

            Assert.True((await child.ConsumeAsync(Payload.Work, attempt.PayloadDigest, Destination, attempt.PhysicalOrdinal, Epoch.Capture().Stamp, token)).IsSuccess);

            CurrentAttempt = LongRestSweepHarness.Require(await Attempts.MarkSentAsync(attempt, token));

            await ValidateAsync(CurrentAttempt, token);
        };
    }

    private async Task ValidateAsync(LongRestDispatchAttempt attempt, CancellationToken token)
    {
        if ((await Capability.ValidateWorkAsync(Payload.Work, Epoch.Capture().Stamp, token)).IsFailure
            || (await Attempts.RenewClaimAsync(Capability.Claim, CanonicalCampaignContext.GlobalOnly, token)).IsFailure
            || (await Attempts.ValidateAttemptAsync(attempt, token)).IsFailure)
            throw new InvalidOperationException("LongRest.StaleFence");
    }

    private async Task InvalidateAsync(string kind)
    {
        if (kind.StartsWith("source", StringComparison.Ordinal)) await MutateSourceAsync();

        if (kind.StartsWith("claim", StringComparison.Ordinal)) await InvalidateClaimAsync();

        if (kind.StartsWith("lease", StringComparison.Ordinal)) await Authority.DisposeAsync();

        if (kind.StartsWith("config", StringComparison.Ordinal)) Aba(false);
    }

    internal Task MutateSourceAsync() => H.ExecuteAsync("UPDATE saga_memories SET Content = 'Changed observation.' WHERE Id = $id;", ("$id", SourceId));

    internal Task InvalidateClaimAsync() => H.ExecuteAsync("UPDATE session_turn_claims SET ExecutorId = $executor WHERE ClaimId = $claim;",
        ("$executor", Guid.NewGuid().ToString("D").ToUpperInvariant()), ("$claim", Capability.Claim.Claim.ClaimId.ToString("D").ToUpperInvariant()));

    internal async Task InvalidateOwnerAsync()
    {
        using var cleanup = CovenantSqliteConnectionInitializer.Instance.Authorize(H.Connection, CovenantSqliteAuthorizationKind.OwnerCleanup);
        await H.ExecuteAsync("DELETE FROM session_campaign_bindings WHERE SessionId = $session;", ("$session", SourceSession.ToString("D").ToUpperInvariant()));
    }

    internal void Aba(bool gate, string? part = null)
    {
        ArcanumSettings changed = LongRestConfigurationEpochTests.Settings();

        if (gate) changed.Features.LongRest = false;
        else if (part == "covenant") changed.Features.Covenant = false;
        else if (part == "model") changed.DefaultModel = "other";
        else changed.Cost.Pricing.DefaultPricing.InputPer1M = 77m;

        Options.Set(changed);

        Options.Set(LongRestConfigurationEpochTests.Settings());
    }

    public async ValueTask DisposeAsync()
    {
        ClientFactory.Dispose();

        Epoch.Dispose();

        await Authority.DisposeAsync();

        await H.DisposeAsync();
    }

    internal sealed class SafeLog : SafeLog<ModelCallExecutor>
    {
    }

    internal class SafeLog<T> : ILogger<T>
    {
        internal List<string> Messages = [];

        internal List<Dictionary<string, object?>> Fields = [];

        internal List<LogLevel> Levels = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);

            Messages.Add(formatter(state, exception));

            Levels.Add(logLevel);

            Fields.Add(((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }

    internal sealed class RecordingAudit : IInferenceAuditLogger
    {
        internal List<InferenceAuditRecord> Records = [];

        public Task LogAsync(InferenceAuditRecord record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Records.Add(record);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<InferenceAuditRecord>> QueryAsync(DateTimeOffset? from, DateTimeOffset? to, string? model, string? sessionId, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<AuditQueryPage<InferenceAuditRecord>>> QueryPageAsync(DateTimeOffset? from, DateTimeOffset? to, string? model, string? sessionId, int limit, string? cursor, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class PaidRunWriter(ITurnRunWriter inner, Action recorded) : ITurnRunWriter
    {
        public Task<Guid> StartRunAsync(InferenceRunStart start, CancellationToken cancellationToken = default) => inner.StartRunAsync(start, cancellationToken);

        public Task CompleteRunAsync(Guid runId, InferenceRunStatus status, CancellationToken cancellationToken = default) => inner.CompleteRunAsync(runId, status, cancellationToken);

        public Task<bool> TryAbandonRunAsync(Guid runId, CancellationToken cancellationToken = default) => inner.TryAbandonRunAsync(runId, cancellationToken);

        public async Task<Guid> RecordBillableOperationAsync(BillableOperationRecord operation, CancellationToken cancellationToken = default)
        {
            Guid id = await inner.RecordBillableOperationAsync(operation, cancellationToken);

            recorded();

            return id;
        }
    }

    internal sealed class NoKeys : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    internal sealed class CountingProvider : HttpMessageHandler
    {
        internal int SendCount;

        internal string? LastBody;

        internal HttpStatusCode Status = HttpStatusCode.OK;

        internal string ChatText = "{\"summary\":\"Accepted summary.\"}";

        internal string? InvalidVector;

        /// <summary>When set, the 64-wide vector the provider embeds a request body to; by default every input embeds to one ascending vector.</summary>
        internal Func<string, float[]>? VectorFor;

        internal Func<Task>? InsideSend;

        internal Func<CancellationToken, Task>? InsideSendWithCancellation;

        /// <summary>When set, the transport fails with this exception instead of answering, as a refused connection would.</summary>
        internal Exception? TransportFailure;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;

            LastBody = await request.Content!.ReadAsStringAsync(CancellationToken.None);

            if (TransportFailure is not null) throw TransportFailure;

            if (InsideSend is not null) await InsideSend();

            if (InsideSendWithCancellation is not null) await InsideSendWithCancellation(cancellationToken);

            string response;

            if (Status != HttpStatusCode.OK) response = """{"error":{"message":"strict unsupported protected-test-body","type":"provider_error","code":"test"}}""";
            else if (request.RequestUri!.AbsolutePath.EndsWith("embeddings", StringComparison.Ordinal))
            {
                float[] vector = VectorFor is null ? Enumerable.Range(1, InvalidVector == "dimensions" ? 63 : 64).Select(i => (float)i).ToArray() : VectorFor(LastBody!);

                if (InvalidVector == "nonfinite") vector[1] = float.NaN;

                string encoded = Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vector.AsSpan()));

                response = "{\"object\":\"list\",\"model\":\"embedding\",\"data\":" + (InvalidVector == "count" ? "[]" : "[{\"object\":\"embedding\",\"index\":0,\"embedding\":\"" + encoded + "\"}]") + ",\"usage\":{\"prompt_tokens\":17,\"total_tokens\":17}}";
            }
            else response = "{\"id\":\"test\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":" + ("\"" + JsonEncodedText.Encode(ChatText).ToString() + "\"") + "}}],\"usage\":{\"prompt_tokens\":17,\"completion_tokens\":3,\"total_tokens\":20}}";

            return new(Status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
