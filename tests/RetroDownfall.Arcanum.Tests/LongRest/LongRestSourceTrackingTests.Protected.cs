using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSourceTrackingTests
{
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(37)]
    public async Task Protected_capture_retains_the_exact_canonical_label_manifest_and_observed_revision(int revision)
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        Guid memory = Guid.NewGuid();

        await SeedSourceAsync(h, memory.ToString("D"));

        SqliteConnection connection = (SqliteConnection)h.Connection;

        ArtifactSensitivityLabel label;

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            DerivedArtifactWrite write = new(SensitiveArtifactKind.Saga, memory, null, null, null, (ulong)revision,
                DerivedArtifactContentDigest.ForText("source " + memory.ToString("D")), ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([Guid.NewGuid()]));

            Result<LabeledArtifactWriteReceipt> written = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, write, CancellationToken.None);

            Assert.True(written.IsSuccess);

            label = (await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, transaction, SensitiveArtifactKind.Saga, memory, CancellationToken.None)).Value!;

            await transaction.CommitAsync();
        }

        LongRestDiscoveryStore store = new(h.Context, TimeProvider.System);

        Guid operation = Guid.NewGuid();

        await ExecuteAsync(connection, "INSERT INTO LongRunningOperations (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary) VALUES ($id, 'long-rest-sweep', 1, 1, $at, 'sweep');",
            ("$id", operation.ToString("N")), ("$at", At));

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None)).Value;

        LongRestConfigurationStamp configuration = new(Guid.NewGuid(), 1, new CovenantDigest(new byte[32]));

        LongRestSweepScope scope = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null), label.SensitivityDigest).Value;

        LongRestSweepFence fence = new(CanonicalCampaignContext.GlobalOnly, 1, authority.Snapshot.DatasetGeneration!.Value,
            1, authority.Snapshot.RuntimeAuthorityGeneration, authority.Snapshot.CapabilityGeneration, configuration);

        LongRestRecipeIdentity recipe = new(1, 1, "provider", "summary", configuration.ValueDigest, "provider", "embedding", configuration.ValueDigest, 64);

        LongRestDiscoveryGeneration generation = (await store.StartOrResumeAsync(operation, scope, fence, recipe, CancellationToken.None)).Value;

        Assert.True((await store.CaptureNextSourcePageAsync(generation.GenerationId, 0, null, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT Revision FROM long_rest_discovery_generations;"));

        LongRestMaintenanceCapability capability = await CapabilityAsync(operation, configuration, authority);

        Assert.True((await store.CaptureNextSourcePageAsync(generation.GenerationId, 0, capability, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_discovery_leaves;"));

        Assert.True((await capability.ConsumeAsync(operation, scope, configuration, CancellationToken.None)).IsSuccess);

        LongRestMaintenanceCapability wrongOperation = await CapabilityAsync(Guid.NewGuid(), configuration, authority);

        Assert.True((await wrongOperation.ConsumeAsync(wrongOperation.OperationId, scope, configuration, CancellationToken.None)).IsSuccess);

        Assert.True((await store.CaptureNextSourcePageAsync(generation.GenerationId, 0, wrongOperation, CancellationToken.None)).IsFailure);

        LongRestMaintenanceCapability wrongPartition = await CapabilityAsync(operation, configuration, authority);

        Assert.True((await wrongPartition.ConsumeAsync(operation, LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null)).Value, configuration, CancellationToken.None)).IsSuccess);

        Assert.True((await store.CaptureNextSourcePageAsync(generation.GenerationId, 0, wrongPartition, CancellationToken.None)).IsFailure);

        await ExecuteAsync(connection, "UPDATE long_rest_discovery_generations SET FenceJson = $fence;",
            ("$fence", JsonSerializer.Serialize(LongRestStoredFence.From(fence with { RuntimeAuthorityGeneration = fence.RuntimeAuthorityGeneration + 1 }), LongRestJsonContext.Default.LongRestStoredFence)));

        Assert.True((await store.CaptureNextSourcePageAsync(generation.GenerationId, 0, capability, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_discovery_leaves;"));

        await ExecuteAsync(connection, "UPDATE long_rest_discovery_generations SET FenceJson = $fence;",
            ("$fence", JsonSerializer.Serialize(LongRestStoredFence.From(fence), LongRestJsonContext.Default.LongRestStoredFence)));

        Result<LongRestSourcePage> captured = await store.CaptureNextSourcePageAsync(generation.GenerationId, 0, capability, CancellationToken.None);

        Assert.True(captured.IsSuccess, captured.IsFailure ? captured.Error.ToString() : "");

        LongRestSourceLeaf leaf = Assert.Single(captured.Value.Leaves);

        Assert.Equal((ulong)revision, leaf.SagaRevision);

        Assert.Equal(label.LabelId, leaf.CanonicalLabel!.LabelId);

        Assert.Equal(label.LabelDigest, leaf.CanonicalLabel.LabelDigest);

        Assert.Equal(label.ArtifactContentDigest, leaf.SagaContentDigest);

        Assert.Equal("source " + memory.ToString("D"), leaf.Content);

        ArtifactSensitivityLabel retained = JsonSerializer.Deserialize((string)(await ScalarAsync(connection, "SELECT LabelJson FROM long_rest_discovery_leaves;"))!, LongRestJsonContext.Default.ArtifactSensitivityLabel)!;

        Assert.Equal(label.LabelId, retained.LabelId);

        Assert.Equal(label.LabelDigest, retained.LabelDigest);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_clean_fts;"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_protected_fts WHERE long_rest_protected_fts MATCH 'source';"));
    }

    private static async Task<LongRestMaintenanceCapability> CapabilityAsync(Guid operation, LongRestConfigurationStamp configuration, CovenantTurnLease authority)
    {
        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CanonicalCampaignContext.GlobalOnly, InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools,
            CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), authority.Snapshot.RuntimeAuthorityGeneration, authority.Snapshot.AuthorityEpoch)).Value;

        SessionTurnClaim claim = new(Guid.NewGuid(), Guid.NewGuid(), 0, Guid.NewGuid(), Guid.NewGuid(), SessionTurnSurface.Intelligence,
            configuration.ValueDigest, configuration.ValueDigest, SessionTurnClaimState.PendingMaintenance, null, 18, 0, 0,
            Guid.NewGuid(), null, null, configuration.BootId, 0, 0, null, DateTimeOffset.UtcNow);

        SessionTurnClaimLease lease = new(claim, SessionTurnClaimDisposition.Created, Guid.NewGuid(), Guid.NewGuid(), configuration.BootId, DateTimeOffset.UtcNow.AddMinutes(5));

        return (await LongRestMaintenanceCapability.CreateAsync(operation, invocation, lease, CanonicalCampaignContext.GlobalOnly, authority, configuration, CancellationToken.None)).Value;
    }
}
