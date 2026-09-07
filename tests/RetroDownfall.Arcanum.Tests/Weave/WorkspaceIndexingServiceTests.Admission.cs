using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed partial class WorkspaceIndexingServiceTests
{
    [SkippableFact]
    public async Task Reconciliation_denied_by_maintenance_creates_no_scope_or_provider_effect()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        await using IGrimoireClosingOwner closing = BeginWorkspaceClosing(gate);

        FailingScopeFactory scopes = new();

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings, scopeFactory: scopes, workAdmission: gate);

        Assert.False(await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None));

        Assert.Equal(0, scopes.ScopeCount);

        Assert.Equal(0, weave.EmbedBatchCallCount);
    }

    [SkippableFact]
    public async Task Reconciliation_owns_one_work_lease_and_one_effect_group_per_changed_file()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("one.cs", "class One {}");

        _workspace.WriteFile("two.cs", "class Two {}");

        RecordingGrimoireWorkAdmissionGate gate = new(new GrimoireConnectionAdmissionGate(TimeProvider.System));

        WorkspaceIndexingService service = CreateService(new FakeWeaveService(), out EmbeddingSettings embeddings, workAdmission: gate);

        Assert.True(await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None));

        Assert.Equal([GrimoireWorkKind.WorkspaceIndexing], gate.RequestedWorkKinds);

        Assert.Equal(2, gate.EffectGroupAttempts);

        Assert.True(await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None));

        Assert.Equal(2, gate.EffectGroupAttempts);
    }

    private static IGrimoireClosingOwner BeginWorkspaceClosing(GrimoireConnectionAdmissionGate gate) =>
        gate.BeginOrResumeExclusive(new CovenantExclusiveRecoveryOwner(
            Guid.NewGuid(),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(new byte[32]))).Value;
}
