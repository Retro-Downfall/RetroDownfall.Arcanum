using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildRosterRefreshStoreTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Retained_scan_discovery_ids_refuse_malformed_and_empty_guids(bool workspace, bool empty)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (var connection = await file.OpenAsync(CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);
        }

        var store = new GuildRosterRefreshStore(new FixtureOrdinaryConnectionFactory(file.ConnectionString),
            new GrimoireConnectionAdmissionGate(TimeProvider.System), new GuildRosterRefreshCoordinator());

        Guid generation = Guid.NewGuid();

        Guid principal = Guid.NewGuid();

        var cursor = await store.SaveCursorAsync(generation, principal, new string('B', 64), "refresh",
            new(0, null, null), CancellationToken.None);

        Assert.True(cursor.IsSuccess, cursor.Error.Message);

        await using (var connection = await file.OpenAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();

            string column = workspace ? "WorkspaceDiscoveryId" : "GlobalDiscoveryId";

            command.CommandText = $"CREATE TEMP TABLE malformed_copy AS SELECT * FROM guild_roster_cursors; UPDATE malformed_copy SET {column}=@value; DELETE FROM guild_roster_cursors; INSERT INTO guild_roster_cursors SELECT * FROM malformed_copy; DROP TABLE malformed_copy;";

            command.Parameters.AddWithValue("@value", empty ? Guid.Empty.ToString("D") : new string('x', 36));

            await command.ExecuteNonQueryAsync();
        }

        var found = await store.ReadCursorAsync(generation, principal, new string('B', 64), "refresh", cursor.Value, CancellationToken.None);

        Assert.True(found.IsFailure);

        Assert.Equal("Guild.Unavailable", found.Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saving_a_scan_discovery_id_refuses_an_explicit_empty_guid(bool workspace)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (var connection = await file.OpenAsync(CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);
        }

        var store = new GuildRosterRefreshStore(new FixtureOrdinaryConnectionFactory(file.ConnectionString),
            new GrimoireConnectionAdmissionGate(TimeProvider.System), new GuildRosterRefreshCoordinator());

        GuildRosterCursorState state = workspace
            ? new(0, null, null, WorkspaceDiscoveryId: Guid.Empty)
            : new(0, null, null, GlobalDiscoveryId: Guid.Empty);

        var saved = await store.SaveCursorAsync(Guid.NewGuid(), Guid.NewGuid(), new string('B', 64), "refresh", state, CancellationToken.None);

        Assert.True(saved.IsFailure);

        Assert.Equal("Guild.CursorInvalid", saved.Error.Code);
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("receipt-digest")]
    [InlineData("cursor-after")]
    [InlineData("cursor-binding")]
    public async Task Replay_bounds_refresh_receipt_and_cursor_buffers_before_materialization(string buffer)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (var connection = await file.OpenAsync(CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);
        }

        var store = new GuildRosterRefreshStore(new FixtureOrdinaryConnectionFactory(file.ConnectionString),
            new GrimoireConnectionAdmissionGate(TimeProvider.System), new GuildRosterRefreshCoordinator());

        Guid generation = Guid.NewGuid();

        Guid principal = Guid.NewGuid();

        var mutation = new GuildMutationIdentity(Guid.NewGuid(), new string('A', 64));

        var receipt = new GuildRefreshReceipt(new(Guid.NewGuid(), mutation.IdempotencyKey, false,
            DateTimeOffset.UtcNow, 1), new([], null, 1), true);

        var accepted = await store.AcceptAsync(generation, principal, mutation, receipt, CancellationToken.None);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        var cursor = await store.SaveCursorAsync(generation, principal, new string('B', 64), "refresh",
            new(0, null, null), CancellationToken.None);

        Assert.True(cursor.IsSuccess, cursor.Error.Message);

        await using (var connection = await file.OpenAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();

            command.CommandText = "PRAGMA ignore_check_constraints=ON;";

            await command.ExecuteNonQueryAsync();

            string table = buffer.StartsWith("receipt", StringComparison.Ordinal) ? "guild_roster_refreshes" : "guild_roster_cursors";

            string column = buffer switch
            {
                "receipt" => "ResultJson",
                "receipt-digest" => "CanonicalRequestDigest",
                "cursor-after" => "AfterKey",
                _ => "BindingDigest",
            };

            // Keep the installed immutable-row guards intact while reproducing a malformed
            // historical row. The temporary copy is private to this fixture connection.
            command.CommandText = $"CREATE TEMP TABLE malformed_copy AS SELECT * FROM {table}; UPDATE malformed_copy SET {column}=@value; DELETE FROM {table}; INSERT INTO {table} SELECT * FROM malformed_copy; PRAGMA ignore_check_constraints=OFF; DROP TABLE malformed_copy;";

            string json = JsonSerializer.Serialize(receipt, GuildJsonContext.Default.GuildRefreshReceipt);

            object value = buffer == "receipt"
                ? Encoding.UTF8.GetBytes(json[..^1] + ",\"ignored\":\"" + new string('x', 65_536) + "\"}")
                : new string('x', buffer == "cursor-after" ? 1_025 : 65_537);

            command.Parameters.AddWithValue("@value", value);

            await command.ExecuteNonQueryAsync();
        }

        if (buffer.StartsWith("receipt", StringComparison.Ordinal))
        {
            var found = await store.FindAsync(generation, principal, mutation, CancellationToken.None);

            Assert.True(found.IsFailure);

            Assert.Equal("Guild.Unavailable", found.Error.Code);
        }
        else
        {
            var found = await store.ReadCursorAsync(generation, principal, new string('B', 64), "refresh", cursor.Value, CancellationToken.None);

            Assert.True(found.IsFailure);

            Assert.Equal("Guild.Unavailable", found.Error.Code);
        }
    }

    [Theory]
    [InlineData("find", true)]
    [InlineData("accept", true)]
    [InlineData("save-cursor", true)]
    [InlineData("read-cursor", true)]
    [InlineData("find", false)]
    [InlineData("accept", false)]
    [InlineData("save-cursor", false)]
    [InlineData("read-cursor", false)]
    public async Task Direct_refresh_storage_requires_healthy_Guild_but_is_independent_of_Covenant(string operation, bool unhealthyGuild)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (var connection = await file.OpenAsync(CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);
        }

        var connections = new FixtureOrdinaryConnectionFactory(file.ConnectionString);

        var gate = new GrimoireConnectionAdmissionGate(TimeProvider.System);

        var store = new GuildRosterRefreshStore(connections, gate, new GuildRosterRefreshCoordinator());

        Guid generation = Guid.NewGuid();

        Guid principal = Guid.NewGuid();

        var mutation = new GuildMutationIdentity(Guid.NewGuid(), new string('A', 64));

        var saved = await store.SaveCursorAsync(generation, principal, new string('B', 64), "refresh",
            new(0, null, null), CancellationToken.None);

        Assert.True(saved.IsSuccess, saved.Error.Message);

        await using (var connection = await file.OpenAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();

            command.CommandText = "UPDATE grimoire_feature_schemas SET HealthCode=2 WHERE FamilyCode=@family;";

            command.Parameters.AddWithValue("@family", unhealthyGuild ? 2 : 1);

            await command.ExecuteNonQueryAsync();
        }

        bool success;

        string code;

        switch (operation)
        {
            case "find":
                var found = await store.FindAsync(generation, principal, mutation, CancellationToken.None);

                success = found.IsSuccess;

                code = found.Error.Code;

                break;

            case "accept":
                var receipt = new GuildRefreshReceipt(new(Guid.NewGuid(), mutation.IdempotencyKey, false,
                    DateTimeOffset.UtcNow, 1), new([], null, 1), true);

                var accepted = await store.AcceptAsync(generation, principal, mutation, receipt, CancellationToken.None);

                success = accepted.IsSuccess;

                code = accepted.Error.Code;

                break;

            case "save-cursor":
                var cursor = await store.SaveCursorAsync(generation, principal, new string('B', 64), "refresh",
                    new(0, null, null), CancellationToken.None);

                success = cursor.IsSuccess;

                code = cursor.Error.Code;

                break;

            default:
                var read = await store.ReadCursorAsync(generation, principal, new string('B', 64), "refresh", saved.Value, CancellationToken.None);

                success = read.IsSuccess;

                code = read.Error.Code;

                break;
        }

        Assert.Equal(!unhealthyGuild, success);

        if (unhealthyGuild)
        {
            Assert.Equal("Guild.Unavailable", code);
        }
    }

    [Fact]
    public async Task Opaque_continuation_survives_reconstruction_and_is_bound_to_caller_and_filters()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (var connection = await file.OpenAsync(CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);
        }

        var connections = new FixtureOrdinaryConnectionFactory(file.ConnectionString);

        var gate = new GrimoireConnectionAdmissionGate(TimeProvider.System);

        var store = new GuildRosterRefreshStore(connections, gate, new GuildRosterRefreshCoordinator());

        Guid generation = Guid.NewGuid();

        Guid principal = Guid.NewGuid();

        var saved = await store.SaveCursorAsync(generation, principal, new string('A', 64), "refresh",
            new(1, "agent-099.md", 73), CancellationToken.None);

        Assert.True(saved.IsSuccess);

        Assert.DoesNotContain("agent-099", saved.Value, StringComparison.Ordinal);

        var restarted = new GuildRosterRefreshStore(connections, gate, new GuildRosterRefreshCoordinator());

        var restored = await restarted.ReadCursorAsync(generation, principal, new string('A', 64), "refresh", saved.Value, CancellationToken.None);

        Assert.True(restored.IsSuccess);

        Assert.Equal("agent-099.md", restored.Value.After);

        Assert.Equal(73, restored.Value.SnapshotSequence);

        var wrongCaller = await restarted.ReadCursorAsync(generation, Guid.NewGuid(), new string('A', 64), "refresh", saved.Value, CancellationToken.None);

        Assert.Equal("Guild.CursorInvalid", wrongCaller.Error.Code);

        var wrongFilter = await restarted.ReadCursorAsync(generation, principal, new string('B', 64), "refresh", saved.Value, CancellationToken.None);

        Assert.Equal("Guild.CursorInvalid", wrongFilter.Error.Code);
    }

    [Fact]
    public async Task Accepted_refresh_survives_restart_and_rejects_changed_intent_under_same_key()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (var connection = await file.OpenAsync(CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);
        }

        var connections = new FixtureOrdinaryConnectionFactory(file.ConnectionString);

        var gate = new GrimoireConnectionAdmissionGate(TimeProvider.System);

        var store = new GuildRosterRefreshStore(connections, gate, new GuildRosterRefreshCoordinator());

        Guid generation = Guid.NewGuid();

        Guid principal = Guid.NewGuid();

        var mutation = new GuildMutationIdentity(Guid.NewGuid(), new string('A', 64));

        var receipt = new GuildRefreshReceipt(new(Guid.NewGuid(), mutation.IdempotencyKey, false,
            DateTimeOffset.UtcNow, 1), new([], null, 1), true);

        Assert.Null((await store.FindAsync(generation, principal, mutation, CancellationToken.None)).Value);

        var accepted = await store.AcceptAsync(generation, principal, mutation, receipt, CancellationToken.None);

        Assert.True(accepted.IsSuccess);

        var restarted = new GuildRosterRefreshStore(connections, gate, new GuildRosterRefreshCoordinator());

        var replay = await restarted.FindAsync(generation, principal, mutation, CancellationToken.None);

        Assert.True(replay.IsSuccess);

        Assert.NotNull(replay.Value);

        Assert.Equal(receipt.Receipt.Id, replay.Value.Receipt.Id);

        Assert.True(replay.Value.Receipt.Replayed);

        var conflict = await restarted.FindAsync(generation, principal,
            mutation with { CanonicalRequestDigest = new string('B', 64) }, CancellationToken.None);

        Assert.False(conflict.IsSuccess);

        Assert.Equal("Guild.IdempotencyConflict", conflict.Error.Code);

        Assert.Null((await restarted.FindAsync(generation, Guid.NewGuid(), mutation, CancellationToken.None)).Value);
    }
}
