using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupRestoreStagedDrainTests
{
    [Fact]
    public async Task A_real_pre_DAG_archive_restores_through_staged_schema_convergence_without_losing_its_source()
    {
        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "core-twenty-five-tapestry",
            CoreSchemaVersionTwentyFiveFixture.ChainSet(),
            async connection =>
            {
                await SeedArchivedMemoryAsync(connection);

                await using SqliteCommand command = connection.CreateCommand();

                command.CommandText = """
                    INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
                    VALUES('pre-dag-source','/pre-dag','a.cs',0,'preserved pre-DAG source',0,24,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
                    INSERT INTO tapestry_generations(GenerationId,ScopeKind,ScopeId,Status,AlgorithmVersion,SettingsFingerprint,SummaryRecipeVersion,EmbeddingDimension,CorpusFingerprint,StartedAt)
                    VALUES('pre-dag-generation','Workspace','/pre-dag','Complete','legacy-v1','settings','recipe',64,'corpus','2026-01-01T00:00:00.0000000Z');
                    INSERT INTO tapestry_nodes(NodeId,GenerationId,ScopeKind,ScopeId,Layer,NodeKind,ParentScopeKey,SourceKind,SourceId,SourceLabel,ContentHash,EmbeddingDimension,CreatedAt)
                    VALUES('pre-dag-leaf','pre-dag-generation','Workspace','/pre-dag',0,'Leaf','pre-dag-generation#root','WorkspaceFileChunk','pre-dag-source','a.cs','hash',64,'2026-01-01T00:00:00.0000000Z');
                    """;

                _ = await command.ExecuteNonQueryAsync(Token);

                Assert.False(await BackupRestoreDatabaseWorker.TableExistsAsync(connection, "tapestry_edges", Token));

                Assert.False(await BackupRestoreDatabaseWorker.TableExistsAsync(connection, "tapestry_scope_revisions", Token));
            });

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(archive.GrimoireSecret);

        Assert.Equal(GrimoireSchemaVersionChains.CoreSchemaVersion, await GrimoireCoreSchemaVersion.ReadAsync(restored, Token));

        Assert.True(await BackupRestoreDatabaseWorker.TableExistsAsync(restored, "tapestry_edges", Token));

        Assert.True(await BackupRestoreDatabaseWorker.TableExistsAsync(restored, "tapestry_scope_revisions", Token));

        Assert.Equal(1L, await ScalarAsync(restored, "SELECT COUNT(*) FROM workspace_file_chunks WHERE ChunkId='pre-dag-source' AND Content='preserved pre-DAG source'"));

        Assert.Equal(1L, await ScalarAsync(restored, "SELECT COUNT(*) FROM tapestry_generations WHERE GenerationId='pre-dag-generation'"));

        Assert.Equal(0L, await ScalarAsync(restored, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
    }

    [Fact]
    public async Task A_current_DAG_archive_cannot_activate_summaries_of_a_purged_protected_entry()
    {
        const string session = "AAAAAAAA-1111-4111-8111-AAAAAAAAAAAA";

        const string other = "BBBBBBBB-1111-4111-8111-BBBBBBBBBBBB";

        const string entry = "CCCCCCCC-1111-4111-8111-CCCCCCCCCCCC";

        const string otherEntry = "EEEEEEEE-1111-4111-8111-EEEEEEEEEEEE";

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "current-protected-dag",
            GrimoireSchemaVersionChains.Default,
            async connection =>
            {
                await using SqliteCommand command = connection.CreateCommand();

                command.CommandText = $"""
                    INSERT INTO Sessions(Id,Status,CreatedAt,UpdatedAt) VALUES('{session}','active','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z'),('{other}','active','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
                    INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES('{entry}','{session}',2,'private protected source','model','2026-01-01T00:00:00.0000000Z',1),('{otherEntry}','{other}',1,'unrelated source','','2026-01-01T00:00:00.0000000Z',1);
                    INSERT INTO artifact_sensitivity(LabelId,ArtifactKindCode,ArtifactId,SensitivityCode,ProvenanceModeCode,ExactGenerationIds,SessionId,ArtifactRevision,ArtifactContentDigest,SensitivityDigest,ArtifactLabelDigest,CreatedAtUtc)
                    VALUES('DDDDDDDD-1111-4111-8111-DDDDDDDDDDDD',{(int)SensitiveArtifactKind.AssistantEntry},'{entry}',1,1,X'07070707070707070707070707070707','{session}',1,zeroblob(32),zeroblob(32),zeroblob(32),'2026-01-01T00:00:00.0000000Z');
                    CREATE TABLE tapestry_node_embeddings_vec(NodeId TEXT PRIMARY KEY,Embedding BLOB NOT NULL);
                    INSERT INTO tapestry_leaf_hashes(SourceKind,SourceId,ContentSha256,IsBlank) VALUES('Entry','{entry}','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',0);
                    """;

                _ = await command.ExecuteNonQueryAsync(Token);

                foreach ((string id, string scope, string status) in new[] { ("affected", session, "Complete"), ("building", session, "Building"), ("unaffected", other, "Complete") })
                {
                    command.CommandText = $"""
                        INSERT INTO tapestry_generations(GenerationId,ScopeKind,ScopeId,Status,AlgorithmVersion,SettingsFingerprint,SummaryRecipeVersion,EmbeddingDimension,CorpusFingerprint,StartedAt)
                        VALUES('{id}','Session','{scope}','Building','legacy','settings','recipe',1536,'corpus','2026-01-01T00:00:00.0000000Z');
                        INSERT INTO tapestry_nodes(NodeId,GenerationId,ScopeKind,ScopeId,Layer,NodeKind,ParentScopeKey,SourceLabel,Content,ContentHash,EmbeddingDimension,CreatedAt)
                        VALUES('{id}-a','{id}','Session','{scope}',1,'Summary','{id}#root','summary','private derived summary','hash',1536,'2026-01-01T00:00:00.0000000Z'),
                              ('{id}-b','{id}','Session','{scope}',1,'Summary','{id}#root','summary','private derived summary','hash',1536,'2026-01-01T00:00:00.0000000Z'),
                              ('{id}-leaf','{id}','Session','{scope}',0,'Leaf','{id}#root','source',NULL,'hash',1536,'2026-01-01T00:00:00.0000000Z');
                        INSERT INTO tapestry_edges(GenerationId,ParentNodeId,ChildNodeId,MembershipReason)
                        VALUES('{id}','{id}-a','{id}-leaf','Legacy'),('{id}','{id}-b','{id}-leaf','Legacy');
                        INSERT INTO tapestry_node_embeddings(NodeId,Embedding,Dim) SELECT NodeId,zeroblob(6144),1536 FROM tapestry_nodes WHERE GenerationId='{id}';
                        INSERT INTO tapestry_node_embeddings_vec SELECT NodeId,Embedding FROM tapestry_node_embeddings WHERE NodeId LIKE '{id}-%';
                        UPDATE tapestry_nodes SET SourceKind='Entry',SourceId='{(scope == session ? entry : otherEntry)}' WHERE NodeId='{id}-leaf';
                        UPDATE tapestry_generations SET Status='{status}' WHERE GenerationId='{id}';
                        """;

                    _ = await command.ExecuteNonQueryAsync(Token);
                }
            });

        BackupRestoreResult result = await harness.CreateRestoreService(covenantStaging: true).RestoreAsync(
            new BackupRestoreRequest(archive.ArchivePath, Confirmed: true, CreateSafetyBackup: false,
                ProtectedStateMode: BackupProtectedStateMode.PurgeProtectedState, ProtectedStateConfirmed: true),
            MemoryErasureRestoreHarness.Passphrase.AsMemory(), Token);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.Contains(result.Phases, static phase => phase.Detail.Contains("removed from staging before replacement", StringComparison.Ordinal));

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(archive.GrimoireSecret);

        Assert.Equal(0L, await ScalarAsync(restored, $"SELECT COUNT(*) FROM Entries WHERE Id='{entry}'"));

        Assert.Equal(1L, await ScalarAsync(restored, "SELECT COUNT(*) FROM tapestry_generations WHERE GenerationId='unaffected'"));

        Assert.Equal(1L, await ScalarAsync(restored, "SELECT COUNT(*) FROM tapestry_generations"));

        Assert.Equal(2L, await ScalarAsync(restored, "SELECT COUNT(*) FROM tapestry_edges"));

        Assert.Equal(3L, await ScalarAsync(restored, "SELECT COUNT(*) FROM tapestry_nodes"));

        Assert.Equal(3L, await ScalarAsync(restored, "SELECT COUNT(*) FROM tapestry_node_embeddings"));

        Assert.Equal(3L, await ScalarAsync(restored, "SELECT COUNT(*) FROM tapestry_node_embeddings_vec"));

        Assert.Equal(0L, await ScalarAsync(restored, "SELECT COUNT(*) FROM tapestry_leaf_hashes"));

        Assert.Equal(0L, await ScalarAsync(restored, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
    }
}
