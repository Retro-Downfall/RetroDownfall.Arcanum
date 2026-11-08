using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(AiProviderKind.OpenAICompatible, true)]
    [InlineData(AiProviderKind.CodexCli, false)]
    [InlineData(AiProviderKind.ClaudeCodeCli, false)]
    public async Task Local_options_freeze_the_resolved_model_and_refuse_opaque_provider_admission(AiProviderKind kind, bool supported)
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Providers = [new() { Name = "configured", Type = kind,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "Canonical-Model" }] }];

        f.Settings.CurrentValue.DefaultModel = "canonical-model";

        GuildLocalExecutionAdmission options = new(f.Settings);

        GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(), TimeProvider.System, options);

        GuildCallerContext caller = await f.OperatorAsync();

        GuildIssuanceIdentity identity = new(f.Generation, null, caller.InvocationId, caller.InvocationId, f.Request.IdempotencyKey);

        var issued = await store.IssueAsync(caller, identity, f.Request, default);

        Assert.Equal(supported, issued.IsSuccess);

        if (!supported)
        {
            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM Apprentices"));

            return;
        }

        GuildLocalExecutionInput original = await ReadInputAsync();

        Assert.Equal("configured", original.Provider);

        Assert.Equal("Canonical-Model", original.Model);

        Assert.Equal(GuildLocalExecutionMode.Apprentice, original.Mode);

        f.Settings.CurrentValue.DefaultModel = "replacement";

        f.Settings.CurrentValue.Providers = [new() { Name = "new-provider", Type = kind, Models = [new() { Name = "replacement" }] }];

        var replay = await store.IssueAsync(caller, identity, f.Request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(issued.Value.Quest.Id, replay.Value.Quest.Id);

        Assert.Equal(original, await ReadInputAsync());

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM Apprentices"));

        async Task<GuildLocalExecutionInput> ReadInputAsync()
        {
            var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

            Assert.True(opened.IsSuccess, opened.Error.Message);

            await using var connection = opened.Value;

            await using SqliteTransaction transaction = connection.Connection.BeginTransaction();

            var input = await GuildLocalExecutionStore.ReadInputWithinAsync(connection.Connection, transaction, issued.Value.Quest.Id, null, default);

            Assert.True(input.IsSuccess, input.Error.Message);

            return input.Value;
        }
    }
}
