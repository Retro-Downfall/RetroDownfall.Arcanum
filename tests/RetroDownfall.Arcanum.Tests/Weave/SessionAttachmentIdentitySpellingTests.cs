using System.Text;

using Microsoft.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore.Storage;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Lexicon;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Infrastructure.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// The behavioural contract for the attachment family: every writer of one attachment identity, driven
/// through its own production entry point against a real Grimoire, and the provenance reads that decide
/// whether an attachment-derived memory or fact can still name its source.
/// </summary>
/// <remarks>
/// This is the third case <see cref="RetroDownfall.Arcanum.Tests.Covenant.IdentitySpellingContractTests"/>
/// deferred to the change that also carries the data migration, and it is a separate suite because the
/// two need different substrates: that one drives a backup import into a KDF-sidecar Grimoire, this one
/// needs the object-relational context the attachment store, the index repository, the Saga store and
/// the Lexicon service all share.
///
/// <para><b>Why every case ends in a read rather than in a column comparison.</b> Writers agreeing on a
/// spelling is not the property that matters; the property that matters is that the joins between them
/// resolve. Some of those joins have no foreign key to make a disagreement loud - a consultation, a
/// Saga memory and a Lexicon fact each report their source unavailable and carry on - so each is
/// asserted through the reader that production actually asks, not through the text of the column.</para>
///
/// <para><b>What is asserted to stay in the minority form.</b> A chunk's own <c>SessionId</c> and its
/// <c>RetrievalScope</c> hold a Session identity the way the indexer wrote it, because the tapestry
/// reads that column as its live scope-id set and moving it would orphan every attachment-scoped
/// generation. Those two are pinned here as deliberately-not-canonical so that a later sweep of this
/// family has to argue with a failing test rather than with a comment.</para>
/// </remarks>
[Collection("Grimoire")]

[Trait("Category", "Integration")]

public sealed class SessionAttachmentIdentitySpellingTests : IAsyncLifetime
{

    private const int Dimensions = 64;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _attachmentsRoot = string.Empty;

    private ArcanumDbContext? _db;

    private ArcanumSettings _settings = null!;

    private SessionAttachmentStore? _attachments;

    private SessionAttachmentIndexRepository? _index;

    public SessionAttachmentIdentitySpellingTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _attachmentsRoot = Path.Combine(
            Path.GetTempPath(),
            "arcanum-attachment-spelling-" + Guid.NewGuid().ToString("N"));

        _ = Directory.CreateDirectory(_attachmentsRoot);

        _db = _fixture.CreateContext(_dbPath);

        _settings = new ArcanumSettings
        {

            Features = new FeatureSettings
            {

                AttachmentRetrieval = true,

            },

            Integrations = new IntegrationSettings
            {

                Embeddings = new EmbeddingIntegrationSettings
                {

                    Dimensions = Dimensions,

                },

            },

        };

        _attachments = new SessionAttachmentStore(
            _db,
            Options.Create(_settings),
            _attachmentsRoot,
            TestEncryptedBlobStore.Create());

        _index = new SessionAttachmentIndexRepository(_db, new WeaveIndexAvailability());

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

