using System.Data.Common;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>Operator reset endpoint backing service — clears embedding tables and companion metadata.</summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class EmbeddingsResetServiceTests : IAsyncLifetime
{
    private const int TestDimensions = 64;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private SagaMemoryStore? _sagaStore;

    private EmbeddingsResetService? _resetService;

    /// <summary>The accelerator flag the Saga store and the reset service share.</summary>
    private readonly WeaveIndexAvailability _vectorAccelerator = new();

    public EmbeddingsResetServiceTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        WeaveIndexAvailability availability = _vectorAccelerator;

        _sagaStore = new SagaMemoryStore(
            _db,
            availability,
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Integrations = new IntegrationSettings
                    {
                        Embeddings = new EmbeddingIntegrationSettings
                        {
                            Dimensions = TestDimensions,
                        },
                    },
                }),
            MemoryErasureTestKeys.Isolated());

        _resetService = new EmbeddingsResetService(
            _db,
            Services());

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
    }

    [SkippableFact]
    public async Task Purge_releases_page_owner_before_dispatching_the_scoped_purger()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid artifactId = Guid.NewGuid();

        await SeedLabelAsync(SensitiveArtifactKind.Saga, artifactId, CancellationToken.None);

        await _db!.Database.CloseConnectionAsync();

        RecordingScopedOrdinaryConnectionFactory connections = new();

        TaskCompletionSource purgeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource allowPurge = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ScopedConnectionPurger scopedPurger = new(
            _db,
            connections,
            purgeEntered,
            allowPurge);

        EmbeddingsResetService service = new(
            _db,
            Services(connections),
            scopedPurger);

        using ScopedConsumerPause pause = new("EmbeddingsResetService.PurgeLabeledKindAsync");

        using CancellationTokenSource resetCts = new(TimeSpan.FromSeconds(20));

        Task<EmbeddingsResetResult> resetting = service.ResetAsync(
            EmbeddingsResetScope.Saga,
            resetCts.Token);

        try
        {
            await pause.WaitUntilEnteredAsync();

            Assert.Equal(GrimoireScopedConsumerFinalUseKind.ReaderMaterialized, pause.FinalUse.Kind);

            Assert.Equal(1, pause.FinalUse.Observation);

            Assert.Equal(1, connections.LiveOwnerLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));

            Assert.Equal(0, connections.LiveBorrowLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));

            pause.Release();

            await purgeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, connections.LiveOwnerLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));

            Assert.Equal(0, connections.LiveBorrowLeaseCountFor(CovenantSqliteConnectionMode.ReadOnly));

            Assert.Equal(1, connections.LiveOwnerLeaseCountFor(CovenantSqliteConnectionMode.ReadWrite));

            Assert.Equal(0, connections.LiveBorrowLeaseCountFor(CovenantSqliteConnectionMode.ReadWrite));
        }
        finally
        {
            pause.Release();

            allowPurge.TrySetResult();

            _ = await resetting.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, connections.LiveLeaseCount);
    }

    /// <summary>
    /// The provider the service resolves its ordinary-connection factory and its label guard from.
    /// </summary>
    /// <remarks>
    /// The guard defaults to the real one over this fixture's database, so a case that never labels
    /// anything is unaffected by being handed the genuine article.
    /// </remarks>
    private ServiceProvider Services(
        RecordingScopedOrdinaryConnectionFactory? connections = null,
        ICovenantLabeledArtifactTransactionGuard? guard = null,
        TapestryBuildBackoff? backoff = null)
    {
        ServiceCollection services = new();

        if (backoff is not null)
        {
            services.AddSingleton(backoff);
        }

        services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(
            connections ?? new RecordingScopedOrdinaryConnectionFactory());

        services.AddSingleton(guard ?? FixtureLabeledArtifactGuard.For(_db!));

        return services.BuildServiceProvider();
    }

    private Task SeedLabelAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken) =>
        SeedLabelRowAsync(
            Guid.NewGuid().ToString("D").ToUpperInvariant(),
            kind,
            artifactId.ToString("D").ToUpperInvariant(),
            cancellationToken);

    /// <summary>
    /// Writes one label row with exactly the identity text given, parseable as a Guid or not, because the
    /// column has no format check and a damaged Grimoire is what the cases that pass garbage describe.
    /// </summary>
    private async Task SeedLabelRowAsync(
        string labelId,
        SensitiveArtifactKind kind,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId,
                ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest,
                ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES ($label, $kind, $artifact, 1, 1, $generations, NULL, NULL, NULL, NULL,
                    1, zeroblob(32), zeroblob(32), NULL, NULL, NULL, zeroblob(32), $now);
            """;

        _ = command.Parameters.AddWithValue("$label", labelId);

        _ = command.Parameters.AddWithValue("$kind", (int)kind);

        _ = command.Parameters.AddWithValue("$artifact", artifactId);

        _ = command.Parameters.AddWithValue("$generations", Enumerable.Repeat((byte)7, 16).ToArray());

        _ = command.Parameters.AddWithValue("$now", "2026-01-01T00:00:00.0000000Z");

        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class ScopedConnectionPurger(
        ArcanumDbContext db,
        RecordingScopedOrdinaryConnectionFactory connections,
        TaskCompletionSource entered,
        TaskCompletionSource release) : ICovenantSensitiveArtifactPurger
    {
        public async ValueTask<Result<CovenantSensitivePurgeOutcome>> PurgeAsync(
            IReadOnlyList<CovenantSensitivePurgeTarget> targets,
            CancellationToken cancellationToken = default)
        {
            SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

            Result<IGrimoireOrdinaryConnectionLease> acquired = await connections.AcquireScopedAsync(
                connection,
                CovenantSqliteConnectionMode.ReadWrite,
                cancellationToken);

            Assert.True(acquired.IsSuccess);

            await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

            entered.SetResult();

            await release.Task.WaitAsync(cancellationToken);

            await RemoveLabelsAsync(connection, targets, cancellationToken);

            return Result<CovenantSensitivePurgeOutcome>.Success(
                new CovenantSensitivePurgeOutcome(
                    [
                        .. targets.Select(target => new CovenantSensitivePurgeResult(
                            target.ArtifactId,
                            target.Kind,
                            CovenantSensitivePurgeDisposition.Purged,
                            CovenantErasureBlocker.None)),
                    ],
                    CovenantArtifactErasureProgress.Empty));
        }
    }

    /// <summary>
    /// A reset whose label scan cannot read the label table stops before it truncates anything, rather
    /// than reading the failure as "nothing here is labelled".
    /// </summary>
    /// <remarks>
    /// The label table is a Core object at every schema version, so a read that fails is a Grimoire whose
    /// protection cannot be checked, not an installation that has no labels. A scan that answered success
    /// there let the set-based truncation below it remove rows nothing had been asked about. The failure is
    /// real: a temporary table of the same name shadows the label table on this connection, so the scan
    /// fails on a column that table does not have. The purger is present and is never called, because the
    /// scan fails before it has a page to dispatch.
    /// </remarks>
    [SkippableTheory]
    [InlineData(EmbeddingsResetScope.Saga)]
    [InlineData(EmbeddingsResetScope.All)]
    public async Task ResetAsync_RefusesWhenTheLabelTableCannotBeRead_AndKeepsTheSagaRows(EmbeddingsResetScope scope)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _ = await _sagaStore!.InsertAsync(
            "mem-kept",
            "a memory the reset must not reach",
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            null,
            "extraction",
            Vec(1f),
            CancellationToken.None);

        await ExecuteAsync("CREATE TEMP TABLE artifact_sensitivity (Unreadable INTEGER);");

        CountingPurger purger = new();

        EmbeddingsResetService service = new(_db!, Services(), purger);

        LabeledArtifactRefusalException refused = await Assert.ThrowsAsync<LabeledArtifactRefusalException>(
            () => service.ResetAsync(scope, CancellationToken.None));

        Assert.Equal(ErrorCodes.Covenant.Unavailable, refused.Error.Code);

        Assert.DoesNotContain("mem-kept", refused.Message, StringComparison.Ordinal);

        Assert.Equal(0, purger.Calls);

        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM saga_memories;"));

        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM saga_memory_embeddings;"));
    }

    /// <summary>
    /// A label row the scan read but could not parse is a label it cannot examine, so the reset stops
    /// there instead of walking past it.
    /// </summary>
    /// <remarks>
    /// The label table keys every artifact by a Guid, but nothing in its schema says so: the column has
    /// no format check. A row whose <c>ArtifactId</c> is not one was dropped without a failure, and a
    /// page whose rows were all dropped came back empty, which ended the walk — so every label after it
    /// for that kind was never dispatched, and the set-based truncation removed the rows they named. That
    /// is the same hole a label table that cannot be read used to be, reached through a different door:
    /// it needs corruption or tampering, and the guard itself already fails closed on a corrupt row.
    ///
    /// <para>Both shapes are here. The first page is a hundred and twenty-eight unparseable rows with a
    /// real label behind them, which is the page that ended the walk; the second is one unparseable row
    /// beside two real ones, which dispatched the two and carried on. Both are refused before the page is
    /// dispatched, so the purger is never called and the labelled memory is still there.</para>
    /// </remarks>
    [SkippableTheory]
    [InlineData(EmbeddingsResetScope.Saga, SensitiveArtifactKind.Saga, 130)]
    [InlineData(EmbeddingsResetScope.Saga, SensitiveArtifactKind.Saga, 1)]
    [InlineData(EmbeddingsResetScope.Entry, SensitiveArtifactKind.Embedding, 130)]
    public async Task ResetAsync_RefusesWhenALabelRowCannotBeParsed_AndDispatchesNothing(
        EmbeddingsResetScope scope,
        SensitiveArtifactKind kind,
        int unparseableRows)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _ = await _sagaStore!.InsertAsync(
            "mem-kept",
            "a labelled memory the reset must not reach",
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            null,
            "extraction",
            Vec(1f),
            CancellationToken.None);

        // Sorted ahead of every real label, so they are what the first page reads.
        for (int index = 1; index <= unparseableRows; index++)
        {
            await SeedLabelRowAsync(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"00000000-0000-0000-0000-{index:D12}"),
                kind,
                $"not-a-guid-{index}");
        }

        await SeedLabelRowAsync(
            "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF",
            kind,
            Guid.NewGuid().ToString("D").ToUpperInvariant());

        CountingPurger purger = new();

        EmbeddingsResetService service = new(_db!, Services(), purger);

        LabeledArtifactRefusalException refused = await Assert.ThrowsAsync<LabeledArtifactRefusalException>(
            () => service.ResetAsync(scope, CancellationToken.None));

        Assert.Equal(ErrorCodes.Covenant.Unavailable, refused.Error.Code);

        Assert.DoesNotContain("not-a-guid", refused.Message, StringComparison.Ordinal);

        Assert.Equal(0, purger.Calls);

        Assert.Equal(unparseableRows + 1, await ScalarAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));

        if (kind is SensitiveArtifactKind.Saga)
        {
            Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM saga_memories;"));
        }
    }

    /// <summary>
    /// A walk over more labels than one page holds dispatches each page in key order and ends only when a
    /// page reads no rows.
    /// </summary>
    /// <remarks>
    /// The cursor is the last label identity read, and the walk stops on zero rows read rather than on
    /// zero rows kept. The refusal for an unparseable row above must not make a clean walk refuse or stop
    /// short, so this is its control: a hundred and thirty real labels are two pages, a full one and the
    /// remainder, and a third read finds nothing.
    /// </remarks>
    [SkippableFact]
    public async Task ResetAsync_DispatchesEveryLabelAcrossPages_WhenEveryRowParses()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        for (int index = 1; index <= 130; index++)
        {
            await SeedLabelRowAsync(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"00000000-0000-0000-0000-{index:D12}"),
                SensitiveArtifactKind.Saga,
                Guid.NewGuid().ToString("D").ToUpperInvariant());
        }

        CountingPurger purger = new(_db);

        EmbeddingsResetService service = new(_db!, Services(), purger);

        _ = await service.ResetAsync(EmbeddingsResetScope.Saga, CancellationToken.None);

        Assert.Equal([128, 2], purger.PageSizes);

        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));
    }

    /// <summary>
    /// The truncation asks the label guard about every kind it truncates, inside its own transaction, so a
    /// label written after the walk cannot be removed along with the rows it names.
    /// </summary>
    /// <remarks>
    /// The walk reads the label table in an earlier read, dispatches the purger, and only then opens the
    /// transaction that runs the set-based deletes, which examine no identity at all. A label committed in
    /// between was removed with its artifact and left a label naming nothing. A second writer on its own
    /// connection tries to label an artifact right after each answer, which is where that label would
    /// land; the transaction holds the write lock from the moment it opens, so that writer is blocked.
    /// A truncation that asked before its transaction opened, or not at all, would leave it free to commit.
    /// The Entry scope truncates the embedding kind, the Saga scope the Saga kind, and All both.
    /// </remarks>
    [SkippableTheory]
    [InlineData(EmbeddingsResetScope.Saga, SensitiveArtifactKind.Saga, "Saga")]
    [InlineData(EmbeddingsResetScope.Entry, SensitiveArtifactKind.Embedding, "Embedding")]
    [InlineData(EmbeddingsResetScope.All, SensitiveArtifactKind.Saga, "Embedding,Saga")]
    public async Task ResetAsync_AsksTheLabelGuardInsideTheTruncatingTransactionForEachKindItTruncates(
        EmbeddingsResetScope scope,
        SensitiveArtifactKind intrudingKind,
        string expectedKinds)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        int kindsTruncated = expectedKinds.Split(',').Length;

        await using ArcanumDbContext sibling = _fixture.CreateContext(_dbPath);

        await sibling.Database.OpenConnectionAsync(CancellationToken.None);

        LabelIntruder intruder = new(
            _ => Task.FromResult((SqliteConnection)sibling.Database.GetDbConnection()),
            intrudingKind);

        KindRecordingGuard recording = new(
            new LabelIntrusionGuard(FixtureLabeledArtifactGuard.For(_db!), intruder));

        EmbeddingsResetService service = new(
            _db!,
            Services(guard: recording),
            new CountingPurger());

        _ = await service.ResetAsync(scope, CancellationToken.None);

        Assert.Equal(expectedKinds, string.Join(',', recording.BulkKinds));

        Assert.Equal(kindsTruncated, intruder.Attempts);

        Assert.Equal(kindsTruncated, intruder.Blocked);

        Assert.Equal(kindsTruncated, intruder.AskedInsideTransaction);

        Assert.Equal(0, intruder.AskedOutsideTransaction);

        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));
    }

    /// <summary>
    /// A labelled artifact still standing when the truncation asks refuses the reset and keeps every row.
    /// </summary>
    /// <remarks>
    /// The purger here removes nothing, which is what a label written after the walk looks like to the
    /// truncation: it is there when the question is asked. The refusal rolls the transaction back before
    /// a single table is touched, and it is the guard's own refusal, <c>Covenant.ForbiddenAuthority</c>.
    /// </remarks>
    [SkippableFact]
    public async Task ResetAsync_RefusesWhenALabelSurvivesToTheTruncation_AndKeepsTheSagaRows()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid memoryId = Guid.NewGuid();

        _ = await _sagaStore!.InsertAsync(
            memoryId.ToString("D"),
            "a labelled memory the reset must not reach",
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            null,
            "extraction",
            Vec(1f),
            CancellationToken.None);

        await SeedLabelAsync(SensitiveArtifactKind.Saga, memoryId, CancellationToken.None);

        CountingPurger purger = new();

        EmbeddingsResetService service = new(_db!, Services(), purger);

        LabeledArtifactRefusalException refused = await Assert.ThrowsAsync<LabeledArtifactRefusalException>(
            () => service.ResetAsync(EmbeddingsResetScope.Saga, CancellationToken.None));

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

        Assert.Equal(1, purger.Calls);

        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM saga_memories;"));

        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM saga_memory_embeddings;"));

        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM artifact_sensitivity;"));
    }

    /// <summary>
    /// A guard that cannot read the label table at the truncation refuses the reset with
    /// <c>Covenant.Unavailable</c>, even when the walk before it read the table without trouble.
    /// </summary>
    [SkippableFact]
    public async Task ResetAsync_RefusesWhenTheGuardCannotReadTheLabelsAtTheTruncation_AndKeepsTheSagaRows()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _ = await _sagaStore!.InsertAsync(
            "mem-kept",
            "a memory the reset must not reach",
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            null,
            "extraction",
            Vec(1f),
            CancellationToken.None);

        UnreadableLabeledArtifactGuard guard = new();

        EmbeddingsResetService service = new(_db!, Services(guard: guard), new CountingPurger());

        LabeledArtifactRefusalException refused = await Assert.ThrowsAsync<LabeledArtifactRefusalException>(
            () => service.ResetAsync(EmbeddingsResetScope.Saga, CancellationToken.None));

        Assert.Equal(ErrorCodes.Covenant.Unavailable, refused.Error.Code);

        Assert.Equal(1, guard.Questions);

        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM saga_memories;"));

        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM saga_memory_embeddings;"));
    }

    /// <summary>A guard that records the kind of every bulk question and passes each one on.</summary>
    private sealed class KindRecordingGuard(ICovenantLabeledArtifactTransactionGuard inner)
        : ICovenantLabeledArtifactTransactionGuard
    {
        /// <summary>The kind of each whole-kind question, in the order they were asked.</summary>
        public List<SensitiveArtifactKind> BulkKinds { get; } = [];

        public ValueTask<Result> EnsureUnlabeledAsync(
            SensitiveArtifactKind kind,
            Guid artifactId,
            CancellationToken cancellationToken = default) =>
            inner.EnsureUnlabeledAsync(kind, artifactId, cancellationToken);

        public ValueTask<Result> EnsureUnlabeledAsync(
            SensitiveArtifactKind kind,
            Guid artifactId,
            DbConnection connection,
            DbTransaction transaction,
            CancellationToken cancellationToken = default) =>
            inner.EnsureUnlabeledAsync(kind, artifactId, connection, transaction, cancellationToken);

        public ValueTask<Result> EnsureAllUnlabeledAsync(
            SensitiveArtifactKind kind,
            IReadOnlyCollection<Guid> artifactIds,
            DbConnection connection,
            DbTransaction transaction,
            CancellationToken cancellationToken = default) =>
            inner.EnsureAllUnlabeledAsync(kind, artifactIds, connection, transaction, cancellationToken);

        public ValueTask<Result> EnsureNoneLabeledAsync(
            SensitiveArtifactKind kind,
            DbConnection connection,
            DbTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            BulkKinds.Add(kind);

            return inner.EnsureNoneLabeledAsync(kind, connection, transaction, cancellationToken);
        }
    }

    /// <summary>A purger that records how often it was asked, with how many targets, and removes nothing.</summary>
    private sealed class CountingPurger(ArcanumDbContext? removeLabelsFrom = null) : ICovenantSensitiveArtifactPurger
    {
        public int Calls { get; private set; }

        /// <summary>The number of targets each call was handed, in call order.</summary>
        public List<int> PageSizes { get; } = [];

        public async ValueTask<Result<CovenantSensitivePurgeOutcome>> PurgeAsync(
            IReadOnlyList<CovenantSensitivePurgeTarget> targets,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            PageSizes.Add(targets.Count);

            // A purger that removed the labels it was handed, as the real one does for what it erases.
            // Without it the labels are still there when the truncation asks, and the truncation refuses.
            if (removeLabelsFrom is not null)
            {
                await RemoveLabelsAsync(
                    (SqliteConnection)removeLabelsFrom.Database.GetDbConnection(),
                    targets,
                    cancellationToken);
            }

            return Result<CovenantSensitivePurgeOutcome>.Success(
                new CovenantSensitivePurgeOutcome([], CovenantArtifactErasureProgress.Empty));
        }
    }

    /// <summary>Removes the label row of every target, which is what a purge that erased them leaves.</summary>
    private static async Task RemoveLabelsAsync(
        SqliteConnection connection,
        IReadOnlyList<CovenantSensitivePurgeTarget> targets,
        CancellationToken cancellationToken)
    {
        foreach (CovenantSensitivePurgeTarget target in targets)
        {
            await using SqliteCommand command = connection.CreateCommand();

            // The label table refuses a delete from anything but a purge, a retention or a maintenance
            // scope, which is what keeps a raw delete from erasing a label by accident.
            using CovenantSqliteAuthorizationScope purge = CovenantSqliteConnectionInitializer.Instance.Authorize(
                connection,
                CovenantSqliteAuthorizationKind.SensitivityRetentionPurge);

            command.CommandText = "DELETE FROM artifact_sensitivity WHERE ArtifactKindCode = $kind AND ArtifactId = $artifact;";

            _ = command.Parameters.AddWithValue("$kind", (int)target.Kind);

            _ = command.Parameters.AddWithValue("$artifact", target.ArtifactId.ToString("D").ToUpperInvariant());

            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// An embeddings reset that clears the Saga scope takes the claims describing the memories it
    /// clears, in the same transaction.
    /// </summary>
    /// <remarks>
    /// The Annals reach a subject only through the row that names it, so a claim left behind by a
    /// truncation of <c>saga_memories</c> is a record no surface can read and no reset can clear. This
    /// endpoint is an operator verb reachable with no Covenant tier, no label and no error, and it
    /// truncates that table by a name held in a list rather than by a statement naming it - which is
    /// why the case enters through the store that writes the claim and the service that clears the
    /// table, and asserts on what is left rather than on either one's own report.
    ///
    /// <para>The second memory is what makes the orphan count mean something: it is present with its
    /// own claim before the reset, so the count moves only on what the reset did rather than on how
    /// many memories there were.</para>
    /// </remarks>
    [SkippableFact]
    public async Task ResetAsync_SagaScope_TakesTheClaimsDescribingTheMemoriesItClears()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SagaMemoryStore claiming = new(
            _db!,
            new WeaveIndexAvailability(),
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Features = new FeatureSettings { Annals = true },
                    Integrations = new IntegrationSettings
                    {
                        Embeddings = new EmbeddingIntegrationSettings
                        {
                            Dimensions = TestDimensions,
                        },
                    },
                }),
            MemoryErasureTestKeys.Isolated());

        _ = await claiming.InsertAsync(
            "mem-claimed-1", "a", DateTimeOffset.UtcNow, Guid.NewGuid(), null, "extraction",
            Vec(1f), CancellationToken.None);

        _ = await claiming.InsertAsync(
            "mem-claimed-2", "b", DateTimeOffset.UtcNow, Guid.NewGuid(), null, "extraction",
            Vec(2f), CancellationToken.None);

        Assert.Equal(2, await ScalarAsync("SELECT COUNT(*) FROM annal_claims WHERE SubjectStoreCode = 1;"));

        _ = await _resetService!.ResetAsync(EmbeddingsResetScope.Saga, CancellationToken.None);

        Assert.Equal(0, await claiming.CountAsync(CancellationToken.None));

        Assert.Equal(
            0,
            await ScalarAsync(
                """
                SELECT COUNT(*) FROM annal_claims
                WHERE SubjectStoreCode = 1
                  AND SubjectId NOT IN (SELECT "Id" FROM "saga_memories");
                """));

        // The claim is the identity, and the records keyed to it go with it or they outlive the only
        // thing that could ever have explained them.
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM annal_heads WHERE SubjectStoreCode = 1;"));

        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM annal_versions;"));
    }

    private async Task<int> ScalarAsync(string sql)
    {
        if (_db!.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(CancellationToken.None);
        }

        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sql)
    {
        if (_db!.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(CancellationToken.None);
        }

        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static float[] Vec(params float[] leading)
    {
        float[] result = new float[TestDimensions];

        leading.AsSpan().CopyTo(result);

        return result;
    }

    [SkippableFact]
    public async Task ResetAsync_SagaScope_ClearsMemoriesAndEmbeddingsAndWatermarks()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        _ = await _sagaStore!.InsertAsync(
            "mem-1",
            "a",
            DateTimeOffset.UtcNow,
            sessionId,
            null,
            "extraction",
            Vec(1f),
            CancellationToken.None);

        await _sagaStore.SetWatermarkAsync(sessionId, DateTimeOffset.UtcNow, CancellationToken.None);

        EmbeddingsResetResult result = await _resetService!.ResetAsync(EmbeddingsResetScope.Saga, CancellationToken.None);

        Assert.Equal(0, await _sagaStore.CountAsync(CancellationToken.None));

        Assert.Null(await _sagaStore.GetWatermarkAsync(sessionId, CancellationToken.None));

        Assert.True(result.DeletedRowCounts.ContainsKey("saga_memories"));

        Assert.True(result.DeletedRowCounts.ContainsKey("saga_memory_embeddings"));

        Assert.True(result.DeletedRowCounts.ContainsKey("saga_extraction_watermarks"));
    }

    /// <summary>
    /// A Saga-scope reset empties a plain vector mirror whatever the accelerator flag says.
    /// </summary>
    /// <remarks>
    /// The mirror is filled through the store's own insert while the flag is on, then the flag goes off
    /// before the reset, which is how a build without the accelerator meets a mirror an earlier build
    /// filled. Leaving those rows would keep the embeddings of memories the reset just removed.
    /// </remarks>
    [SkippableFact]
    public async Task ResetAsync_SagaScope_EmptiesAPlainVectorMirrorWhileTheFlagIsOff()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // The plain table a test stands in for the accelerator's mirror: no schema file installs it.
        await ExecuteAsync(
            """
            CREATE TABLE "saga_memory_embeddings_vec" ("MemoryId" TEXT PRIMARY KEY, "Embedding" BLOB NOT NULL)
            """);

        _vectorAccelerator.SetAvailable(true);

        Assert.Equal(
            SagaMemoryWriteOutcome.Written,
            await _sagaStore!.InsertAsync(
                "mem-vec",
                "c",
                DateTimeOffset.UtcNow,
                Guid.NewGuid(),
                null,
                "extraction",
                Vec(3f),
                CancellationToken.None));

        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM saga_memory_embeddings_vec;"));

        _vectorAccelerator.SetAvailable(false);

        EmbeddingsResetResult result = await _resetService!.ResetAsync(EmbeddingsResetScope.Saga, CancellationToken.None);

        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM saga_memory_embeddings_vec;"));

        Assert.Equal(1, result.DeletedRowCounts["saga_memory_embeddings_vec"]);
    }

    /// <summary>Every scope that owns a vector mirror, with the mirror's table.</summary>
    /// <remarks>
    /// None of these tables is installed by a schema file, because only an accelerator ever built them,
    /// so each case creates the shape it needs.
    /// </remarks>
    public static TheoryData<EmbeddingsResetScope, string> MirrorScopes => new()
    {
        { EmbeddingsResetScope.Entry, "entry_embeddings_vec" },
        { EmbeddingsResetScope.WorkspaceFile, "workspace_file_embeddings_vec" },
        { EmbeddingsResetScope.SessionAttachment, "session_attachment_embeddings_vec" },
        { EmbeddingsResetScope.Tapestry, "tapestry_node_embeddings_vec" },
        { EmbeddingsResetScope.Saga, "saga_memory_embeddings_vec" },
    };

    /// <summary>Each mirror's table and the column it keys on.</summary>
    private static readonly (string Table, string Key)[] EveryMirror =
    [
        ("entry_embeddings_vec", "EntryId"),
        ("workspace_file_embeddings_vec", "ChunkId"),
        ("session_attachment_embeddings_vec", "ChunkId"),
        ("tapestry_node_embeddings_vec", "NodeId"),
        ("saga_memory_embeddings_vec", "MemoryId"),
    ];

    /// <summary>
    /// A reset empties the scope's plain vector mirror whatever the accelerator flag says, and leaves
    /// every other scope's mirror alone.
    /// </summary>
    /// <remarks>
    /// The mirror holds the embedding itself, so rows left in it are the embeddings of content the
    /// operator just reset. Whether the mirror holds rows is a property of the database, which an
    /// earlier build may have filled, and not of whether this process loaded an accelerator. Every
    /// mirror is filled before the reset so a scope that emptied more than its own would show.
    /// </remarks>
    [SkippableTheory]
    [MemberData(nameof(MirrorScopes))]
    public async Task ResetAsync_EmptiesAPlainVectorMirrorWhileTheFlagIsOff(
        EmbeddingsResetScope scope,
        string mirror)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        foreach ((string table, string tableKey) in EveryMirror)
        {
            await CreatePlainMirrorAsync(table, tableKey);

            await SeedMirrorRowsAsync(table, tableKey, 2);
        }

        Assert.False(_vectorAccelerator.IsVecAvailable);

        EmbeddingsResetResult result = await _resetService!.ResetAsync(scope, CancellationToken.None);

        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM \"{mirror}\";"));

        Assert.Equal(2, result.DeletedRowCounts[mirror]);

        foreach ((string table, _) in EveryMirror.Where(entry => entry.Table != mirror))
        {
            Assert.Equal(2, await ScalarAsync($"SELECT COUNT(*) FROM \"{table}\";"));
        }
    }

    /// <summary>
    /// A legacy <c>vec0</c> mirror this runtime cannot open is skipped, the reset still succeeds, and
    /// the table keeps what it held.
    /// </summary>
    /// <remarks>
    /// An FTS5 virtual table stands in for it, because it records the same <c>CREATE VIRTUAL TABLE</c>
    /// text in <c>sqlite_master</c>, which is all that classifying a mirror reads. This runtime could
    /// not open the real one, and a statement against it would fail the whole reset, so what is checked
    /// is that no statement reaches it: the stand-in could be emptied, and still holds both rows.
    /// </remarks>
    [SkippableTheory]
    [MemberData(nameof(MirrorScopes))]
    public async Task ResetAsync_SkipsALegacyVirtualMirrorWithoutFailing(
        EmbeddingsResetScope scope,
        string mirror)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string key = EveryMirror.Single(entry => entry.Table == mirror).Key;

        await CreateLegacyMirrorAsync(mirror, key);

        await SeedMirrorRowsAsync(mirror, key, 2);

        EmbeddingsResetResult result = await _resetService!.ResetAsync(scope, CancellationToken.None);

        Assert.Equal(2, await ScalarAsync($"SELECT COUNT(*) FROM \"{mirror}\";"));

        Assert.Equal(0, result.DeletedRowCounts[mirror]);
    }

    [SkippableFact]
    public async Task ResetAsync_AllScope_EmptiesEveryPlainVectorMirrorWhileTheFlagIsOff()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        foreach ((string table, string key) in EveryMirror)
        {
            await CreatePlainMirrorAsync(table, key);

            await SeedMirrorRowsAsync(table, key, 3);
        }

        EmbeddingsResetResult result = await _resetService!.ResetAsync(EmbeddingsResetScope.All, CancellationToken.None);

        foreach ((string table, _) in EveryMirror)
        {
            Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM \"{table}\";"));

            Assert.Equal(3, result.DeletedRowCounts[table]);
        }
    }

    /// <summary>
    /// One legacy mirror among plain ones costs the reset only that mirror: the others are emptied and
    /// nothing fails.
    /// </summary>
    [SkippableFact]
    public async Task ResetAsync_AllScope_SkipsALegacyVirtualMirrorAndEmptiesTheOthers()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string Legacy = "entry_embeddings_vec";

        foreach ((string table, string key) in EveryMirror)
        {
            if (table == Legacy)
            {
                await CreateLegacyMirrorAsync(table, key);
            }
            else
            {
                await CreatePlainMirrorAsync(table, key);
            }

            await SeedMirrorRowsAsync(table, key, 2);
        }

        EmbeddingsResetResult result = await _resetService!.ResetAsync(EmbeddingsResetScope.All, CancellationToken.None);

        Assert.Equal(2, await ScalarAsync($"SELECT COUNT(*) FROM \"{Legacy}\";"));

        Assert.Equal(0, result.DeletedRowCounts[Legacy]);

        foreach ((string table, _) in EveryMirror.Where(entry => entry.Table != Legacy))
        {
            Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM \"{table}\";"));

            Assert.Equal(2, result.DeletedRowCounts[table]);
        }
    }

    /// <summary>The plain table a build without an accelerator can read, write, and delete from.</summary>
    private Task CreatePlainMirrorAsync(string table, string key) =>
        ExecuteAsync($"CREATE TABLE \"{table}\" (\"{key}\" TEXT PRIMARY KEY, \"Embedding\" BLOB NOT NULL)");

    /// <summary>
    /// Stands in for a <c>vec0</c> mirror an earlier build left: an FTS5 virtual table, which records
    /// the same <c>CREATE VIRTUAL TABLE</c> text.
    /// </summary>
    private Task CreateLegacyMirrorAsync(string table, string key) =>
        ExecuteAsync($"CREATE VIRTUAL TABLE \"{table}\" USING fts5(\"{key}\", \"Embedding\")");

    /// <summary>
    /// Rows a build that has since lost its accelerator no longer writes, so no production path of
    /// this harness can put them there.
    /// </summary>
    private async Task SeedMirrorRowsAsync(string table, string key, int count)
    {
        for (int index = 0; index < count; index++)
        {
            await ExecuteAsync(
                $"INSERT INTO \"{table}\" (\"{key}\", \"Embedding\") VALUES ('{table}-{index}', 'v{index}')");
        }
    }

    [SkippableFact]
    public async Task ResetAsync_AllScope_CoversSagaTables()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        _ = await _sagaStore!.InsertAsync(
            "mem-2",
            "b",
            DateTimeOffset.UtcNow,
            sessionId,
            null,
            "extraction",
            Vec(2f),
            CancellationToken.None);

        EmbeddingsResetResult result = await _resetService!.ResetAsync(EmbeddingsResetScope.All, CancellationToken.None);

        Assert.Equal(0, await _sagaStore.CountAsync(CancellationToken.None));

        Assert.True(result.DeletedRowCounts.ContainsKey("saga_memories"));

        Assert.True(result.DeletedRowCounts.ContainsKey("saga_memory_embeddings"));

        Assert.True(result.DeletedRowCounts.ContainsKey("entry_embeddings"));

        Assert.True(result.DeletedRowCounts.ContainsKey("workspace_file_embeddings"));

        Assert.True(result.DeletedRowCounts.ContainsKey("workspace_file_chunks"));

        Assert.True(result.DeletedRowCounts.ContainsKey("session_attachment_embeddings"));

        Assert.True(result.DeletedRowCounts.ContainsKey("session_attachment_chunks"));

        Assert.True(result.DeletedRowCounts.ContainsKey("session_attachment_index_state"));

        Assert.True(result.DeletedRowCounts.ContainsKey("tapestry_generations"));

        Assert.True(result.DeletedRowCounts.ContainsKey("tapestry_nodes"));

        Assert.True(result.DeletedRowCounts.ContainsKey("tapestry_node_embeddings"));
    }

    /// <summary>
    /// Dropping the trees is how an operator asks for them to be rebuilt, so the record that holds a failing
    /// build off for up to a day has to go with them: the corpus, settings and model are unchanged, and
    /// without this the rebuild they asked for would still answer "backing off".
    /// </summary>
    [SkippableTheory]
    [InlineData(EmbeddingsResetScope.Tapestry, true)]
    [InlineData(EmbeddingsResetScope.All, true)]
    [InlineData(EmbeddingsResetScope.Entry, false)]
    [InlineData(EmbeddingsResetScope.WorkspaceFile, false)]
    public async Task ResetAsync_ForgetsTheRecordOfFailedTapestryBuildsOnlyWhenItDropsTheTrees(
        EmbeddingsResetScope scope,
        bool forgets)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TapestryBuildBackoff backoff = new();

        TapestryScope tapestryScope = new(TapestryScopeKind.Workspace, "/repo");

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = backoff.RecordFailure(tapestryScope, "build-1", now, TimeSpan.FromHours(1));

        EmbeddingsResetService service = new(_db!, Services(backoff: backoff));

        _ = await service.ResetAsync(scope, CancellationToken.None);

        Assert.Equal(!forgets, backoff.IsBackingOff(tapestryScope, "build-1", now));
    }

    [SkippableFact]
    public async Task ResetAsync_TapestryScope_DropsTreeTablesAndNothingElse()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        _ = await _sagaStore!.InsertAsync(
            "mem-3",
            "c",
            DateTimeOffset.UtcNow,
            sessionId,
            null,
            "extraction",
            Vec(3f),
            CancellationToken.None);

        EmbeddingsResetResult result = await _resetService!.ResetAsync(
            EmbeddingsResetScope.Tapestry,
            CancellationToken.None);

        // Trees are derived data: dropping them must not touch the leaf corpora they were woven from,
        // or any other feature's embeddings.
        Assert.Equal(1, await _sagaStore.CountAsync(CancellationToken.None));

        Assert.Equal(
            [
                "tapestry_generations",
                "tapestry_node_embeddings",
                "tapestry_node_embeddings_vec",
                "tapestry_nodes",
            ],
            result.DeletedRowCounts.Keys.Order(StringComparer.Ordinal));
    }
}
