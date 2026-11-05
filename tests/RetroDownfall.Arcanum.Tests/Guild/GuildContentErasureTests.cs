using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildContentErasureTests
{
    [Fact]
    public async Task Labelled_Guild_content_refuses_an_ordinary_direct_delete()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync("DELETE FROM guild_content"));

        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM guild_content"));
    }

    [Fact]
    public async Task Protected_purge_erases_content_and_card_projections_but_preserves_exact_receipt()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        Result<CovenantArtifactErasureProgress> result = await fixture.PurgeAsync();

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(CovenantErasureBlocker.None, result.Value.Blocker);

        Assert.Equal(1UL, result.Value.ErasedCount);

        await fixture.AssertErasedAsync();
    }

    [Fact]
    public async Task Session_projection_counts_only_the_labels_remaining_after_Guild_erasure()
    {
        await using Fixture fixture = await Fixture.CreateAsync(sessionOwned: true);

        Assert.Equal(1L, await fixture.CountAsync("SELECT SUM(TaintedArtifactCount) FROM session_sensitivity_state"));

        Result<CovenantArtifactErasureProgress> result = await fixture.PurgeAsync();

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(CovenantErasureBlocker.None, result.Value.Blocker);

        Assert.Equal(0L, await fixture.CountAsync("SELECT SUM(TaintedArtifactCount) FROM session_sensitivity_state"));
    }

    [Fact]
    public async Task Protected_purge_refuses_a_content_digest_that_disagrees_with_its_label()
    {
        await using Fixture fixture = await Fixture.CreateAsync(mismatchedDigest: true);

        Result<CovenantArtifactErasureProgress> result = await fixture.PurgeAsync();

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(CovenantErasureBlocker.IntegrityFailure, result.Value.Blocker);

        Assert.Equal(0UL, result.Value.ErasedCount);

        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM guild_content"));

        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity WHERE ArtifactKindCode=17"));
    }

    [Fact]
    public async Task A_conflicting_retained_erasure_receipt_blocks_without_throwing_or_deleting()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await fixture.ExecuteAsync("""
            INSERT INTO guild_content_erasure_receipts
                (ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,ByteLength,ErasedAtUtc)
            SELECT ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,length(Body)+1,CreatedAtUtc
            FROM guild_content;
            """);

        Result<CovenantArtifactErasureProgress> result = await fixture.PurgeAsync();

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(CovenantErasureBlocker.IntegrityFailure, result.Value.Blocker);

        Assert.Equal(0UL, result.Value.ErasedCount);

        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM guild_content"));

        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM guild_agent_cards WHERE Available=1 AND ContentId IS NOT NULL"));

        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity WHERE ArtifactKindCode=17"));
    }

    [Fact]
    public async Task A_failed_content_delete_rolls_back_receipt_and_pointer_changes()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await fixture.ExecuteAsync("CREATE TRIGGER fail_guild_delete BEFORE DELETE ON guild_content BEGIN SELECT RAISE(ABORT,'test failure'); END;");

        Result<CovenantArtifactErasureProgress> result = await fixture.PurgeAsync();

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.IsBlocked);

        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM guild_content"));

        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM guild_agent_cards WHERE Available=1 AND ContentId IS NOT NULL"));

        Assert.Equal(0L, await fixture.CountAsync("SELECT COUNT(*) FROM guild_content_erasure_receipts"));

        Assert.Equal(1L, await fixture.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity WHERE ArtifactKindCode=17"));
    }

    [Fact]
    public async Task Restore_protected_state_purge_uses_the_same_Guild_content_closure()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        Result<BackupRestoreProtectedStatePurgeReceipt> result = await BackupRestoreProtectedStatePurger.PurgeStagedAsync(
            fixture.Connection, transaction, CovenantSqliteConnectionInitializer.Instance, TimeProvider.System, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        await transaction.CommitAsync();

        await fixture.AssertErasedAsync();
    }

    private sealed class Fixture(SqliteConnection connection, Guid contentId, Guid cardId, ArtifactSensitivityLabel label) : IAsyncDisposable
    {
        internal SqliteConnection Connection => connection;

        internal static async Task<Fixture> CreateAsync(bool mismatchedDigest = false, bool sessionOwned = false)
        {
            SqliteNativeRuntime.Instance.Initialize();

            SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

            Guid contentId = Guid.NewGuid();

            Guid cardId = Guid.NewGuid();

            Guid? sessionId = sessionOwned ? Guid.NewGuid() : null;

            byte[] body = Encoding.UTF8.GetBytes("protected Guild guidance");

            CovenantDigest digest = new(SHA256.HashData(body));

            GenerationProvenance provenance = GenerationProvenance.Create([CovenantOperationGateFixture.DatasetGeneration]);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = """
                INSERT INTO Sessions(Id,Title,CreatedAt,UpdatedAt) SELECT $session,'Guild erasure','2026-11-05T00:00:00.0000000Z','2026-11-05T00:00:00.0000000Z' WHERE $session IS NOT NULL;
                INSERT INTO guild_content(ContentId,SessionId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,ExactGenerationIds,CreatedAtUtc)
                VALUES($content,$session,0,$card,1,$digest,'text/plain',$body,1,1,$generations,'2026-11-05T00:00:00.0000000Z');
                INSERT INTO guild_agent_cards(CardId,AgentId,SessionId,SourceKindCode,SourceIdentity,CurrentRevision,ContentId,Available,CreatedAtUtc,UpdatedAtUtc)
                VALUES($card,$agent,$session,$sourceKind,'fixture',1,$content,1,'2026-11-05T00:00:00.0000000Z','2026-11-05T00:00:00.0000000Z');
                INSERT INTO guild_agent_card_revisions(CardId,Revision,ContentId,SourceDigest,CreatedAtUtc)
                VALUES($card,1,$content,'source-digest','2026-11-05T00:00:00.0000000Z');
                INSERT INTO guild_agent_card_skills(CardId,Revision,SkillId,Ordinal) VALUES($card,1,'protected-skill',0);
                """;

            _ = command.Parameters.AddWithValue("$content", Format(contentId));

            _ = command.Parameters.AddWithValue("$session", sessionId is { } owner ? Format(owner) : DBNull.Value);

            _ = command.Parameters.AddWithValue("$sourceKind", sessionOwned ? 2 : 0);

            _ = command.Parameters.AddWithValue("$card", Format(cardId));

            _ = command.Parameters.AddWithValue("$agent", Format(Guid.NewGuid()));

            _ = command.Parameters.AddWithValue("$digest", mismatchedDigest ? new byte[32] : digest.Bytes);

            _ = command.Parameters.AddWithValue("$body", body);

            _ = command.Parameters.AddWithValue("$generations", provenance.ToCanonicalExactBytes());

            _ = await command.ExecuteNonQueryAsync();

            DerivedArtifactWrite write = new(SensitiveArtifactKind.GuildContent, contentId, sessionId, null, null, 1,
                digest, ContentSensitivity.CovenantDerived, provenance);

            Result<LabeledArtifactWriteReceipt> written = await ArtifactSensitivityLedger.WriteWithinAsync(
                connection, transaction, write, CancellationToken.None);

            Assert.True(written.IsSuccess, written.Error.Message);

            await transaction.CommitAsync();

            Result<ArtifactSensitivityLabel?> label = await ArtifactSensitivityLedger.ReadLabelWithinAsync(
                connection, null, SensitiveArtifactKind.GuildContent, contentId, CancellationToken.None);

            Assert.True(label.IsSuccess, label.Error.Message);

            Assert.NotNull(label.Value);

            return new Fixture(connection, contentId, cardId, label.Value);
        }

        internal async Task<Result<CovenantArtifactErasureProgress>> PurgeAsync()
        {
            CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

            await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize), CancellationToken.None)).Value;

            CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority.ForExclusive(
                lease, CovenantExclusiveOperation.CovenantFamilyReinitialize).Value;

            CovenantProtectedArtifactErasureKernel kernel = new(new FixedCovenantConnectionSource(connection),
                CovenantSqliteConnectionInitializer.Instance, TimeProvider.System);

            CovenantProtectedArtifactErasureItem item = new(contentId, SensitiveArtifactKind.GuildContent, label.SessionId,
                label.LabelId, label, label.ArtifactContentDigest, 1);

            return await kernel.ErasePageAsync(new CovenantProtectedArtifactErasurePage(
                CovenantOperationGateFixture.DatasetGeneration, [item]), authority, CancellationToken.None);
        }

        internal async Task AssertErasedAsync()
        {
            Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM guild_content"));

            Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM guild_agent_card_revisions"));

            Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM guild_agent_card_skills"));

            Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM artifact_sensitivity WHERE ArtifactKindCode=17"));

            Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM guild_agent_cards WHERE Available=0 AND ContentId IS NULL"));

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "SELECT COUNT(*) FROM guild_content_erasure_receipts WHERE ContentId=$content AND SourceRecordId=$card AND Revision=1 AND Digest=$digest AND ByteLength=24";

            _ = command.Parameters.AddWithValue("$content", Format(contentId));

            _ = command.Parameters.AddWithValue("$card", Format(cardId));

            _ = command.Parameters.AddWithValue("$digest", label.ArtifactContentDigest.Bytes);

            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }

        internal async Task<long> CountAsync(string sql)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            return (long)(await command.ExecuteScalarAsync())!;
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            _ = await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();

        private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();
    }
}
