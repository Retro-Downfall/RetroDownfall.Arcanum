using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Compare-and-set guard behind inference-run crash recovery, exercised against real SQLite
/// rather than a fake, because the guarantee lives in the <c>WHERE … AND Status = Running</c> clause.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class TurnRunWriterRecoveryTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();
        _db = fixture.CreateContext(_dbPath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

            await _db.DisposeAsync();

            SqliteConnection.ClearPool(connection);
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static InferenceRunStart Start() =>
        new(
            RequestId: Guid.NewGuid().ToString("N"),
            SessionId: null,
            Surface: "test",
            Purpose: "recovery",
            IdempotencyClaimId: null,
            StartedAt: DateTimeOffset.UtcNow);

    private async Task<InferenceRunStatus> ReadStatusAsync(Guid runId)
    {
        await using SqliteCommand command = (SqliteCommand)_db!.Database.GetDbConnection().CreateCommand();

        command.CommandText = """SELECT "Status" FROM "InferenceRuns" WHERE "Id" = @id""";
        _ = command.Parameters.AddWithValue("@id", runId.ToString("N"));

        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        return (InferenceRunStatus)Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    [SkippableFact]
    public async Task Abandoning_a_running_run_succeeds_exactly_once()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TurnRunWriter writer = new(_db!);
        Guid runId = await writer.StartRunAsync(Start());

        bool first = await writer.TryAbandonRunAsync(runId);
        bool second = await writer.TryAbandonRunAsync(runId);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(InferenceRunStatus.Abandoned, await ReadStatusAsync(runId));
    }

    /// <summary>
    /// The case that makes repeated startup reconciliation safe: a run that genuinely finished
    /// before the crash landed keeps its terminal status.
    /// </summary>
    [SkippableFact]
    public async Task Abandoning_never_downgrades_a_run_that_already_completed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TurnRunWriter writer = new(_db!);
        Guid runId = await writer.StartRunAsync(Start());

        await writer.CompleteRunAsync(runId, InferenceRunStatus.Completed);

        Assert.False(await writer.TryAbandonRunAsync(runId));
        Assert.Equal(InferenceRunStatus.Completed, await ReadStatusAsync(runId));
    }

    [SkippableFact]
    public async Task Abandoning_an_unknown_run_reports_that_it_did_nothing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TurnRunWriter writer = new(_db!);

        Assert.False(await writer.TryAbandonRunAsync(Guid.NewGuid()));
    }
}
