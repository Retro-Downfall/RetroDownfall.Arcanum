using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildPortalStoreTests
{
    [Theory]
    [InlineData("guild_portal_streams", "ProducerKey")]
    [InlineData("guild_envelopes", "RootQuestId")]
    [InlineData("guild_envelopes", "SessionId")]
    [InlineData("guild_envelopes", "CampaignId")]
    [InlineData("guild_envelopes", "ContentId")]
    [InlineData("guild_envelopes", "CreatedAtUtc")]
    public async Task Read_corrupt_Portal_envelope_columns_refuses_before_large_values_are_materialized(string table, string column)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        var published = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id, Progress(), default);

        Assert.True(published.IsSuccess, published.Error.Message);

        await CorruptPortalMetadataAsync(f, table, column, false);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

        long before = GC.GetAllocatedBytesForCurrentThread();

        Task<Result<GuildEnvelope>> pending = GuildPortalStore.ReadEnvelopeWithinAsync(connection, transaction, published.Value.MessageId, default);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(pending.IsCompleted, "The allocation probe requires an inline SQLite fixture read.");

        var read = await pending;

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, read.Error.Code);

        Assert.True(allocated < 4 * 1024 * 1024, $"Corrupt Portal metadata caused {allocated:N0} managed bytes to be materialized.");
    }

    [Theory]
    [InlineData("SessionId", false)]
    [InlineData("CampaignId", false)]
    [InlineData("Digest", true)]
    [InlineData("MimeType", false)]
    public async Task Read_corrupt_Portal_body_columns_refuses_before_large_values_are_materialized(string column, bool blob)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        var published = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id, Progress(), default);

        Assert.True(published.IsSuccess, published.Error.Message);

        await CorruptPortalMetadataAsync(f, "guild_content", column, blob);

        var read = await f.Authority.InQuestTransactionAsync<byte[]>(root.Quest.Id, async (connection, transaction, signets, ct) =>
        {
            var envelope = await GuildPortalStore.ReadEnvelopeWithinAsync(connection, transaction, published.Value.MessageId, ct);

            Assert.True(envelope.IsSuccess, envelope.Error.Message);

            long before = GC.GetAllocatedBytesForCurrentThread();

            Task<Result<byte[]>> pending = f.Portal.ReadBodyWithinAsync(connection, transaction, signets, f.Caller, envelope.Value, ct);

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(pending.IsCompleted, "The allocation probe requires an inline SQLite fixture read.");

            var result = await pending;

            Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, result.Error.Code);

            Assert.True(allocated < 4 * 1024 * 1024, $"Corrupt Portal body metadata caused {allocated:N0} managed bytes to be materialized.");

            return result;
        }, default);

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, read.Error.Code);
    }

    private static async Task CorruptPortalMetadataAsync(Fixture f, string table, string column, bool blob)
    {
        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name=$name";

        string triggerName = table + "_guard_update";

        command.Parameters.AddWithValue("$name", triggerName);

        string trigger = Assert.IsType<string>(await command.ExecuteScalarAsync());

        command.Parameters.Clear();

        command.CommandText = "PRAGMA ignore_check_constraints=ON; DROP TRIGGER " + triggerName;

        _ = await command.ExecuteNonQueryAsync();

        command.CommandText = "UPDATE " + table + " SET " + column + "=" + (blob ? "zeroblob(8388608)" : "CAST(zeroblob(8388608) AS TEXT)")
            + (table == "guild_content" ? " WHERE ContentKindCode=3" : string.Empty);

        _ = await command.ExecuteNonQueryAsync();

        command.CommandText = trigger;

        _ = await command.ExecuteNonQueryAsync();

        command.CommandText = "PRAGMA ignore_check_constraints=OFF";

        _ = await command.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }
}
