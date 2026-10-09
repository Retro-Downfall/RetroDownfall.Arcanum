using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed class SessionSummaryMaintenanceStoreTests
{
    private static readonly Guid Session = Guid.Parse("7B0B0C0D-0E0F-4123-ABCD-718192A3B4C5");

    [Fact]
    public async Task A_clean_full_history_snapshot_keeps_its_previous_summary_and_publishes_exact_immutable_evidence()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        SessionSummaryMaintenanceInput input = (await fixture.Store.PrepareAsync(Session, 1, CancellationToken.None)).Value!;

        Assert.Equal("The copied prefix remains in the ordinary summary.", input.PreviousSummary);

        Assert.Single(input.Entries);

        Assert.Equal("native tail one", input.Entries[0].Content);

        Assert.Equal(2, input.HistoryRevision);

        Result saved = await fixture.Store.PublishAsync(input, "The full prefix and first tail.", CancellationToken.None);

        Assert.True(saved.IsSuccess, saved.Error.Message);

        Assert.Equal("The full prefix and first tail.", await fixture.ScalarTextAsync("SELECT Summary FROM Sessions;"));

        Assert.Equal(1, await fixture.ScalarAsync("SELECT Revision FROM session_summary_state;"));

        Assert.Equal(1, await fixture.ScalarAsync("SELECT COUNT(*) FROM session_summary_artifacts;"));

        Assert.Equal(1, await fixture.ScalarAsync("SELECT UnsummarizedEntryCount FROM Sessions;"));

        await using SqliteCommand digest = fixture.Connection.CreateCommand();

        digest.CommandText = "SELECT ContentDigest FROM session_summary_artifacts;";

        Assert.Equal(DerivedArtifactContentDigest.ForText("The full prefix and first tail.").Bytes,
            Assert.IsType<byte[]>(await digest.ExecuteScalarAsync()));

        Assert.Equal(0, fixture.Source.CanonicalOpens);

        Assert.Equal(2, fixture.Source.CoreOpens);
    }

    [Fact]
    public async Task Tainted_background_input_is_refused_before_the_summary_column_is_materialized()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await fixture.TaintAsync();

        fixture.Connection.CreateFunction<string>("protected_summary_read", () =>
        {
            fixture.ProtectedSummaryReads++;

            throw new InvalidOperationException("Protected summary text was materialized.");
        });

        await fixture.ExecuteAsync("""
            ALTER TABLE Sessions RENAME TO SessionHeaders;
            CREATE VIEW Sessions AS
                SELECT Id, protected_summary_read() AS Summary, LastSummarizedMessageAt
                FROM SessionHeaders;
            """);

        Result<SessionSummaryMaintenanceInput?> input = await fixture.Store.PrepareAsync(Session, 1, CancellationToken.None);

        Assert.True(input.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, input.Error.Code);

        Assert.Equal(0, fixture.ProtectedSummaryReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_changed_source_or_new_taint_after_paid_output_publishes_neither_content_pointer_nor_watermark(bool taint)
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        SessionSummaryMaintenanceInput input = (await fixture.Store.PrepareAsync(Session, 1, CancellationToken.None)).Value!;

        if (taint)
        {
            await fixture.TaintAsync();
        }
        else
        {
            await fixture.ExecuteAsync("UPDATE Entries SET Content = 'changed source' WHERE Sequence = 1;");
        }

        Result saved = await fixture.Store.PublishAsync(input, "stale paid output", CancellationToken.None);

        Assert.True(saved.IsFailure);

        Assert.Equal("The copied prefix remains in the ordinary summary.", await fixture.ScalarTextAsync("SELECT Summary FROM Sessions;"));

        Assert.Null(await fixture.ScalarTextAsync("SELECT LastSummarizedMessageAt FROM Sessions;"));

        Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM session_summary_state;"));

        Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM session_summary_artifacts;"));
    }

    private sealed class Fixture(CovenantSchemaScratchDatabase database) : IAsyncDisposable
    {
        public SqliteConnection Connection => database.Connection;

        public CountingSource Source { get; } = new(database.Connection);

        public SessionSummaryMaintenanceStore Store { get; private set; } = null!;

        public int ProtectedSummaryReads { get; set; }

        public static async Task<Fixture> CreateAsync()
        {
            CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

            try
            {
                await database.InstallCoreObjectsAsync(
                [
                    "Campaigns", "Sessions", "Entries", "artifact_sensitivity", "artifact_sensitivity_guard_delete",
                    "artifact_sensitivity_guard_update", "session_sensitivity_state", "session_summary_artifacts",
                    "session_summary_artifacts_guard_delete", "session_summary_artifacts_guard_update", "session_summary_state",
                ], CancellationToken.None);

                Fixture fixture = new(database);

                fixture.Store = new(fixture.Source, CovenantSqliteConnectionInitializer.Instance);

                await using SqliteCommand seed = database.Connection.CreateCommand();

                seed.CommandText = """
                    INSERT INTO Sessions(Id,Title,Summary,CreatedAt,UpdatedAt,UnsummarizedEntryCount)
                    VALUES($session,'summary','The copied prefix remains in the ordinary summary.',$now,$now,2);
                    INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
                    VALUES($one,$session,1,'native tail one','model','2026-10-01T00:00:00.0000000+00:00',1),
                          ($two,$session,2,'native tail two','model','2026-10-01T00:01:00.0000000+00:00',2);
                    """;

                _ = seed.Parameters.AddWithValue("$session", Session.ToString("D").ToUpperInvariant());

                _ = seed.Parameters.AddWithValue("$one", Guid.NewGuid().ToString("D").ToUpperInvariant());

                _ = seed.Parameters.AddWithValue("$two", Guid.NewGuid().ToString("D").ToUpperInvariant());

                _ = seed.Parameters.AddWithValue("$now", "2026-10-01T00:00:00.0000000+00:00");

                _ = await seed.ExecuteNonQueryAsync();

                return fixture;
            }
            catch
            {
                await database.DisposeAsync();

                throw;
            }
        }

        public Task TaintAsync() => ExecuteAsync($"""
            INSERT INTO session_sensitivity_state
                (SessionId,TaintedArtifactCount,MaximumSensitivityCode,GenerationProvenanceDigest,Revision,UpdatedAtUtc)
            VALUES ('{Session.ToString("D").ToUpperInvariant()}',1,1,zeroblob(32),1,'2026-10-01T00:02:00.0000000+00:00');
            """);

        public Task ExecuteAsync(string sql) => database.ExecuteAsync(sql, CancellationToken.None);

        public Task<long> ScalarAsync(string sql) => database.ScalarLongAsync(sql, CancellationToken.None);

        public Task<string?> ScalarTextAsync(string sql) => database.ScalarStringAsync(sql, CancellationToken.None);

        public ValueTask DisposeAsync() => database.DisposeAsync();
    }

    private sealed class CountingSource(SqliteConnection connection) : ICovenantConnectionSource
    {
        public int CoreOpens { get; private set; }

        public int CanonicalOpens { get; private set; }

        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
        {
            CanonicalOpens++;

            return ValueTask.FromResult(connection);
        }

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken)
        {
            CoreOpens++;

            return ValueTask.FromResult(connection);
        }
    }
}
