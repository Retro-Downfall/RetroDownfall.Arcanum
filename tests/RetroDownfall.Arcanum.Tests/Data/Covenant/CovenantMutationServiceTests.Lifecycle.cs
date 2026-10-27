using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed partial class CovenantMutationServiceTests
{
    [Theory]
    [InlineData("set")]
    [InlineData("retire")]
    [InlineData("correct")]
    [InlineData("curate")]
    public async Task Operator_commits_answer_retryable_unavailable_when_a_native_writer_remains_busy(string operation)
    {
        TestCapturingLogger<CovenantMutationService> logger = new();

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token,
            busyRetryDeadlineForTesting: TimeSpan.Zero, mutationLoggerForTesting: logger);

        const string key = "preference.busy";

        const string content = "Build from the repository root.";

        if (operation != "set")
        {
            await harness.SetAsync(CovenantScope.Global, null, key, "An existing preference.", Token);
        }

        long headsBefore = await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_heads;");

        long versionsBefore = await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_versions;");

        long receiptsBefore = await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_mutation_receipts;");

        long curationBefore = await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_curation_receipts;");

        Guid mutation = Guid.CreateVersion7();

        await using CovenantInstallationReadLease read = (await harness.Gate.AcquireInstallationReadAsync(Token)).Value;

        string preflight;

        Guid target = Guid.Empty;

        string hash = string.Empty;

        if (operation == "correct")
        {
            await using SqliteCommand query = harness.Fixture.Connection.CreateCommand();

            query.CommandText = "SELECT VersionId, RenderedHash FROM covenant_versions LIMIT 1;";

            await using SqliteDataReader reader = await query.ExecuteReaderAsync(Token);

            Assert.True(await reader.ReadAsync(Token));

            target = Guid.Parse(reader.GetString(0));

            hash = Convert.ToHexStringLower((byte[])reader.GetValue(1));
        }

        if (operation == "curate")
        {
            Result<CovenantCurationPreflightDto> prepared = await harness.Service.PrepareCurationAsync(
                new(CovenantCurationKind.Pin, CovenantScope.Global, null, key, CovenantLane.Confirmed, 0, mutation), read, Token);

            Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

            preflight = prepared.Value.PreflightToken;
        }
        else
        {
            Result<CovenantMutationPreflightDto> prepared = operation switch
            {
                "retire" => await harness.Service.PrepareRetireAsync(
                    new(CovenantScope.Global, null, key, CovenantLane.Confirmed, 1, mutation), read, Token),
                "correct" => await harness.Service.PrepareCorrectAsync(
                    new(CovenantScope.Global, null, key, content, target, CovenantLane.Confirmed, 1, hash, mutation), read, Token),
                _ => await harness.Service.PrepareSetAsync(
                    new(CovenantScope.Global, null, key, content, 0, mutation, false), read, Token),
            };

            Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

            preflight = prepared.Value.PreflightToken;
        }

        harness.Fixture.Connection.DefaultTimeout = 1;

        await using (SqliteCommand timeout = harness.Fixture.Connection.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout = 10;";

            _ = await timeout.ExecuteNonQueryAsync(Token);
        }

        await using CovenantWriteLease write = (await harness.Gate.AcquireWriteAsync(CovenantOperationScope.Global, Token)).Value;

        Error? refusal = null;

        Exception? failure;

        await using (SqliteConnection blocker = await harness.Fixture.OpenAdditionalConnectionAsync(Token))
        {
            await using SqliteTransaction heldWriter = blocker.BeginTransaction(deferred: false);

            failure = await Record.ExceptionAsync(async () =>
            {
                if (operation == "curate")
                {
                    Result<CovenantCurationResultDto> result = await harness.Service.CurateAsync(
                        new(CovenantCurationKind.Pin, CovenantScope.Global, null, key, CovenantLane.Confirmed, 0, mutation, preflight), write, Token);

                    refusal = result.IsFailure ? result.Error : null;
                }
                else
                {
                    Result<CovenantMutationResultDto> result = operation switch
                    {
                        "retire" => await harness.Service.RetireAsync(
                            new(CovenantScope.Global, null, key, CovenantLane.Confirmed, 1, mutation, preflight), write, Token),
                        "correct" => await harness.Service.CorrectAsync(
                            new(CovenantScope.Global, null, key, content, target, CovenantLane.Confirmed, 1, hash, mutation, preflight), write, Token),
                        _ => await harness.Service.SetAsync(
                            new(CovenantScope.Global, null, key, content, 0, mutation, false, preflight), write, Token),
                    };

                    refusal = result.IsFailure ? result.Error : null;
                }
            });
        }

        Assert.Null(failure);

        Assert.NotNull(refusal);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, refusal.Value.Code);

        Assert.Contains("retry", refusal.Value.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("same request", refusal.Value.Message, StringComparison.OrdinalIgnoreCase);

        TestLogEntry warning = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, warning.Level);

        Assert.Null(warning.Exception);

        Assert.Equal("Begin", Assert.Single(warning.Properties!, static field => field.Key == "Stage").Value?.ToString());

        Assert.Equal(5, Assert.Single(warning.Properties!, static field => field.Key == "SqliteErrorCode").Value);

        Assert.Equal(headsBefore, await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_heads;"));

        Assert.Equal(versionsBefore, await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_versions;"));

        Assert.Equal(receiptsBefore, await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_mutation_receipts;"));

        Assert.Equal(curationBefore, await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_curation_receipts;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_native_busy_COMMIT_is_not_retried_as_a_fresh_operator_attempt(bool curation)
    {
        TestCapturingLogger<CovenantMutationService> logger = new();

        SqliteTransaction? heldReader = null;

        int delays = 0;

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token,
            busyRetryDeadlineForTesting: TimeSpan.FromMinutes(1),
            busyRetryDelayForTesting: async (_, _) =>
            {
                delays++;

                // A forbidden COMMIT retry could now succeed, so the oracle observes actual replay,
                // rather than only the final error selected when a zero budget stops every retry.
                await heldReader!.DisposeAsync();

                heldReader = null;
            }, mutationLoggerForTesting: logger);

        CovenantSetRequest? set = null;

        CovenantCurationRequest? curate = null;

        CovenantWriteLease write;

        if (curation)
        {
            (curate, write) = await PreparedRetryCurationAsync(harness);
        }
        else
        {
            (set, write) = await PreparedRetrySetAsync(harness);
        }

        await using (write)
        {
            await using (SqliteCommand mode = harness.Fixture.Connection.CreateCommand())
            {
                mode.CommandText = "PRAGMA journal_mode = DELETE;";

                Assert.Equal("delete", await mode.ExecuteScalarAsync(Token));
            }

            harness.Fixture.Connection.DefaultTimeout = 1;

            // The regular helper reapplies WAL. This pure native reader preserves DELETE mode;
            // the scratch fixture already initialized the pinned SQLCipher runtime.
            await using SqliteConnection blocker = new(harness.Fixture.Connection.ConnectionString);

            await blocker.OpenAsync(Token);

            await using (heldReader = blocker.BeginTransaction(deferred: true))
            {
                await using SqliteCommand pin = blocker.CreateCommand();

                pin.Transaction = heldReader;

                pin.CommandText = "SELECT COUNT(*) FROM covenant_heads;";

                Assert.Equal(curation ? 1L : 0L, await pin.ExecuteScalarAsync(Token));

                Error? refusal;

                if (curation)
                {
                    Result<CovenantCurationResultDto> result = await harness.Service.CurateAsync(curate!, write, Token);

                    refusal = result.IsFailure ? result.Error : null;
                }
                else
                {
                    Result<CovenantMutationResultDto> result = await harness.Service.SetAsync(set!, write, Token);

                    refusal = result.IsFailure ? result.Error : null;
                }

                Assert.NotNull(refusal);

                Assert.Equal(ErrorCodes.Covenant.WriteFailed, refusal.Value.Code);

                Assert.Contains("outcome", refusal.Value.Message, StringComparison.OrdinalIgnoreCase);

                Assert.Equal(0, delays);

                TestLogEntry logged = Assert.Single(logger.Entries);

                Assert.Equal("Commit", Assert.Single(logged.Properties!, static item => item.Key == "Stage").Value?.ToString());
            }
        }

        Assert.Equal(curation ? 1 : 0, await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_mutation_receipts;"));

        Assert.Equal(curation ? 1 : 0, await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_heads;"));

        Assert.Equal(0, await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_curation_receipts;"));
    }

    [Fact]
    public async Task A_curation_busy_attempt_retries_after_native_release_and_replays_one_receipt_before_token_expiry_checks()
    {
        SqliteTransaction? heldWriter = null;

        int delays = 0;

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token,
            busyRetryDeadlineForTesting: TimeSpan.FromMinutes(1),
            busyRetryDelayForTesting: async (_, _) =>
            {
                delays++;

                await heldWriter!.DisposeAsync();

                heldWriter = null;
            });

        (CovenantCurationRequest request, CovenantWriteLease write) = await PreparedRetryCurationAsync(harness);

        await using (write)
        {
            harness.Fixture.Connection.DefaultTimeout = 1;

            await using SqliteConnection blocker = await harness.Fixture.OpenAdditionalConnectionAsync(Token);

            await using (heldWriter = blocker.BeginTransaction(deferred: false))
            {
                Result<CovenantCurationResultDto> result = await harness.Service.CurateAsync(request, write, Token);

                Assert.True(result.IsSuccess);

                Assert.False(result.Value.Replayed);

                Assert.True(result.Value.IsPinned);

                Assert.Equal(1, delays);
            }

            harness.Advance(TimeSpan.FromMinutes(10));

            Result<CovenantCurationResultDto> replayed = await harness.Service.CurateAsync(request, write, Token);

            Assert.True(replayed.IsSuccess);

            Assert.True(replayed.Value.Replayed);

            Assert.Equal(1, delays);
        }

        Assert.Equal(1, await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_curation_receipts;"));

        Assert.Equal(1, await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_mutation_receipts;"));
    }

    [Theory]
    [InlineData("release")]
    [InlineData("cancel")]
    [InlineData("revoke")]
    public async Task Native_busy_retry_releases_cleanly_and_honors_request_cancellation_and_lease_revocation(string mode)
    {
        using CancellationTokenSource cancellation = new();

        SqliteTransaction? held = null;

        CovenantWriteLease? write = null;

        int delays = 0;

        TestCapturingLogger<CovenantMutationService> logger = new();

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token,
            busyRetryDeadlineForTesting: TimeSpan.FromSeconds(15),
            busyRetryDelayForTesting: async (_, token) =>
            {
                delays++;

                if (mode == "cancel")
                {
                    cancellation.Cancel();

                    token.ThrowIfCancellationRequested();
                }
                else if (mode == "revoke")
                {
                    await write!.DisposeAsync();
                }
                else
                {
                    await held!.DisposeAsync();

                    held = null;
                }
            }, mutationLoggerForTesting: logger);

        (CovenantSetRequest request, CovenantWriteLease acquired) = await PreparedRetrySetAsync(harness);

        await using (write = acquired)
        {
            harness.Fixture.Connection.DefaultTimeout = 1;

            await using SqliteConnection blocker = await harness.Fixture.OpenAdditionalConnectionAsync(Token);

            held = blocker.BeginTransaction(deferred: false);

            Result<CovenantMutationResultDto>? result = null;

            Exception? failure;

            try
            {
                failure = await Record.ExceptionAsync(async () => result = await harness.Service.SetAsync(
                    request, write, cancellation.Token));
            }
            finally
            {
                if (held is not null)
                {
                    await held.DisposeAsync();
                }
            }

            Assert.Equal(1, delays);

            if (mode == "cancel")
            {
                Assert.IsAssignableFrom<OperationCanceledException>(failure);

                Assert.Null(result);
            }
            else
            {
                Assert.Null(failure);

                Assert.NotNull(result);

                Assert.Equal(mode == "release", result.IsSuccess);
            }

            Assert.Empty(logger.Entries);

            Assert.Equal(mode == "release" ? 1 : 0,
                await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_mutation_receipts;"));

            Assert.Equal(mode == "release" ? 1 : 0,
                await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_heads;"));

            if (mode == "release")
            {
                Result<CovenantMutationResultDto> replay = await harness.Service.SetAsync(request, write, Token);

                Assert.True(replay.IsSuccess);

                Assert.True(replay.Value.Replayed);

                Assert.Equal(1, delays);

                Assert.Equal(1, await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_mutation_receipts;"));
            }
        }
    }

    [Theory]
    [InlineData("busy", ErrorCodes.Covenant.Unavailable, LogLevel.Warning)]
    [InlineData("io", ErrorCodes.Covenant.WriteFailed, LogLevel.Error)]
    [InlineData("access", ErrorCodes.Covenant.WriteFailed, LogLevel.Error)]
    [InlineData("schema", ErrorCodes.Covenant.WriteFailed, LogLevel.Error)]
    public async Task Operator_storage_refusals_log_one_closed_cause_without_arbitrary_exception_data(string kind, string code, LogLevel level)
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Exception cause = kind switch
        {
            "busy" => new SqliteException("PRIVATE_OPERATOR_CANARY /private/profile", 5),
            "schema" => new SqliteException("PRIVATE_OPERATOR_CANARY schema SQL", 1),
            "access" => new UnauthorizedAccessException("PRIVATE_OPERATOR_CANARY /private/profile"),
            _ => new PRIVATE_OPERATOR_CANARY_Exception("PRIVATE_OPERATOR_CANARY /private/profile"),
        };

        TestCapturingLogger<CovenantMutationService> logger = new();

        CovenantMutationService service = new(fixture.Store, new CovenantCompiler(), new StubEnvelopeCodec(),
            new OperatorFaultSource(cause), new CovenantMutationKernel(new CovenantQuotaGuard(), fixture.ErasureKeys),
            new CovenantCurationKernel(), new StubAuthority(), TimeProvider.System,
            DetachedAvailabilityRepublisher.Create(), logger);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantWriteLease write = (await gate.AcquireWriteAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantMutationResultDto> result = await service.SetAsync(
            new(CovenantScope.Global, null, "preference.error", "PRIVATE_CONTENT_CANARY", 0, Guid.CreateVersion7(), false, "opaque"),
            write, Token);

        Assert.True(result.IsFailure);

        Assert.Equal(code, result.Error.Code);

        TestLogEntry logged = Assert.Single(logger.Entries);

        Assert.Equal(level, logged.Level);

        Assert.Null(logged.Exception);

        Assert.NotNull(logged.Properties);

        Assert.Contains(logged.Properties, static field => field.Key == "FailureKind" && field.Value is Enum);

        Assert.Contains(logged.Properties, static field => field.Key == "Stage" && field.Value is Enum);

        Assert.Contains(logged.Properties, static field => field.Key == "MutationId" && field.Value is Guid);

        foreach (string value in logged.Properties.Select(static field => field.Value?.ToString() ?? string.Empty)
            .Append(logged.Message).Append(result.Error.Message))
        {
            Assert.DoesNotContain("PRIVATE_", value, StringComparison.Ordinal);

            Assert.DoesNotContain("/private/profile", value, StringComparison.Ordinal);
        }
    }

    private static async Task<(CovenantSetRequest Request, CovenantWriteLease Write)> PreparedRetrySetAsync(CovenantServiceHarness harness)
    {
        Guid mutation = Guid.CreateVersion7();

        Result<CovenantMutationPreflightDto> prepared;

        await using (CovenantInstallationReadLease read = (await harness.Gate.AcquireInstallationReadAsync(Token)).Value)
        {
            prepared = await harness.Service.PrepareSetAsync(
                new(CovenantScope.Global, null, "preference.retry", "A retried preference.", 0, mutation, false), read, Token);
        }

        Assert.True(prepared.IsSuccess);

        CovenantWriteLease write = (await harness.Gate.AcquireWriteAsync(CovenantOperationScope.Global, Token)).Value;

        return (new(CovenantScope.Global, null, "preference.retry", "A retried preference.", 0, mutation, false,
            prepared.Value.PreflightToken), write);
    }

    private static async Task<(CovenantCurationRequest Request, CovenantWriteLease Write)> PreparedRetryCurationAsync(CovenantServiceHarness harness)
    {
        const string key = "preference.curation-retry";

        await harness.SetAsync(CovenantScope.Global, null, key, "A curation retry subject.", Token);

        Guid mutation = Guid.CreateVersion7();

        Result<CovenantCurationPreflightDto> prepared;

        await using (CovenantInstallationReadLease read = (await harness.Gate.AcquireInstallationReadAsync(Token)).Value)
        {
            prepared = await harness.Service.PrepareCurationAsync(
                new(CovenantCurationKind.Pin, CovenantScope.Global, null, key, CovenantLane.Confirmed, 0, mutation), read, Token);
        }

        Assert.True(prepared.IsSuccess);

        CovenantWriteLease write = (await harness.Gate.AcquireWriteAsync(CovenantOperationScope.Global, Token)).Value;

        return (new(CovenantCurationKind.Pin, CovenantScope.Global, null, key, CovenantLane.Confirmed, 0,
            mutation, prepared.Value.PreflightToken), write);
    }

    private sealed class OperatorFaultSource(Exception failure) : ICovenantConnectionSource
    {
        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) => throw failure;

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken) => throw failure;
    }

    private sealed class PRIVATE_OPERATOR_CANARY_Exception(string message) : IOException(message);
}
