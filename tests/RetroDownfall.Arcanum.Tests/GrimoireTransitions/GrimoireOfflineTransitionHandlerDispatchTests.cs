using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Tests.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

/// <summary>
/// Adopting the lease and running the registered handler, in that order and under one owner.
/// </summary>
/// <remarks>
/// The order is not a preference. The coordinator compares the row's lease owner with the owner it is
/// handed, so a handler dispatched before the adoption is refused after the gate has already closed
/// around an adopted owner — which strands the installation in the exact posture the recovery pass
/// exists to leave.
/// </remarks>
[Collection("WorkspacePathPolicy")]
public sealed class GrimoireOfflineTransitionHandlerDispatchTests : IAsyncLifetime
{

    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public async Task The_lease_is_adopted_first_and_the_handler_runs_under_that_owner()
    {

        FakeTimeProvider time = new();

        FakeLongRunningOperationStore store = new(time);

        (LongRunningOperation seeded, CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner adopted) =
            await AdoptableAsync(store);

        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.DataRetentionMutation,
            supportedCheckpointVersion: 4,
            _ => LongRunningOperationRecoveryResult.Completed());

        RecordingLeaseAdoption adoption = new(store);

        using Held held = Hold("dispatch");

        Result dispatched = await CovenantOfflineTransitionLaunchGapResumption.ResumeBeforeReadinessAsync(
                Dispatch(store, time, adoption, handler),
                held.Lock,
                held.Root,
                adopted,
                Token);

        Assert.True(dispatched.IsSuccess, dispatched.IsFailure ? dispatched.Error.Message : null);

        Assert.Equal(seeded.Id, Assert.Single(handler.Invocations));

        Assert.Equal(held.Root, adoption.GuardedDirectory);

        Assert.NotNull(adoption.OwnerId);

