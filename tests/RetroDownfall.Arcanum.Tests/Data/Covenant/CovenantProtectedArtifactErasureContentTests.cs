using System.Data.Common;
using System.Globalization;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// What a Covenant erasure actually removes, over content the production writers wrote.
/// </summary>
/// <remarks>
/// Deliberately not a fixture that inserts its own content rows. The identity a purge has to match is
/// chosen by whichever writer owns the kind, and those writers do not agree: Saga extraction writes a
/// lowercase 36-character identity, the Lexicon and the idempotency claim store write 32 lowercase hex
/// characters with no dashes, and a backup import writes a lowercase identity into a column the
/// object-relational writer fills with an uppercase one. A fixture that seeds a content row in one
/// chosen spelling and then asserts the purge found it is asserting on its own choice, and a purge
/// that matched nothing at all passed exactly that test for as long as it existed.
///
/// <para>Every case here therefore drives the real writer, reads the identity back out of the row that
/// writer created, labels it through the one production ledger, and enters the kernel at
/// <see cref="CovenantProtectedArtifactErasureKernel.ErasePageAsync"/>.</para>
/// </remarks>
public sealed class CovenantProtectedArtifactErasureContentTests
{

    /// <summary>
    /// Matches <c>ArcanumSettingClamps.EmbeddingsDimensions</c>'s floor, so the Saga store's own
    /// dimension guard accepts what the fake embedder produces.
    /// </summary>
    private const int Dimensions = 64;

    private static CancellationToken Token => CancellationToken.None;

