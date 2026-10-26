using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class MemoryEvaluationSchemaEvolutionTests
{
    [Fact]
    public void Published_v18_remains_frozen()
    {
        Assert.Equal(CoreSchemaVersionEighteenFixture.PublishedFingerprint, CoreSchemaVersionEighteenFixture.Fingerprint);

        Assert.Equal(28, GrimoireSchemaVersionChains.CoreSchemaVersion);

        Assert.Equal(CoreSchemaVersionEighteenFixture.PublishedFingerprint, GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).SourceDefinitionFingerprintFor(18));
    }

    [Fact]
    public async Task Evolved_v18_and_fresh_head_have_identical_catalogs()
    {
        using var oldFile = EvolutionScratchDatabase.Create();

        await using var old = await oldFile.OpenAsync(CancellationToken.None);

        Assert.Equal(18, (await GrimoireSchemaTestInstaller.InstallAsync(old, CoreSchemaVersionEighteenFixture.ChainSet(), 64, CancellationToken.None)).Core.SchemaVersion);

        Assert.Equal(28, (await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(old, 64, CancellationToken.None)).Core.SchemaVersion);

        using var newFile = EvolutionScratchDatabase.Create();

        await using var fresh = await newFile.OpenAsync(CancellationToken.None);

        Assert.Equal(28, (await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(fresh, 64, CancellationToken.None)).Core.SchemaVersion);

        Assert.Equal(await Catalog(fresh), await Catalog(old));
    }

    [Fact]
    public async Task ReceiptEvolutionPreservesProtectedHistoryAndAdmitsCleanControlWithoutWeakeningGuards()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using var connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionEighteenFixture.ChainSet(), 64, CancellationToken.None);

        var journal = new CovenantDisclosureTransactionWriter(Guid.NewGuid());

        Guid origin = Guid.NewGuid();

        Guid operation = Guid.NewGuid();

        var provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

        var sensitive = new ProviderCallSensitivity(ContentSensitivity.CovenantDerived, provenance,
            CovenantDigests.Sensitivity(provenance.ToDigestInput(ContentSensitivity.CovenantDerived)));

        CovenantDigest digest = new(new byte[32]);

        var protectedDraft = new CovenantDisclosureDraft(origin, CovenantDisclosureSubjectKind.Operation, operation,
            digest, CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, digest, sensitive.Digest,
            null, digest, null, 1_700_000_000_000);

        Assert.True((await journal.AcknowledgeAsync(connection, protectedDraft, CovenantDisclosureEffectCategory.ProviderDispatch,
            sensitive, CancellationToken.None)).IsSuccess);

        string before = await ReceiptBytes(connection);

        string subjectBefore = await ReceiptBytes(connection, "disclosure_subject_state");

        string foldBefore = await ReceiptBytes(connection, "external_disclosure_state");

        string[] catalogBefore = await Catalog(connection);

        using (CancellationTokenSource cancellation = new())
        {
            connection.CreateFunction("cancel_receipt_upgrade", () => { cancellation.Cancel(); return 0L; });

            var core = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

            var step = core.Steps.Single(value => value.ToVersion == 19);

            var interrupted = step with
            {
                Statements = [.. step.Statements.Where(value => value.Ordinal < 150),
                new("Transitions.V19.150_cancel", 150, "cancel", "SELECT cancel_receipt_upgrade();"),
                .. step.Statements.Where(value => value.Ordinal > 150)]
            };

            var chain = new GrimoireSchemaVersionChainSet([new(core.HeadManifest, core.HeadObjects,
                [.. core.Steps.Where(value => value.ToVersion < 19), interrupted, .. core.Steps.Where(value => value.ToVersion > 19)]),
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator)]);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GrimoireSchemaTestInstaller.InstallAsync(connection, chain, 64, cancellation.Token));
        }

        Assert.Equal(before, await ReceiptBytes(connection));

        Assert.Equal(catalogBefore, await Catalog(connection));

        await GrimoireSchemaTestInstaller.InstallAsync(connection, GrimoireSchemaVersionChains.Default, 64, CancellationToken.None);

        Assert.Equal(before, await ReceiptBytes(connection));

        Assert.Equal(subjectBefore, await ReceiptBytes(connection, "disclosure_subject_state"));

        Assert.Equal(foldBefore, await ReceiptBytes(connection, "external_disclosure_state"));

        var empty = GenerationProvenance.CreateExact([]);

        var clean = new ProviderCallSensitivity(ContentSensitivity.None, empty, CovenantDigests.Sensitivity(empty.ToDigestInput(ContentSensitivity.None)));

        var cleanDraft = new CovenantDisclosureDraft(origin, CovenantDisclosureSubjectKind.Operation, operation,
            new(Enumerable.Repeat((byte)1, 32).ToArray()), CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable,
            digest, clean.Digest, null, digest, null, 1_700_000_000_001);

        var cleanReceipt = await journal.AcknowledgeAsync(connection, cleanDraft, CovenantDisclosureEffectCategory.ProviderDispatch, clean, CancellationToken.None);

        Assert.True(cleanReceipt.IsSuccess);

        var replay = await journal.AcknowledgeAsync(connection, cleanDraft, CovenantDisclosureEffectCategory.ProviderDispatch, clean, CancellationToken.None);

        Assert.True(replay.IsSuccess);

        Assert.Equal(cleanReceipt.Value.Digest, replay.Value.Digest);

        Assert.Equal(2ul, replay.Value.AllocatedSubjectOrdinal);

        Assert.Throws<ArgumentException>(() => new ProviderCallSensitivity(ContentSensitivity.CovenantDerived, empty,
            CovenantDigests.Sensitivity(empty.ToDigestInput(ContentSensitivity.CovenantDerived))));

        Assert.Throws<ArgumentException>(() => new ProviderCallSensitivity(ContentSensitivity.None, provenance,
            CovenantDigests.Sensitivity(provenance.ToDigestInput(ContentSensitivity.None))));

        await using var command = connection.CreateCommand();

        command.CommandText = "UPDATE external_disclosure_receipts SET SensitivityCode = 1 WHERE SensitivityCode = 0";

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());

        command.CommandText = "DELETE FROM external_disclosure_receipts";

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());

        command.CommandText = "SELECT count(*) FROM external_disclosure_receipts";

        Assert.Equal(2L, await command.ExecuteScalarAsync());

        // Read the actual provider bucket by its closed code, independently of any stored receipt payload.
        command.CommandText = "SELECT sum(JoinedCount) FROM external_disclosure_state";

        Assert.Equal(2L, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT ProviderAttemptCount FROM disclosure_subject_state";

        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReceiptBytes(SqliteConnection connection, string table = "external_disclosure_receipts")
    {
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT * FROM " + table;

        await using var reader = await command.ExecuteReaderAsync();

        List<string> values = [];

        while (await reader.ReadAsync())
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                values.Add(reader.GetValue(i) is byte[] bytes ? Convert.ToHexString(bytes) : reader.GetValue(i).ToString()!);
            }
        }

        return string.Join("|", values);
    }

    private static async Task<string[]> Catalog(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT type || ':' || name || ':' || sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type,name";

        await using var reader = await command.ExecuteReaderAsync();

        List<string> values = [];

        while (await reader.ReadAsync()) values.Add(GrimoireSqlNormalizer.Normalize(reader.GetString(0)));

        return values.ToArray();
    }
}
