using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A real offline-transition phase authority over a temporary root this test owns outright.
/// </summary>
/// <remarks>
/// Deliberately the production authority over real components rather than a stand-in that records
/// calls. What a coordinator needs proved is that the phases it publishes form a legal ladder, and
/// only the real journal and the real lifecycle validator can say so — a double would accept
/// whatever it was handed and the first illegal sequence would reach an operator instead of a test.
///
/// <para>Everything it substitutes is a matter of where rather than what. The guarded root is a
/// temporary directory instead of the installation's, because <c>ArcanumMaintenanceLock</c> is an
/// exclusive per-directory lock and a suite that took the installation's would contend with whichever
/// host-backed collection happened to be running beside it. The credential store is in memory
/// because the alternative is writing transition keys into the developer's login keychain, which
/// this suite forbids everywhere else for the same reason.</para>
///
/// <para>Acquiring the lock also has a side effect the journal depends on: it tightens the parent
/// directory to owner-only, which the journal's file primitives require before they will open
/// anything. A root created without it is refused, which is a confusing failure to meet for the
/// first time inside an erasure.</para>
/// </remarks>
/// <summary>
/// How far a seeded journal carried its compaction replacement.
/// </summary>
/// <remarks>
/// A record rather than a stage enum because every arm a resumed run takes is chosen by comparing
/// recorded digests against what the world now reports, so a seed that named only a stage could not
/// arrange the case where the two disagree — which is the case the refusals exist for.
/// </remarks>
internal sealed record SeededReplacement(
    string StagingLeaf,
    CovenantDigest Source,
    CovenantDigest? StagingIdentity,
    CovenantDigest? StagedContent);

