using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Refresh_owner_proof_accepts_live_distinct_owner_lineage_then_refuses_deleted_ancestor()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored parent = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(parent);

        GuildQuestStored child = await f.ChildAsync(parent, driver);

        Assert.NotEqual(parent.Quest.SessionId, child.Quest.SessionId);

        await using SqliteConnection connection = await f.OpenAsync();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Result live = await GuildSpendRefreshOwnerProof.RequireWithinAsync(connection, transaction,
                child.Quest.Id, parent.Quest.Id, default);

            Assert.True(live.IsSuccess, live.IsFailure ? live.Error.Message : null);

            await transaction.CommitAsync();
        }

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention))
        await using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM Sessions WHERE Id=$id";

            delete.Parameters.AddWithValue("$id", parent.Quest.SessionId!.Value.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await delete.ExecuteNonQueryAsync());
        }

        await using SqliteTransaction after = connection.BeginTransaction(deferred: false);

        Result erased = await GuildSpendRefreshOwnerProof.RequireWithinAsync(connection, after,
            child.Quest.Id, parent.Quest.Id, default);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, erased.Error.Code);
    }

    [Fact]
    public async Task Refresh_owner_proof_allows_session_only_root_and_ordinary_revocation()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        Assert.NotNull(root.Quest.SessionId);

        Assert.Null(root.Quest.CampaignId);

        Assert.Null(root.Quest.WorkspaceId);

        await f.ExecuteAsync("UPDATE guild_quests SET CancellationRequested=1,Revision=Revision+1; UPDATE guild_signets SET Revoked=1,Revision=Revision+1,RevocationGeneration=RevocationGeneration+1;");

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Result allowed = await GuildSpendRefreshOwnerProof.RequireWithinAsync(connection, transaction, root.Quest.Id, root.Quest.Id, default);

        Assert.True(allowed.IsSuccess, allowed.IsFailure ? allowed.Error.Message : null);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("missing")]
    [InlineData("wrongRoot")]
    [InlineData("workspace")]
    public async Task Refresh_owner_proof_refuses_inconsistent_lineage(string corruption)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        Guid unrelated = Guid.NewGuid();

        string value = unrelated.ToString("D").ToUpperInvariant();

        string id = root.Quest.Id.ToString("D").ToUpperInvariant();

        string mutation = corruption switch
        {
            "cycle" => $"ParentQuestId='{id}'",
            "missing" => $"ParentQuestId='{value}'",
            "wrongRoot" => $"RootQuestId='{value}'",
            _ => $"WorkspaceId='{value}'",
        };

        // Deliberately bypass immutable identity only in this scratch database to model corruption.
        await f.ExecuteAsync($"DROP TRIGGER guild_quests_guard_update; UPDATE guild_quests SET {mutation} WHERE Id='{id}';");

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Result refused = await GuildSpendRefreshOwnerProof.RequireWithinAsync(connection, transaction, root.Quest.Id, root.Quest.Id, default);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, refused.Error.Code);
    }
}
