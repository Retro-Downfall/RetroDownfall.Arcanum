using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Remote_execution_binding_freezes_options_and_replay_cannot_replace_destination()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        GuildRemoteTransportDescriptor target = new("peer-a", "https://remote.example/agent", "1.0", ["JSONRPC"], [], null, null, null);

        GuildRemoteExecutionInput input = new(1, "https://remote.example/", target,
            RetroDownfall.Arcanum.Infrastructure.A2A.A2ADispatchMode.Continuable, ["text/plain"], null, []);

        var created = await GuildRemoteExecutionStore.CreateWithinAsync(connection, transaction, row, input, null,
            DateTimeOffset.UtcNow, default);

        Assert.True(created.IsSuccess, created.Error.Message);

        var replay = await GuildRemoteExecutionStore.CreateWithinAsync(connection, transaction, row, input, null,
            DateTimeOffset.UtcNow, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(created.Value, replay.Value);

        var changed = await GuildRemoteExecutionStore.CreateWithinAsync(connection, transaction, row,
            input with { Transport = target with { Endpoint = "https://other.example/agent" } }, null, DateTimeOffset.UtcNow, default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, changed.Error.Code);

        var read = await GuildRemoteExecutionStore.ReadInputWithinAsync(connection, transaction, row.Quest.Id, default);

        Assert.True(read.IsSuccess, read.Error.Message);

        Assert.Equal(input.Transport.Endpoint, read.Value.Transport.Endpoint);

        Assert.Equal(input.Transport.Interfaces.ToArray(), read.Value.Transport.Interfaces.ToArray());

        Assert.Equal(input.Mode, read.Value.Mode);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Remote_execution_binding_refuses_unsupported_finite_grants(bool usd, bool tokens)
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Host.Grant = f.Host.Grant with { Budget = new(new(usd ? GuildBudgetMode.Finite : GuildBudgetMode.Unlimited, usd ? 1m : null),
            new(tokens ? GuildBudgetMode.Finite : GuildBudgetMode.Unlimited, tokens ? 1L : null)) };

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request with { Grant = f.Host.Grant }, default)).Value;

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        GuildRemoteExecutionInput input = new(1, "https://remote.example/", new("peer-a", "https://remote.example/agent", "1.0",
            ["JSONRPC"], [], null, null, null), RetroDownfall.Arcanum.Infrastructure.A2A.A2ADispatchMode.Blocking, ["text/plain"], null, []);

        var created = await GuildRemoteExecutionStore.CreateWithinAsync(connection, transaction, row, input, null,
            DateTimeOffset.UtcNow, default);

        Assert.Equal(ErrorCodes.Guild.BudgetCapabilityUnavailable, created.Error.Code);
    }
    [Fact]
    public async Task Remote_recovery_route_survives_input_erasure_without_reconstructing_its_goal()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        GuildRemoteExecutionInput input = new(1, "https://remote.example/", new("peer-a", "https://remote.example/agent", "1.0",
            ["JSONRPC"], [], "ENV_REFERENCE", "https://provider.example/", "https://docs.example/"),
            RetroDownfall.Arcanum.Infrastructure.A2A.A2ADispatchMode.Blocking, ["text/plain"], null, []);

        var bound = await GuildRemoteExecutionStore.CreateWithinAsync(connection, transaction, row, input, null,
            DateTimeOffset.UtcNow, default);

        Assert.True(bound.IsSuccess, bound.Error.Message);

        await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection, transaction, bound.Value.Input.ContentId.ToString("N"), default);

        await using SqliteCommand erase = connection.CreateCommand();

        erase.Transaction = transaction;

        erase.CommandText = "DELETE FROM guild_content WHERE ContentId=$id";

        erase.Parameters.AddWithValue("$id", bound.Value.Input.ContentId.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await erase.ExecuteNonQueryAsync());

        var erased = await GuildRemoteExecutionStore.ReadInputWithinAsync(connection, transaction, row.Quest.Id, default);

        Assert.Equal(ErrorCodes.Guild.ContentErased, erased.Error.Code);

        var retained = await GuildRemoteExecutionStore.ReadRecoveryWithinAsync(connection, transaction, row.Quest.Id, default);

        Assert.True(retained.IsSuccess, retained.Error.Message);

        Assert.Equal(input.DiscoveryEndpoint, retained.Value.DiscoveryEndpoint);

        Assert.Equal(input.Transport.Endpoint, retained.Value.Transport.Endpoint);

        Assert.Null(retained.Value.Transport.CredentialReference);

        Assert.Null(retained.Value.Transport.ProviderLink);

        Assert.Null(retained.Value.Transport.DocumentationLink);

        erase.Parameters.Clear();

        erase.CommandText = "UPDATE guild_remote_executions SET RecoveryDiscoveryEndpoint='https://other.example/' WHERE QuestId=$quest";

        erase.Parameters.AddWithValue("$quest", row.Quest.Id.ToString("D").ToUpperInvariant());

        await Assert.ThrowsAsync<SqliteException>(() => erase.ExecuteNonQueryAsync());

        erase.Parameters.Clear();

        erase.CommandText = "SELECT COUNT(*) FROM guild_content_erasure_receipts WHERE ContentId=$id AND (" + GuildQuestRetentionPins.ContentReferences + ")";

        erase.Parameters.AddWithValue("$id", bound.Value.Input.ContentId.ToString("D").ToUpperInvariant());

        Assert.Equal(1L, await erase.ExecuteScalarAsync());
    }
}
