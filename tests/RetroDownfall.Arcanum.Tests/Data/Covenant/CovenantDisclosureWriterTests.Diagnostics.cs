using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed partial class CovenantDisclosureWriterTests
{
    [Fact]
    public async Task An_unknown_collaborator_category_and_private_refusal_text_never_enter_fault_diagnostics()
    {
        Error refusal = new(ErrorCodes.Covenant.Unavailable, "PRIVATE-refusal-path/key/SQL")
        {
            ConnectionFailure = new(GrimoireConnectionFailureStage.Open, (GrimoireConnectionFailureKind)9999, 10, 266),
        };

        TestCapturingLogger<CovenantDisclosureWriter> logger = new();

        await using WriterHarness harness = await WriterHarness.CreateAsync(
            wrap: _ => new RefusingOpenFactory(refusal), logger: logger);

        var first = await harness.AcknowledgeAsync(1);

        Assert.Equal(refusal, first.Error);

        var later = await harness.AcknowledgeAsync(2);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, later.Error.Code);

        Assert.Contains("Other", later.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("PRIVATE", later.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("9999", later.Error.Message, StringComparison.Ordinal);

        var warning = Assert.Single(logger.Entries);

        Assert.Null(warning.Exception);

        Assert.All(warning.Properties!, property => Assert.DoesNotContain("PRIVATE", property.Value?.ToString() ?? string.Empty, StringComparison.Ordinal));

        Assert.Contains(warning.Properties!, property => property.Key == "CauseCode" && Equals(property.Value, ErrorCodes.Covenant.Unavailable));

        Assert.Contains(warning.Properties!, property => property.Key == "Failure" && Equals(property.Value, GrimoireConnectionFailureKind.Other));
    }

    private sealed class RefusingOpenFactory(Error refusal) : IGrimoireOrdinaryConnectionFactory
    {
        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(SqliteConnection connection, CovenantSqliteConnectionMode mode, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind kind, CancellationToken cancellationToken) => Task.FromResult(Result<IGrimoireOrdinaryConnectionLease>.Failure(refusal));
    }

    [Fact]
    public async Task Cancellation_with_unproved_candidate_release_still_closes_admission_and_preserves_cancellation()
    {
        using CancellationTokenSource cancellation = new();

        FailingReleaseFactory? wrapper = null;

        await using WriterHarness harness = await WriterHarness.CreateAsync(new CovenantDisclosureTransactionWriter(BootId),
            wrap: inner => wrapper = new FailingReleaseFactory(inner, cancellation.Cancel));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Subject.AcknowledgeAsync(
                Draft(1), CovenantDisclosureEffectCategory.ProviderDispatch, Sensitivity, cancellation.Token).AsTask());

            var refused = await harness.AcknowledgeAsync(2);

            Assert.True(refused.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.Unavailable, refused.Error.Code);

            Assert.Contains("CandidateRelease", refused.Error.Message, StringComparison.Ordinal);

            Assert.Equal(0, await CountReceiptsAsync(Assert.Single(harness.FreshConnections.Opened)));

            wrapper!.FailRelease = false;

            Assert.True((await harness.Subject.ReopenAsync(Token)).IsSuccess);

            Assert.Equal(0, harness.FreshConnections.LiveLeaseCount);
        }
        finally
        {
            wrapper!.FailRelease = false;

            if (wrapper.LastLease is { } lease)
            {
                await lease.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_thrown_native_open_fault_retains_its_safe_category_and_numeric_codes_only_once()
    {
        TestCapturingLogger<CovenantDisclosureWriter> logger = new();

        await using WriterHarness harness = await WriterHarness.CreateAsync(
            wrap: _ => new ThrowingOpenFactory(), logger: logger);

        var first = await harness.AcknowledgeAsync(1);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, first.Error.Code);

        var second = await harness.AcknowledgeAsync(2);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, second.Error.Code);

        Assert.Contains("Sqlite", second.Error.Message, StringComparison.Ordinal);

        var warning = Assert.Single(logger.Entries);

        Assert.Contains(warning.Properties!, property => property.Key == "CauseCode" && Equals(property.Value, first.Error.Code));

        Assert.Contains(warning.Properties!, property => property.Key == "Failure" && Equals(property.Value, GrimoireConnectionFailureKind.Sqlite));

        Assert.Contains(warning.Properties!, property => property.Key == "SqliteErrorCode" && Equals(property.Value, 10));

        Assert.Contains(warning.Properties!, property => property.Key == "SqliteExtendedErrorCode" && Equals(property.Value, 266));

        Assert.Null(warning.Exception);

        Assert.DoesNotContain("PRIVATE", warning.Message, StringComparison.Ordinal);

        Assert.All(warning.Properties!, property => Assert.DoesNotContain("PRIVATE", property.Value?.ToString() ?? string.Empty, StringComparison.Ordinal));
    }

    private sealed class ThrowingOpenFactory : IGrimoireOrdinaryConnectionFactory
    {
        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(SqliteConnection connection, CovenantSqliteConnectionMode mode, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind kind, CancellationToken cancellationToken) =>
            Task.FromException<Result<IGrimoireOrdinaryConnectionLease>>(new SqliteException("PRIVATE-native-path/key/SQL", 10, 266));
    }

    [Fact]
    public async Task A_failed_candidate_release_overrides_preparation_refusal_and_blocks_reopen_until_release_is_proven()
    {
        FailingReleaseFactory? wrapper = null;

        TestCapturingLogger<CovenantDisclosureWriter> logger = new();

        await using WriterHarness harness = await WriterHarness.CreateAsync(wrap: inner => wrapper = new FailingReleaseFactory(inner), logger: logger);

        try
        {
            harness.Availability!.Publish(CovenantCapabilityState.Healthy, Guid.NewGuid());

            var refused = await harness.AcknowledgeAsync(1);

            Assert.True(refused.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, refused.Error.Code);

            Assert.Contains("release", refused.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.True((await harness.AcknowledgeAsync(2)).IsFailure);

            harness.Availability.Publish(CovenantCapabilityState.Healthy, harness.DatasetGeneration);

            Assert.True((await harness.Subject.ReopenAsync(Token)).IsFailure);

            Assert.Single(logger.Entries);

            wrapper!.FailRelease = false;

            Assert.True((await harness.Subject.ReopenAsync(Token)).IsSuccess);

            Assert.Equal(0, harness.FreshConnections.LiveLeaseCount);

            Assert.True((await harness.AcknowledgeAsync(3)).IsSuccess);
        }
        finally
        {
            wrapper!.FailRelease = false;

            if (wrapper.LastLease is { } lease)
            {
                await lease.DisposeAsync();
            }
        }
    }

    private sealed class FailingReleaseFactory(IGrimoireOrdinaryConnectionFactory inner, Action? afterOpen = null) : IGrimoireOrdinaryConnectionFactory
    {
        internal bool FailRelease { get; set; } = true;

        internal IGrimoireOrdinaryConnectionLease? LastLease { get; private set; }

        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(SqliteConnection connection, CovenantSqliteConnectionMode mode, CancellationToken cancellationToken) =>
            inner.AcquireScopedAsync(connection, mode, cancellationToken);

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind kind, CancellationToken cancellationToken)
        {
            var opened = await inner.OpenFreshAsync(kind, cancellationToken);

            if (opened.IsFailure)
            {
                return opened;
            }

            LastLease = new Lease(this, opened.Value);

            afterOpen?.Invoke();

            return Result<IGrimoireOrdinaryConnectionLease>.Success(LastLease);
        }

        private sealed class Lease(FailingReleaseFactory owner, IGrimoireOrdinaryConnectionLease inner) : IGrimoireOrdinaryConnectionLease
        {
            public SqliteConnection Connection => inner.Connection;

            public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

            public async ValueTask DisposeAsync()
            {
                if (owner.FailRelease)
                {
                    throw new IOException("PRIVATE-release-path/key");
                }

                await inner.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_fault_closure_reports_one_safe_warning_and_no_receipt_before_explicit_restoration()
    {
        await using WriterHarness harness = await WriterHarness.CreateAsync();

        TestCapturingLogger<CovenantDisclosureWriter> logger = new();

        ServiceCollection services = [];

        services.AddArcanumGrimoireForCli();

        services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(harness.FreshConnections);

        services.AddSingleton<ICovenantAvailability>(harness.Availability!);

        services.AddSingleton<ILogger<CovenantDisclosureWriter>>(logger);

        await using ServiceProvider provider = services.BuildServiceProvider();

        var journal = provider.GetRequiredService<ICovenantDisclosureJournal>();

        harness.FreshConnections.RefuseNextOpen("PRIVATE-cause-code");

        for (byte effect = 1; effect < 4; effect++)
        {
            Assert.True((await journal.AcknowledgeAsync(Draft(effect), CovenantDisclosureEffectCategory.ProviderDispatch, Sensitivity, Token)).IsFailure);
        }

        var warning = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Warning, warning.Level);

        Assert.Contains(warning.Properties!, property => property.Key == "CauseCode" && Equals(property.Value, "Other"));

        Assert.Null(warning.Exception);

        Assert.DoesNotContain("PRIVATE", warning.Message, StringComparison.Ordinal);

        Assert.All(warning.Properties!, property => Assert.DoesNotContain("PRIVATE", property.Value?.ToString() ?? string.Empty, StringComparison.Ordinal));

        Assert.Empty(harness.FreshConnections.Opened);
    }

    [Fact]
    public async Task An_earlier_open_fault_stays_closed_with_a_cause_and_restart_remedy()
    {
        await using WriterHarness harness = await WriterHarness.CreateAsync();

        harness.FreshConnections.RefuseNextOpen();

        Result<CovenantDisclosureReceipt> first = await harness.AcknowledgeAsync(1);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, first.Error.Code);

        harness.Availability!.Publish(CovenantCapabilityState.Healthy, harness.DatasetGeneration);

        for (byte effect = 2; effect < 4; effect++)
        {
            Result<CovenantDisclosureReceipt> next = await harness.AcknowledgeAsync(effect);

            Assert.True(next.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.Unavailable, next.Error.Code);

            Assert.Contains("earlier", next.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("restart", next.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain("closed for maintenance", next.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Empty(harness.FreshConnections.Opened);
        }

        Assert.True((await harness.Subject.ReopenAsync(Token)).IsSuccess);

        Assert.True((await harness.AcknowledgeAsync(4)).IsSuccess);
    }
}
