using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

namespace RetroDownfall.Arcanum.Tests.Coordination;

[Collection("WorkspacePathPolicy")]
public sealed class CompositeClientMutationEvidenceProbeTests : IDisposable
{

    private readonly string _container;

    private readonly string _guardedRoot;

    public CompositeClientMutationEvidenceProbeTests()
    {

        _container = Path.Combine(
            Path.GetTempPath(),
            "arcanum-client-evidence-" + Guid.NewGuid().ToString("N"));

        _guardedRoot = Path.Combine(_container, "arcanum");

        Directory.CreateDirectory(_guardedRoot);

    }

    public void Dispose()
    {

        if (Directory.Exists(_container))
        {

            Directory.Delete(_container, recursive: true);

        }

    }

    [Theory]
    [InlineData(true, false, (byte)ClientMutationEvidenceDisposition.Blocked)]
    [InlineData(false, true, (byte)ClientMutationEvidenceDisposition.Blocked)]
    [InlineData(false, false, (byte)ClientMutationEvidenceDisposition.Clear)]
    public async Task Reset_and_restore_evidence_are_composed_without_collapsing_absence(
        bool resetActive,
        bool restoreActive,
        byte expectedValue)
    {

        CompositeClientMutationEvidenceProbe probe = new(
            new ClientMutationBlockerStore(_guardedRoot),
            new FakeResetProbe(Result<ActiveInstallationReset?>.Success(
                resetActive
                    ? new ActiveInstallationReset(
                        Scope: InstallationResetScope.All,
                        WorkspaceRoot: null,
                        PlanId: "accepted-plan",
                        OperationId: Guid.NewGuid())
                    : null)),
            new FakeRestoreProbe(Restore(restoreActive)));

        ClientMutationEvidenceResult result = await probe.InspectAsync(
            CancellationToken.None);

        Assert.Equal(
            (ClientMutationEvidenceDisposition)expectedValue,
            result.Disposition);

    }

    [Fact]
    public async Task A_durable_blocker_short_circuits_other_evidence_as_blocked()
    {

        ClientMutationBlockerStore blocker = new(_guardedRoot);

        using (ArcanumClientMutationLock held = ArcanumClientMutationLock
            .AcquireDetailed(_guardedRoot)
            .BorrowAcquiredLock())
        {

            _ = (await blocker.PublishAsync(
                held,
                new ClientMutationBlockerRecord(
                    ClientMutationBlockerStore.CurrentVersion,
                    Guid.NewGuid(),
                    ClientMutationBlockerKind.ReplacementRestore,
                    Scope: null,
                    PlanId: null,
                    OperationId: Guid.NewGuid()))).Value;

        }

        FakeResetProbe reset = new(
            Result<ActiveInstallationReset?>.Success(null));

        FakeRestoreProbe restore = new(Restore(active: false));

        ClientMutationEvidenceResult result = await new CompositeClientMutationEvidenceProbe(
                blocker,
                reset,
                restore)
            .InspectAsync(CancellationToken.None);

        Assert.Equal(ClientMutationEvidenceDisposition.Blocked, result.Disposition);

        Assert.Equal(0, reset.Calls);

        Assert.Equal(0, restore.Calls);

    }

    [Fact]
    public async Task An_uninspectable_authority_is_unsafe_and_never_clear()
    {

        CompositeClientMutationEvidenceProbe probe = new(
            new ClientMutationBlockerStore(_guardedRoot),
            new FakeResetProbe(Result<ActiveInstallationReset?>.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "reset evidence unavailable"))),
            new FakeRestoreProbe(Restore(active: false)));

        ClientMutationEvidenceResult result = await probe.InspectAsync(
            CancellationToken.None);

        Assert.Equal(ClientMutationEvidenceDisposition.Unsafe, result.Disposition);

        Assert.Equal(ErrorCodes.Data.ControlPathUnavailable, result.Error.Code);

    }

    private sealed class FakeResetProbe(
        Result<ActiveInstallationReset?> result)
        : IClientMutationResetEvidenceProbe
    {

        internal int Calls { get; private set; }

        public Task<Result<ActiveInstallationReset?>> InspectAsync(
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            Calls++;

            return Task.FromResult(result);

        }

    }

    private static Result<ActiveReplacementRestore?> Restore(bool active) =>
        Result<ActiveReplacementRestore?>.Success(
            active ? new ActiveReplacementRestore(Guid.NewGuid()) : null);

    private sealed class FakeRestoreProbe(
        Result<ActiveReplacementRestore?> result)
        : IClientMutationRestoreEvidenceProbe
    {

        internal int Calls { get; private set; }

        public Task<Result<ActiveReplacementRestore?>> InspectAsync(
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            Calls++;

            return Task.FromResult(result);

        }

    }

}
