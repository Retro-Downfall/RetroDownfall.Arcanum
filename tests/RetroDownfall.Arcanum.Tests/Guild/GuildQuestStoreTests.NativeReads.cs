using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Native_result_read_keeps_erasure_metadata_without_materializing_the_body()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        var (_, child, receipt) = await CompletedReceiptChildAsync(fixture);

        var before = await fixture.Store.ReadLocalResultAsync(fixture.Caller, child.Quest.Id, true, default);

        Assert.True(before.IsSuccess, before.Error.Message);

        Assert.Equal("Immutable child contribution", before.Value.Body);

        await using (SqliteConnection connection = await fixture.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction();

            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = """
                INSERT INTO guild_content_erasure_receipts
                    (ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,ByteLength,ErasedAtUtc)
                SELECT ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,length(Body),'2026-11-08T00:00:00.0000000Z'
                FROM guild_content WHERE ContentId=$content
                """;

            command.Parameters.AddWithValue("$content", receipt.Content!.ContentId.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await command.ExecuteNonQueryAsync());

            command.CommandText = "DELETE FROM guild_content WHERE ContentId=$content";

            Assert.Equal(1, await command.ExecuteNonQueryAsync());

            await transaction.CommitAsync();
        }

        var metadata = await fixture.Store.ReadLocalResultAsync(fixture.Caller, child.Quest.Id, false, default);

        Assert.True(metadata.IsSuccess, metadata.Error.Message);

        Assert.True(metadata.Value.Receipt!.Content!.ContentErased);

        Assert.Null(metadata.Value.Body);

        var withheld = await fixture.Store.ReadLocalResultAsync(fixture.Caller, child.Quest.Id, true, default);

        Assert.Equal(ErrorCodes.Guild.ContentErased, withheld.Error.Code);
    }
}