    [SkippableFact]
    public async Task A_saga_memory_written_by_extraction_is_erased_with_its_embedding()
    {

        await using ErasureHarness harness = ErasureHarness.Create();

        Guid memoryId = await harness.ExtractOneSagaMemoryAsync("The operator prefers dark mode.");

        await harness.LabelAsync(SensitiveArtifactKind.Saga, memoryId, sessionId: null, "The operator prefers dark mode.");

        CovenantArtifactErasureProgress progress = await harness.EraseAsync(SensitiveArtifactKind.Saga, memoryId);

        Assert.Equal(1UL, progress.ErasedCount);

        Assert.Equal(CovenantErasureBlocker.None, progress.Blocker);

        Assert.Equal(0, await harness.CountAsync("SELECT COUNT(*) FROM saga_memories;"));

        Assert.Equal(0, await harness.CountAsync("SELECT COUNT(*) FROM saga_memory_embeddings;"));

        Assert.Equal(0, await harness.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

    }

    /// <summary>
    /// The vector mirror holds the embedding itself, so an erasure that stopped at the BLOB table
    /// would leave the same content reachable through the acceleration path.
    /// </summary>
    /// <remarks>
    /// The mirror is created here before the write, because no schema file installs it — it exists
    /// only where the accelerator built it — and the store then fills it through its own production
    /// write path rather than through anything this test inserts. That is the same arrangement the
    /// retention suites use to exercise the pruner's mirror deletes on a build that ships no
    /// accelerator.
    /// </remarks>
    [SkippableFact]
    public async Task The_saga_vector_mirror_is_erased_with_the_memory_whose_embedding_it_holds()
    {

        await using ErasureHarness harness = ErasureHarness.Create();

        await harness.CreateVectorMirrorAsync("saga_memory_embeddings_vec", "MemoryId");

        harness.VectorAccelerator.SetAvailable(true, "Test mirror present.");

        Guid memoryId = await harness.ExtractOneSagaMemoryAsync("The operator prefers dark mode.");

        // The production writer filled the mirror, not this test. An assertion that the erasure
        // emptied a table nothing had put a row in would pass on a build where the write never ran.
        Assert.Equal(1, await harness.CountAsync("SELECT COUNT(*) FROM saga_memory_embeddings_vec;"));

        await harness.LabelAsync(SensitiveArtifactKind.Saga, memoryId, sessionId: null, "The operator prefers dark mode.");

        CovenantArtifactErasureProgress progress = await harness.EraseAsync(SensitiveArtifactKind.Saga, memoryId);

        Assert.Equal(1UL, progress.ErasedCount);

        Assert.Equal(0, await harness.CountAsync("SELECT COUNT(*) FROM saga_memory_embeddings_vec;"));

    }

    /// <summary>
    /// The mirror's absence is the ordinary case on a build with no accelerator, and it has to be a
    /// skipped statement rather than a failed transaction.
    /// </summary>
    [SkippableFact]
    public async Task A_missing_vector_mirror_leaves_the_erasure_unblocked()
    {

        await using ErasureHarness harness = ErasureHarness.Create();

        Guid memoryId = await harness.ExtractOneSagaMemoryAsync("The operator prefers dark mode.");

        Assert.Equal(0, await harness.CountAsync("SELECT COUNT(*) FROM sqlite_master WHERE name = 'saga_memory_embeddings_vec';"));

        await harness.LabelAsync(SensitiveArtifactKind.Saga, memoryId, sessionId: null, "The operator prefers dark mode.");

        CovenantArtifactErasureProgress progress = await harness.EraseAsync(SensitiveArtifactKind.Saga, memoryId);

        Assert.Equal(CovenantErasureBlocker.None, progress.Blocker);

        Assert.Equal(1UL, progress.ErasedCount);

        Assert.Equal(0, await harness.CountAsync("SELECT COUNT(*) FROM saga_memories;"));

    }

    [SkippableFact]
    public async Task A_lexicon_entry_written_by_the_lexicon_service_is_erased()
    {

        await using ErasureHarness harness = ErasureHarness.Create();

        LexiconEntryDto entry = await harness.UpsertLexiconEntryAsync("Nimue", "The lake keeps her counsel.");

        await harness.LabelAsync(SensitiveArtifactKind.Lexicon, entry.Id, sessionId: null, entry.Name);

        CovenantArtifactErasureProgress progress = await harness.EraseAsync(SensitiveArtifactKind.Lexicon, entry.Id);

        Assert.Equal(1UL, progress.ErasedCount);

        Assert.Equal(CovenantErasureBlocker.None, progress.Blocker);

        Assert.Equal(0, await harness.CountAsync("SELECT COUNT(*) FROM lexicon_entries;"));

    }

    /// <summary>
    /// The claim row is preserved on purpose and only its cached body is removed, so both halves are
    /// asserted: a purge that deleted the row would take the only thing that can still answer a
    /// replay with a typed denial.
    /// </summary>
    [SkippableFact]
    public async Task A_completed_idempotency_claims_cached_body_is_redacted_and_its_row_kept()
    {

        await using ErasureHarness harness = ErasureHarness.Create();

        Guid claimId = await harness.CompleteOneIdempotencyClaimAsync("""{"answer":"protected"}""");

        // The precondition the redaction is about. A claim whose body was already null would satisfy
        // the assertion below whether or not the purge matched anything.
        Assert.Equal(
            1,
            await harness.CountAsync("""SELECT COUNT(*) FROM "IdempotencyClaims" WHERE "ResponseBody" IS NOT NULL;"""));

        await harness.LabelAsync(SensitiveArtifactKind.IdempotencyClaim, claimId, sessionId: null, "claim");

        CovenantArtifactErasureProgress progress = await harness.EraseAsync(
            SensitiveArtifactKind.IdempotencyClaim,
            claimId);

        Assert.Equal(1UL, progress.ErasedCount);

        Assert.Equal(1, await harness.CountAsync("""SELECT COUNT(*) FROM "IdempotencyClaims";"""));

        Assert.Equal(
            0,
            await harness.CountAsync("""SELECT COUNT(*) FROM "IdempotencyClaims" WHERE "ResponseBody" IS NOT NULL;"""));

    }

    /// <summary>
    /// One temporary Grimoire, the production writers that fill it, and the kernel reading the same
    /// connection they wrote through.
    /// </summary>
    private sealed class ErasureHarness : IAsyncDisposable
    {

        private readonly GrimoireFixture _fixture;

        private readonly ArcanumDbContext _db;

        private long _sequence;

        private ErasureHarness(GrimoireFixture fixture, ArcanumDbContext db)
        {

            _fixture = fixture;

            _db = db;

            CovenantConnectionDrain drain = new();

            RecordingScopedOrdinaryConnectionFactory connections = new();

            Ledger = new ArtifactSensitivityLedger(new CovenantConnectionSource(db, connections));

            Kernel = new CovenantProtectedArtifactErasureKernel(
                new CovenantConnectionSource(db, connections),
                CovenantSqliteConnectionInitializer.Instance,
                TimeProvider.System);

        }

        internal CovenantProtectedArtifactErasureKernel Kernel { get; }

        internal ArtifactSensitivityLedger Ledger { get; }

        internal WeaveIndexAvailability VectorAccelerator { get; } = new();

        private DbConnection Connection => _db.Database.GetDbConnection();

        internal static ErasureHarness Create()
        {

            Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

            GrimoireFixture fixture = new();

            return new ErasureHarness(fixture, fixture.CreateContext(fixture.CopyDatabase()));

        }

        /// <summary>
        /// Drives one real extraction pass and returns the identity it chose for the memory it wrote.
        /// </summary>
        internal async Task<Guid> ExtractOneSagaMemoryAsync(string conclusion)
        {

            Guid sessionId = Guid.NewGuid();

            _db.Sessions.Add(
                new Session
                {
                    Id = sessionId,
                    Status = "active",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });

            _db.Entries.Add(
                new Entry
                {
                    Id = Guid.NewGuid(),
                    SessionId = sessionId,
                    Role = MessageRole.User,
                    Content = "I like dark mode.",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Sequence = ++_sequence,
                });

            _ = await _db.SaveChangesAsync(Token);

            ArcanumSettings settings = new()
            {
                FastModel = "fast-test-model",
                Features = new FeatureSettings
                {
                    Embeddings = true,
                    Saga = true,
                    SagaExtraction = true,
                },
                Integrations = new IntegrationSettings
                {
                    Embeddings = new EmbeddingIntegrationSettings
                    {
                        Provider = "test",
                        Model = "test-embed",
                        Dimensions = Dimensions,
                    },
                },
            };

            EmbeddingSettings embeddings = ArcanumRuntimeDefaults.Embeddings with
            {
                Enabled = true,
                SagaEnabled = true,
                Provider = "test",
                Model = "test-embed",
                Dimensions = Dimensions,
                Saga = ArcanumRuntimeDefaults.Embeddings.Saga with { ExtractionEnabled = true },
            };

            ServiceCollection services = new();

            _ = services.AddSingleton(_db);

            _ = services.AddSingleton<IWeaveService>(new FixedEmbeddingWeaveService());

            _ = services.AddSingleton<IArcanumIntelligenceProvider>(
                new FixedConclusionIntelligenceProvider($$"""{ "memories": ["{{conclusion}}"] }"""));

            _ = services.AddSingleton<ISagaMemoryStore, SagaMemoryStore>();

            _ = services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
                new TestOptionsMonitor<ArcanumSettings>(settings));

            _ = services.AddSingleton(VectorAccelerator);

            _ = services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(
                new RecordingScopedOrdinaryConnectionFactory());

            _ = services.AddScoped<IGrimoireRepository>(
                sp => new GrimoireRepository(
                    _db,
                    new NoOpSessionAttachmentStore(),
                    NullLogger<GrimoireRepository>.Instance,
                    new TestOptionsSnapshot<ArcanumSettings>(settings),
                    attachmentIndex: null,
                    covenantKernel: null,
                    sp.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()));

            await using ServiceProvider provider = services.BuildServiceProvider();

            SagaExtractionService extraction = new(
                new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
                NullLogger<SagaExtractionService>.Instance);

            await using AsyncServiceScope scope = provider
                .GetRequiredService<IServiceScopeFactory>()
                .CreateAsyncScope();

            await extraction.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, Token);

            // Read back rather than remembered: the identity under test is the one the extraction path
            // chose, and a value this method had supplied would prove nothing about the spelling
            // production stores.
            return Guid.Parse(await ScalarStringAsync("SELECT Id FROM saga_memories;"), CultureInfo.InvariantCulture);

        }

