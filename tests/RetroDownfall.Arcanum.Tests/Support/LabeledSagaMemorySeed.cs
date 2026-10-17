using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Covenant;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A Saga memory that carries a live Covenant sensitivity label, written into the Grimoire at
/// <see cref="ArcanumPaths.GrimoireDatabaseFile"/>, and the row count that proves it is still there.
/// </summary>
/// <remarks>
/// The label is written through the ledger the serving host writes labels with, so the row a test
/// protects is shaped by production rather than by the test's idea of a label. The pair is shared by
/// the stopped-host reset cases that need an installation the Covenant has something to say about.
/// </remarks>
internal static class LabeledSagaMemorySeed
{
    public static async Task SeedAsync(GrimoireFixture fixture, Guid memoryId)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using ArcanumDbContext context = fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile);

        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using (SqliteCommand memory = connection.CreateCommand())
        {
            memory.CommandText = """
                INSERT INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode)
                VALUES ($id, 'labelled saga fact', $created, 1);
                """;

            _ = memory.Parameters.AddWithValue("$id", memoryId.ToString("D").ToUpperInvariant());

            _ = memory.Parameters.AddWithValue("$created", "2026-01-01T00:00:00.0000000Z");

            _ = await memory.ExecuteNonQueryAsync(CancellationToken.None);
        }

        CovenantConnectionSource connections = new(
            context,
            FixtureOrdinaryConnectionFactory.For(context));

        try
        {
            ArtifactSensitivityLedger ledger = new(connections);

            Result<LabeledArtifactWriteReceipt> receipt = await ledger.LabelAsync(
                new DerivedArtifactWrite(
                    SensitiveArtifactKind.Saga,
                    memoryId,
                    null,
                    null,
                    null,
                    1,
                    CovenantTask6Fixture.D(11),
                    ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([Guid.Parse("5E6F7081-92A3-4B5C-8D9E-0F1A2B3C4D5E")])),
                CancellationToken.None);

            Assert.True(receipt.IsSuccess, receipt.IsFailure ? receipt.Error.Message : string.Empty);
        }
        finally
        {
            connections.Dispose();
        }
    }

    public static async Task<int> CountRowsAsync(
        GrimoireFixture fixture,
        string table,
        string column,
        Guid id)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using ArcanumDbContext context = fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile);

        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = $id;";

        _ = command.Parameters.AddWithValue("$id", id.ToString("D").ToUpperInvariant());

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            global::System.Globalization.CultureInfo.InvariantCulture);
    }
}
