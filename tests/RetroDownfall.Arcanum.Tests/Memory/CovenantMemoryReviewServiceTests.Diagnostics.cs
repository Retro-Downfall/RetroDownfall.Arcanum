using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Memory;

public sealed partial class CovenantMemoryReviewServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_canonical_state_has_a_closed_contextual_diagnostic_on_review_reads(bool prepare)
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        (CovenantReviewBulkPrepareRequest request, _) = await PrepareConfirmAsync(runtime);

        await ExecuteAsync(runtime.Fixture.Connection, "DELETE FROM covenant_state;");

        await using CovenantReadLease lease = runtime.ReadLease();

        Error? error = null;

        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            if (prepare)
            {
                Result<MemoryReviewBulkPlanDto> result = await runtime.Service.PrepareAsync(request, lease, Token);

                Assert.True(result.IsFailure);

                error = result.Error;
            }
            else
            {
                Result<CovenantReviewPageDto> result = await runtime.Service.ListAsync(
                    new(CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed, 50, null), lease, Token);

                Assert.True(result.IsFailure);

                error = result.Error;
            }
        });

        Assert.Null(failure);

        Assert.NotNull(error);

        Assert.Equal(ErrorCodes.MemoryReview.IntegrityFailure, error.Value.Code);

        Assert.Contains("diagnostic", error.Value.Message, StringComparison.OrdinalIgnoreCase);

        AssertReviewDiagnostic(runtime, LogLevel.Error, prepare ? "Prepare" : "List", "CanonicalStateMissing");
    }

    [Fact]
    public async Task A_damaged_provenance_count_is_diagnosed_without_logging_the_key_or_source()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        await runtime.Fixture.AddCampaignAsync(CampaignOne, "One", Token);

        var seeded = await runtime.Fixture.SeedHeadAsync(CovenantScope.Campaign, CampaignOne,
            "private-key-canary", CovenantLane.Confirmed, CovenantOperation.Set, "private-content-canary", Token);

        await runtime.Fixture.AddProvenanceAsync(seeded.VersionId, 0,
            new MaterializationSourceDigestInput(Guid.CreateVersion7(), Guid.CreateVersion7(), "PRIVATE_DRIVER_CANARY.md",
                Digest(0xAB), CovenantMaterializationSourceRange.WholeSource, null, null,
                ImmutableArray<MaterializationOccurrenceDigestInput>.Empty), Token);

        await using CovenantReadLease lease = runtime.ReadLease();

        Result<CovenantReviewPageDto> result = await runtime.Service.ListAsync(
            new(CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed, 50, null), lease, Token);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.MemoryReview.IntegrityFailure, result.Error.Code);

        AssertReviewDiagnostic(runtime, LogLevel.Error, "List", "ProvenanceCountMismatch");
    }

    [Fact]
    public async Task A_corrupt_replay_response_digest_has_a_closed_reason_and_request_identity()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        (CovenantReviewBulkPrepareRequest request, MemoryReviewBulkPlanDto plan) = await PrepareConfirmAsync(runtime);

        await using CovenantWriteLease lease = runtime.WriteLease();

        Assert.True((await runtime.Service.ApplyAsync(new(request, plan.PreparedPlanToken), lease, Token)).IsSuccess);

        await ExecuteAsync(runtime.Fixture.Connection,
            "UPDATE covenant_review_decision_receipts SET ResponseReceiptDigest = zeroblob(32);");

        Result<MemoryReviewBulkResultDto> replay = await runtime.Service.ApplyAsync(new(request, plan.PreparedPlanToken), lease, Token);

        Assert.True(replay.IsFailure);

        Assert.Equal(ErrorCodes.MemoryReview.IntegrityFailure, replay.Error.Code);

        TestLogEntry logged = AssertReviewDiagnostic(runtime, LogLevel.Error, "Apply", "ReplayResponseDigestMismatch");

        Assert.Contains(logged.Properties!, property => property.Key == "RequestId" && Equals(property.Value, request.RequestId));
    }

    [Theory]
    [InlineData("busy", ErrorCodes.Covenant.Unavailable, "StorageBusy", LogLevel.Warning)]
    [InlineData("io", ErrorCodes.Covenant.Unavailable, "StorageUnavailable", LogLevel.Warning)]
    [InlineData("access", ErrorCodes.Covenant.Unavailable, "StorageUnavailable", LogLevel.Warning)]
    [InlineData("schema", ErrorCodes.MemoryReview.IntegrityFailure, "StorageFault", LogLevel.Error)]
    public async Task Review_read_acquisition_failures_are_safe_actionable_results(
        string kind, string code, string reason, LogLevel severity)
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        Exception cause = kind switch
        {
            "busy" => new SqliteException("PRIVATE_DRIVER_CANARY /private/profile/database", 5),
            "schema" => new SqliteException("PRIVATE_DRIVER_CANARY schema text", 1),
            "access" => new UnauthorizedAccessException("PRIVATE_DRIVER_CANARY /private/profile"),
            _ => new PRIVATE_DRIVER_CANARY_Exception("PRIVATE_DRIVER_CANARY /private/profile"),
        };

        CovenantMemoryReviewService service = ReviewServiceWithFault(runtime, cause);

        await using CovenantReadLease lease = runtime.ReadLease();

        Result<CovenantReviewPageDto>? result = null;

        Exception? failure = await Record.ExceptionAsync(async () => result = await service.ListAsync(
            new(CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed, 50, null), lease, Token));

        Assert.Null(failure);

        Assert.NotNull(result);

        Assert.True(result.IsFailure);

        Assert.Equal(code, result.Error.Code);

        Assert.DoesNotContain("PRIVATE_DRIVER_CANARY", result.Error.Message, StringComparison.Ordinal);

        AssertReviewDiagnostic(runtime, severity, "List", reason);
    }

    [Fact]
    public async Task Busy_review_apply_is_a_warning_with_closed_reason_and_no_driver_text()
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync(busyRetryDeadline: TimeSpan.Zero);

        (CovenantReviewBulkPrepareRequest request, MemoryReviewBulkPlanDto plan) = await PrepareConfirmAsync(runtime);

        runtime.Fixture.Connection.DefaultTimeout = 1;

        await ExecuteAsync(runtime.Fixture.Connection, "PRAGMA busy_timeout = 50;");

        await using SqliteConnection writer = await runtime.Fixture.OpenAdditionalConnectionAsync(Token);

        await ExecuteAsync(writer, "BEGIN IMMEDIATE;");

        try
        {
            await using CovenantWriteLease lease = runtime.WriteLease();

            Result<MemoryReviewBulkResultDto> result = await runtime.Service.ApplyAsync(new(request, plan.PreparedPlanToken), lease, Token);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.Unavailable, result.Error.Code);

            AssertReviewDiagnostic(runtime, LogLevel.Warning, "Apply", "StorageBusy");
        }
        finally
        {
            await ExecuteAsync(writer, "ROLLBACK;");
        }
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(-1L, 1L)]
    public async Task A_malformed_persisted_marker_is_an_integrity_refusal_never_a_synthetic_initial_marker(long through, long revision)
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        _ = await PrepareConfirmAsync(runtime);

        await ExecuteAsync(runtime.Fixture.Connection, "PRAGMA ignore_check_constraints = ON;");

        try
        {
            await using SqliteCommand command = runtime.Fixture.Connection.CreateCommand();

            command.CommandText = """
                INSERT INTO covenant_review_markers
                    (DatasetGeneration, ScopeCode, CampaignId, LaneCode, ReviewedThroughSequence, Revision)
                SELECT DatasetGeneration, 2, $campaign, 1, $through, $revision
                FROM covenant_state WHERE StateKey = 1;
                """;

            command.Parameters.AddWithValue("$campaign", CampaignOne.ToString("D"));

            command.Parameters.AddWithValue("$through", through);

            command.Parameters.AddWithValue("$revision", revision);

            Assert.Equal(1, await command.ExecuteNonQueryAsync(Token));
        }
        finally
        {
            await ExecuteAsync(runtime.Fixture.Connection, "PRAGMA ignore_check_constraints = OFF;");
        }

        await using CovenantReadLease lease = runtime.ReadLease();

        Result<CovenantReviewPageDto>? result = null;

        Exception? failure = await Record.ExceptionAsync(async () => result = await runtime.Service.ListAsync(
            new(CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed, 50, null), lease, Token));

        Assert.Null(failure);

        Assert.NotNull(result);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.MemoryReview.IntegrityFailure, result.Error.Code);

        AssertReviewDiagnostic(runtime, LogLevel.Error, "List", "StoredStateInvalid");

        Assert.Equal(1L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_markers;"));

        Assert.Equal(revision, await ScalarAsync(runtime.Fixture.Connection, "SELECT Revision FROM covenant_review_markers;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_interrupted_review_reads_distinguish_requested_cancellation_from_storage_interrupt(bool cancel)
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        using CancellationTokenSource cancellation = new();

        await using CovenantReadLease lease = runtime.ReadLease();

        int callbacks = 0;

        raw.sqlite3_progress_handler(runtime.Fixture.Connection.Handle, 1, (object _) =>
        {
            callbacks++;

            if (cancel)
            {
                cancellation.Cancel();
            }

            return 1;
        }, null);

        Result<CovenantReviewPageDto>? result = null;

        Exception? failure;

        try
        {
            failure = await Record.ExceptionAsync(async () => result = await runtime.Service.ListAsync(
                new(CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed, 50, null), lease, cancellation.Token));
        }
        finally
        {
            raw.sqlite3_progress_handler(runtime.Fixture.Connection.Handle, 0, null, null);
        }

        output.WriteLine($"Native interrupt: cancel={cancel}; callbacks={callbacks}; error={(result is { IsFailure: true } ? result.Error.Code : "none")}; exception={failure?.GetType().Name ?? "none"}.");

        Assert.True(callbacks > 0);

        if (cancel)
        {
            Assert.True(cancellation.IsCancellationRequested);

            Assert.IsAssignableFrom<OperationCanceledException>(failure);

            Assert.Empty(runtime.Logger.Entries);
        }
        else
        {
            Assert.Null(failure);

            Assert.NotNull(result);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.Unavailable, result.Error.Code);

            TestLogEntry logged = AssertReviewDiagnostic(runtime, LogLevel.Warning, "List", "StorageUnavailable");

            Assert.Contains(logged.Properties!, property => property.Key == "SqliteErrorCode" && Equals(property.Value, 9));
        }

        Assert.Equal(0L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_markers;"));

        Assert.Equal(0L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_decision_receipts;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_interrupted_apply_preflight_distinguishes_cancellation_without_writing(bool cancel)
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        (CovenantReviewBulkPrepareRequest request, MemoryReviewBulkPlanDto plan) = await PrepareConfirmAsync(runtime);

        using CancellationTokenSource cancellation = new();

        await using CovenantWriteLease lease = runtime.WriteLease();

        int callbacks = 0;

        raw.sqlite3_progress_handler(runtime.Fixture.Connection.Handle, 1, (object _) =>
        {
            callbacks++;

            if (cancel)
            {
                cancellation.Cancel();
            }

            return 1;
        }, null);

        Result<MemoryReviewBulkResultDto>? result = null;

        Exception? failure;

        try
        {
            failure = await Record.ExceptionAsync(async () => result = await runtime.Service.ApplyAsync(
                new(request, plan.PreparedPlanToken), lease, cancellation.Token));
        }
        finally
        {
            raw.sqlite3_progress_handler(runtime.Fixture.Connection.Handle, 0, null, null);
        }

        output.WriteLine($"Native apply interrupt: cancel={cancel}; callbacks={callbacks}; error={(result is { IsFailure: true } ? result.Error.Code : "none")}; exception={failure?.GetType().Name ?? "none"}.");

        Assert.True(callbacks > 0);

        if (cancel)
        {
            Assert.True(cancellation.IsCancellationRequested);

            Assert.IsAssignableFrom<OperationCanceledException>(failure);

            Assert.Empty(runtime.Logger.Entries);
        }
        else
        {
            Assert.Null(failure);

            Assert.NotNull(result);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.WriteFailed, result.Error.Code);

            AssertReviewDiagnostic(runtime, LogLevel.Error, "Apply", "StorageFault");
        }

        Assert.Equal(0L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_markers;"));

        Assert.Equal(0L, await ScalarAsync(runtime.Fixture.Connection, "SELECT count(*) FROM covenant_review_decision_receipts;"));
    }

    [Theory]
    [InlineData("programmer")]
    [InlineData("maintenance")]
    [InlineData("cancel")]
    public async Task Review_read_boundaries_do_not_reclassify_programmer_maintenance_or_cancellation_failures(string kind)
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        Exception cause = kind switch
        {
            "maintenance" => new GrimoireMaintenanceUnavailableException(),
            "cancel" => new OperationCanceledException(),
            _ => new InvalidOperationException("PRIVATE_DRIVER_CANARY programmer failure"),
        };

        CovenantMemoryReviewService service = ReviewServiceWithFault(runtime, cause);

        await using CovenantReadLease lease = runtime.ReadLease();

        Exception? failure = await Record.ExceptionAsync(async () => await service.ListAsync(
            new(CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed, 50, null), lease, Token));

        Assert.Same(cause, failure);

        Assert.Empty(runtime.Logger.Entries);
    }

    [Theory]
    [InlineData("shape", "ReplayReceiptInvalid")]
    [InlineData("generation", "ReplayGenerationMismatch")]
    [InlineData("event", "ReplayOriginalEventMissing")]
    public async Task Stored_replay_corruption_has_an_exact_closed_reason_without_rewriting_receipts(string damage, string reason)
    {
        await using ReviewRuntime runtime = await ReviewRuntime.CreateAsync();

        (CovenantReviewBulkPrepareRequest request, MemoryReviewBulkPlanDto plan) = await PrepareConfirmAsync(runtime);

        await using CovenantWriteLease lease = runtime.WriteLease();

        Assert.True((await runtime.Service.ApplyAsync(new(request, plan.PreparedPlanToken), lease, Token)).IsSuccess);

        await ExecuteAsync(runtime.Fixture.Connection, "PRAGMA foreign_keys = OFF;");

        try
        {
            await ExecuteAsync(runtime.Fixture.Connection, damage switch
            {
                "shape" => "UPDATE covenant_review_decision_receipts SET DecisionId = DecisionId || '|PRIVATE_DRIVER_CANARY';",
                "generation" => "UPDATE covenant_review_decision_receipts SET DatasetGeneration = randomblob(16);",
                _ => "UPDATE covenant_review_decision_receipts SET ReviewEventSequence = 9000000;",
            });
        }
        finally
        {
            await ExecuteAsync(runtime.Fixture.Connection, "PRAGMA foreign_keys = ON;");
        }

        string before = await ScalarTextAsync(runtime.Fixture.Connection,
            "SELECT DecisionId || ':' || hex(DatasetGeneration) || ':' || ReviewEventSequence || ':' || hex(ResponseReceiptDigest) FROM covenant_review_decision_receipts;");

        Result<MemoryReviewBulkResultDto> replay = await runtime.Service.ApplyAsync(new(request, plan.PreparedPlanToken), lease, Token);

        Assert.True(replay.IsFailure);

        Assert.Equal(ErrorCodes.MemoryReview.IntegrityFailure, replay.Error.Code);

        AssertReviewDiagnostic(runtime, LogLevel.Error, "Apply", reason);

        Assert.Equal(before, await ScalarTextAsync(runtime.Fixture.Connection,
            "SELECT DecisionId || ':' || hex(DatasetGeneration) || ':' || ReviewEventSequence || ':' || hex(ResponseReceiptDigest) FROM covenant_review_decision_receipts;"));

        Assert.Equal(2L, await ScalarAsync(runtime.Fixture.Connection, "SELECT Revision FROM covenant_review_markers;"));
    }

    private static TestLogEntry AssertReviewDiagnostic(ReviewRuntime runtime, LogLevel severity, string operation, string reason)
    {
        TestLogEntry logged = Assert.Single(runtime.Logger.Entries);

        Assert.Equal(severity, logged.Level);

        Assert.Null(logged.Exception);

        Assert.NotNull(logged.Properties);

        Assert.Contains(logged.Properties, property => property.Key == "Operation" && property.Value?.ToString() == operation);

        Assert.Contains(logged.Properties, property => property.Key == "Reason" && property.Value?.ToString() == reason);

        Assert.Contains(logged.Properties, property => property.Key == "Stage" && property.Value is not null);

        foreach (string value in logged.Properties.Select(static property => property.Value?.ToString() ?? string.Empty).Append(logged.Message))
        {
            Assert.DoesNotContain("PRIVATE_DRIVER_CANARY", value, StringComparison.Ordinal);

            Assert.DoesNotContain("private-key-canary", value, StringComparison.Ordinal);

            Assert.DoesNotContain("private-content-canary", value, StringComparison.Ordinal);

            Assert.DoesNotContain("/private/profile", value, StringComparison.Ordinal);
        }

        return logged;
    }

    private static CovenantMemoryReviewService ReviewServiceWithFault(ReviewRuntime runtime, Exception failure) => new(
        new ReviewFaultSource(failure), new CovenantCompiler(), runtime.Codec,
        new CovenantMutationKernel(new CovenantQuotaGuard(), MemoryErasureTestKeys.Isolated()),
        new CovenantCurationKernel(), runtime.Time, DetachedAvailabilityRepublisher.Create(), runtime.Logger);

    private sealed class ReviewFaultSource(Exception failure) : ICovenantConnectionSource
    {
        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) => throw failure;

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken) => throw failure;
    }

    private sealed class PRIVATE_DRIVER_CANARY_Exception(string message) : IOException(message);
}