        if (Directory.Exists(_attachmentsRoot))
        {

            Directory.Delete(_attachmentsRoot, recursive: true);

        }

    }

    /// <summary>
    /// Persisting and indexing an attachment through the shipped entry points leaves the parent, both
    /// foreign-key children and the indexing read path agreeing on the canonical spelling.
    /// </summary>
    /// <remarks>
    /// <c>GetStatusesAsync</c> is asserted by name because it is the one attachment predicate in the
    /// index repository that binds its identity through a shared list-parameter helper rather than at
    /// the call site. A conversion that missed it would leave the column canonical and this lookup
    /// empty, which is a working feature reporting nothing rather than an error.
    /// </remarks>
    [SkippableFact]
    public async Task An_indexed_attachment_holds_one_spelling_from_its_row_to_its_chunks()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "notes");

        await IndexAsync(attachment, sessionId);

        Assert.Equal(
            Canonical(attachment.Id),
            await ScalarStringAsync("SELECT \"Id\" FROM \"SessionAttachments\" WHERE \"LogicalKey\" = 'notes'"));

        Assert.Equal(
            Canonical(sessionId),
            await ScalarStringAsync("SELECT \"SessionId\" FROM \"SessionAttachments\" WHERE \"LogicalKey\" = 'notes'"));

        Assert.Equal(
            Canonical(attachment.Id),
            await ScalarStringAsync("SELECT AttachmentId FROM session_attachment_chunks LIMIT 1"));

        Assert.Equal(
            Canonical(attachment.Id),
            await ScalarStringAsync("SELECT AttachmentId FROM session_attachment_index_state"));

        Assert.NotEmpty(await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None));

        Assert.Equal(
            SessionAttachmentIndexStatus.Indexed,
            Assert.Contains(
                attachment.Id,
                await _index.GetStatusesAsync([attachment.Id], CancellationToken.None)));

        Assert.Equal(
            Legacy(sessionId),
            await ScalarStringAsync("SELECT SessionId FROM session_attachment_chunks LIMIT 1"));

        Assert.Equal(
            Legacy(sessionId),
            await ScalarStringAsync("SELECT RetrievalScope FROM session_attachment_chunks LIMIT 1"));

    }

    /// <summary>
    /// The attachment index purge removes a staged generation's chunks, which it can reach only by
    /// asking <c>SessionAttachments</c> which attachments belong to the Session.
    /// </summary>
    /// <remarks>
    /// This is what pins the split parameter, and a staged generation is the only state that can see the
    /// difference. A published chunk carries the Session identity in its own column and is deleted by
    /// the first arm of that <c>OR</c> whatever the second arm does; a staged one carries
    /// <see cref="Guid.Empty"/> there until publication, so only the second arm reaches it - and that arm
    /// compares a canonical <c>SessionAttachments.SessionId</c> while the first compares a chunk column
    /// that stays minority-spelled. One parameter served both before this change, and because the two
    /// predicates are joined by <c>OR</c> the failure was a silent under-delete rather than anything that
    /// raised.
    ///
    /// <para><b>Why the purge is entered here rather than through <c>PurgeSessionAsync</c>.</b> That
    /// caller also deletes the attachment rows, and <c>session_attachment_chunks.AttachmentId</c> carries
    /// <c>ON DELETE CASCADE</c>, so the chunks disappear either way and the under-delete is invisible
    /// from there. Entering at the index maintenance port is what isolates the predicate this case is
    /// about; the cascade is a second mechanism, not a reason the first one may be wrong.</para>
    /// </remarks>
    [SkippableFact]
    public async Task The_index_purge_removes_a_staged_generation_it_can_only_reach_through_its_attachment()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, _) = await SeedSessionAndEntryAsync();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "staged");

        await StageOneUnpublishedChunkAsync(attachment);

        Assert.Equal(
            Guid.Empty.ToString("D").ToLowerInvariant(),
            await ScalarStringAsync("SELECT SessionId FROM session_attachment_chunks LIMIT 1"));

        await using (IDbContextTransaction transaction = await _db!.Database
            .BeginTransactionAsync(CancellationToken.None))
        {

            await _index!.DeleteForSessionInAmbientTransactionAsync(sessionId, CancellationToken.None);

            await transaction.CommitAsync(CancellationToken.None);

        }

        Assert.Equal(0, await ScalarIntAsync("SELECT COUNT(*) FROM session_attachment_chunks"));

        Assert.Equal(1, await ScalarIntAsync("SELECT COUNT(*) FROM \"SessionAttachments\""));

    }

    /// <summary>
    /// A binding that moves an attachment onto its Entry writes the Entry reference in the spelling the
    /// object-relational writer gave that Entry, so the two join.
    /// </summary>
    [SkippableFact]
    public async Task An_attachment_bound_to_an_entry_names_it_in_the_spelling_the_entry_holds()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid entryId) = await SeedSessionAndEntryAsync();

        string pendingTurnId = Guid.NewGuid().ToString("N");

        _ = await _attachments!.PersistNewAsync(
            null,
            pendingTurnId,
            null,
            "pending",
            "pending.txt",
            Encoding.UTF8.GetBytes("pending content"),
            "text/plain",
            SessionAttachmentKind.Text);

        await _attachments.PromotePendingAsync(pendingTurnId, sessionId, entryId);

        Assert.Equal(
            Canonical(entryId),
            await ScalarStringAsync(
                "SELECT \"EntryId\" FROM \"SessionAttachments\" WHERE \"LogicalKey\" = 'pending'"));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                """
                SELECT COUNT(*) FROM "SessionAttachments" a
                JOIN "Entries" e ON e."Id" = a."EntryId";
                """));

    }

    /// <summary>
    /// A fork cut off at an Entry still copies the attachment bound to that Entry, over an exact
    /// comparison rather than a case-insensitive one.
    /// </summary>
    /// <remarks>
    /// This is the arm the fork reader takes whenever a caller names an Entry to cut at
    /// (<c>includeEntrylessAttachments: cutoffEntry is null</c>), and the only one that compares
    /// <c>SessionAttachments."EntryId"</c> against <c>Entries."Id"</c> at all. It carried
    /// <c>COLLATE NOCASE</c> on both of its comparisons, which was the only thing that let it work while
    /// the attachment table held one spelling and <c>Entries</c> held another - and which forfeited the
    /// index behind <c>Entries</c>' primary key, once per attachment row, on a request path. Both sides
    /// now hold the canonical form, so the comparison is exact and this case is what says the exactness
    /// is correct rather than merely faster.
    /// </remarks>
    [SkippableFact]
    public async Task A_fork_cut_off_at_an_entry_still_copies_the_attachment_bound_to_it()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository sessions = new(
            _db!,
            _attachments!,
            new TestOptionsMonitor<ArcanumSettings>(_settings),
            FixtureOrdinaryConnectionFactory.For(_db!),
            new NullAttachmentIndexQueue());

        Session source = await sessions.CreateAsync(
            campaignId: null,
            title: "Fork source",
            CancellationToken.None);

        Entry cutoff = new()
        {

            Id = Guid.NewGuid(),

            SessionId = source.Id,

            Role = MessageRole.Assistant,

            Content = "the turn the fork cuts at",

            ModelUsed = "test-model",

            CreatedAt = DateTimeOffset.UtcNow,

            Sequence = 1,

        };

        _db!.Entries.Add(cutoff);

        _ = await _db.SaveChangesAsync();

        string pendingTurnId = Guid.NewGuid().ToString("N");

        _ = await _attachments!.PersistNewAsync(
            null,
            pendingTurnId,
            null,
            "bound",
            "bound.txt",
            Encoding.UTF8.GetBytes("fork me"),
            "text/plain",
            SessionAttachmentKind.Text);

        await _attachments.PromotePendingAsync(pendingTurnId, source.Id, cutoff.Id);

        Result<Session> forked = await sessions.ForkAsync(
            source.Id,
            new ForkSessionRequest(UpToEntryId: cutoff.Id),
            CancellationToken.None);

        Assert.True(forked.IsSuccess);

        SessionAttachmentRecord copied = Assert.Single(
            await _attachments.ListBoundAsync(forked.Value.Id, CancellationToken.None));

        Assert.Equal("bound", copied.LogicalKey);

        Assert.Equal(
            Canonical(forked.Value.Id),
            await ScalarStringAsync(
                "SELECT \"SessionId\" FROM \"SessionAttachments\" WHERE \"Id\" = '"
                + Canonical(copied.Id)
                + "'"));

    }

    /// <summary>
    /// A consultation recorded against a live attachment reports its source as available, asked through
    /// the reader the inference pipeline asks.
    /// </summary>
    [SkippableFact]
    public async Task A_recorded_consultation_still_finds_the_attachment_it_names()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid entryId) = await SeedSessionAndEntryAsync();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "consulted");

        AttachmentMemoryProvenanceStore store = new(_db!);

        await store.RecordConsultationsAsync(
            entryId,
            [Provenance(sessionId, attachment.Id)],
            CancellationToken.None);

        Assert.Equal(
            Canonical(attachment.Id),
            await ScalarStringAsync("SELECT AttachmentId FROM attachment_memory_consultations"));

        AttachmentMemoryProvenance reloaded = Assert.Single(
            await store.ListConsultationsAsync(
                sessionId,
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1),
                CancellationToken.None));

        Assert.Equal(AttachmentSourceAvailability.Available, reloaded.Availability);

    }

    /// <summary>
    /// A Saga memory written with attachment provenance still names a live attachment when it is read
    /// back, asked through the store's own listing.
    /// </summary>
    [SkippableFact]
    public async Task A_saga_memory_written_with_provenance_still_finds_its_attachment()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, _) = await SeedSessionAndEntryAsync();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "remembered");

        SagaMemoryStore memories = new(
            _db!,
            new WeaveIndexAvailability(),
            new TestOptionsMonitor<ArcanumSettings>(_settings));

        _ = await memories.InsertAsync(
            Guid.NewGuid().ToString(),
            "the attachment said so",
            DateTimeOffset.UtcNow,
            sessionId,
            null,
            "attachment",
            new float[Dimensions],
            Provenance(sessionId, attachment.Id),
            CancellationToken.None);

        Assert.Equal(
            Canonical(attachment.Id),
            await ScalarStringAsync("SELECT AttachmentId FROM saga_memory_attachment_provenance"));

        SagaMemoryDto memory = Assert.Single(
            await memories.ListAsync(null, sessionId, MemoryScope.Installation, 10, 0, CancellationToken.None));

        Assert.Equal(
            AttachmentSourceAvailability.Available,
            Assert.IsType<AttachmentMemoryProvenance>(memory.AttachmentProvenance).Availability);

    }

    /// <summary>
    /// A Lexicon fact written with attachment provenance still names a live attachment when the entity
    /// is read back by name.
    /// </summary>
    [SkippableFact]
    public async Task A_lexicon_fact_written_with_provenance_still_finds_its_attachment()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, _) = await SeedSessionAndEntryAsync();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "described");

        LexiconService lexicon = new(
            _db!,
            NullLogger<LexiconService>.Instance,
            new TestOptionsMonitor<ArcanumSettings>(_settings));

        Result<LexiconEntryDto> written = await lexicon.UpsertAsync(
            "Alpha",
            "concept",
            ["Alpha is described in the attachment."],
            Provenance(sessionId, attachment.Id),
            LexiconScope.Global,
            CancellationToken.None);

        Assert.True(written.IsSuccess);

        Assert.Equal(
            Canonical(attachment.Id),
            await ScalarStringAsync("SELECT AttachmentId FROM lexicon_fact_attachment_provenance"));

        Result<LexiconEntryDto?> read = await lexicon.GetByNameAsync(
            "Alpha",
            LexiconScope.Global,
            CancellationToken.None);

        Assert.True(read.IsSuccess);

        LexiconEntryDto entry = Assert.IsType<LexiconEntryDto>(read.Value);

        LexiconFactProvenance fact = Assert.Single(
            Assert.IsType<LexiconFactProvenance[]>(entry.FactProvenance));

        Assert.Equal(AttachmentSourceAvailability.Available, fact.Source.Availability);

    }

    /// <summary>The canonical spelling: uppercase, dashed, 36 characters, as the provider renders it.</summary>
    private static string Canonical(Guid identity) => identity.ToString("D").ToUpperInvariant();

    /// <summary>
    /// The minority spelling, which two chunk columns keep on purpose and which nothing else may hold.
    /// </summary>
    private static string Legacy(Guid identity) => identity.ToString("D").ToLowerInvariant();

    private static AttachmentMemoryProvenance Provenance(Guid sessionId, Guid attachmentId) =>
        new(
            sessionId,
            attachmentId,
            "notes",
            1,
            "content-hash",
            DateTimeOffset.UtcNow,
            "WorkspaceFile",
            AttachmentSourceAvailability.Available);

    /// <summary>
    /// A Session and an assistant Entry written through the object-relational writer, which is the only
    /// thing that has ever created either and therefore the only spelling an installation holds.
    /// </summary>
    private async Task<(Guid SessionId, Guid EntryId)> SeedSessionAndEntryAsync()
    {

        Session session = new()
        {

            Id = Guid.NewGuid(),

            Status = "active",

            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),

            UpdatedAt = DateTimeOffset.UtcNow,

        };

        Entry entry = new()
        {

            Id = Guid.NewGuid(),

            SessionId = session.Id,

            Role = MessageRole.Assistant,

            Content = "Done",

            ModelUsed = "test-model",

            CreatedAt = DateTimeOffset.UtcNow,

            Sequence = 1,

        };

        _db!.Sessions.Add(session);

        _db.Entries.Add(entry);

        _ = await _db.SaveChangesAsync();

        return (session.Id, entry.Id);

    }

    private Task<SessionAttachmentRecord> PersistAsync(Guid sessionId, string logicalKey) =>
        _attachments!.PersistNewAsync(
            sessionId,
            null,
            null,
            logicalKey,
            logicalKey + ".txt",
            Encoding.UTF8.GetBytes("alpha\nbeta\ngamma"),
            "text/plain",
            SessionAttachmentKind.Text);

    /// <summary>
    /// Writes one chunk of a generation and stops before publication, which is the checkpointed state
    /// the indexer leaves behind whenever a batch is interrupted.
    /// </summary>
    private async Task StageOneUnpublishedChunkAsync(SessionAttachmentRecord attachment)
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _index!.SetPendingAsync(attachment, 1, CancellationToken.None);

        SessionAttachmentIndexCheckpoint checkpoint = await _index.BeginReplaceAsync(
            attachment,
            Dimensions,
            "test-pipeline",
            now,
            CancellationToken.None);

        float[] vector = new float[Dimensions];

        vector[0] = 1f;

        await _index.AppendReplaceBatchAsync(
            attachment,
            checkpoint.GenerationId,
            [new SessionAttachmentTextChunk(0, 0, 5, 1, 1, "alpha")],
            [new Embedding<float>(vector)],
            Dimensions,
            now,
            now,
            CancellationToken.None);

    }

    private async Task IndexAsync(SessionAttachmentRecord attachment, Guid sessionId)
    {

        SessionAttachmentIndexProcessor processor = new(
            new TestOptionsMonitor<ArcanumSettings>(_settings),
            new ConstantWeaveService(),
            _attachments!,
            _index!,
            NullLogger<SessionAttachmentIndexProcessor>.Instance);

        SessionAttachmentIndexOutcome outcome = await processor.ProcessAsync(
            new SessionAttachmentIndexRequest(attachment.Id, sessionId),
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, outcome.Status);

    }

    private async Task<string?> ScalarStringAsync(string sql)
    {

        await using System.Data.Common.DbCommand command = _db!.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(CancellationToken.None);

        return value is null or DBNull ? null : (string)value;

    }

    private async Task<int> ScalarIntAsync(string sql)
    {

        await using System.Data.Common.DbCommand command = _db!.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            System.Globalization.CultureInfo.InvariantCulture);

    }

    /// <summary>An index queue that drops what it is handed, because no case here is about indexing.</summary>
    private sealed class NullAttachmentIndexQueue : ISessionAttachmentIndexQueue
    {

        public bool TryEnqueue(SessionAttachmentIndexRequest request) => true;

    }

    /// <summary>An embedder that answers instantly, because none of these cases is about embeddings.</summary>
    private sealed class ConstantWeaveService : IWeaveService
    {

        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(
            string text,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(Vector())));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<Embedding<float>[]>.Success(
                    [.. texts.Select(static _ => new Embedding<float>(Vector()))]));

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(
            string text,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static float[] Vector()
        {

            float[] vector = new float[Dimensions];

            vector[0] = 1f;

            return vector;

        }

    }

}
