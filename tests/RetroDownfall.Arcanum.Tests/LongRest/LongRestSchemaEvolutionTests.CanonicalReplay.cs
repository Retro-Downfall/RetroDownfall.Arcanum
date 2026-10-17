using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSchemaEvolutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Published_v17_canonical_receipts_replay_after_evolution_without_changing_any_evidence_field(bool pinned)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionSeventeenFixture.ChainSet(), 64, CancellationToken.None);

        await MemoryAsync(connection, "source", contentHash: AnnalContentDigest.ForSagaMemory("source"));

        await MemoryAsync(connection, "survivor", contentHash: AnnalContentDigest.ForSagaMemory("survivor"));

        if (pinned)
        {
            await ExecuteAsync(connection, "UPDATE saga_memories SET PinnedAtUtc = $at WHERE Id = 'source';", ("$at", At));
        }

        await using ArcanumDbContext context = SagaMemoryMidUpgradeWriteTests.CreateContext(file);

        SagaLongRestService service = new(context, TimeProvider.System);

        LongRestRequest request = new(LongRestTransformationKind.EquivalentObservations,
            [new("source", "source-v1", Convert.ToHexString(AnnalContentDigest.ForSagaMemory("source"))), new("survivor", "survivor-v1", Convert.ToHexString(AnnalContentDigest.ForSagaMemory("survivor")))]);

        LongRestReceipt receipt = LongRestSweepHarness.Require(await service.ApplyAsync(request, CancellationToken.None));

        Assert.Equal(pinned ? LongRestOutcome.NoChange : LongRestOutcome.Applied, receipt.Outcome);

        string[] before = await EveryEvidenceFieldAsync(connection);

        await InstallHeadAsync(connection);

        Assert.Equal(before, await EveryEvidenceFieldAsync(connection));

        SqliteConnection serviceConnection = (SqliteConnection)context.Database.GetDbConnection();

        long changes = (long)(await ScalarAsync(serviceConnection, "SELECT total_changes();"))!;

        Assert.Equal(receipt.ReceiptId, LongRestSweepHarness.Require(await service.ApplyAsync(request, CancellationToken.None)).ReceiptId);

        Assert.Equal(1, LongRestSweepHarness.Require(await service.GetReceiptAsync(receipt.ReceiptId, CancellationToken.None)).PolicyVersion);

        Assert.Equal(changes, (long)(await ScalarAsync(serviceConnection, "SELECT total_changes();"))!);

        Assert.Equal(before, await EveryEvidenceFieldAsync(connection));
    }

    private static async Task<string[]> EveryEvidenceFieldAsync(SqliteConnection connection)
    {
        List<string> rows = [];

        foreach (string table in new[] { "long_rest_receipts", "long_rest_receipt_inputs", "long_rest_suppressions", "annal_claims", "annal_versions", "annal_heads", "annal_dependencies" })
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "SELECT * FROM " + table;

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                rows.Add(table + "|" + string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index => reader.GetName(index) + ":" + (reader.IsDBNull(index) ? "NULL" : reader.GetValue(index) is byte[] bytes ? "BLOB:" + Convert.ToHexString(bytes) : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)))));
            }
        }

        return rows.Order(StringComparer.Ordinal).ToArray();
    }
}
