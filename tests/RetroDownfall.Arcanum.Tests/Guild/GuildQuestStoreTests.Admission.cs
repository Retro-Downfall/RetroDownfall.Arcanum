using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task A_selected_card_cannot_bypass_the_rosters_owner_visibility_rules()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = """
                INSERT INTO Campaigns(Id,Name,NameLower,Path,Type,Settings,CreatedAt,UpdatedAt)
                VALUES($id,'Other campaign','other campaign','/other-guild-fixture',0,'{}',$now,$now)
                """;

            command.Parameters.AddWithValue("$id", campaign.ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$now", UtcInstantText.Format(DateTimeOffset.UtcNow));

            _ = await command.ExecuteNonQueryAsync();
        }

        GuildStore roster = new(f.Connections, f.Gate, TimeProvider.System);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Other campaign agent", "Owner-scoped guidance", "1", [], ["text/plain"], ["text/plain"],
            new(false, false, false), new(GuildAgentSourceKind.DeclaredWorkspace, campaign, null, true, null));

        var stored = await roster.UpsertCardAsync(new("owner-scope-fixture", null, null, null, new(card, "", []), new string('A', 64)), default);

        Assert.True(stored.IsSuccess, stored.Error.Message);

        await f.ExecuteAsync($"DELETE FROM Campaigns WHERE Id='{campaign.ToString("D").ToUpperInvariant()}'");

        var invisible = await roster.ResolveAsync(new(campaign, null, null), stored.Value.Payload.Card.AgentId, null, null, default);

        Assert.True(invisible.IsFailure);

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction();

            var selected = await GuildStore.SelectQuestCardWithinAsync(connection, transaction,
                stored.Value.Payload.Card.AgentId, new(campaign, null, null), default);

            Assert.True(selected.IsFailure, "Quest destination selection must reject a Card whose workspace owner was deleted.");
        }

        var admitted = await f.Store.IssueAsync(f.Caller, f.Identity,
            f.Request with { AgentId = stored.Value.Payload.Card.AgentId, WorkspaceId = campaign }, default);

        Assert.True(admitted.IsFailure, "Quest destination selection must preserve the Roster's owner visibility.");

        Assert.Equal(0L, await f.CountAsync("guild_quests"));

        Assert.Equal(0L, await f.CountAsync("Sessions"));
    }

    [Fact]
    public async Task Purged_payload_and_removed_generic_claims_do_not_reopen_an_accepted_issue_identity()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored first = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            using SqliteTransaction transaction = connection.BeginTransaction();

            await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection, transaction, first.Quest.GoalRef.ContentId.ToString("N"), default);

            await using SqliteCommand erase = connection.CreateCommand();

            erase.Transaction = transaction;

            erase.CommandText = "DELETE FROM guild_content WHERE ContentId=$content; DELETE FROM IdempotencyClaims; DELETE FROM long_running_operation_request_identities;";

            erase.Parameters.AddWithValue("$content", first.Quest.GoalRef.ContentId.ToString("D").ToUpperInvariant());

            _ = await erase.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }

        f.Restart();

        f.Host.RefuseResolution = true;

        var replay = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(first.Quest.Id, replay.Value.Quest.Id);

        Assert.True(replay.Value.Quest.GoalRef.ContentErased);

        Assert.Null(replay.Value.Quest.Name);

        Assert.Equal(1L, await f.CountAsync("guild_quests"));

        Assert.Equal(1L, await f.CountAsync("guild_content"));

        Assert.Equal(1L, await f.CountAsync("fixture_allocations"));
    }

    [Fact]
    public async Task A_deleted_generated_owner_cannot_be_recreated_by_replaying_its_original_issue()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored first = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            using CovenantSqliteAuthorizationScope deletion = CovenantSqliteConnectionInitializer.Instance.Authorize(connection,
                CovenantSqliteAuthorizationKind.SessionRetention);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "DELETE FROM Sessions WHERE Id=$id";

            command.Parameters.AddWithValue("$id", first.Quest.SessionId!.Value.ToString("D").ToUpperInvariant());

            _ = await command.ExecuteNonQueryAsync();
        }

        f.Restart();

        var replay = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(replay.IsFailure);

        Assert.Equal(0L, await f.CountAsync("Sessions"));

        Assert.Equal(1L, await f.CountAsync("guild_quests"));

        Assert.Equal(1L, await f.CountAsync("fixture_allocations"));
    }

    [Fact]
    public async Task Host_resolution_cannot_silently_widen_the_explicit_root_grant()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildIssueRootRequest request = f.Request with { Grant = f.Request.Grant with { Delegation = [] } };

        var result = await f.Store.IssueAsync(f.Caller, f.Identity, request, default);

        Assert.True(result.IsFailure, "The host plan returned broader delegation rights than the explicit root intent.");

        Assert.Equal(0L, await f.CountAsync("guild_quests"));

        Assert.Equal(0L, await f.CountAsync("Sessions"));
    }

    [Fact]
    public async Task Omitted_campaign_resolves_the_supplied_sessions_canonical_owner_and_replay_keeps_it()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = """
                INSERT INTO Campaigns(Id,Name,NameLower,Path,Type,Settings,CreatedAt,UpdatedAt)
                VALUES($campaign,'Guild','guild','/guild-fixture',0,'{}',$now,$now);
                INSERT INTO Sessions(Id,CampaignId,CreatedAt,UpdatedAt) VALUES($session,$campaign,$now,$now);
                INSERT INTO session_campaign_bindings(SessionId,BindingKindCode,CampaignId,BoundAtUtc) VALUES($session,2,$campaign,$now);
                """;

            command.Parameters.AddWithValue("$campaign", campaign.ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$session", session.ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$now", UtcInstantText.Format(DateTimeOffset.UtcNow));

            using CovenantSqliteAuthorizationScope bindingAuthorization = CovenantSqliteConnectionInitializer.Instance
                .Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

            _ = await command.ExecuteNonQueryAsync();
        }

        GuildIssueRootRequest request = f.Request with { SessionId = session };

        var first = await f.Store.IssueAsync(f.Caller, f.Identity, request, default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(campaign, first.Value.Quest.CampaignId);

        Assert.Equal(session, first.Value.Quest.SessionId);

        Assert.Equal(1L, await f.CountAsync("Sessions"));

        var conflict = await f.Store.IssueAsync(f.Caller, f.Identity, request with { CampaignId = campaign }, default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, conflict.Error.Code);
    }

    [Theory]
    [InlineData("Sessions")]
    [InlineData("guild_agent_cards")]
    [InlineData("guild_content")]
    [InlineData("LongRunningOperations")]
    [InlineData("guild_signets")]
    [InlineData("fixture_allocations")]
    [InlineData("guild_quests")]
    [InlineData("guild_quest_start_intents")]
    public async Task Admission_fault_at_each_durable_insert_leaves_no_partial_identity(string boundary)
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = $"CREATE TRIGGER fixture_admission_fault BEFORE INSERT ON \"{boundary}\" BEGIN SELECT RAISE(ABORT,'fixture injected fault'); END;";

            _ = await command.ExecuteNonQueryAsync();
        }

        var failed = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(failed.IsFailure);

        foreach (string table in new[] { "Sessions", "guild_agent_cards", "guild_content", "LongRunningOperations", "guild_signets", "fixture_allocations", "guild_quests", "guild_quest_start_intents" })
        {
            Assert.Equal(0L, await f.CountAsync(table));
        }

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "DROP TRIGGER fixture_admission_fault";

            _ = await command.ExecuteNonQueryAsync();
        }

        f.Restart();

        Assert.True((await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).IsSuccess);

        Assert.Equal(1L, await f.CountAsync("guild_quests"));
    }

    [Fact]
    public async Task A_copied_caller_cannot_use_an_existing_issue_key_as_authority()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Assert.True((await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).IsSuccess);

        GuildCallerContext copied = new(GuildCallerKind.Operator, f.Caller.PrincipalId!.Value, f.Caller.InvocationId, f.Caller.InstallationGeneration);

        var result = await f.Store.IssueAsync(copied, f.Identity, f.Request, default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, result.Error.Code);

        Assert.Equal(1L, await f.CountAsync("guild_quests"));
    }

    [Fact]
    public async Task Old_installation_generation_cannot_issue_into_a_new_namespace()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext old = new(GuildCallerKind.Operator, f.Caller.PrincipalId!.Value, f.Caller.InvocationId, Guid.NewGuid());

        var result = await f.Store.IssueAsync(old, f.Identity with { InstallationGeneration = old.InstallationGeneration }, f.Request, default);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, result.Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_quests"));
    }
}
