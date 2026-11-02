using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupRestoreStagedDrainTests
{
    [Fact]
    public async Task A_Core34_archive_retires_all_plain_vector_mirrors_before_erasure_reconciliation_and_preserves_the_embedding_bytes()
    {
        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        byte[] vector = [0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 0, 0];

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "core34-plain-vector-mirrors", CoreSchemaVersionThirtyFourFixture.ChainSet(),
            async connection =>
            {
                await using SqliteCommand command = connection.CreateCommand();

                command.CommandText = """
                    INSERT INTO Sessions(Id,Status,CreatedAt,UpdatedAt)
                    VALUES('11111111-1111-4111-8111-111111111111','active','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
                    INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
                    VALUES('22222222-2222-4222-8222-222222222222','11111111-1111-4111-8111-111111111111',0,'unrelated preserved embedding source','model','2026-01-01T00:00:00.0000000Z',1);
                    INSERT INTO entry_embeddings(EntryId,Embedding,Dim)
                    VALUES('22222222-2222-4222-8222-222222222222',$vector,3);
                    """;

                _ = command.Parameters.AddWithValue("$vector", vector);

                _ = await command.ExecuteNonQueryAsync(Token);

                foreach ((string table, string key) in new[]
                {
                    ("entry_embeddings_vec", "EntryId"),
                    ("workspace_file_embeddings_vec", "ChunkId"),
                    ("session_attachment_embeddings_vec", "ChunkId"),
                    ("saga_memory_embeddings_vec", "MemoryId"),
                    ("tapestry_node_embeddings_vec", "NodeId"),
                })
                {
                    command.CommandText = $"CREATE TABLE {table}({key} TEXT PRIMARY KEY, Embedding BLOB NOT NULL); INSERT INTO {table} VALUES('22222222-2222-4222-8222-222222222222',$vector);";

                    _ = await command.ExecuteNonQueryAsync(Token);
                }
            });

        bool reconciled = false;

        BackupRestoreService service = harness.CreateRestoreService(options: new BackupRestoreServiceOptions
        {
            EmbeddingDimensions = 3,
            AfterErasurePurgeForTests = async (connection, transaction, cancellationToken) =>
            {
                await using SqliteCommand command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('entry_embeddings_vec','workspace_file_embeddings_vec','session_attachment_embeddings_vec','saga_memory_embeddings_vec','tapestry_node_embeddings_vec');";

                Assert.Equal(0L, await command.ExecuteScalarAsync(cancellationToken));

                command.CommandText = "SELECT Embedding FROM entry_embeddings WHERE EntryId='22222222-2222-4222-8222-222222222222';";

                Assert.Equal(vector, Assert.IsType<byte[]>(await command.ExecuteScalarAsync(cancellationToken)));

                reconciled = true;
            },
        });

        BackupRestoreResult result = await RestoreAsync(service, archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.True(reconciled);

        Assert.Equal(0, result.Reconciliation?.EmbeddingsToRebuild);

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(archive.GrimoireSecret);

        Assert.Equal(35, await GrimoireCoreSchemaVersion.ReadAsync(restored, Token));

        await using SqliteCommand read = restored.CreateCommand();

        read.CommandText = "SELECT Embedding FROM entry_embeddings WHERE EntryId='22222222-2222-4222-8222-222222222222';";

        Assert.Equal(vector, Assert.IsType<byte[]>(await read.ExecuteScalarAsync(Token)));
    }

    [Fact]
    public async Task An_archived_unsupported_vec0_mirror_refuses_restore_without_replacing_the_live_installation()
    {
        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        string before = await harness.LiveDatabaseDigestAsync();

        string sentinel = Path.Combine(harness.InstallationRoot, "retirement-sentinel.txt");

        await File.WriteAllTextAsync(sentinel, "preserve this live installation");

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "core34-unsupported-vector-mirror", CoreSchemaVersionThirtyFourFixture.ChainSet(),
            async connection =>
            {
                await using SqliteCommand command = connection.CreateCommand();

                command.CommandText = """
                    PRAGMA writable_schema=ON;
                    INSERT INTO sqlite_schema(type,name,tbl_name,rootpage,sql)
                    VALUES('table','entry_embeddings_vec','entry_embeddings_vec',0,'CREATE VIRTUAL TABLE entry_embeddings_vec USING vec0(EntryId TEXT PRIMARY KEY, Embedding FLOAT[1536])');
                    PRAGMA writable_schema=RESET;
                    """;

                _ = await command.ExecuteNonQueryAsync(Token);
            });

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Contains(result.Issues, static issue => issue.Code == Unjoinable);

        Assert.Equal(before, await harness.LiveDatabaseDigestAsync());

        Assert.Equal("preserve this live installation", await File.ReadAllTextAsync(sentinel));

        Assert.DoesNotContain(result.Phases, static phase => phase.Phase == BackupRestorePhase.Commit);
    }
}
