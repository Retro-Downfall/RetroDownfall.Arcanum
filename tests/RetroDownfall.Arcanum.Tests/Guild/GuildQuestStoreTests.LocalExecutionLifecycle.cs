using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Local_binding_protected_input_requires_disclosure_and_staged_purge_preserves_receipt()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        ArtifactSensitivityLabel source = new(Guid.NewGuid(), SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(),
            quest.Quest.SessionId, null, null, 1, new(SHA256.HashData("protected-source"u8)),
            ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([Guid.NewGuid()]), null, null, null, DateTimeOffset.UtcNow);

        await using SqliteConnection connection = await f.OpenAsync();

        GuildLocalExecutionBinding binding;

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            var created = await GuildLocalExecutionStore.CreateWithinAsync(connection, transaction, quest, LocalInput(quest), source, DateTimeOffset.UtcNow, default);

            Assert.True(created.IsSuccess, created.Error.Message);

            binding = created.Value;

            var denied = await GuildLocalExecutionStore.ReadInputWithinAsync(connection, transaction, quest.Quest.Id, null, default);

            Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, denied.Error.Code);

            var read = await GuildLocalExecutionStore.ReadInputWithinAsync(connection, transaction, quest.Quest.Id,
                (label, _) => Task.FromResult(label.Provenance.Equals(source.Provenance) ? Result.Success() : Result.Failure(new("fixture.wrong-label", "Wrong label"))), default);

            Assert.True(read.IsSuccess, read.Error.Message);

            transaction.Commit();
        }

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            var purged = await BackupRestoreProtectedStatePurger.PurgeStagedAsync(connection, transaction,
                CovenantSqliteConnectionInitializer.Instance, TimeProvider.System, default);

            Assert.True(purged.IsSuccess, purged.Error.Message);

            transaction.Commit();
        }

        await using SqliteTransaction readTransaction = connection.BeginTransaction();

        var retained = await GuildLocalExecutionStore.ReadWithinAsync(connection, readTransaction, quest.Quest.Id, default);

        Assert.True(retained.IsSuccess, retained.Error.Message);

        Assert.Equal(binding.ApprenticeId, retained.Value.ApprenticeId);

        Assert.True(retained.Value.Input.ContentErased);
    }

    [Fact]
    public async Task Local_binding_restore_preserves_identity_but_refuses_execution_input()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, quest, LocalInput(quest));

        await using SqliteConnection connection = await f.OpenAsync();

        Assert.True((await BackupRestoreGuildSignetSanitation.SanitizeStagedAsync(connection, default)).IsSuccess);

        await using SqliteTransaction transaction = connection.BeginTransaction();

        Assert.Equal(binding, (await GuildLocalExecutionStore.ReadWithinAsync(connection, transaction, quest.Quest.Id, default)).Value);

        var denied = await GuildLocalExecutionStore.ReadInputWithinAsync(connection, transaction, quest.Quest.Id, null, default);

        Assert.True(denied.IsFailure);
    }

    [Fact]
    public async Task Local_binding_owner_cleanup_tombstone_prevents_same_id_reactivation()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        _ = await CreateLocalAsync(f, quest, LocalInput(quest));

        await f.ExecuteAsync($"""
            INSERT INTO guild_owner_cleanup(OwnerKindCode,OwnerId,PhaseCode,CreatedAtUtc,UpdatedAtUtc)
            VALUES(2,'{quest.Quest.SessionId!.Value.ToString("D").ToUpperInvariant()}',6,
                '2026-11-08T00:00:00.0000000Z','2026-11-08T00:00:00.0000000Z');
            """);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        var denied = await GuildLocalExecutionStore.ReadInputWithinAsync(connection, transaction, quest.Quest.Id, null, default);

        Assert.True(denied.IsFailure);
    }
}
