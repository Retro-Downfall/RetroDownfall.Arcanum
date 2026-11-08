using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Storage;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildContextBoundaryTests
{
    [Fact]
    public async Task Guild_pins_and_Tapestry_refuse_before_unscoped_dependencies_are_read()
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        var execution = await f.RootAsync();

        using var invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        var pins = new SessionContextPinMaterializer(null!, null!, null!, null!);

        var pinError = await Assert.ThrowsAsync<GuildMemoryAccessException>(() => pins.MaterializeAsync(Guid.NewGuid(), "/workspace", default));

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, pinError.Error.Code);

        var store = new TapestryStore(null!);

        var discoveryError = await Assert.ThrowsAsync<GuildMemoryAccessException>(() => store.DiscoverScopesAsync(true, true, true, default));

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, discoveryError.Error.Code);

        var retriever = new TapestryRetriever(null!, null!, NullLogger<TapestryRetriever>.Instance);

        var request = new TapestryRetrievalRequest([], [1f], new("fixture", 1), default, 1, 1024, 128, 0f, new(), "fixture");

        var retrievalError = await Assert.ThrowsAsync<GuildMemoryAccessException>(() => retriever.RetrieveAsync(request, default));

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, retrievalError.Error.Code);
    }

    [Fact]
    public async Task Attachment_ids_and_indexes_are_filtered_by_canonical_Session_before_materialization()
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid(), own = Guid.NewGuid(), other = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, own, other);

        await using (var bindingConnection = await f.OpenAsync())
        {
            using var permission = CovenantSqliteConnectionInitializer.Instance.Authorize(bindingConnection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

            await using var command = bindingConnection.CreateCommand();

        command.CommandText = $"INSERT INTO session_campaign_bindings(SessionId,BindingKindCode,CampaignId,BoundAtUtc) VALUES('{own.ToString("D").ToUpperInvariant()}',2,'{campaign.ToString("D").ToUpperInvariant()}','2026-11-05T00:00:00.0000000Z'),('{other.ToString("D").ToUpperInvariant()}',2,'{campaign.ToString("D").ToUpperInvariant()}','2026-11-05T00:00:00.0000000Z')";

            await command.ExecuteNonQueryAsync();
        }

        var grant = f.Grant with { Memory = [new(GuildMemoryStore.Attachment, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Session, own))] };

        f.Policy.HostGrant = grant;

        f.Policy.NextOwners = new(own, campaign, null);

        var execution = await f.RootAsync(grant);

        await using var connection = await f.OpenAsync();

        await using var db = GrimoireFixture.CreateContext(connection);

        string directory = Path.Combine(Path.GetTempPath(), "guild-attachment-" + Guid.NewGuid().ToString("N"));

        try
        {
            var store = new SessionAttachmentStore(db, Options.Create(new ArcanumSettings()), directory,
                new EncryptedBlobStore(new FixedFileEncryptionKeyProvider(), new EncryptedBlobStoreOptions { ChunkSize = 64 }));

            var owned = await store.PersistNewAsync(own, null, null, "own", "own.txt", "owned"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

            var foreign = await store.PersistNewAsync(other, null, null, "foreign", "foreign.txt", "secret"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

            var document = await store.PersistNewAsync(own, null, null, "z-document", "document.pdf", "fake pdf fixture"u8.ToArray(), "application/pdf", SessionAttachmentKind.Binary);

            await f.ExecuteAsync($"INSERT INTO session_attachment_chunks(ChunkId,SessionId,AttachmentId,LogicalKey,Version,OriginalFileName,MimeType,ContentSha256,ChunkIndex,CharacterStart,CharacterEnd,StartLine,EndLine,Content,EmbeddingDimension,ExtractedAt,IndexedAt,RetrievalScope) VALUES('document-chunk','{own:D}','{document.Id.ToString("D").ToUpperInvariant()}','z-document',1,'document.pdf','application/pdf','{document.ContentSha256}',0,0,13,1,1,'document text',1,'2026-11-05T00:00:00.0000000Z','2026-11-05T00:00:00.0000000Z','{own:D}')");

            await f.ExecuteAsync($"INSERT INTO session_attachment_index_state(AttachmentId,Status,ContentSha256,PublishedGenerationId,UpdatedAt) VALUES('{document.Id.ToString("D").ToUpperInvariant()}','Indexed','{document.ContentSha256}','legacy','2026-11-05T00:00:00.0000000Z')");

            var indexSettings = new ArcanumSettings();

            indexSettings.Features.AttachmentRetrieval = true;

            indexSettings.Features.Comprehension = true;

            var index = new SessionAttachmentIndexRepository(db, new TestOptionsMonitor<ArcanumSettings>(indexSettings));

            using var invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

            Assert.Equal(owned.Id, (await store.GetByIdAsync(owned.Id))?.Id);

            using (var trace = new SqliteStatementTrace(connection))
            {
                Assert.True(await store.GetByIdAsync(foreign.Id) is null, string.Join("\n", trace.Statements));
            }

            Assert.Null(await store.GetByLogicalAsync(other, "foreign", null));

            Assert.Empty(await store.BuildIndexAsync(other, 1));

            Assert.Equal("own", Assert.Single(await store.BuildIndexAsync(own, 1)).LogicalKey);

            Assert.Equal("owned"u8.ToArray(), (await store.ReadBytesAsync(owned)).ToArray());

            Assert.Equal("document text", Assert.Single(await index.GetChunksForAttachmentAsync(document.Id, default)).Content);

            indexSettings.Features.Comprehension = false;

            Assert.Empty(await index.GetChunksForAttachmentAsync(document.Id, default));

            Assert.Empty(await index.GetRetrievedChunksAsync(own, [new("document-chunk", 1f, new Dictionary<string, string>())], default));

            var denied = await Assert.ThrowsAsync<GuildMemoryAccessException>(() => store.ReadBytesAsync(foreign));

            Assert.Equal(ErrorCodes.Guild.MemoryNotGranted, denied.Error.Code);

            var streamError = await Assert.ThrowsAsync<GuildMemoryAccessException>(() => store.OpenReadAsync(owned));

            Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, streamError.Error.Code);

            f.Policy.HostGrant = f.Grant;

            var removed = await Assert.ThrowsAsync<GuildMemoryAccessException>(() => store.GetByIdAsync(owned.Id));

            Assert.Equal(ErrorCodes.Guild.MemoryNotGranted, removed.Error.Code);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
    [Theory]
    [InlineData("clean", true, true)]
    [InlineData("document", true, true)]
    [InlineData("document-disabled", false, false)]
    [InlineData("document-retrieval-disabled", false, false)]
    [InlineData("protected-entry", false, false)]
    [InlineData("workspace-lineage", false, false)]
    [InlineData("missing-entry", false, false)]
    [InlineData("pending-source", false, false)]
    [InlineData("wrong-chunk-version", true, false)]
    [InlineData("wrong-chunk-hash", true, false)]
    [InlineData("wrong-chunk-mime", true, false)]
    [InlineData("unpublished-chunk", true, false)]
    public async Task Real_attachment_source_append_retains_current_materialization_and_source_gates(string mode, bool allowed, bool candidateAllowed)
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid(), own = Guid.NewGuid(), other = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, own, other);

        await using var connection = await f.OpenAsync();

        using (var permission = CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite))
        {
            await using var binding = connection.CreateCommand();

            binding.CommandText = "INSERT INTO session_campaign_bindings(SessionId,BindingKindCode,CampaignId,BoundAtUtc) VALUES($session,2,$campaign,'2026-11-05T00:00:00.0000000Z')";

            binding.Parameters.AddWithValue("$session", own.ToString("D").ToUpperInvariant());

            binding.Parameters.AddWithValue("$campaign", campaign.ToString("D").ToUpperInvariant());

            await binding.ExecuteNonQueryAsync();
        }

        var grant = f.Grant with { Memory = [
            new(GuildMemoryStore.Attachment, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Session, own)),
            new(GuildMemoryStore.Lexicon, GuildMemoryOperation.Append, new(GuildMemoryScopeKind.Campaign, campaign))] };

        f.Policy.HostGrant = grant;

        f.Policy.NextOwners = new(own, campaign, null);

        var execution = await f.RootAsync(grant);

        var settings = new ArcanumSettings();

        settings.Features.Annals = false;

        settings.Features.AttachmentRetrieval = true;

        settings.Features.Comprehension = true;

        await using var db = GrimoireFixture.CreateContext(connection);

        string directory = Path.Combine(Path.GetTempPath(), "guild-source-" + Guid.NewGuid().ToString("N"));

        try
        {
            var store = new SessionAttachmentStore(db, Options.Create(settings), directory,
                new EncryptedBlobStore(new FixedFileEncryptionKeyProvider(), new EncryptedBlobStoreOptions { ChunkSize = 64 }));

            Guid? entry = mode is "protected-entry" or "missing-entry" ? Guid.NewGuid() : null;

            if (mode == "protected-entry")
            {
                await f.ExecuteAsync($"INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES('{entry!.Value.ToString("D").ToUpperInvariant()}','{own.ToString("D").ToUpperInvariant()}',2,'protected producer','','2026-11-05T00:00:00.0000000Z',1)");
            }

            bool document = mode.StartsWith("document", StringComparison.Ordinal);

            byte[] bytes = document ? Encoding.ASCII.GetBytes("{\\rtf1\\ansi source fact}") : "source fact"u8.ToArray();

            var record = await store.PersistNewAsync(own, null, entry, "a-source", document ? "source.rtf" : "source.txt", bytes,
                document ? "application/rtf" : "text/plain", document ? SessionAttachmentKind.Binary : SessionAttachmentKind.Text);

            var independent = await store.PersistNewAsync(own, null, null, "z-independent", "independent.txt", "independent"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

            await SeedIndexedSourceAsync(connection, record, "source-chunk", [1f, 0f]);

            await SeedIndexedSourceAsync(connection, independent, "independent-chunk", [0.8f, 0.6f]);

            if (document)
            {
                var extracted = await SessionAttachmentTextExtractor.ExtractDocumentAsync(store, record, settings, 4096, default);

                Assert.Equal(SessionAttachmentExtractionStatus.Extracted, extracted.Status);

                Assert.Contains("source fact", extracted.Text, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(bytes, (await store.ReadBytesAsync(record)).ToArray());
            }

            var provenance = new AttachmentMemoryProvenance(own, record.Id, record.LogicalKey, record.Version,
                record.ContentSha256, DateTimeOffset.UtcNow, AttachmentSourceKind.SnapshotOnly.ToString(), AttachmentSourceAvailability.Available);

            using var turn = AttachmentMemoryGateAmbient.BeginTurn(own);

            turn.RegisterMaterialized(provenance);

            if (mode == "protected-entry")
            {
                var ledger = new ArtifactSensitivityLedger(new FixedCovenantConnectionSource(connection));

                var label = await ledger.LabelAsync(new DerivedArtifactWrite(SensitiveArtifactKind.AssistantEntry, entry!.Value,
                    own, campaign, null, 1, new CovenantDigest(SHA256.HashData("protected producer"u8)),
                    ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([Guid.NewGuid()])), default);

                Assert.True(label.IsSuccess, label.Error.Message);
            }
            else if (mode == "workspace-lineage")
            {
                await f.ExecuteAsync($"UPDATE SessionAttachments SET SourceKind='WorkspaceFile',SourceStatus='WorkspaceUnavailable' WHERE Id='{record.Id.ToString("D").ToUpperInvariant()}'");
            }
            else if (mode == "document-disabled")
            {
                settings.Features.Comprehension = false;
            }
            else if (mode == "document-retrieval-disabled")
            {
                settings.Features.AttachmentRetrieval = false;
            }
            else if (mode == "pending-source")
            {
                await f.ExecuteAsync($"UPDATE SessionAttachments SET State='Pending' WHERE Id='{record.Id.ToString("D").ToUpperInvariant()}'");
            }
            else if (mode == "wrong-chunk-version")
            {
                await f.ExecuteAsync("UPDATE session_attachment_chunks SET Version=2 WHERE ChunkId='source-chunk'");
            }
            else if (mode == "wrong-chunk-hash")
            {
                await f.ExecuteAsync("UPDATE session_attachment_chunks SET ContentSha256='incorrect-digest' WHERE ChunkId='source-chunk'");
            }
            else if (mode == "wrong-chunk-mime")
            {
                await f.ExecuteAsync("UPDATE session_attachment_chunks SET MimeType='application/pdf' WHERE ChunkId='source-chunk'");
            }
            else if (mode == "unpublished-chunk")
            {
                await f.ExecuteAsync($"UPDATE session_attachment_index_state SET PublishedGenerationId='other' WHERE AttachmentId='{record.Id.ToString("D").ToUpperInvariant()}'");
            }

            using var invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

            var search = await new DivinationService(db, NullLogger<DivinationService>.Instance, new TestOptionsMonitor<ArcanumSettings>(settings))
                .SearchScopedAsync("session_attachment_embeddings", "ChunkId", "Embedding", "session_attachment_chunks", "ChunkId",
                    "RetrievalScope", own.ToString(), new Embedding<float>(new float[] { 1f, 0f }), 1, 0f, default);

            Assert.True(search.IsSuccess, search.Error.Message);

            Assert.Equal(candidateAllowed ? "source-chunk" : "independent-chunk", Assert.Single(search.Value).Id);

            if (mode is "protected-entry" or "workspace-lineage" or "missing-entry" or "pending-source")
            {
                Assert.Null(await store.GetByIdAsync(record.Id));

                Assert.Equal("z-independent", Assert.Single(await store.BuildIndexAsync(own, 1)).LogicalKey);

                await Assert.ThrowsAsync<GuildMemoryAccessException>(() => store.ReadBytesAsync(record));
            }

            var lexicon = new LexiconService(db, NullLogger<LexiconService>.Instance,
                new TestOptionsMonitor<ArcanumSettings>(settings), MemoryErasureTestKeys.Isolated());

            var result = await lexicon.UpsertAsync("SourceFact", null, ["source fact"], provenance, LexiconScope.ForCampaign(campaign));

            Assert.Equal(allowed, result.IsSuccess);

            Assert.Equal(allowed ? 1L : 0L, await f.ScalarAsync("SELECT count(*) FROM lexicon_entries"));

            Assert.Equal(allowed ? 1L : 0L, await f.ScalarAsync("SELECT count(*) FROM guild_memory_attribution"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task SeedIndexedSourceAsync(SqliteConnection connection, SessionAttachmentRecord source, string chunkId, float[] vector)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO session_attachment_chunks(ChunkId,SessionId,AttachmentId,LogicalKey,Version,OriginalFileName,
                MimeType,ContentSha256,ChunkIndex,CharacterStart,CharacterEnd,StartLine,EndLine,Content,EmbeddingDimension,
                ExtractedAt,IndexedAt,RetrievalScope)
            VALUES($chunk,$session,$attachment,$key,$version,$name,$mime,$hash,0,0,4,1,1,'fact',2,
                '2026-11-05T00:00:00.0000000Z','2026-11-05T00:00:00.0000000Z',$session);
            INSERT INTO session_attachment_embeddings(ChunkId,Embedding,Dim) VALUES($chunk,$vector,2);
            INSERT INTO session_attachment_index_state(AttachmentId,Status,ContentSha256,PublishedGenerationId,UpdatedAt)
            VALUES($attachment,'Indexed',$hash,'legacy','2026-11-05T00:00:00.0000000Z');
            """;

        command.Parameters.AddWithValue("$chunk", chunkId);

        command.Parameters.AddWithValue("$session", source.SessionId!.Value.ToString());

        command.Parameters.AddWithValue("$attachment", source.Id.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$key", source.LogicalKey);

        command.Parameters.AddWithValue("$version", source.Version);

        command.Parameters.AddWithValue("$name", source.OriginalFileName);

        command.Parameters.AddWithValue("$mime", source.MimeType);

        command.Parameters.AddWithValue("$hash", source.ContentSha256);

        command.Parameters.AddWithValue("$vector", EmbeddingBlobCodec.Encode(vector));

        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedFileEncryptionKeyProvider : IFileEncryptionKeyProvider
    {
        private readonly FileEncryptionKeyMaterial _material = FileEncryptionKeyMaterial.Create(new byte[32]);

        public ValueTask<FileEncryptionKeyMaterial> GetForWriteAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_material);

        public ValueTask<FileEncryptionKeyMaterial> GetForReadAsync(string keyId, CancellationToken cancellationToken = default) =>
            keyId == _material.KeyId ? ValueTask.FromResult(_material) : throw new EncryptedBlobKeyException("test key unavailable");
    }
}
