using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed partial class CovenantConnectionDrainTests
{
    [Fact]
    public async Task Production_operation_store_enrolment_names_its_actual_enrolling_component()
    {
        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        await using UnclosableConnection stuck = new(database.Connection.ConnectionString);

        await stuck.OpenAsync(Token);

        await using var context = GrimoireFixture.CreateContext(stuck);

        CovenantConnectionDrain drain = new();

        using LongRunningOperationStore store = new(context, new RecordingFreshOrdinaryConnectionFactory(stuck.ConnectionString), drain);

        try
        {
            var result = await drain.DrainAsync(Token);

            Assert.True(result.IsFailure);

            Assert.Contains("OperationStore", result.Error.Message, StringComparison.Ordinal);

            Assert.DoesNotContain("OrdinaryGrimoire", result.Error.Message, StringComparison.Ordinal);
        }
        finally
        {
            stuck.ForceClose();
        }
    }

    [Fact]
    public async Task Production_ordinary_enrolment_names_its_actual_enrolling_component()
    {
        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        await using UnclosableConnection stuck = new(database.Connection.ConnectionString);

        CovenantConnectionDrain drain = new();

        GrimoireConnectionAdmissionGate admission = new(TimeProvider.System, drain);

        var options = new DbContextOptionsBuilder<DrainProbeDbContext>().UseSqlite(stuck)
            .AddInterceptors(new CovenantConnectionEnrolmentInterceptor(
                new GrimoireOrdinaryConnectionLifecycle(admission, drain), drain,
                CovenantSqliteConnectionInitializer.Instance, SqliteNativeRuntime.Instance)).Options;

        await using DrainProbeDbContext context = new(options);

        await context.Database.OpenConnectionAsync(Token);

        try
        {
            var result = await drain.DrainAsync(Token);

            Assert.True(result.IsFailure);

            Assert.Contains("OrdinaryGrimoire", result.Error.Message, StringComparison.Ordinal);

            Assert.DoesNotContain("OperationStore", result.Error.Message, StringComparison.Ordinal);
        }
        finally
        {
            stuck.ForceClose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diagnostic_owner_counts_follow_only_live_registrations_on_the_same_handle(bool releaseOperationOwner)
    {
        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        await using UnclosableConnection stuck = new(database.Connection.ConnectionString);

        await stuck.OpenAsync(Token);

        CovenantConnectionDrain drain = new();

        using IDisposable ordinary = drain.Register(stuck, CovenantConnectionOwner.OrdinaryGrimoire);

        using IDisposable operation = drain.Register(stuck, CovenantConnectionOwner.OperationStore);

        if (releaseOperationOwner)
        {
            operation.Dispose();
        }

        try
        {
            Result result = await drain.DrainAsync(Token);

            Assert.True(result.IsFailure);

            Assert.Contains("OrdinaryGrimoire", result.Error.Message, StringComparison.Ordinal);

            Assert.Contains("Handles=1", result.Error.Message, StringComparison.Ordinal);

            Assert.Contains(releaseOperationOwner ? "Enrolments=1" : "Enrolments=2", result.Error.Message, StringComparison.Ordinal);

            Assert.Equal(!releaseOperationOwner, result.Error.Message.Contains("OperationStore", StringComparison.Ordinal));
        }
        finally
        {
            stuck.ForceClose();
        }
    }

    [Fact]
    public async Task Refusing_a_handle_reports_bounded_enrolling_owner_and_snapshot_counts()
    {
        await using CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        await using UnclosableConnection stuck = new(database.Connection.ConnectionString);

        await stuck.OpenAsync(Token);

        CovenantConnectionDrain drain = new();

        using IDisposable first = drain.Register(stuck);

        using IDisposable second = drain.Register(stuck);

        try
        {
            Result result = await drain.DrainAsync(Token);

            Assert.True(result.IsFailure);

            Assert.Contains("OrdinaryGrimoire", result.Error.Message, StringComparison.Ordinal);

            Assert.Contains("Handles=1", result.Error.Message, StringComparison.Ordinal);

            Assert.Contains("Enrolments=2", result.Error.Message, StringComparison.Ordinal);
        }
        finally
        {
            stuck.ForceClose();
        }
    }

    [Fact]
    public async Task A_SQLite_close_refusal_never_exposes_the_driver_message()
    {
        CovenantConnectionDrain drain = new();

        await using ThrowingCloseConnection connection = new();

        using IDisposable enrolled = drain.Register(connection);

        Result result = await drain.DrainAsync(Token);

        Assert.True(result.IsFailure);

        Assert.DoesNotContain("PRIVATE-driver-path", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("SQLite=10", result.Error.Message, StringComparison.Ordinal);
    }

    private sealed class ThrowingCloseConnection : SqliteConnection
    {
        public override Task CloseAsync() => Task.FromException(new SqliteException("PRIVATE-driver-path/key/SQL", 10));
    }
}
