using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Unknown-Usage Settlement. The frame that owns a physical send closes its attempt, inside the still-held external-effect group,
// when the SDK call ends without a result and without cancellation: failed, no completion evidence, one more failure toward the
// Unknown-Usage Cap, and never usage written as zero or estimated. Only a failure raised before any request byte is "not sent".
public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cleanup_refusal_is_visible_without_masking_provider_failure_or_replaying_work(bool release, bool throws)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        if (release)
        {
            f.Keys = new CredentialRefusals(refuseResolution: 1);

            f.ReleaseUnsent = (_, _) => throws ? throw new InvalidOperationException("private-fault-body")
                : Task.FromResult(Result.Failure(new Error(ErrorCodes.LongRest.StaleInput, "private-fault-body")));
        }
        else
        {
            f.Handler.Status = HttpStatusCode.Unauthorized;

            f.SettleUnresolved = (_, _, _) => throws ? throw new InvalidOperationException("private-fault-body")
                : Task.FromResult(Result<LongRestDispatchAttempt>.Failure(new Error(ErrorCodes.LongRest.StaleInput, "private-fault-body")));
        }

        LongRestSweepProgress result = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(release ? "LongRest.CredentialUnavailable" : ErrorCodes.LongRest.ProviderRejected, result.DeferredReason);

        Dictionary<string, object?> cleanup = Assert.Single(f.MaintenanceLog.Fields);

        Assert.Equal(release ? "long_rest.release_unsent_failed" : "long_rest.settlement_failed", cleanup["Code"]);

        Assert.Equal(f.Operation, cleanup["OperationId"]);

        Dictionary<string, object?> parked = Assert.Single(f.CoordinatorLog.Fields);

        Assert.Equal(result.DeferredReason, parked["Code"]);

        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, Assert.Single(f.MaintenanceLog.Levels));

        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, Assert.Single(f.CoordinatorLog.Levels));

        if (release)
        {
            Dictionary<string, object?> credential = Assert.Single(f.CredentialLog.Fields);

            Assert.Equal(f.Operation, credential["OperationId"]);

            Assert.Equal(result.DeferredReason, credential["Code"]);
        }

        Assert.Equal(f.Operation, parked["OperationId"]);

        Assert.Equal(release ? 0 : 1, f.Handler.SendCount);

        Assert.Equal(1L, await UnsettledEffectsAsync(f));
    }

    private const string EffectRowsSql = """
        SELECT StateCode, COALESCE(FailureCode, ''), FailureCount, SentCount, PaidCount, PhysicalOrdinal, CompletionJson IS NULL, InputTokens, OutputTokens
        FROM long_rest_provider_effects ORDER BY PhysicalOrdinal, WorkItemId;
        """;

    private static async Task<EffectRow[]> EffectRowsAsync(MaintenanceFixture f)
    {
        List<EffectRow> rows = [];

        await using SqliteCommand command = f.H.Connection.CreateCommand();

        command.CommandText = EffectRowsSql;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.GetInt64(6) == 1, reader.GetInt64(7), reader.GetInt64(8)));
        }

        return [.. rows];
    }

    private sealed record EffectRow(long State, string FailureCode, long Failures, long Sent, long Paid, long Ordinal, bool CompletionNull,
        long InputTokens, long OutputTokens);

    private static void SummaryOrDecision(MaintenanceFixture f) => f.Handler.InsideSend = () =>
    {
        f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

        return Task.CompletedTask;
    };

    [Fact]
    public async Task A_provider_500_settles_the_sent_attempt_in_frame_as_unknown_usage_and_the_retry_gets_a_new_ordinal()
    {
        ManualWallClockTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, clock: clock);

        f.Handler.Status = HttpStatusCode.InternalServerError;

        LongRestSweepProgress failed = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(ErrorCodes.LongRest.ProviderUnavailable, failed.DeferredReason);

        Assert.Equal(1, f.Handler.SendCount);

        // Settled in the frame that sent it, not left Sent for a reboot: failed, no completion evidence, usage untouched.
        EffectRow settled = Assert.Single(await EffectRowsAsync(f));

        Assert.Equal((long)LongRestDispatchAttemptState.Failed, settled.State);

        Assert.Equal(ErrorCodes.LongRest.TransportUnknown, settled.FailureCode);

        Assert.Equal(1L, settled.Failures);

        Assert.Equal(1L, settled.Sent);

        Assert.Equal(0L, settled.Paid);

        Assert.True(settled.CompletionNull);

        Assert.Equal((0L, 0L), (settled.InputTokens, settled.OutputTokens));

        Assert.Equal(1L, settled.Ordinal);

        // The accounting run is not closed at a fabricated zero: no billable operation was recorded for the unknown call.
        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Assert.Equal(ErrorCodes.LongRest.ProviderUnavailable, parked.TerminalErrorCode);

        // The cooldown passes, the provider recovers, and the worker readmits the same sweep: a new attempt, ordinal two.
        f.Handler.Status = HttpStatusCode.OK;

        SummaryOrDecision(f);

        clock.Advance(LongRestFaultPolicy.Cooldown(0));

        LongRestSweepProgress completed = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, completed.Phase);

        EffectRow[] rows = await EffectRowsAsync(f);

        // One effect row per work item: the unknown 500 was ordinal one, the retried summary ordinal two and the embedding ordinal three.
        EffectRow retried = Assert.Single(rows, static row => row.Failures == 1);

        Assert.Equal(3L, retried.Ordinal);

        Assert.Equal((long)LongRestDispatchAttemptState.Accepted, retried.State);

        Assert.Equal(3L, retried.Sent);

        Assert.Equal(2L, retried.Paid);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);
    }

    [Fact]
    public async Task A_connection_refused_before_any_request_byte_settles_as_not_sent_and_is_not_charged_to_the_cap()
    {
        ManualWallClockTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, clock: clock);

        f.Handler.TransportFailure = new HttpRequestException(HttpRequestError.ConnectionError, "connection refused", new SocketException((int)SocketError.ConnectionRefused));

        LongRestSweepProgress failed = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(ErrorCodes.LongRest.ProviderUnreachable, failed.DeferredReason);

        EffectRow settled = Assert.Single(await EffectRowsAsync(f));

        Assert.Equal((long)LongRestDispatchAttemptState.Failed, settled.State);

        Assert.Equal(ErrorCodes.LongRest.ProviderUnreachable, settled.FailureCode);

        // Not an unknown-usage attempt, so the cap is not charged; no usage, no completion evidence.
        Assert.Equal(0L, settled.Failures);

        Assert.Equal(0L, settled.Paid);

        Assert.True(settled.CompletionNull);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(ErrorCodes.LongRest.ProviderUnreachable, (await f.Operations.GetAsync(f.Operation))!.TerminalErrorCode);

        // The sweep is resumable, and the retry is a fresh attempt.
        f.Handler.TransportFailure = null;

        SummaryOrDecision(f);

        clock.Advance(LongRestFaultPolicy.Cooldown(0));

        Assert.Equal(LongRestSweepPhase.Completed, LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None)).Phase);

        // The refused attempt (ordinal one) was never charged: the retried summary and the embedding follow it as ordinals two and three.
        Assert.Contains(await EffectRowsAsync(f), static row => row.Ordinal == 3 && row.Sent == 3 && row.Paid == 2 && row.Failures == 0
            && row.State == (long)LongRestDispatchAttemptState.Accepted);
    }

    public static TheoryData<string, string, bool> TypedFailures => new()
    {
        { "status:408", ErrorCodes.LongRest.ProviderUnavailable, true },

        { "status:429", ErrorCodes.LongRest.ProviderUnavailable, true },

        { "status:503", ErrorCodes.LongRest.ProviderUnavailable, true },

        { "status:401", ErrorCodes.LongRest.ProviderRejected, true },

        { "status:404", ErrorCodes.LongRest.ProviderRejected, true },

        { "status:422", ErrorCodes.LongRest.ProviderRejected, true },

        { "transport:ResponseEnded", ErrorCodes.LongRest.ProviderUnavailable, true },

        { "transport:ConnectionError", ErrorCodes.LongRest.ProviderUnreachable, false },

        { "transport:SecureConnectionError", ErrorCodes.LongRest.ProviderUnreachable, false },

        { "timeout", ErrorCodes.LongRest.ProviderUnavailable, true },
    };

    [Theory]
    [MemberData(nameof(TypedFailures))]
    public async Task Provider_failures_are_typed_by_status_and_transport_and_carry_no_provider_body(string failure, string expectedCode, bool unknownUsage)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        if (failure.StartsWith("status:", StringComparison.Ordinal)) f.Handler.Status = (HttpStatusCode)int.Parse(failure["status:".Length..], System.Globalization.CultureInfo.InvariantCulture);
        else if (failure == "timeout") f.Handler.TransportFailure = new TimeoutException("the provider did not answer");
        else
        {
            HttpRequestError kind = Enum.Parse<HttpRequestError>(failure["transport:".Length..]);

            // The inner exception is what the runtime really raises: a socket failure while connecting, a TLS failure while
            // handshaking, and an IO failure on a connection that was already established. A name-resolution or address-policy failure
            // is not built by hand here: the production egress guard raises its own exception for it, so those cases are pinned through
            // the real guard and the real SDK in LongRestOwnedTransportLoopbackTests.
            Exception inner = kind switch
            {
                HttpRequestError.ConnectionError => new SocketException((int)SocketError.ConnectionRefused),
                HttpRequestError.SecureConnectionError => new AuthenticationException("the TLS handshake failed"),
                _ => new IOException("the established connection was dropped"),
            };

            f.Handler.TransportFailure = new HttpRequestException(kind, "transport failed", inner);
        }

        LongRestSweepProgress parked = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(expectedCode, parked.DeferredReason);

        Assert.Equal(1, f.Handler.SendCount);

        EffectRow settled = Assert.Single(await EffectRowsAsync(f));

        Assert.Equal((long)LongRestDispatchAttemptState.Failed, settled.State);

        // Unknown usage is charged to the cap; a failure before any request byte is not.
        Assert.Equal(unknownUsage ? 1L : 0L, settled.Failures);

        Assert.Equal(unknownUsage ? ErrorCodes.LongRest.TransportUnknown : ErrorCodes.LongRest.ProviderUnreachable, settled.FailureCode);

        // No provider text reaches the typed result or anything durable that names the failure.
        Assert.NotNull(f.ProviderResult);

        Assert.True(f.ProviderResult!.IsFailure);

        Assert.Equal(expectedCode, f.ProviderResult.Error.Code);

        Assert.DoesNotContain("protected-test-body", f.ProviderResult.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("strict unsupported", f.ProviderResult.Error.Message, StringComparison.Ordinal);

        LongRunningOperation operation = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(expectedCode, operation.TerminalErrorCode);

        Assert.DoesNotContain("protected-test-body", operation.PublicSummary ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_400_is_a_rejection_that_needs_an_operator_and_is_never_readmitted()
    {
        ManualWallClockTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, clock: clock);

        f.Handler.Status = HttpStatusCode.BadRequest;

        LongRestSweepProgress rejected = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(ErrorCodes.LongRest.ProviderRejected, rejected.DeferredReason);

        // The rejected call may still have been billed, so it is settled like any unknown usage.
        EffectRow settled = Assert.Single(await EffectRowsAsync(f));

        Assert.Equal((long)LongRestDispatchAttemptState.Failed, settled.State);

        Assert.Equal(1L, settled.Failures);

        Assert.True(settled.CompletionNull);

        clock.Advance(TimeSpan.FromDays(1));

        Assert.Null(LongRestSweepHarness.Require(await new RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestSweepStore(f.H.Saga.Context, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None)).OperationId);

        // The worker door leaves it alone, and a request for the scope names the operator remedy.
        int sends = f.Handler.SendCount;

        Assert.Equal(ErrorCodes.LongRest.ProviderRejected,
            LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None)).DeferredReason);

        Assert.Equal(sends, f.Handler.SendCount);

        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(request.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.SweepParked, request.Error.Code);

        // A rejected call can be tried again once the operator has fixed the cause, so retry and cancel are both offered.
        Assert.Contains("retry", request.Error.Message, StringComparison.Ordinal);

        Assert.Contains("cancel", request.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_call_stays_Sent_and_is_never_settled_in_frame()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        using CancellationTokenSource stopping = new();

        int settlements = 0;

        f.SettleUnresolved = (_, _, _) =>
        {
            Interlocked.Increment(ref settlements);

            return null;
        };

        f.Handler.InsideSendWithCancellation = async token =>
        {
            await stopping.CancelAsync();

            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };

        Exception? stopped = await Record.ExceptionAsync(() => f.ResumeAdmittedAsync(f.Operation, stopping.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(stopped);

        EffectRow sent = Assert.Single(await EffectRowsAsync(f));

        Assert.Equal((long)LongRestDispatchAttemptState.Sent, sent.State);

        Assert.True(sent.CompletionNull);

        Assert.Equal(0L, sent.Failures);

        Assert.Equal(0, settlements);
    }

    [Fact]
    public async Task A_refused_settlement_write_leaves_the_attempt_Sent_and_the_reboot_settles_it()
    {
        ManualWallClockTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, clock: clock);

        int refused = 0;

        f.SettleUnresolved = (_, _, _) =>
        {
            Interlocked.Increment(ref refused);

            return Task.FromResult(Result<LongRestDispatchAttempt>.Failure(new Error(ErrorCodes.LongRest.StaleInput, "the settlement write was refused")));
        };

        f.Handler.Status = HttpStatusCode.InternalServerError;

        LongRestSweepProgress failed = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        // The original typed failure still parks the sweep; the best-effort settlement did not mask it.
        Assert.Equal(ErrorCodes.LongRest.ProviderUnavailable, failed.DeferredReason);

        Assert.Equal(1, refused);

        EffectRow stranded = Assert.Single(await EffectRowsAsync(f));

        Assert.Equal((long)LongRestDispatchAttemptState.Sent, stranded.State);

        Assert.True(stranded.CompletionNull);

        // A new boot settles the interrupted send under its own code, exactly as before this change, and the sweep completes.
        f.SettleUnresolved = null;

        f.Handler.Status = HttpStatusCode.OK;

        SummaryOrDecision(f);

        await f.RebootAsync();

        clock.Advance(LongRestFaultPolicy.Cooldown(0));

        Assert.Equal(LongRestSweepPhase.Completed, LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None)).Phase);

        // The stranded attempt was settled by the reboot (one failure charged), so the readmitted sweep could prepare a new attempt.
        Assert.Contains(await EffectRowsAsync(f), static row => row.Failures == 1 && row.Sent == 3 && row.Paid == 2);
    }

    [Fact]
    public async Task The_third_unknown_attempt_parks_as_needs_operator_before_any_send()
    {
        ManualWallClockTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, clock: clock);

        f.Handler.Status = HttpStatusCode.InternalServerError;

        for (int attempt = 1; attempt <= LongRestFaultPolicy.UnknownUsageCap; attempt++)
        {
            LongRestSweepProgress failed = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

            Assert.Equal(ErrorCodes.LongRest.ProviderUnavailable, failed.DeferredReason);

            Assert.Equal(attempt, f.Handler.SendCount);

            EffectRow row = Assert.Single(await EffectRowsAsync(f));

            Assert.Equal((long)attempt, row.Failures);

            Assert.Equal((long)attempt, row.Ordinal);

            clock.Advance(LongRestFaultPolicy.Cooldown(attempt - 1));
        }

        // The unit has now ended its allowed attempts with unknown billing: the next readmission refuses before any send.
        LongRestSweepProgress capped = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(ErrorCodes.LongRest.TransportUnknown, capped.DeferredReason);

        Assert.Equal(LongRestFaultPolicy.UnknownUsageCap, f.Handler.SendCount);

        EffectRow final = Assert.Single(await EffectRowsAsync(f));

        Assert.Equal((long)LongRestFaultPolicy.UnknownUsageCap, final.Failures);

        Assert.Equal((long)LongRestFaultPolicy.UnknownUsageCap, final.Ordinal);

        Assert.Equal((long)LongRestDispatchAttemptState.Failed, final.State);

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Assert.Equal(ErrorCodes.LongRest.TransportUnknown, parked.TerminalErrorCode);

        Assert.Equal(LongRestFaultClass.NeedsOperator, LongRestFaultPolicy.Classify(parked.TerminalErrorCode));

        // Nothing automatic resumes it, and a request for the scope says an operator must act.
        clock.Advance(TimeSpan.FromDays(1));

        Assert.Null(LongRestSweepHarness.Require(await new RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestSweepStore(f.H.Saga.Context, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None)).OperationId);

        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.Equal(ErrorCodes.LongRest.SweepParked, request.Error.Code);

        // A retry re-trips the same per-unit cap at once, so the only remedy the message may name is cancel.
        Assert.Contains("cancel", request.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("retry", request.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_fast_model_id_is_the_SDK_model_on_the_wire_for_the_chat_call_and_the_default_model_is_otherwise()
    {
        await using MaintenanceFixture withFast = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false,
            configure: settings =>
            {
                settings.Providers[0].Models = ["model", "embedding", "fast"];

                settings.FastModel = "fast";
            });

        List<string> bodies = [];

        withFast.Handler.InsideSend = () =>
        {
            bodies.Add(withFast.Handler.LastBody!);

            withFast.Handler.ChatText = DecisionOrSummary(withFast.Handler.LastBody!);

            return Task.CompletedTask;
        };

        Assert.Equal(LongRestSweepPhase.Completed, LongRestSweepHarness.Require(await withFast.Coordinator.ResumeAsync(withFast.Operation, null, CancellationToken.None)).Phase);

        string[] chat = [.. bodies.Where(static body => body.Contains("long_rest_summary", StringComparison.Ordinal) || body.Contains("long_rest_adjudication", StringComparison.Ordinal))];

        Assert.NotEmpty(chat);

        Assert.All(chat, static body => Assert.Contains("\"model\":\"fast\"", body, StringComparison.Ordinal));

        Assert.All(bodies.Except(chat), static body => Assert.Contains("\"model\":\"embedding\"", body, StringComparison.Ordinal));

        // The recipe the generation was captured under names the fast model, so a later change of the fast model starts a new one.
        Assert.Equal("fast", ((ILongRestConfigurationProvider)withFast.Epoch).Capture().Recipe.Value.SummaryModel);

        await using MaintenanceFixture plain = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        List<string> plainBodies = [];

        plain.Handler.InsideSend = () =>
        {
            plainBodies.Add(plain.Handler.LastBody!);

            plain.Handler.ChatText = DecisionOrSummary(plain.Handler.LastBody!);

            return Task.CompletedTask;
        };

        Assert.Equal(LongRestSweepPhase.Completed, LongRestSweepHarness.Require(await plain.Coordinator.ResumeAsync(plain.Operation, null, CancellationToken.None)).Phase);

        Assert.All(plainBodies.Where(static body => body.Contains("long_rest_summary", StringComparison.Ordinal)),
            static body => Assert.Contains("\"model\":\"model\"", body, StringComparison.Ordinal));
    }
}