        // The handler ran under the owner the adoption wrote, which is what makes the verdict's
        // owner-bound compare-exchange land.
        Assert.Equal(
            LongRunningOperationState.Completed,
            Assert.Single(store.Operations, row => row.Id == seeded.Id).State);

    }

    [Theory]
    [InlineData(CovenantExclusiveOperation.CovenantReset)]
    [InlineData(CovenantExclusiveOperation.HealthyCatalogFactoryErasure)]
    public async Task Launch_gap_capability_selects_its_matching_full_owner(
        CovenantExclusiveOperation exclusiveOperation)
    {
        FakeTimeProvider time = new();

        FakeLongRunningOperationStore store = new(time);

        (LongRunningOperation seeded, CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner adopted) =
            await AdoptableAsync(store, exclusiveOperation);

        RecordingRecoveryHandler handler = new(
            seeded.Kind,
            seeded.CheckpointVersion,
            _ => LongRunningOperationRecoveryResult.Completed());

        RecordingLeaseAdoption adoption = new(store);

        using Held held = Hold("full-owner-" + exclusiveOperation);

        Result resumed = await CovenantOfflineTransitionLaunchGapResumption.ResumeBeforeReadinessAsync(
            Dispatch(store, time, adoption, handler),
            held.Lock,
            held.Root,
            adopted,
            Token);

        Assert.True(resumed.IsSuccess, resumed.IsFailure ? resumed.Error.Message : null);

        Assert.Equal([seeded.Id], handler.Invocations);

    }

    [Theory]
    [InlineData("kind")]
    [InlineData("version")]
    [InlineData("operation-id")]
    [InlineData("operation")]
    [InlineData("digest")]
    public async Task Mismatched_offline_identity_is_refused_without_rewriting_the_row(
        string mismatch)
    {
        FakeTimeProvider time = new();

        FakeLongRunningOperationStore store = new(time);

        CovenantExclusiveRecoveryOwner expectedOwner = Owner(
            CovenantExclusiveOperation.CovenantReset,
            'a');

        LongRunningOperation expected = CovenantAdoptedOwnerTestIssuer.BuildLaunch(
            expectedOwner,
            revision: 1);

        CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner adopted =
            await CovenantAdoptedOwnerTestIssuer.IssueAsync(expected);

        CovenantOfflineTransitionLaunchV4 launch = CovenantRecoveryCheckpointCodec
            .DecodeCovenantOfflineTransitionLaunch(expected.CheckpointPayload!)
            .Value;

        LongRunningOperation drifted = mismatch switch
        {
            "kind" => expected with
            {
                Kind = LongRunningOperationKinds.DataRetentionFactoryReset,
            },
            "version" => expected with
            {
                CheckpointVersion = expected.CheckpointVersion - 1,
            },
            "operation-id" => expected with
            {
                CheckpointPayload = CovenantRecoveryCheckpointCodec.Encode(
                    launch with { OperationId = Guid.NewGuid() }),
            },
            "operation" => expected with
            {
                CheckpointPayload = CovenantRecoveryCheckpointCodec.Encode(
                    launch with
                    {
                        Operation = CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                    }),
            },
            "digest" => expected with
            {
                CheckpointPayload = CovenantRecoveryCheckpointCodec.Encode(
                    launch with { EffectDigest = new string('b', 64) }),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };

        store.Add(drifted);

        RecordingRecoveryHandler handler = new(
            drifted.Kind,
            drifted.CheckpointVersion,
            _ => LongRunningOperationRecoveryResult.Completed());

        using Held held = Hold("digest-drift");

        Result resumed = await CovenantOfflineTransitionLaunchGapResumption.ResumeBeforeReadinessAsync(
            Dispatch(store, time, new RecordingLeaseAdoption(store), handler),
            held.Lock,
            held.Root,
            adopted,
            Token);

        Assert.True(resumed.IsFailure);

        Assert.Empty(handler.Invocations);

        Assert.Same(drifted, await store.GetAsync(drifted.Id, Token));

    }

    [Fact]
    public async Task A_lease_the_installation_lock_cannot_take_refuses_before_the_handler()
    {

        FakeTimeProvider time = new();

        FakeLongRunningOperationStore store = new(time);

        (_, CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner adopted) =
            await AdoptableAsync(store);

        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.DataRetentionMutation,
            supportedCheckpointVersion: 4,
            _ => LongRunningOperationRecoveryResult.Completed());

        RecordingLeaseAdoption adoption = new(store) { Refuse = true };

        using Held held = Hold("refused");

        Result dispatched = await CovenantOfflineTransitionLaunchGapResumption.ResumeBeforeReadinessAsync(
                Dispatch(store, time, adoption, handler),
                held.Lock,
                held.Root,
                adopted,
                Token);

        Assert.True(dispatched.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, dispatched.Error.Code);

        Assert.Empty(handler.Invocations);

    }

    private Held Hold(string name)
    {

        string root = _workspace.CreateSubdir("handler-dispatch-" + name);

        return new Held(
            Assert.IsType<ArcanumMaintenanceLock>(ArcanumMaintenanceLock.TryAcquire(root)),
            root);

    }

    private static async Task<(
        LongRunningOperation Operation,
        CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner Adopted)> AdoptableAsync(
        FakeLongRunningOperationStore store,
        CovenantExclusiveOperation exclusiveOperation = CovenantExclusiveOperation.CovenantReset)
    {
        CovenantExclusiveRecoveryOwner owner = Owner(exclusiveOperation, 'a');

        LongRunningOperation operation =
            CovenantAdoptedOwnerTestIssuer.BuildLaunch(owner, revision: 1);

        store.Add(operation);

        CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner adopted =
            await CovenantAdoptedOwnerTestIssuer.IssueAsync(operation);

        return (operation, adopted);
    }

    private static CovenantExclusiveRecoveryOwner Owner(
        CovenantExclusiveOperation operation,
        char digestCharacter) =>
        new(
            Guid.NewGuid(),
            operation,
            new CovenantDigest(Convert.FromHexString(new string(digestCharacter, 64))));

    private static GrimoireOfflineTransitionHandlerDispatch Dispatch(
        FakeLongRunningOperationStore store,
        TimeProvider time,
        RecordingLeaseAdoption adoption,
        params ILongRunningOperationRecoveryHandler[] handlers)
    {

        ServiceCollection services = new();

        services.AddSingleton<ILongRunningOperationStore>(store);

        services.AddSingleton<ILongRunningOperationMaintenanceLeaseAdoption>(adoption);

        services.AddSingleton(time);

        services.AddSingleton(new LongRunningOperationOwnership());

        foreach (ILongRunningOperationRecoveryHandler handler in handlers)
        {

            services.AddSingleton(handler);

        }

        services.AddScoped(sp => new LongRunningOperationReconciler(
            sp.GetRequiredService<ILongRunningOperationStore>(),
            sp.GetServices<ILongRunningOperationRecoveryHandler>(),
            sp.GetRequiredService<TimeProvider>(),
            NullLogger<LongRunningOperationReconciler>.Instance,
            sp.GetRequiredService<LongRunningOperationOwnership>()));

        ServiceProvider provider = services.BuildServiceProvider();

        return new GrimoireOfflineTransitionHandlerDispatch(
            provider.GetRequiredService<IServiceScopeFactory>(),
            time);

    }

    private sealed record Held(ArcanumMaintenanceLock Lock, string Root) : IDisposable
    {

        public void Dispose() => Lock.Dispose();

    }

    /// <summary>
    /// An adoption that writes the owner into the store exactly as the real one does.
    /// </summary>
    /// <remarks>
    /// It has to write, rather than merely report success: the settle that follows compare-exchanges
    /// the verdict against the row's lease owner, so a double that only said "acquired" would leave
    /// the dispatch passing for a reason production does not have.
    /// </remarks>
    private sealed class RecordingLeaseAdoption(FakeLongRunningOperationStore store)
        : ILongRunningOperationMaintenanceLeaseAdoption
    {

        internal bool Refuse { get; init; }

        internal string? OwnerId { get; private set; }

        internal string? GuardedDirectory { get; private set; }

        public async Task<LongRunningOperationLeaseResult> AdoptUnderInstallationLockAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            string guardedDirectory,
            LongRunningOperationRecoveryFingerprint expected,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default)
        {

            heldInstallationLock.AssertHeldFor(guardedDirectory);

            OwnerId = ownerId;

            GuardedDirectory = guardedDirectory;

            LongRunningOperation current =
                await store.GetAsync(expected.OperationId, cancellationToken)
                ?? throw new InvalidOperationException("The double was asked for an absent row.");

            if (Refuse
                || current.Id != expected.OperationId
                || !string.Equals(current.Kind, expected.Kind, StringComparison.Ordinal)
                || current.CheckpointVersion != expected.CheckpointVersion
                || current.Revision != expected.Revision)
            {

                return new LongRunningOperationLeaseResult(false, current);

            }

            LongRunningOperation adopted = current with
            {

                LeaseOwner = ownerId,

                LeaseExpiresAt = leaseExpiresAt,

                Revision = current.Revision + 1,

            };

            store.Add(adopted);

            return new LongRunningOperationLeaseResult(true, adopted);

        }

    }

}
