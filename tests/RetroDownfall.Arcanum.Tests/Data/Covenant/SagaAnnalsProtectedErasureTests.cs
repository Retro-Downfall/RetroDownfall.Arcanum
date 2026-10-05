using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

using Xunit.Sdk;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// A labelled Saga memory whose claim carries every kind of Annals row, erased by both protected
/// purges, leaves nothing of that claim behind.
/// </summary>
/// <remarks>
/// The claim graph is built through the production writers wherever one exists: the store's insert
/// opens each claim, its correction appends the second version and the <c>Supersedes</c> edge, and
/// the review service's Confirm writes the decision receipt. The head triggers write the review events
/// on their own. The one row written directly is the cross-claim <c>DerivedFrom</c> edge, because no
/// production writer emits an edge between two claims yet, and an erasure has to take an edge whichever
/// end of it is going.
///
/// <para>A second, unrelated memory with a claim of its own is there so that an erasure over-reaching
/// into the other claim shows up as a count, rather than passing because it emptied everything.</para>
/// </remarks>
public sealed class SagaAnnalsProtectedErasureTests
{
    private static readonly DateTimeOffset Created =
        DateTimeOffset.Parse("2026-09-28T12:00:00Z", CultureInfo.InvariantCulture);

    private static CancellationToken Token => CancellationToken.None;

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_labelled_saga_memory_with_a_full_annals_graph_leaves_no_claim_row_behind(bool staged)
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        Guid target = Guid.NewGuid();

        Guid other = Guid.NewGuid();

        await InsertAsync(harness, target, "the operator prefers tabs", Created);

        SagaCurationOutcome corrected = await harness.Store.CorrectAsync(
            target.ToString(),
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            "the operator prefers spaces",
            harness.Embedding(2),
            Created.AddMinutes(1),
            Token);

        Assert.Equal(SagaCurationOutcomeKind.Applied, corrected.Kind);

        await InsertAsync(harness, other, "the operator reads the changelog", Created.AddMinutes(2));

        await ExecuteAsync(
            harness,
            """
            INSERT INTO annal_dependencies (
                DependentVersionId, DependentSequence, DependencyVersionId, DependencySequence,
                RelationCode, Ordinal, CreatedAtUtc)
            SELECT dependent.VersionId, dependent.Sequence, dependency.VersionId, dependency.Sequence,
                   2, 1, dependent.RecordedAtUtc
            FROM annal_versions AS dependent
            JOIN annal_claims AS dependentClaim ON dependentClaim.ClaimId = dependent.ClaimId
            JOIN annal_versions AS dependency
            JOIN annal_claims AS dependencyClaim ON dependencyClaim.ClaimId = dependency.ClaimId
            WHERE dependentClaim.SubjectId = $other AND dependent.Revision = 1
                AND dependencyClaim.SubjectId = $target AND dependency.Revision = 1;
            """,
            ("$other", other.ToString()),
            ("$target", target.ToString()));

        await ConfirmHeadAsync(harness, target);

        await harness.LabelSensitiveAsync(target);

        Assert.Equal(2, await harness.CountAsync("annal_claims", "1 = 1"));

        Assert.Equal(3, await harness.CountAsync("annal_versions", "1 = 1"));

        Assert.Equal(2, await harness.CountAsync("annal_heads", "1 = 1"));

        Assert.Equal(2, await harness.CountAsync("annal_dependencies", "1 = 1"));

        Assert.Equal(3, await harness.CountAsync("annal_review_events", "1 = 1"));

        Assert.Equal(1, await harness.CountAsync("annal_review_decision_receipts", "1 = 1"));

        Assert.Equal(1, await harness.CountAsync("artifact_sensitivity", "1 = 1"));

        if (staged)
        {
            await PurgeStagedAsync(harness);
        }
        else
        {
            await EraseLiveAsync(harness, target);
        }

        (int claims, int versions, int heads, int edges, int reviewEvents, int receipts) = (
            await harness.CountAsync("annal_claims", "1 = 1"),
            await harness.CountAsync("annal_versions", "1 = 1"),
            await harness.CountAsync("annal_heads", "1 = 1"),
            await harness.CountAsync("annal_dependencies", "1 = 1"),
            await harness.CountAsync("annal_review_events", "1 = 1"),
            await harness.CountAsync("annal_review_decision_receipts", "1 = 1"));

