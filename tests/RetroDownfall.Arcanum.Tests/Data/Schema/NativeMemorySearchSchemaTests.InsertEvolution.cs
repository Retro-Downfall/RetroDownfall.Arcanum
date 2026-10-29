using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed partial class NativeMemorySearchSchemaTests
{
    [Fact]
    public void Core31_is_the_complete_frozen_predecessor_to_indexed_projection_writes()
    {
        _projectionWorkOutput.WriteLine("Core32 source fingerprint: " + GrimoireSchemaCatalog.ComputeSourceFingerprint(
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).HeadObjects));

        Assert.Equal(388, CoreSchemaVersionThirtyOneFixture.Objects.Count);

        Assert.Equal(CoreSchemaVersionThirtyOneFixture.PublishedFingerprint, CoreSchemaVersionThirtyOneFixture.Fingerprint);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Core32_fresh_and_upgraded_projection_writes_preserve_overlapping_conflicts_and_rollback(bool evolved, bool recursive)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection,
            evolved ? CoreSchemaVersionThirtyOneFixture.ChainSet() : GrimoireSchemaVersionChains.Default,
            64, CancellationToken.None);

        await ExecuteAsync(connection, $"PRAGMA recursive_triggers={(recursive ? 1 : 0)};" + SessionRows()
            + EntrySql(EntryOne, 1, "firsttoken") + EntrySql(EntryTwo, 2, "secondtoken") + $"""
            INSERT INTO SessionAttachments(Id,SessionId,State,LogicalKey,OriginalFileName,Version,RelativePath,ContentSha256,MimeType,ByteLength,Kind,CreatedAt)
            VALUES('{AttachmentOne}','{SessionOne}','Bound','notes','notes.txt',1,'notes.txt','hash','text/plain',6,'Text','2026-01-01T00:00:00.0000000Z');
            """ + AttachmentSql("first", "alphatoken")
            + AttachmentSql("second", "betatoken").Replace("'hash',0,", "'hash',1,", StringComparison.Ordinal));

        if (evolved)
        {
            await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(connection, 64, CancellationToken.None);
        }

        Assert.Equal(1L, await CountAsync(connection, "fir"));

        Assert.Equal(1L, await CountAsync(connection, "alp"));

        await ExecuteAsync(connection, EntrySql(EntryOne, 2, "ignoredtoken").Replace("INSERT INTO", "INSERT OR IGNORE INTO", StringComparison.Ordinal)
            + AttachmentSql("first", "ignoredtoken").Replace("'hash',0,", "'hash',1,", StringComparison.Ordinal)
                .Replace("INSERT INTO", "INSERT OR IGNORE INTO", StringComparison.Ordinal));

        Assert.Equal(0L, await CountAsync(connection, "ign"));

        await ExecuteAsync(connection, EntrySql(EntryOne, 2, "replacementtoken").Replace("INSERT INTO", "INSERT OR REPLACE INTO", StringComparison.Ordinal)
            + AttachmentSql("first", "replacementtoken").Replace("'hash',0,", "'hash',1,", StringComparison.Ordinal)
                .Replace("INSERT INTO", "INSERT OR REPLACE INTO", StringComparison.Ordinal));

        Assert.Equal(0L, await CountAsync(connection, "fir"));

        Assert.Equal(0L, await CountAsync(connection, "sec"));

        Assert.Equal(0L, await CountAsync(connection, "alp"));

        Assert.Equal(0L, await CountAsync(connection, "bet"));

        Assert.Equal(2L, await CountAsync(connection, "rep"));

        await ExecuteAsync(connection, $"UPDATE Entries SET Content='changedtoken' WHERE Id='{EntryOne}';"
            + "UPDATE session_attachment_chunks SET Content='changedtoken' WHERE ChunkId='first';");

        Assert.Equal(0L, await CountAsync(connection, "rep"));

        Assert.Equal(2L, await CountAsync(connection, "cha"));

        await ExecuteAsync(connection, $"BEGIN; UPDATE Entries SET Id='{EntryTwo}',Content='rollbacktoken' WHERE Id='{EntryOne}';"
            + "UPDATE session_attachment_chunks SET ChunkId='second',Content='rollbacktoken' WHERE ChunkId='first'; ROLLBACK;");

        Assert.Equal(0L, await CountAsync(connection, "rol"));

        Assert.Equal(2L, await CountAsync(connection, "cha"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            $"UPDATE Entries SET Content=NULL WHERE Id='{EntryOne}';"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE session_attachment_chunks SET Content=NULL WHERE ChunkId='first';"));

        Assert.Equal(2L, await CountAsync(connection, "cha"));

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM memory_search_documents WHERE Corpus IN (0,2)"));

        await ExecuteAsync(connection, "INSERT INTO memory_search_fts(memory_search_fts,rank) VALUES('integrity-check',1)");

        using var freshFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection fresh = await freshFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(fresh, 64, CancellationToken.None);

        Assert.Equal(await ProjectionCatalogAsync(fresh), await ProjectionCatalogAsync(connection));

        Exception refusal = await Assert.ThrowsAnyAsync<Exception>(() => GrimoireSchemaTestInstaller.InstallAsync(
            connection, CoreSchemaVersionThirtyOneFixture.ChainSet(), 64, CancellationToken.None));

        Assert.Contains("IncompatibleNewerVersion", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Core32_each_DDL_failure_restores_the_complete_Core31_catalog_and_FTS_contents()
    {
        GrimoireSchemaVersionChain core = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        GrimoireSchemaVersionStep step = Assert.Single(core.Steps, static item => item.ToVersion == 32);

        foreach (GrimoireSchemaTransitionStatement stop in step.Statements)
        {
            using var file = EvolutionScratchDatabase.Create();

            await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

            await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionThirtyOneFixture.ChainSet(), 64, CancellationToken.None);

            await ExecuteAsync(connection, SessionRows() + EntrySql(EntryOne, 1, "retainedtoken"));

            string[] before = await ProjectionCatalogAsync(connection);

            GrimoireSchemaVersionStep interrupted = step with
            {
                Statements = [.. step.Statements.SelectMany(statement => statement == stop
                    ? new[] { statement, new GrimoireSchemaTransitionStatement("InjectedFailure", stop.Ordinal + 1,
                        "InjectedFailure", "INSERT INTO MissingProjectionTransitionTable VALUES(1)") }
                    : new[] { statement })],
            };

            GrimoireSchemaVersionChainSet chains = new(
            [
                new(core.HeadManifest, core.HeadObjects, [.. core.Steps.Select(item => item.ToVersion == 32 ? interrupted : item)]),
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            ]);

            await Assert.ThrowsAsync<SqliteException>(() => GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 64, CancellationToken.None));

            Assert.Equal(before, await ProjectionCatalogAsync(connection));

            Assert.Equal(31L, await ScalarAsync(connection, "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode=0 AND TransactionTierCode=0"));

            Assert.Equal(1L, await CountAsync(connection, "ret"));

            await ExecuteAsync(connection, "INSERT INTO memory_search_fts(memory_search_fts,rank) VALUES('integrity-check',1)");
        }
    }

    private static async Task<string[]> ProjectionCatalogAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT type || '|' || name || '|' || sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY type,name;";

        List<string> rows = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));

        return [.. rows];
    }
}
