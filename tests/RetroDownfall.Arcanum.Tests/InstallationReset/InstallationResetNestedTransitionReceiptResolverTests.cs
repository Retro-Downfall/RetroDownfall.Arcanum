using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// Whether a transition is the nested arm of a broader reset, decided from durable evidence alone.
/// </summary>
/// <remarks>
/// Every case here is stated twice: once as a first entry, where the journal has committed to nothing
/// yet, and once as a resume, where it has. The two paths have to reach the same answer from the same
/// record, because a recovery in a fresh process has no caller to be told anything by — and a resume
/// that could not find its parent has to refuse rather than quietly become standalone work.
/// </remarks>
[Collection("WorkspacePathPolicy")]
public sealed class InstallationResetNestedTransitionReceiptResolverTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    private static readonly Guid Installation =
        Guid.Parse("30303030-3030-4030-8030-303030303030");

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public async Task An_absent_record_is_no_parent_on_entry_and_a_refusal_on_a_bound_resume()
    {

        using Harness harness = Create("absent");

        Assert.Null(await ResolvedAsync(harness, committed: null));

        // The journal says it is somebody's nested arm and the record that would agree is not there.
        // Continuing as standalone work is the one downgrade the two-record split exists to forbid.
        Assert.True(
            (await harness.Resolver.ResolveAsync(
                harness.Lock,
                GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
                Effect,
                Digest(0x77),
                CancellationToken.None)).IsFailure);

    }

    [Fact]
    public async Task A_claim_binds_the_transition_and_the_same_binding_resumes_it()
    {

        using Harness harness = Create("claim");

        Guid nested = Guid.Parse("40404040-4040-4040-8040-404040404040");

        InstallationResetActiveRecord record = await ClaimAsync(harness, nested);

        IGrimoireOfflineTransitionParentReceiptSink sink =
            Assert.IsAssignableFrom<IGrimoireOfflineTransitionParentReceiptSink>(
                await ResolvedAsync(harness, committed: null));

        CovenantDigest expected = Value(
            GrimoireOfflineTransitionParentReceipt.BindingDigest(
                record.OperationId,
                nested,
                Effect));

        Assert.Equal(expected, sink.BindingDigest);

        Assert.NotNull(await ResolvedAsync(harness, expected));

        // A binding the record cannot reproduce names a different piece of work, and nothing here is
        // allowed to decide which of the two is the real one.
        Assert.True(
            (await harness.Resolver.ResolveAsync(
                harness.Lock,
                GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
                Effect,
                Digest(0x66),
                CancellationToken.None)).IsFailure);

    }

    [Fact]
    public async Task A_direct_reset_and_a_foreign_effect_are_both_refused()
    {

        using Harness harness = Create("refusals");

        _ = await ClaimAsync(harness, Guid.Parse("50505050-5050-4050-8050-505050505050"));

        // A direct Covenant reset is never the database arm of an installation reset, so a claim
        // standing beside one is two records describing different work under one identity.
        Assert.True(
            (await harness.Resolver.ResolveAsync(
                harness.Lock,
                GrimoireOfflineTransitionKind.CovenantReset,
                Effect,
                committedBindingDigest: null,
                CancellationToken.None)).IsFailure);

    }

    [Fact]
    public async Task Publishing_the_receipt_proves_it_by_rereading_and_never_republishes_it()
    {

        using Harness harness = Create("publish");

        Guid nested = Guid.Parse("60606060-6060-4060-8060-606060606060");

        InstallationResetActiveRecord record = await ClaimAsync(harness, nested);

        IGrimoireOfflineTransitionParentReceiptSink sink =
            Assert.IsAssignableFrom<IGrimoireOfflineTransitionParentReceiptSink>(
                await ResolvedAsync(harness, committed: null));

        CovenantDigest winner = Digest(0x81);

        CovenantDigest proved = Value(
            await sink.PublishAndRereadAsync(winner, CancellationToken.None));

        Assert.Equal(sink.BindingDigest, proved);

        InstallationResetActivePublication after = Published(harness);

        Assert.Equal(
            InstallationResetNestedTransitionPhase.Completed,
            after.Payload.NestedTransitionReceipt!.Phase);

        Assert.Equal(winner, after.Payload.NestedTransitionReceipt.TerminalWinnerDigest);

        Assert.Equal(record.OperationId, after.Payload.OperationId);

        // A replay rereads and proves. Republishing an identical receipt would advance the outer
        // envelope revision and invalidate every authority bound to the previous one, for no new fact.
        ulong revision = after.Envelope.Revision;

        Assert.Equal(
            proved,
            Value(await sink.PublishAndRereadAsync(winner, CancellationToken.None)));

        Assert.Equal(revision, Published(harness).Envelope.Revision);

    }

    private static CovenantDigest Effect => Digest(0x11);

    private async Task<IGrimoireOfflineTransitionParentReceiptSink?> ResolvedAsync(
        Harness harness,
        CovenantDigest? committed)
    {

        Result<IGrimoireOfflineTransitionParentReceiptSink?> resolved =
            await harness.Resolver.ResolveAsync(
                harness.Lock,
                GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
                Effect,
                committed,
                CancellationToken.None);

        Assert.True(resolved.IsSuccess, resolved.IsFailure ? resolved.Error.Message : null);

        return resolved.Value;

    }

    private static async Task<InstallationResetActiveRecord> ClaimAsync(
        Harness harness,
        Guid nestedOperationId)
    {

        InstallationResetActiveRecord record = Record() with
        {
            NestedTransitionReceipt = new InstallationResetNestedTransitionReceiptV1(
                Version: 1,
                nestedOperationId,
                InstallationResetNestedTransitionPhase.Claimed,
                NestedEffectDigest: null,
                TerminalWinnerDigest: null),
        };

        Result<InstallationResetActivePublication> begun = await harness.Store.BeginAsync(
            harness.Lock,
            Installation,
            record,
            CancellationToken.None);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        return record;

    }

    private static InstallationResetActivePublication Published(Harness harness)
    {

        Result<InstallationResetActiveRecoveryState> inspected =
            harness.Store.InspectAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(inspected.IsSuccess, inspected.IsFailure ? inspected.Error.Message : null);

        return Assert.IsType<InstallationResetActivePublication>(inspected.Value.Publication);

    }

    private static InstallationResetActiveRecord Record() =>
        new(
            InstallationResetActiveStore.CurrentVersion,
            Guid.Parse("20202020-2020-4020-8020-202020202020"),
            "nested-plan",
            InstallationResetScope.Global,
            Workspace: null,
            new InstallationResetAcceptedBinding(
                "binding",
                ["/selected"],
                [],
                [],
                ["master-api-key"],
                ["data-plan"]),
            InstallationResetPhase.Prepared,
            PointOfNoReturn: true,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: null);

    private Harness Create(string name)
    {

        string guardedRoot = _workspace.CreateSubdir("nested-receipt-" + name);

        ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        InstallationResetActiveStore store = new(guardedRoot, new InMemoryOsCredentialStore());

        return new Harness(
            held,
            store,
            new InstallationResetNestedTransitionReceiptResolver(store));

    }

    private static CovenantDigest Digest(byte first) =>
        new([.. Enumerable.Range(first, 32).Select(static value => (byte)value)]);

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        return result.Value;

    }

    private sealed record Harness(
        ArcanumMaintenanceLock Lock,
        InstallationResetActiveStore Store,
        InstallationResetNestedTransitionReceiptResolver Resolver) : IDisposable
    {

        public void Dispose() => Lock.Dispose();

    }

}
