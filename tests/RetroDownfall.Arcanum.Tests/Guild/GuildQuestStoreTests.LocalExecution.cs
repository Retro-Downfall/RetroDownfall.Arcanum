using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Local_binding_replays_exact_skeleton_and_accepted_input_after_restart()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildLocalExecutionInput input = LocalInput(quest) with { Files = [new("selected.txt", quest.Quest.GoalRef)] };

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, quest, input);

        f.Restart();

        Assert.Equal(binding, await CreateLocalAsync(f, quest, input));

        Assert.Equal(1L, await f.CountAsync("Apprentices"));

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        var loaded = await GuildLocalExecutionStore.ReadInputWithinAsync(connection, transaction, quest.Quest.Id, null, default);

        Assert.True(loaded.IsSuccess, loaded.Error.Message);

        Assert.Equal(input.Model, loaded.Value.Model);

        Assert.Equal(input.Provider, loaded.Value.Provider);

        Assert.Equal(GuildLocalExecutionMode.Sterile, loaded.Value.Mode);

        Assert.Equal(512, loaded.Value.MaxOutputTokens);

        Assert.Equal("selected.txt", Assert.Single(loaded.Value.Files).Name);

        Assert.Equal(input.Goal, loaded.Value.Files[0].Content);

        Assert.Equal(quest.Quest.GoalRef, loaded.Value.Goal);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT Goal,Plan,SessionId,CheckpointData FROM Apprentices";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        Assert.DoesNotContain(f.Request.Goal, reader.GetString(0), StringComparison.Ordinal);

        Assert.Equal("[]", reader.GetString(1));

        Assert.Equal(quest.Quest.SessionId, Guid.Parse(reader.GetString(2)));

        Assert.True(ApprenticeRepository.DeserializeCheckpoint(reader.GetString(3))!.LaunchRequested);
    }

    [Fact]
    public async Task Local_binding_changed_input_conflicts_without_replacing_child()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, quest, LocalInput(quest));

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        var conflict = await GuildLocalExecutionStore.CreateWithinAsync(connection, transaction, quest,
            LocalInput(quest) with { Model = "changed" }, null, DateTimeOffset.UtcNow, default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, conflict.Error.Code);

        Assert.Equal(binding, (await GuildLocalExecutionStore.ReadWithinAsync(connection, transaction, quest.Quest.Id, default)).Value);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("session")]
    [InlineData("root")]
    [InlineData("parent")]
    public async Task Local_binding_refuses_forged_canonical_identity(string axis)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestStored forged = quest with { Quest = axis switch
        {
            "run" => quest.Quest with { RunId = Guid.NewGuid() },
            "session" => quest.Quest with { SessionId = Guid.NewGuid() },
            "root" => quest.Quest with { RootQuestId = Guid.NewGuid() },
            _ => quest.Quest with { ParentQuestId = Guid.NewGuid() },
        } };

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        var rejected = await GuildLocalExecutionStore.CreateWithinAsync(connection, transaction, forged,
            LocalInput(quest), null, DateTimeOffset.UtcNow, default);

        Assert.Equal(ErrorCodes.Guild.AuthorityMismatch, rejected.Error.Code);

        transaction.Rollback();

        Assert.Equal(0L, await f.CountAsync("Apprentices"));
    }

    [Fact]
    public async Task Local_binding_derives_parent_apprentice_from_the_accepted_parent()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildLocalExecutionBinding parent = await CreateLocalAsync(f, root, LocalInput(root));

        GuildQuestStored child = await f.ChildAsync(root, await f.DriverAsync(root));

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, child, LocalInput(child));

        Assert.Equal(parent.ApprenticeId, binding.ParentApprenticeId);

        Assert.Equal(root.Quest.Id, binding.RootQuestId);

        Assert.Equal(child.Quest.SessionId, binding.SessionId);

        Assert.NotEqual(parent.SessionId, binding.SessionId);
    }

    [Fact]
    public async Task Binding_insert_failure_rolls_back_skeleton_and_execution_input()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        await f.ExecuteAsync("CREATE TRIGGER fixture_local_insert_failure BEFORE INSERT ON guild_local_executions BEGIN SELECT RAISE(ABORT,'fixture binding failure'); END;");

        long before = await f.CountAsync("guild_content");

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction();

            await Assert.ThrowsAsync<SqliteException>(() => GuildLocalExecutionStore.CreateWithinAsync(connection, transaction,
                quest, LocalInput(quest), null, DateTimeOffset.UtcNow, default));

            transaction.Rollback();
        }

        Assert.Equal(0L, await f.CountAsync("Apprentices"));

        Assert.Equal(0L, await f.CountAsync("guild_local_executions"));

        Assert.Equal(before, await f.CountAsync("guild_content"));
    }

    [Fact]
    public async Task Erased_execution_input_retains_binding_and_cannot_be_recreated_or_read()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, quest, LocalInput(quest));

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection, transaction, binding.Input.ContentId.ToString("N"), default);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "DELETE FROM guild_content WHERE ContentId=$id";

        command.Parameters.AddWithValue("$id", binding.Input.ContentId.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        var retained = await GuildLocalExecutionStore.ReadWithinAsync(connection, transaction, quest.Quest.Id, default);

        Assert.True(retained.IsSuccess, retained.Error.Message);

        Assert.Equal(binding.ApprenticeId, retained.Value.ApprenticeId);

        Assert.True(retained.Value.Input.ContentErased);

        var input = await GuildLocalExecutionStore.ReadInputWithinAsync(connection, transaction, quest.Quest.Id, null, default);

        Assert.Equal(ErrorCodes.Guild.ContentErased, input.Error.Code);

        var replay = await GuildLocalExecutionStore.CreateWithinAsync(connection, transaction, quest, LocalInput(quest), null, DateTimeOffset.UtcNow, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.True(replay.Value.Input.ContentErased);
    }

    [Fact]
    public async Task Missing_apprentice_never_mints_a_replacement_on_replay()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, quest, LocalInput(quest));

        await f.ExecuteAsync("DELETE FROM Apprentices");

        Assert.Equal(binding, await CreateLocalAsync(f, quest, LocalInput(quest)));

        Assert.Equal(0L, await f.CountAsync("Apprentices"));

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        var loaded = await GuildLocalExecutionStore.ReadInputWithinAsync(connection, transaction, quest.Quest.Id, null, default);

        Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, loaded.Error.Code);
    }

    private static GuildLocalExecutionInput LocalInput(GuildQuestStored quest) =>
        new(1, GuildLocalExecutionMode.Sterile, quest.Quest.GoalRef, "fixture-provider", "fixture-model", [], 512);

    private static async Task<GuildLocalExecutionBinding> CreateLocalAsync(Fixture f, GuildQuestStored quest, GuildLocalExecutionInput input)
    {
        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        var created = await GuildLocalExecutionStore.CreateWithinAsync(connection, transaction, quest, input, null, DateTimeOffset.UtcNow, default);

        Assert.True(created.IsSuccess, created.Error.Message);

        transaction.Commit();

        return created.Value;
    }
}
