using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildStoreTests
{
    [Fact]
    public async Task Refresh_keeps_identity_and_pinned_content_while_advancing_revision()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (var connection = await file.OpenAsync(CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);
        }

        GuildStore store = new(new FixtureOrdinaryConnectionFactory(file.ConnectionString), new GrimoireConnectionAdmissionGate(TimeProvider.System), TimeProvider.System);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Agent", "Original description", "1", [], ["text/plain"], ["text/plain"], new(false,false,false), new(GuildAgentSourceKind.DeclaredGlobal,null,null,true,null));

        GuildCardWrite write = new("global:/agents/a.md", Guid.NewGuid(), null, null, new(card,"guidance",[]), new string('A',64));

        var first = await store.UpsertCardAsync(write, CancellationToken.None);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Code : null);

        Assert.NotEqual(write.ExplicitAgentId, first.Value.Payload.Card.AgentId);

        var replay = await store.UpsertCardAsync(write, CancellationToken.None);

        Assert.Equal(first.Value.Content, replay.Value.Content);

        var changed = await store.UpsertCardAsync(write with { SourceDigest = new string('B',64), Payload = write.Payload with { Card = card with { Description = "Changed description" } }, ExpectedRevision = 1 }, CancellationToken.None);

        Assert.True(changed.IsSuccess, changed.IsFailure ? changed.Error.Code : null);

        Assert.Equal(first.Value.Payload.Card.AgentId, changed.Value.Payload.Card.AgentId);

        Assert.Equal(2, changed.Value.Payload.Card.Revision);

        var pinned = await store.ResolveAsync(first.Value.Payload.Card.AgentId, first.Value.Payload.Card.CardId, 1, CancellationToken.None);

        Assert.Equal("Original description", pinned.Value.Payload.Card.Description);
    }
    [Fact]
    public async Task A_valid_refresh_of_identical_bytes_restores_destination_availability()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (var connection = await file.OpenAsync(CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);
        }

        GuildStore store = new(new FixtureOrdinaryConnectionFactory(file.ConnectionString), new GrimoireConnectionAdmissionGate(TimeProvider.System), TimeProvider.System);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Agent", "Description", "1", [], ["text/plain"], ["text/plain"], new(false,false,false), new(GuildAgentSourceKind.DeclaredGlobal,null,null,true,null));

        GuildCardWrite write = new("global:/agents/a.md", null, null, null, new(card,"guidance",[]), new string('A',64));

        var first = await store.UpsertCardAsync(write, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.True((await store.SetUnavailableAsync(write.SourceIdentity, "Guild.InvalidRequest", CancellationToken.None)).IsSuccess);

        var replay = await store.UpsertCardAsync(write, CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.True(replay.Value.Payload.Card.Source.Available);

        Assert.Null(replay.Value.Payload.Card.Source.ReasonCode);

        Assert.Equal(first.Value.Content, replay.Value.Content);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Erased_content_identity_and_source_revision_cannot_be_reintroduced(bool replaceContentIdentity)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        GuildStore store = new(new FixtureOrdinaryConnectionFactory(file.ConnectionString), new GrimoireConnectionAdmissionGate(TimeProvider.System), TimeProvider.System);

        Guid contentId = Guid.NewGuid();

        Guid sourceId = Guid.NewGuid();

        byte[] body = "erased content"u8.ToArray();

        GuildContentWrite Write(Guid id) => new(id, sourceId, GuildContentKind.CommissionGoal, 1, null, null, "text/plain", body,
            new DerivedArtifactWrite(SensitiveArtifactKind.GuildContent, id, null, null, null, 1,
                new CovenantDigest(SHA256.HashData(body)), ContentSensitivity.None, GenerationProvenance.CreateExact([])));

        Assert.True((await store.WriteContentAsync(Write(contentId), CancellationToken.None)).IsSuccess);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO guild_content_erasure_receipts(ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,ByteLength,ErasedAtUtc)
            SELECT ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,length(Body),'2026-01-01T00:00:00.0000000Z' FROM guild_content;
            DELETE FROM guild_content;
            """;

        _ = await command.ExecuteNonQueryAsync();

        Result<GuildDataRef> replay = await store.WriteContentAsync(Write(replaceContentIdentity ? Guid.NewGuid() : contentId), CancellationToken.None);

        Assert.True(replay.IsFailure);

        Assert.Equal(ErrorCodes.Guild.ContentErased, replay.Error.Code);

        command.CommandText = "SELECT COUNT(*) FROM guild_content";

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task Skill_filtered_pages_keep_admission_order_and_initial_membership()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection connection = await file.OpenAsync(CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);
        }

        GuildStore store = new(new FixtureOrdinaryConnectionFactory(file.ConnectionString), new GrimoireConnectionAdmissionGate(TimeProvider.System), TimeProvider.System);

        List<Guid> expected = [];

        for (int i = 0; i < 6; i++)
        {
            var written = await store.UpsertCardAsync(CardWrite(i), CancellationToken.None);

            Assert.True(written.IsSuccess, written.Error.Message);

            if (i % 2 == 0)
            {
                expected.Add(written.Value.Payload.Card.CardId);
            }
        }

        GuildCardReadScope scope = new(null, null, null);

        var first = await store.ListAsync(scope, GuildAgentSourceKind.DeclaredGlobal, "target", null, 1, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(expected[0], Assert.Single(first.Value.Items).Payload.Card.CardId);

        Assert.NotNull(first.Value.NextCardId);

        Assert.True((await store.UpsertCardAsync(CardWrite(100), CancellationToken.None)).IsSuccess);

        var rest = await store.ListAsync(scope, GuildAgentSourceKind.DeclaredGlobal, "target", first.Value.NextCardId, 100,
            CancellationToken.None, first.Value.SnapshotSequence);

        Assert.True(rest.IsSuccess, rest.Error.Message);

        Assert.Equal(expected.Skip(1), rest.Value.Items.Select(item => item.Payload.Card.CardId));

        Assert.Null(rest.Value.NextCardId);
    }

    [Fact]
    public async Task Byte_bounded_pages_make_progress_without_limiting_total_records()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (SqliteConnection connection = await file.OpenAsync(CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);
        }

        GuildStore store = new(new FixtureOrdinaryConnectionFactory(file.ConnectionString), new GrimoireConnectionAdmissionGate(TimeProvider.System), TimeProvider.System);

        for (int i = 0; i < 18; i++)
        {
            var written = await store.UpsertCardAsync(CardWrite(i, new string('x', 60_000)), CancellationToken.None);

            Assert.True(written.IsSuccess, written.Error.Message);
        }

        string? cursor = null;

        long? snapshot = null;

        HashSet<Guid> ids = [];

        do
        {
            var page = await store.ListAsync(new(null,null,null), null, null, cursor, 100, CancellationToken.None, snapshot);

            Assert.True(page.IsSuccess, page.Error.Message);

            Assert.NotEmpty(page.Value.Items);

            Assert.True(page.Value.Items.Sum(item => item.Content.ByteLength + 4096) <= 1_048_576);

            foreach (GuildStoredCard item in page.Value.Items)
            {
                Assert.True(ids.Add(item.Payload.Card.CardId));
            }

            Assert.True(page.Value.NextCardId is null || page.Value.NextCardId != cursor);

            cursor = page.Value.NextCardId;

            snapshot = page.Value.SnapshotSequence;
        } while (cursor is not null);

        Assert.Equal(18, ids.Count);
    }

    [Fact]
    public async Task Owner_deletion_denies_read_and_refresh_before_cleanup_catches_up()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Guid session = Guid.NewGuid();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "INSERT INTO Sessions(Id,CreatedAt,UpdatedAt) VALUES($id,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')";

        command.Parameters.AddWithValue("$id", session.ToString("D").ToUpperInvariant());

        _ = await command.ExecuteNonQueryAsync();

        GuildStore store = new(new FixtureOrdinaryConnectionFactory(file.ConnectionString), new GrimoireConnectionAdmissionGate(TimeProvider.System), TimeProvider.System);

        GuildCardWrite original = CardWrite(1);

        GuildCardWrite write = original with { SessionId = session, Payload = original.Payload with
        {
            Card = original.Payload.Card with { Source = new(GuildAgentSourceKind.Derived, null, null, true, null) }
        } };

        var accepted = await store.UpsertCardAsync(write, CancellationToken.None);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention))
        {
            command.CommandText = "DELETE FROM Sessions WHERE Id=$id";

            _ = await command.ExecuteNonQueryAsync();
        }

        var read = await store.ResolveAsync(accepted.Value.Payload.Card.AgentId, null, null, CancellationToken.None);

        Assert.Equal(ErrorCodes.Guild.NotFound, read.Error.Code);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, (await store.UpsertCardAsync(write, CancellationToken.None)).Error.Code);

        command.CommandText = "SELECT COUNT(*) FROM guild_content";

        Assert.Equal(1L, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode=2";

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Protected_card_write_and_exact_replay_keep_atomic_label_without_granting_read_authority()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Guid session = await AddSessionAsync(connection);

        GuildStore store = new(new FixtureOrdinaryConnectionFactory(file.ConnectionString), new GrimoireConnectionAdmissionGate(TimeProvider.System), TimeProvider.System);

        GuildCardWrite write = ProtectedWrite(session);

        var first = await store.UpsertCardAsync(write, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);

        var replay = await store.UpsertCardAsync(write, CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(first.Value.Content, replay.Value.Content);

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, (await store.ResolveAsync(first.Value.Payload.Card.AgentId, null, null, CancellationToken.None)).Error.Code);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM guild_content c JOIN artifact_sensitivity l ON l.ArtifactKindCode=17 AND l.ArtifactId=c.ContentId AND l.ArtifactContentDigest=c.Digest AND l.ArtifactRevision=c.Revision WHERE c.SensitivityCode=1";

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Identical_source_bytes_with_changed_inherited_sensitivity_create_an_immutable_revision()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Guid session = await AddSessionAsync(connection);

        GuildStore store = new(new FixtureOrdinaryConnectionFactory(file.ConnectionString), new GrimoireConnectionAdmissionGate(TimeProvider.System), TimeProvider.System);

        GuildCardWrite write = ProtectedWrite(session);

        var clean = await store.UpsertCardAsync(write with { Label = null }, CancellationToken.None);

        Assert.True(clean.IsSuccess, clean.Error.Message);

        var protectedCard = await store.UpsertCardAsync(write, CancellationToken.None);

        Assert.True(protectedCard.IsSuccess, protectedCard.Error.Message);

        Assert.Equal(2, protectedCard.Value.Payload.Card.Revision);

        Assert.NotEqual(clean.Value.Content.ContentId, protectedCard.Value.Content.ContentId);

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, (await store.ResolveAsync(clean.Value.Payload.Card.AgentId, null, null, CancellationToken.None)).Error.Code);

        Assert.True((await store.ResolveAsync(clean.Value.Payload.Card.AgentId, clean.Value.Payload.Card.CardId, 1, CancellationToken.None)).IsSuccess);

        GuildCardWrite anotherGeneration = ProtectedWrite(session);

        var changedProvenance = await store.UpsertCardAsync(anotherGeneration, CancellationToken.None);

        Assert.True(changedProvenance.IsSuccess, changedProvenance.Error.Message);

        Assert.Equal(3, changedProvenance.Value.Payload.Card.Revision);
    }

    [Theory]
    [InlineData("application/octet-stream", false)]
    [InlineData("text/plain", true)]
    public async Task Normalized_content_refuses_unsupported_media_and_invalid_utf8(string mime, bool invalidUtf8)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        GuildStore store = new(new FixtureOrdinaryConnectionFactory(file.ConnectionString), new GrimoireConnectionAdmissionGate(TimeProvider.System), TimeProvider.System);

        Guid id = Guid.NewGuid();

        byte[] body = invalidUtf8 ? [0xC3, 0x28] : "valid text"u8.ToArray();

        GuildContentWrite write = new(id, Guid.NewGuid(), GuildContentKind.CommissionGoal, 1, null, null, mime, body,
            new(SensitiveArtifactKind.GuildContent, id, null, null, null, 1, new(SHA256.HashData(body)), ContentSensitivity.None, GenerationProvenance.CreateExact([])));

        var result = await store.WriteContentAsync(write, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guild.InvalidRequest, result.Error.Code);
    }

    [Fact]
    public async Task Restored_content_with_an_erasure_receipt_is_unreadable_and_fails_Guild_admission()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        GuildStore store = new(new FixtureOrdinaryConnectionFactory(file.ConnectionString), new GrimoireConnectionAdmissionGate(TimeProvider.System), TimeProvider.System);

        var accepted = await store.UpsertCardAsync(CardWrite(1), CancellationToken.None);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO guild_content_erasure_receipts(ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,ByteLength,ErasedAtUtc)
            SELECT ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,length(Body),'2026-01-01T00:00:00.0000000Z' FROM guild_content;
            """;

        _ = await command.ExecuteNonQueryAsync();

        Assert.True((await store.ResolveAsync(accepted.Value.Payload.Card.AgentId, null, null, CancellationToken.None)).IsFailure);

        var admitted = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Assert.True(admitted.Core.IsHealthy);

        Assert.True(admitted.CovenantCanonical.IsHealthy);

        Assert.False(admitted.GuildCanonical.IsHealthy);

        await using SqliteConnection fresh = await file.OpenAsync(CancellationToken.None);

        Assert.True((await GuildStorageAvailability.CheckAsync(fresh, null, CancellationToken.None)).IsFailure);
    }

    [Theory]
    [InlineData(false, "Body")]
    [InlineData(true, "Body")]
    [InlineData(true, "ExactGenerationIds")]
    [InlineData(true, "GenerationBloom")]
    [InlineData(true, "MixedProvenance")]
    public async Task Replay_bounds_malformed_persisted_buffers_before_materialization(bool protect, string column)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        GuildStore store = new(new FixtureOrdinaryConnectionFactory(file.ConnectionString), new GrimoireConnectionAdmissionGate(TimeProvider.System), TimeProvider.System);

        GuildCardWrite write = protect ? ProtectedWrite(await AddSessionAsync(connection)) : CardWrite(1);

        var accepted = await store.UpsertCardAsync(write, CancellationToken.None);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "CREATE TEMP TABLE oversized_copy AS SELECT * FROM guild_content";

        _ = await command.ExecuteNonQueryAsync();

        command.CommandText = column switch
        {
            "Body" => "UPDATE oversized_copy SET Body=zeroblob(65537)",
            "ExactGenerationIds" => "UPDATE oversized_copy SET ExactGenerationIds=zeroblob(129)",
            "MixedProvenance" => "UPDATE oversized_copy SET GenerationBloom=zeroblob(32)",
            _ => "UPDATE oversized_copy SET GenerationBloom=zeroblob(33)"
        };

        _ = await command.ExecuteNonQueryAsync();

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SensitivityRetentionPurge))
        {
            command.CommandText = "PRAGMA ignore_check_constraints=ON; DELETE FROM guild_content; INSERT INTO guild_content SELECT * FROM oversized_copy; PRAGMA ignore_check_constraints=OFF; DROP TABLE oversized_copy;";

            _ = await command.ExecuteNonQueryAsync();
        }

        var replay = await store.UpsertCardAsync(write, CancellationToken.None);

        Assert.True(replay.IsFailure);

        Assert.Equal(column == "MixedProvenance" ? ErrorCodes.Covenant.IntegrityFailure : ErrorCodes.Guild.PayloadTooLarge, replay.Error.Code);
    }

    [Theory]
    [InlineData("SourceDigest")]
    [InlineData("PeerId")]
    public async Task Replay_bounds_malformed_persisted_source_metadata_before_materialization(string column)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        GuildStore store = new(new FixtureOrdinaryConnectionFactory(file.ConnectionString), new GrimoireConnectionAdmissionGate(TimeProvider.System), TimeProvider.System);

        GuildCardWrite write = CardWrite(1);

        Assert.True((await store.UpsertCardAsync(write, CancellationToken.None)).IsSuccess);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = column == "PeerId"
            ? "PRAGMA ignore_check_constraints=ON; UPDATE guild_agent_cards SET PeerId=$text; PRAGMA ignore_check_constraints=OFF;"
            : "CREATE TEMP TABLE oversized_copy AS SELECT * FROM guild_agent_card_revisions; UPDATE oversized_copy SET SourceDigest=$text; PRAGMA ignore_check_constraints=ON; DELETE FROM guild_agent_card_revisions; INSERT INTO guild_agent_card_revisions SELECT * FROM oversized_copy; PRAGMA ignore_check_constraints=OFF; DROP TABLE oversized_copy;";

        command.Parameters.AddWithValue("$text", new string('x', 65537));

        _ = await command.ExecuteNonQueryAsync();

        var replay = await store.UpsertCardAsync(write, CancellationToken.None);

        Assert.True(replay.IsFailure);

        Assert.Equal(ErrorCodes.Guild.PayloadTooLarge, replay.Error.Code);
    }

    [Fact]
    public async Task Exact_protected_replay_refuses_a_source_label_from_another_owner()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        GuildStore store = new(new FixtureOrdinaryConnectionFactory(file.ConnectionString), new GrimoireConnectionAdmissionGate(TimeProvider.System), TimeProvider.System);

        GuildCardWrite write = ProtectedWrite(await AddSessionAsync(connection));

        Assert.True((await store.UpsertCardAsync(write, CancellationToken.None)).IsSuccess);

        ArtifactSensitivityLabel source = write.Label!;

        ArtifactSensitivityLabel otherOwner = new(Guid.NewGuid(), source.ArtifactKind, source.ArtifactId, Guid.NewGuid(), source.CampaignId,
            source.TurnId, source.ArtifactRevision, source.ArtifactContentDigest, source.Sensitivity, source.Provenance,
            source.ProducingPlanDigest, source.ProducingAdmissionDigest, source.ProducingMaintenanceReceiptDigest, source.CreatedAt);

        var replay = await store.UpsertCardAsync(write with { Label = otherOwner }, CancellationToken.None);

        Assert.True(replay.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, replay.Error.Code);
    }

    private static async Task<Guid> AddSessionAsync(SqliteConnection connection)
    {
        Guid id = Guid.NewGuid();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "INSERT INTO Sessions(Id,CreatedAt,UpdatedAt) VALUES($id,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')";

        command.Parameters.AddWithValue("$id", id.ToString("D").ToUpperInvariant());

        _ = await command.ExecuteNonQueryAsync();

        return id;
    }

    private static GuildCardWrite ProtectedWrite(Guid session)
    {
        GuildCardWrite original = CardWrite(1);

        ArtifactSensitivityLabel label = new(Guid.NewGuid(), SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(), session, null, null,
            1, new(SHA256.HashData("source"u8)), ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid()]), null, null, null, DateTimeOffset.UtcNow);

        return original with { SessionId = session, Label = label, Payload = original.Payload with
        {
            Card = original.Payload.Card with { Source = new(GuildAgentSourceKind.Derived, null, null, true, null) }
        } };
    }

    private static GuildCardWrite CardWrite(int ordinal, string description = "Description")
    {
        GuildAgentSkillDto skill = new(ordinal % 2 == 0 ? "target" : "other", "Skill", "Skill description", [], ["text/plain"], ["text/plain"]);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Agent " + ordinal, description, "1", [skill], ["text/plain"], ["text/plain"],
            new(false,false,false), new(GuildAgentSourceKind.DeclaredGlobal,null,null,true,null));

        return new("global:/agents/" + ordinal + ".md", null, null, null, new(card,"guidance",[]), new string('A',64));
    }
}