        Assert.Equal((1, 1, 1, 0, 1, 0), (claims, versions, heads, edges, reviewEvents, receipts));

        Assert.Equal(1, await harness.CountAsync("annal_claims", $"SubjectId = '{other}'"));

        Assert.Equal(0, await harness.CountAsync("saga_memories", $"Id = '{target}'"));

        Assert.Equal(1, await harness.CountAsync("saga_memories", $"Id = '{other}'"));

        Assert.Equal(0, await harness.CountAsync("artifact_sensitivity", "1 = 1"));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection);
    }

    /// <summary>
    /// The shared assertion fails on the one state it exists to catch, so every test that ends with it
    /// is asking a question that can come back no.
    /// </summary>
    [SkippableFact]
    public async Task The_orphan_assertion_fails_on_a_claim_whose_subject_row_is_gone()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        Guid memory = Guid.NewGuid();

        await InsertAsync(harness, memory, "the operator prefers tabs", Created);

        // An intact claim passes, so the failure below is about the deletion and not about the harness.
        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection);

        await ExecuteAsync(harness, "DELETE FROM saga_memories WHERE Id = $id;", ("$id", memory.ToString()));

        Assert.Equal(1, await harness.CountAsync("annal_claims", $"SubjectId = '{memory}'"));

        XunitException failure = await Assert.ThrowsAnyAsync<XunitException>(
            () => AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection));

        Assert.Contains("'Saga'", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The foreign-key half of the shared assertion fires too: a version and head left behind by a
    /// claim that went on its own are caught even though no claim is left to name a missing subject.
    /// </summary>
    /// <remarks>
    /// Enforcement is switched off only to build the broken state, the way a path that deleted the
    /// claim alone under a connection with foreign keys off would leave it. It is back on before the
    /// assertion runs, and the check does not depend on it either way.
    /// </remarks>
    [SkippableFact]
    public async Task The_orphan_assertion_fails_on_a_version_whose_claim_is_gone()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        Guid memory = Guid.NewGuid();

        await InsertAsync(harness, memory, "the operator prefers tabs", Created);

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection);

        await ExecuteAsync(harness, "PRAGMA foreign_keys = OFF;");

        await ExecuteAsync(harness, "DELETE FROM annal_claims WHERE SubjectId = $id;", ("$id", memory.ToString()));

        await ExecuteAsync(harness, "PRAGMA foreign_keys = ON;");

        Assert.Equal(0, await harness.CountAsync("annal_claims", "1 = 1"));

        Assert.Equal(1, await harness.CountAsync("annal_versions", "1 = 1"));

        XunitException failure = await Assert.ThrowsAnyAsync<XunitException>(
            () => AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Connection));

        Assert.Contains("annal_versions", failure.Message, StringComparison.Ordinal);

        // The row's claim is gone, so no store can be named for it, and the message says that rather than
        // listing the stores as though one of them were the diagnosis.
        Assert.Contains("store not recoverable once the claim is gone", failure.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("Saga or Lexicon", failure.Message, StringComparison.Ordinal);
    }

    private static async Task InsertAsync(SagaStoreHarness harness, Guid id, string content, DateTimeOffset createdAt)
    {
        SagaMemoryWriteOutcome outcome = await harness.Store.InsertAsync(
            id.ToString(),
            content,
            createdAt,
            sessionId: null,
            tags: null,
            source: "saga-extraction",
            harness.Embedding(content.Length),
            Token);

        Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);
    }

    /// <summary>
    /// Confirms one memory's current head through the review service, the way
    /// <c>SagaMemoryReviewServiceTests.Confirm_records_only_review_and_marker_waits_for_every_older_gap</c>
    /// does, which records a decision receipt and moves no head.
    /// </summary>
    private static async Task ConfirmHeadAsync(SagaStoreHarness harness, Guid memory)
    {
        FakeTimeProvider time = new();

        time.SetUtcNow(Created.AddHours(1));

        MemoryReviewTokenCodec codec = new(time);

        SagaMemoryReviewService review = new(
            harness.Context,
            new NoEmbeddingWeaveService(),
            codec,
            new WeaveIndexAvailability(),
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Features = new FeatureSettings { Annals = true },
                    Integrations = new IntegrationSettings
                    {
                        Embeddings = new EmbeddingIntegrationSettings { Dimensions = 64 },
                    },
                }),
            MemoryErasureTestKeys.Isolated(),
            new OperatorAuthorityContextIssuer(new FakeCovenantAuthorityProvider()),
            time,
            NullLogger<SagaMemoryReviewService>.Instance);

        Result<SagaReviewPageDto> page = await review.ListAsync(
            new SagaReviewListRequest(SagaMemoryScopeKind.Global, CampaignId: null, Limit: 10, Cursor: null),
            Token);

        Assert.True(page.IsSuccess, page.IsFailure ? page.Error.Message : string.Empty);

        SagaReviewItemDto head = Assert.Single(page.Value.Items, item => item.SubjectId == memory.ToString());

        SagaReviewBulkPrepareRequest request = new(
            Guid.NewGuid(),
            SagaMemoryScopeKind.Global,
            CampaignId: null,
            MemoryReviewAction.Confirm,
            [new SagaReviewDecision(head.ObservationToken, ReplacementContent: null)]);

        Result<MemoryReviewBulkPlanDto> plan = await review.PrepareAsync(request, Token);

        Assert.True(plan.IsSuccess, plan.IsFailure ? plan.Error.Message : string.Empty);

        Result<MemoryReviewBulkResultDto> confirmed = await review.ApplyAsync(
            new SagaReviewBulkApplyRequest(request, plan.Value.PreparedPlanToken),
            Token);

        Assert.True(confirmed.IsSuccess, confirmed.IsFailure ? confirmed.Error.Message : string.Empty);
    }

    /// <summary>
    /// Runs the staged restore purge over the harness database and commits it.
    /// </summary>
    private static async Task PurgeStagedAsync(SagaStoreHarness harness)
    {
        SqliteConnection connection = (SqliteConnection)harness.Connection;

        await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            Token);

        await using SqliteTransaction transaction = connection.BeginTransaction();

        Result<BackupRestoreProtectedStatePurgeReceipt> purged = await BackupRestoreProtectedStatePurger.PurgeStagedAsync(
            connection,
            transaction,
            CovenantSqliteConnectionInitializer.Instance,
            TimeProvider.System,
            Token);

        Assert.True(purged.IsSuccess, purged.IsFailure ? purged.Error.Message : string.Empty);

        await transaction.CommitAsync(Token);
    }

    /// <summary>
    /// Erases the memory through the live kernel under an exclusive family-reinitialize authority, with
    /// the page item built from the label read back out of the ledger.
    /// </summary>
    private static async Task EraseLiveAsync(SagaStoreHarness harness, Guid memory)
    {
        using CovenantConnectionSource connections = new(
            harness.Context,
            FixtureOrdinaryConnectionFactory.For(harness.Context));

        await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
            await connections.GetOpenConnectionAsync(Token),
            CovenantSqliteConnectionMode.ReadWrite,
            Token);

        ArtifactSensitivityLedger ledger = new(connections);

        ArtifactSensitivityLabel label =
            (await ledger.TryReadLabelAsync(SensitiveArtifactKind.Saga, memory, Token)).Value!;

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize),
            Token)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority
            .ForExclusive(lease, CovenantExclusiveOperation.CovenantFamilyReinitialize)
            .Value;

        CovenantProtectedArtifactErasureKernel kernel = new(
            connections,
            CovenantSqliteConnectionInitializer.Instance,
            TimeProvider.System);

        Result<CovenantArtifactErasureProgress> erased = await kernel.ErasePageAsync(
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

        Assert.True(erased.IsSuccess, erased.IsFailure ? erased.Error.Message : string.Empty);

        Assert.Equal(CovenantErasureBlocker.None, erased.Value.Blocker);

        Assert.Equal(1UL, erased.Value.ErasedCount);
    }

    private static async Task ExecuteAsync(
        SagaStoreHarness harness,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using DbCommand command = harness.Connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = name;

            parameter.Value = value;

            _ = command.Parameters.Add(parameter);
        }

        _ = await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>A Confirm embeds nothing, so any call here is a defect in the precondition.</summary>
    private sealed class NoEmbeddingWeaveService : IWeaveService
    {
        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A review Confirm does not embed.");

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A review Confirm does not embed.");

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(
            string text,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A review Confirm does not chunk.");
    }
}
