using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.DataLifecycle;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Actual_retention_owners_remove_only_their_literal_projection_and_secure_FTS_tokens(int owner)
    {
        RequireSqlCipher();
        (Guid sessionId, Guid entryId) = await SeedSessionAsync(pinned: false);
        SeededAttachment attachment = await SeedAttachmentAsync(sessionId, entryId);
        await ExecuteAsync("UPDATE Entries SET Content='vxq'; UPDATE Sessions SET Summary='jzx'; UPDATE session_attachment_chunks SET Content='qzv';");
        await ExecuteAsync("""
            INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
            VALUES('literal-workspace','/fake/workspace','file.cs',0,'zqb',0,3,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
            """);
        Assert.Equal(4, await CountAllAsync("memory_search_documents"));
        Assert.True(await LiteralTokenBlocksAsync("qzv") > 0);
        var service = CreateService();
        DataRetentionRequest request = owner switch
        {
            0 => new(DataRetentionOperation.DeleteSession, sessionId),
            1 => new(DataRetentionOperation.DeleteAttachment, attachment.AttachmentId),
            2 => new(DataRetentionOperation.ResetMemory, MemoryScope: MemoryResetScope.Attachments),
            3 => new(DataRetentionOperation.ResetMemory, MemoryScope: MemoryResetScope.Workspace),
            _ => new(DataRetentionOperation.FactoryReset),
        };
        if (owner == 4)
        {
            var recovered = await ReconcileFactoryResetV0Async(service, "literal-search-factory-owner");
            Assert.Equal(1, recovered.Summary.Completed);
            Assert.Equal(0, recovered.Summary.RequiresAttention);
        }
        else
        {
            DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);
            var result = await service.ApplyAsync(new(request, plan.PlanId), CancellationToken.None);
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        }
        Assert.Equal(owner is 0 or 4 ? 0 : 1, await CountAsync("memory_search_documents", "Corpus", "0"));
        Assert.Equal(owner is 0 or 4 ? 0 : 1, await CountAsync("memory_search_documents", "Corpus", "1"));
        Assert.Equal(owner is 0 or 1 or 2 or 4 ? 0 : 1, await CountAsync("memory_search_documents", "Corpus", "2"));
        Assert.Equal(owner is 3 or 4 ? 0 : 1, await CountAsync("memory_search_documents", "Corpus", "3"));
        if (owner is 0 or 1 or 2 or 4) Assert.Equal(0L, await LiteralTokenBlocksAsync("qzv"));
        await ExecuteAsync("INSERT INTO memory_search_fts(memory_search_fts,rank) VALUES('integrity-check',1)");
    }

    private async Task<long> LiteralTokenBlocksAsync(string token)
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM memory_search_fts_data WHERE instr(block,@token)>0";
        _ = command.Parameters.AddWithValue("@token", Encoding.UTF8.GetBytes(token));
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