        internal async Task<LexiconEntryDto> UpsertLexiconEntryAsync(string name, string fact)
        {

            LexiconService lexicon = new(
                _db,
                NullLogger<LexiconService>.Instance,
                new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

            Result<LexiconEntryDto> written = await lexicon.UpsertAsync(
                name,
                "Person",
                [fact],
                LexiconScope.Global,
                Token);

            Assert.True(written.IsSuccess);

            return written.Value;

        }

        internal async Task<Guid> CompleteOneIdempotencyClaimAsync(string responseBody)
        {

            IdempotencyClaimStore claims = new(_db);

            IdempotencyClaimAcquireResult acquired = await claims.TryAcquireAsync(
                new IdempotencyClaimAcquireRequest(
                    "claim-key-hash",
                    "fingerprint-hash",
                    "owner-1",
                    DateTimeOffset.UtcNow.AddMinutes(5),
                    DateTimeOffset.UtcNow),
                Token);

            Assert.True(acquired.Acquired);

            await claims.CompleteAsync(
                acquired.Claim.Id,
                "owner-1",
                200,
                "application/json",
                responseBody,
                terminalStreamValid: true,
                runId: null,
                Token);

            return acquired.Claim.Id;

        }

        /// <summary>
        /// Labels one artifact through the single production writer of <c>artifact_sensitivity</c>.
        /// </summary>
        internal async Task LabelAsync(
            SensitiveArtifactKind kind,
            Guid artifactId,
            Guid? sessionId,
            string content)
        {

            Result<LabeledArtifactWriteReceipt> receipt = await Ledger.LabelAsync(
                new DerivedArtifactWrite(
                    kind,
                    artifactId,
                    sessionId,
                    campaignId: null,
                    turnId: null,
                    artifactRevision: 1,
                    DerivedArtifactContentDigest.ForText(content),
                    ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([CovenantOperationGateFixture.DatasetGeneration])),
                Token);

            Assert.True(receipt.IsSuccess);

        }

        /// <summary>
        /// Erases one labelled artifact through the kernel's outermost entry point, under a real
        /// exclusive lease.
        /// </summary>
        /// <remarks>
        /// The page item is built from the label read back out of the ledger, exactly the way
        /// <c>CovenantSensitiveRetentionPurgeCoordinator</c> builds it. Constructing the expected
        /// label by hand would let this method describe an artifact the database does not hold.
        /// </remarks>
        internal async Task<CovenantArtifactErasureProgress> EraseAsync(
            SensitiveArtifactKind kind,
            Guid artifactId)
        {

            Result<ArtifactSensitivityLabel?> read = await Ledger.TryReadLabelAsync(kind, artifactId, Token);

            Assert.True(read.IsSuccess);

            ArtifactSensitivityLabel label = read.Value!;

            CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

            await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize),
                Token)).Value;

            CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority
                .ForExclusive(lease, CovenantExclusiveOperation.CovenantFamilyReinitialize)
                .Value;

            Result<CovenantArtifactErasureProgress> erased = await Kernel.ErasePageAsync(
                new CovenantProtectedArtifactErasurePage(
                    CovenantOperationGateFixture.DatasetGeneration,
                    [
                        new CovenantProtectedArtifactErasureItem(
                            label.ArtifactId,
                            label.ArtifactKind,
                            label.SessionId,
                            label.LabelId,
                            label,
                            label.ArtifactContentDigest,
                            label.ArtifactRevision),
                    ]),
                authority,
                Token);

            Assert.True(erased.IsSuccess);

            return erased.Value;

        }

        /// <summary>
        /// Creates one vector mirror the way an accelerator would, so a production write can fill it on
        /// a build that ships none.
        /// </summary>
        internal async Task CreateVectorMirrorAsync(string table, string keyColumn) =>
            await ExecuteAsync(
                $"""
                 CREATE TABLE "{table}" ("{keyColumn}" TEXT PRIMARY KEY, "Embedding" BLOB NOT NULL);
                 """);

        internal async Task ExecuteAsync(string sql)
        {

            await using DbCommand command = Connection.CreateCommand();

            command.CommandText = sql;

            _ = await command.ExecuteNonQueryAsync(Token);

        }

        internal async Task<long> CountAsync(string sql)
        {

            await using DbCommand command = Connection.CreateCommand();

            command.CommandText = sql;

            return Convert.ToInt64(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);

        }

        internal async Task<string> ScalarStringAsync(string sql)
        {

            await using DbCommand command = Connection.CreateCommand();

            command.CommandText = sql;

            return (string)(await command.ExecuteScalarAsync(Token))!;

        }

        public async ValueTask DisposeAsync()
        {

            await _db.DisposeAsync();

            _fixture.Dispose();

        }

    }

    /// <summary>An embedder that always succeeds, so extraction reaches its write.</summary>
    private sealed class FixedEmbeddingWeaveService : IWeaveService
    {

        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(new float[Dimensions])));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Saga extraction embeds one memory at a time.");

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(
            string text,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Saga extraction does not chunk.");

    }

    /// <summary>An extraction model that returns one fixed conclusion.</summary>
    private sealed class FixedConclusionIntelligenceProvider(string response) : IArcanumIntelligenceProvider
    {

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null) =>
            Task.FromResult(Result<PromptTurnResult>.Success(new PromptTurnResult(response, null)));

        public IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null) =>
            throw new NotSupportedException("Saga extraction does not stream.");

    }

}
