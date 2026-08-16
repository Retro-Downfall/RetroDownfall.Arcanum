using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The immutable artifact, current pointer, and label behind the two mutable Session columns.
/// </summary>
/// <remarks>
/// A summary and a title are the two places in the Grimoire where model output overwrites itself in
/// place. These tests exist to prove the overwrite carries its evidence with it: the previous
/// revision and its label go away in the same transaction that installs the new ones, and a clean
/// replacement is the only thing that can leave a Session with no protected label at all.
/// </remarks>
public sealed class SessionDerivedArtifactStoreTests
{

    private static readonly Guid Session = Guid.Parse("4E5F6071-8293-4EAF-90B1-4C5D6E7F8091");

    private static readonly Guid Generation = Guid.Parse("5F607182-93A4-4FB0-81C2-5D6E7F8091A2");

    [Fact]
    public async Task A_tainted_summary_replacement_writes_its_artifact_pointer_column_and_label()
    {

        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        Result<SessionDerivedArtifactWriteReceipt> receipt =
            await ((ISessionSummaryArtifactStore)fixture.Store).ReplaceAsync(
                new SessionSummaryArtifactWrite(
                    Session,
                    "the operator prefers terse answers",
                    new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero),
                    ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([Generation])),
                CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.Error.Message);

        Assert.Equal(1, receipt.Value.Revision);

        Assert.NotNull(receipt.Value.LabelId);

        Assert.Equal(
            "the operator prefers terse answers",
            await fixture.ScalarStringAsync("SELECT \"Summary\" FROM \"Sessions\";"));

        Assert.Equal(
            receipt.Value.ArtifactId.ToString().ToUpperInvariant(),
            await fixture.ScalarStringAsync("SELECT CurrentArtifactId FROM session_summary_state;"));

        Assert.Equal(1, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

    }

    [Fact]
    public async Task A_replacement_retires_the_previous_revision_and_its_label_together()
    {

        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        ISessionSummaryArtifactStore store = fixture.Store;

        _ = await store.ReplaceAsync(TaintedSummary("first"), CancellationToken.None);

        Result<SessionDerivedArtifactWriteReceipt> second =
            await store.ReplaceAsync(TaintedSummary("second"), CancellationToken.None);

        Assert.Equal(2, second.Value.Revision);

        // Exactly one artifact and one label survive: the current revision's.
        Assert.Equal(1, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM session_summary_artifacts;"));

        Assert.Equal(1, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

        Assert.Equal(
            second.Value.ArtifactId.ToString().ToUpperInvariant(),
            await fixture.ScalarStringAsync("SELECT ArtifactId FROM artifact_sensitivity;"));

    }

    [Fact]
    public async Task A_clean_operator_replacement_removes_the_prior_tainted_label()
    {

        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        ISessionTitleArtifactStore store = fixture.Store;

        _ = await store.ReplaceAsync(
            new SessionTitleArtifactWrite(
                Session,
                "a model-generated title",
                ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([Generation])),
            CancellationToken.None);

        Assert.Equal(1, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

        Result<SessionDerivedArtifactWriteReceipt> clean = await store.ReplaceAsync(
            new SessionTitleArtifactWrite(
                Session,
                "an operator title",
                ContentSensitivity.None,
                GenerationProvenance.CreateExact([])),
            CancellationToken.None);

        Assert.True(clean.IsSuccess, clean.Error.Message);

        Assert.Null(clean.Value.LabelId);

        // The old label described the old title, and the old title is gone with it.
        Assert.Equal(0, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

        Assert.Equal("an operator title", await fixture.ScalarStringAsync("SELECT \"Title\" FROM \"Sessions\";"));

    }

    [Fact]
    public async Task Clearing_a_summary_is_a_revision_rather_than_an_untracked_null()
    {

        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        ISessionSummaryArtifactStore store = fixture.Store;

        _ = await store.ReplaceAsync(TaintedSummary("something"), CancellationToken.None);

        Result<SessionDerivedArtifactWriteReceipt> cleared = await store.ReplaceAsync(
            new SessionSummaryArtifactWrite(
                Session,
                null,
                null,
                ContentSensitivity.None,
                GenerationProvenance.CreateExact([])),
            CancellationToken.None);

        Assert.Equal(2, cleared.Value.Revision);

        Assert.Null(await fixture.ScalarStringAsync("SELECT \"Summary\" FROM \"Sessions\";"));

        Assert.Equal(0, await fixture.ScalarLongAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

    }

    [Fact]
    public async Task An_artifact_row_cannot_be_edited_in_place()
    {

        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        _ = await ((ISessionSummaryArtifactStore)fixture.Store)
            .ReplaceAsync(TaintedSummary("immutable"), CancellationToken.None);

        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(() =>
            fixture.ExecuteAsync("UPDATE session_summary_artifacts SET SensitivityCode = 0;"));

        Assert.Contains("immutable", refused.Message, StringComparison.OrdinalIgnoreCase);

    }

    private static SessionSummaryArtifactWrite TaintedSummary(string content) =>
        new(
            Session,
            content,
            null,
            ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Generation]));

    private sealed class StoreFixture : IAsyncDisposable
    {

        private readonly CovenantSchemaScratchDatabase _database;

        private StoreFixture(CovenantSchemaScratchDatabase database)
        {

            _database = database;

            Store = new SessionDerivedArtifactStore(
                new FixedCovenantConnectionSource(database.Connection),
                CovenantSqliteConnectionInitializer.Instance);

        }

        internal SessionDerivedArtifactStore Store { get; }

        internal static async Task<StoreFixture> CreateAsync()
        {

            CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase
                .CreateAsync(CancellationToken.None);

            try
            {

                await database.InstallCoreObjectsAsync(
                    [
                        "Campaigns",
                        "Sessions",
                        "artifact_sensitivity",
                        "artifact_sensitivity_guard_delete",
                        "artifact_sensitivity_guard_update",
                        "session_sensitivity_state",
                        "session_summary_artifacts",
                        "session_summary_artifacts_guard_delete",
                        "session_summary_artifacts_guard_update",
                        "session_summary_state",
                        "session_title_artifacts",
                        "session_title_artifacts_guard_delete",
                        "session_title_artifacts_guard_update",
                        "session_title_state",
                    ],
                    CancellationToken.None);

                await using SqliteCommand seed = database.Connection.CreateCommand();

                seed.CommandText = """
                    INSERT INTO "Sessions" ("Id", "Title", "CreatedAt", "UpdatedAt")
                    VALUES ($sessionId, 'derived', $now, $now);
                    """;

                _ = seed.Parameters.AddWithValue("$sessionId", Session.ToString().ToUpperInvariant());

                _ = seed.Parameters.AddWithValue("$now", "2026-08-16T00:00:00.0000000+00:00");

                _ = await seed.ExecuteNonQueryAsync(CancellationToken.None);

                return new StoreFixture(database);

            }
            catch
            {

                await database.DisposeAsync();

                throw;

            }

        }

        internal Task<long> ScalarLongAsync(string sql) =>
            _database.ScalarLongAsync(sql, CancellationToken.None);

        internal Task<string?> ScalarStringAsync(string sql) =>
            _database.ScalarStringAsync(sql, CancellationToken.None);

        internal Task ExecuteAsync(string sql) => _database.ExecuteAsync(sql, CancellationToken.None);

        public ValueTask DisposeAsync() => _database.DisposeAsync();

    }

}
