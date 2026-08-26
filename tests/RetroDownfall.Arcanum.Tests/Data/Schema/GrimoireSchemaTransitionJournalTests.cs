using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The journal row is the only record that a version run is in flight, so every field it carries has
/// to survive a round trip exactly, and every advance has to be conditional on the revision it was
/// read at.
/// </summary>
public sealed class GrimoireSchemaTransitionJournalTests
{

    static GrimoireSchemaTransitionJournalTests() => SqliteNativeRuntime.Instance.Initialize();

    private const string TargetFingerprint =
        "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Every numeric field holds a different value, so transposing two columns in the projection
    /// fails rather than reproducing the row it was given.
    /// </summary>
    [Fact]
    public async Task A_row_survives_a_round_trip_field_for_field()
    {

        await using SqliteConnection connection = await InstallAsync();

        GrimoireSchemaTransitionJournalRow row = SampleRow();

        await InsertAsync(connection, row);

        GrimoireSchemaTransitionJournalRow? read = await GrimoireSchemaTransitionJournal.ReadAsync(
            connection,
            transaction: null,
            GrimoireSchemaTransactionTier.CovenantAccelerator,
            CancellationToken.None);

        Assert.Equal(row, read);

    }

    [Fact]
    public async Task Reading_a_tier_with_no_run_returns_nothing()
    {

        await using SqliteConnection connection = await InstallAsync();

        Assert.Null(
            await GrimoireSchemaTransitionJournal.ReadAsync(
                connection,
                transaction: null,
                GrimoireSchemaTransactionTier.Core,
                CancellationToken.None));

    }

    /// <summary>
    /// A host coordinator and a CLI bootstrap can both hold the encrypted file, so the loser of a
    /// race must fail rather than move a cursor past work only the winner did.
    /// </summary>
    [Fact]
    public async Task An_advance_at_a_stale_revision_is_refused()
    {

        await using SqliteConnection connection = await InstallAsync();

        GrimoireSchemaTransitionJournalRow row = SampleRow();

        await InsertAsync(connection, row);

        Assert.True(await AdvanceAsync(connection, row, "cursor-9", rowsProcessed: 55));

        // The same row object still carries revision 0, which is exactly what a second writer that
        // read before the first advance would hold.
        Assert.False(await AdvanceAsync(connection, row, "cursor-11", rowsProcessed: 99));

        GrimoireSchemaTransitionJournalRow? read = await GrimoireSchemaTransitionJournal.ReadAsync(
            connection,
            transaction: null,
            GrimoireSchemaTransactionTier.CovenantAccelerator,
            CancellationToken.None);

        Assert.NotNull(read);

        Assert.Equal("cursor-9", read.BackfillCursor);

        Assert.Equal(55, read.BackfillRowsProcessed);

        Assert.Equal(1, read.Revision);

    }

    [Fact]
    public async Task A_delete_at_a_stale_revision_is_refused()
    {

        await using SqliteConnection connection = await InstallAsync();

        GrimoireSchemaTransitionJournalRow row = SampleRow();

        await InsertAsync(connection, row);

        Assert.True(await AdvanceAsync(connection, row, "cursor-9", rowsProcessed: 55));

        Assert.False(await DeleteAsync(connection, row));

        Assert.NotNull(
            await GrimoireSchemaTransitionJournal.ReadAsync(
                connection,
                transaction: null,
                GrimoireSchemaTransactionTier.CovenantAccelerator,
                CancellationToken.None));

        Assert.True(await DeleteAsync(connection, row with { Revision = 1 }));

        Assert.Null(
            await GrimoireSchemaTransitionJournal.ReadAsync(
                connection,
                transaction: null,
                GrimoireSchemaTransactionTier.CovenantAccelerator,
                CancellationToken.None));

    }

    [Fact]
    public async Task Every_in_flight_run_is_listed()
    {

        await using SqliteConnection connection = await InstallAsync();

        await InsertAsync(connection, SampleRow());

        await InsertAsync(
            connection,
            SampleRow() with
            {

                Family = GrimoireSchemaFamily.Core,

                TransactionTier = GrimoireSchemaTransactionTier.Core,

                BackfillName = null,

                BackfillCursor = null,

            });

        IReadOnlyList<GrimoireSchemaTransitionJournalRow> all =
            await GrimoireSchemaTransitionJournal.ReadAllAsync(connection, CancellationToken.None);

        Assert.Equal(2, all.Count);

        Assert.Contains(all, candidate => candidate.TransactionTier == GrimoireSchemaTransactionTier.Core);

        Assert.Contains(
            all,
            candidate => candidate.TransactionTier == GrimoireSchemaTransactionTier.CovenantAccelerator);

    }

    /// <summary>
    /// Family and tier deliberately hold different codes, so a projection that read one column into
    /// the other's field would not reproduce the row.
    /// </summary>
    private static GrimoireSchemaTransitionJournalRow SampleRow() =>
        new(
            GrimoireSchemaFamily.Covenant,
            GrimoireSchemaTransactionTier.CovenantAccelerator,
            FromVersion: 1,
            TargetVersion: 3,
            CompletedThroughVersion: 2,
            TargetSourceDefinitionFingerprint: TargetFingerprint,
            BackfillName: "widen-validity",
            BackfillCursor: "cursor-7",
            BackfillRowsProcessed: 41,
            Revision: 0);

    private static async Task InsertAsync(
        SqliteConnection connection,
        GrimoireSchemaTransitionJournalRow row)
    {

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(CancellationToken.None);

        await GrimoireSchemaTransitionJournal.InsertAsync(
            connection,
            transaction,
            row,
            Now,
            CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

    }

    private static async Task<bool> AdvanceAsync(
        SqliteConnection connection,
        GrimoireSchemaTransitionJournalRow row,
        string? cursor,
        long rowsProcessed)
    {

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(CancellationToken.None);

        bool advanced = await GrimoireSchemaTransitionJournal.AdvanceAsync(
            connection,
            transaction,
            row,
            row.CompletedThroughVersion,
            row.BackfillName,
            cursor,
            rowsProcessed,
            Now,
            CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        return advanced;

    }

    private static async Task<bool> DeleteAsync(
        SqliteConnection connection,
        GrimoireSchemaTransitionJournalRow row)
    {

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(CancellationToken.None);

        bool deleted = await GrimoireSchemaTransitionJournal.DeleteAsync(
            connection,
            transaction,
            row,
            CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        return deleted;

    }

    private static async Task<SqliteConnection> InstallAsync()
    {

        SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            "Data Source=:memory:",
            CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None);

        return connection;

    }

}
