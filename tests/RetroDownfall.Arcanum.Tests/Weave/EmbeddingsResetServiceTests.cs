using System.Data.Common;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
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

    public EmbeddingsResetServiceTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        WeaveIndexAvailability availability = new();

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
                }));

        ServiceCollection services = new();

        services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(
            new RecordingScopedOrdinaryConnectionFactory());

        _resetService = new EmbeddingsResetService(
            _db,
            availability,
            services.BuildServiceProvider());

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

        ServiceCollection services = new();

        services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(connections);

        EmbeddingsResetService service = new(
            _db,
            new WeaveIndexAvailability(),
            services.BuildServiceProvider(),
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

    private async Task SeedLabelAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken)
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

        _ = command.Parameters.AddWithValue("$label", Guid.NewGuid().ToString("D").ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$kind", (int)kind);

        _ = command.Parameters.AddWithValue("$artifact", artifactId.ToString("D").ToUpperInvariant());

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

            return Result<CovenantSensitivePurgeOutcome>.Success(
                new CovenantSensitivePurgeOutcome(
                    [
                        .. targets.Select(target => new CovenantSensitivePurgeResult(
                            target.ArtifactId,
                            target.Kind,
                            CovenantSensitivePurgeDisposition.Unlabeled,
                            CovenantErasureBlocker.None)),
                    ],
                    CovenantArtifactErasureProgress.Empty));

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
                }));

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