internal sealed class LocalOfflineTransitionPhaseAuthority
    : IGrimoireOfflineTransitionPhaseAuthority, IDisposable
{

    private readonly string _root;

    private readonly ArcanumMaintenanceLock _lock;

    private readonly GrimoireOfflineTransitionPhaseAuthority _authority;

    internal LocalOfflineTransitionPhaseAuthority(ILongRunningOperationStore operations)
    {

        _root = Path.Combine(
            Path.GetTempPath(),
            "arcanum-local-transition-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                _root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }

        Guarded = Path.Combine(_root, "arcanum");

        Directory.CreateDirectory(Guarded);

        _lock = ArcanumMaintenanceLock.TryAcquire(Guarded)
            ?? throw new InvalidOperationException(
                "The local offline-transition authority could not take its maintenance lock.");

        InMemoryOsCredentialStore credentials = new();

        GrimoireOfflineTransitionJournalLocation location =
            new GrimoireOfflineTransitionJournalFileStore().ResolveLocation(Guarded).Value;

        _ = new BackupRestoreJournalInstallationIdentityProvider(credentials)
            .SeedFromDatabase(_lock, Guarded, location.ProfileNamespace, Installation);

        _authority = new GrimoireOfflineTransitionPhaseAuthority(
            new GrimoireOfflineTransitionLifecycleStore(
                new GrimoireOfflineTransitionJournalStore(credentials),
                GrimoireOfflineTransitionHandlerRegistry.Production),
            new HeldLockAccessor(_lock, Guarded),
            new FixedInstallationIdentity(),
            operations,
            credentials,
            Guarded);

    }

    /// <summary>The installation this authority's journal is bound to.</summary>
    internal static Guid Installation { get; } = Guid.Parse("A0A0A0A0-0000-4000-8000-00000000A0A0");

    /// <summary>The canonical journal file, present exactly while a transition is active.</summary>
    internal string JournalPath =>
        new GrimoireOfflineTransitionJournalFileStore().ResolveLocation(Guarded).Value.JournalPath;

    /// <summary>The guarded root this authority's journal lives beside.</summary>
    internal string Guarded { get; }

    public Task<Result<GrimoireOfflineTransitionPhaseSession>> OpenOrResumeAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        _authority.OpenOrResumeAsync(operation, cancellationToken);

    /// <summary>
    /// Drives this authority's journal to the phase a resumed run is meant to pick up from.
    /// </summary>
    /// <remarks>
    /// A resumed run is one whose journal already records progress, so a suite that wants to test
    /// resumption has to produce that journal rather than assert it into existence. Driving the real
    /// ladder is the only way to get one the coordinator will accept — and it means a seeding step
    /// that publishes an illegal sequence fails here, in the arrangement, instead of looking like a
    /// coordinator defect later.
    /// </remarks>
    internal Task SeedAsync(
        LongRunningOperation operation,
        CovenantResetPhase phase,
        bool factoryContinuationCompleted,
        CancellationToken cancellationToken) =>
        SeedAsync(operation, phase, factoryContinuationCompleted, null, cancellationToken);

    /// <summary>
    /// Seeds a journal that also carries a partly-published compaction replacement.
    /// </summary>
    /// <remarks>
    /// The three publications are only legal from a completed write-ahead-log truncation with nothing
    /// in flight, so a seed that carries any of them is a seed of that exact position. Publishing them
    /// through the real session rather than writing a payload keeps the seed subject to the same
    /// validator a production run is: a stage this arrangement could not legally reach is one no
    /// resumed run has to handle, and a suite that wrote it directly would be testing an impossible
    /// world.
    /// </remarks>
    internal async Task SeedAsync(
        LongRunningOperation operation,
        CovenantResetPhase phase,
        bool factoryContinuationCompleted,
        SeededReplacement? replacement,
        CancellationToken cancellationToken)
    {

        if (phase is CovenantResetPhase.InventoryPrepared)
        {

            return;

        }

        Result<GrimoireOfflineTransitionPhaseSession> opened =
            await OpenOrResumeAsync(operation, cancellationToken);

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

        GrimoireOfflineTransitionPhaseSession session = opened.Value;

        Assert.True((await session.EnterClosingAsync(cancellationToken)).IsSuccess);

        Assert.True((await session.RecordClosedAsync(cancellationToken)).IsSuccess);

        Assert.True((await session.EnterApplyingAsync(cancellationToken)).IsSuccess);

        foreach (CovenantResetPhase step in CovenantResetPhaseMachine.Ordered)
        {

            if (step is CovenantResetPhase.InventoryPrepared)
            {

                continue;

            }

            if (step > phase || step is CovenantResetPhase.ReopenedVerified)
            {

                break;

            }

            if (factoryContinuationCompleted && step is CovenantResetPhase.HandlesClosed)
            {

                Assert.True((await session.RecordFactoryContinuationAsync(cancellationToken)).IsSuccess);

            }

            Assert.True(
                (await session.BeginPhaseAsync(step, cancellationToken)).IsSuccess,
                "seed begin " + step);

            Assert.True(
                (await session.CompletePhaseAsync(step, cancellationToken)).IsSuccess,
                "seed complete " + step);

        }

        if (replacement is not { } staged)
        {

            return;

        }

        Assert.Equal(CovenantResetPhase.WalTruncated, phase);

        Assert.True(
            (await session.RecordReplacementPlannedAsync(
                staged.StagingLeaf,
                staged.Source,
                staged.Source,
                staged.Source,
                cancellationToken)).IsSuccess,
            "seed replacement plan");

        if (staged.StagingIdentity is not { } stagingIdentity)
        {

            return;

        }

        Assert.True(
            (await session.RecordStagingIdentityAsync(stagingIdentity, cancellationToken)).IsSuccess,
            "seed replacement staging identity");

        if (staged.StagedContent is not { } stagedContent)
        {

            return;

        }

        Assert.True(
            (await session.RecordStagedContentAsync(stagedContent, cancellationToken)).IsSuccess,
            "seed replacement staged content");

    }

    public void Dispose()
    {

        _lock.Dispose();

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    private sealed class HeldLockAccessor(ArcanumMaintenanceLock held, string guarded)
        : IInstallationResetMaintenanceLockAccessor
    {

        public Result<ArcanumMaintenanceLock> BorrowHeldLock(string guardedDirectory) =>
            string.Equals(guardedDirectory, guarded, StringComparison.Ordinal)
                ? Result<ArcanumMaintenanceLock>.Success(held)
                : Result<ArcanumMaintenanceLock>.Failure(
                    new Error(
                        ErrorCodes.Covenant.Unavailable,
                        "No maintenance lock is held for that directory."));

    }

    private sealed class FixedInstallationIdentity : IInstallationResetDatabaseIdentityReader
    {

        public Task<Result<Guid>> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Guid>.Success(Installation));

    }

}
