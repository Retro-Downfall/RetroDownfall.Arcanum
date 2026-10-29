using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GrimoireSchemaQuiescentExecutionTests
{
    static GrimoireSchemaQuiescentExecutionTests() => SqliteNativeRuntime.Instance.Initialize();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_open_source_with_held_lock_and_correct_phase_is_accepted(bool staged)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        string root = Path.GetDirectoryName(new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource)!;

        string guarded = staged ? Path.Combine(root, "live") : root;

        Directory.CreateDirectory(guarded);

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(guarded).BorrowAcquiredLock();

        using GrimoireSchemaQuiescentExecution execution = staged
            ? GrimoireSchemaQuiescentExecution.ForStagedRestore(connection, held, guarded, new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource)
            : GrimoireSchemaQuiescentExecution.ForStartup(connection, held, guarded, new GrimoireDbReadiness());

        execution.Require(connection);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("released-lock")]
    [InlineData("closed")]
    [InlineData("reopened")]
    [InlineData("disposed")]
    [InlineData("other-connection")]
    [InlineData("wrong-root")]
    public async Task Startup_execution_refuses_lost_or_mismatched_quiescence(string invalid)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        string root = Path.GetDirectoryName(new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource)!;

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(root).BorrowAcquiredLock();

        GrimoireDbReadiness readiness = new();

        using GrimoireSchemaQuiescentExecution execution = GrimoireSchemaQuiescentExecution.ForStartup(
            connection, held, invalid == "wrong-root" ? Path.Combine(root, "wrong") : root, readiness);

        if (invalid == "ready") readiness.MarkReady();

        if (invalid == "released-lock") held.Dispose();

        if (invalid is "closed" or "reopened") await connection.CloseAsync();

        if (invalid == "reopened") await connection.OpenAsync();

        if (invalid == "disposed") execution.Dispose();

        await using SqliteConnection other = await scratch.OpenAsync(CancellationToken.None);

        Assert.ThrowsAny<InvalidOperationException>(() => execution.Require(invalid == "other-connection" ? other : connection));
    }

    [Fact]
    public async Task Staged_execution_cannot_designate_the_live_database_or_another_source()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        string root = Path.GetDirectoryName(new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource)!;

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(root).BorrowAcquiredLock();

        Assert.Throws<InvalidOperationException>(() =>
            GrimoireSchemaQuiescentExecution.ForStagedRestore(connection, held, root, new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource).Require(connection));

        Assert.Throws<InvalidOperationException>(() =>
            GrimoireSchemaQuiescentExecution.ForStagedRestore(connection, held, root, Path.Combine(root, "different.db")).Require(connection));
    }
}
