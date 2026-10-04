using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// How the sensitivity purge coordinator classifies each target it dispatches.
/// </summary>
/// <remarks>
/// <para>The coordinator is built from the real operation gate, a real write lease, and real operator
/// authority. Only the two things it asks questions of are scripted: the label ledger, whose answer can
/// change between the first read and the re-read, and the database kernel, whose answer for each item is
/// fixed in advance. That is what lets these tests state exactly what the coordinator was told and assert
/// what it reported.</para>
///
/// <para>Each item is classified from its own erasure progress, never from a page total: an item the
/// kernel did not erase is never reported purged, and nothing after a blocked item is examined
/// (§10.20.2).</para>
/// </remarks>
public sealed class CovenantSensitiveRetentionPurgeCoordinatorTests
{
    private static readonly Guid A = new("A1A1A1A1-0000-4000-8000-000000000001");

    private static readonly Guid B = new("B2B2B2B2-0000-4000-8000-000000000002");

    private static readonly Guid C = new("C3C3C3C3-0000-4000-8000-000000000003");

    private static readonly Guid D = new("D4D4D4D4-0000-4000-8000-000000000004");

    private static readonly CovenantArtifactErasureProgress Erased = new(1, 1, 0, CovenantErasureBlocker.None);

    private static readonly CovenantArtifactErasureProgress ExaminedNotErased = new(1, 0, 1, CovenantErasureBlocker.None);

    /// <summary>
    /// A label that changed between the coordinator's first read and the kernel's own reread is still
    /// there, so the item is blocked as stale rather than reported purged.
    /// </summary>
    /// <remarks>
    /// The kernel answers "no live label at the identity this page named" with one examined and nothing
    /// erased. Reading that as success would tell the route the row is gone while the row and its new
    /// label are both still present.
    /// </remarks>
    [Fact]
    public async Task A_label_that_moved_before_the_kernel_is_blocked_as_stale_not_purged()
    {
        ScriptedLabelLedger ledger = new();

        ledger.Script(A, LabelRead.Of(Label(A, Guid.NewGuid())), LabelRead.Of(Label(A, Guid.NewGuid())));

        ScriptedErasureKernel kernel = new();

        kernel.Script(A, ExaminedNotErased);

        CovenantSensitivePurgeOutcome outcome = Succeeded(await PurgeAsync(ledger, kernel, A));

        CovenantSensitivePurgeResult a = Assert.Single(outcome.Results);

        Assert.Equal(CovenantSensitivePurgeDisposition.Blocked, a.Disposition);

        Assert.Equal(CovenantErasureBlocker.AuthorityStale, a.Blocker);

        Assert.False(outcome.WasPurged(A));

        Assert.False(outcome.RequiresOrdinaryDelete(A));

        Assert.Equal(2, ledger.ReadsOf(A));
    }

    /// <summary>
    /// A label that was removed before the kernel reached the item leaves an unlabelled artifact, which
    /// the route deletes through its ordinary path.
    /// </summary>
    [Fact]
    public async Task A_label_removed_before_the_kernel_is_reported_unlabeled_so_the_caller_deletes_it()
    {
        ScriptedLabelLedger ledger = new();

        ledger.Script(A, LabelRead.Of(Label(A, Guid.NewGuid())), LabelRead.None);

        ScriptedErasureKernel kernel = new();

        kernel.Script(A, ExaminedNotErased);

        CovenantSensitivePurgeOutcome outcome = Succeeded(await PurgeAsync(ledger, kernel, A));

        CovenantSensitivePurgeResult a = Assert.Single(outcome.Results);

        Assert.Equal(CovenantSensitivePurgeDisposition.Unlabeled, a.Disposition);

        Assert.Equal(CovenantErasureBlocker.None, a.Blocker);

        Assert.True(outcome.RequiresOrdinaryDelete(A));

        Assert.False(outcome.WasPurged(A));

        Assert.False(outcome.IsBlocked);

        Assert.Equal(2, ledger.ReadsOf(A));
    }

    /// <summary>
    /// Items are dispatched one at a time. Those the kernel erased before a blocked item are purged; the
    /// blocked item and every item after it are blocked, and the later ones are never sent to the kernel
    /// or read again.
    /// </summary>
    /// <remarks>
    /// D is scripted to erase cleanly if it were ever dispatched, so a coordinator that kept going after
    /// C would report it purged.
    /// </remarks>
    [Fact]
    public async Task Items_before_a_blocked_item_are_purged_and_items_after_it_are_blocked_unexamined()
    {
        Guid[] order = [A, B, C, D];

        ScriptedLabelLedger ledger = new();

        foreach (Guid id in order)
        {
            ledger.Script(id, LabelRead.Of(Label(id, Guid.NewGuid())));
        }

        ScriptedErasureKernel kernel = new();

        kernel.Script(A, Erased);

        kernel.Script(B, Erased);

        kernel.Script(C, new CovenantArtifactErasureProgress(1, 0, 0, CovenantErasureBlocker.ManualOwnershipMismatch));

        kernel.Script(D, Erased);

        CovenantSensitivePurgeOutcome outcome = Succeeded(await PurgeAsync(ledger, kernel, order));

        int TargetOrder(CovenantSensitivePurgeResult result) => Array.IndexOf(order, result.ArtifactId);

        CovenantSensitivePurgeDisposition[] expected =
        [
            CovenantSensitivePurgeDisposition.Purged,
            CovenantSensitivePurgeDisposition.Purged,
            CovenantSensitivePurgeDisposition.Blocked,
            CovenantSensitivePurgeDisposition.Blocked,
        ];

        Assert.Equal(expected, outcome.Results.OrderBy(TargetOrder).Select(static result => result.Disposition));

        Assert.All(
            outcome.Results.Where(static result => result.ArtifactId == C || result.ArtifactId == D),
            static result => Assert.Equal(CovenantErasureBlocker.ManualOwnershipMismatch, result.Blocker));

        Assert.Equal([[A], [B], [C]], kernel.Pages);

        Assert.Equal(
            new CovenantArtifactErasureProgress(3, 2, 0, CovenantErasureBlocker.ManualOwnershipMismatch),
            outcome.Progress);

        Assert.Equal(1, ledger.ReadsOf(D));
    }

    /// <summary>
    /// A label read that fails with a storage error refuses the whole purge before anything is
    /// dispatched.
    /// </summary>
    /// <remarks>
    /// Skipping the target that could not be read would report it unlabelled, and the route would then
    /// delete it through the ordinary path whatever its label says.
    /// </remarks>
    [Fact]
    public async Task A_storage_failure_while_resolving_labels_fails_closed()
    {
        ScriptedLabelLedger ledger = new();

        ledger.Script(A, LabelRead.Of(Label(A, Guid.NewGuid())));

        ledger.Script(B, LabelRead.StorageFailure);

        ScriptedErasureKernel kernel = new();

        kernel.Script(A, Erased);

        Result<CovenantSensitivePurgeOutcome> result = await PurgeAsync(ledger, kernel, A, B);

        Assert.True(result.IsFailure, "A storage failure while reading a label must refuse the purge.");

        Assert.Equal(ErrorCodes.Covenant.Unavailable, result.Error.Code);

        Assert.Empty(kernel.Pages);
    }

    /// <summary>
    /// The re-read that decides between unlabelled and stale fails closed on a storage error too.
    /// </summary>
    [Fact]
    public async Task A_storage_failure_while_re_reading_a_moved_label_fails_closed()
    {
        ScriptedLabelLedger ledger = new();

        ledger.Script(A, LabelRead.Of(Label(A, Guid.NewGuid())), LabelRead.StorageFailure);

        ScriptedErasureKernel kernel = new();

        kernel.Script(A, ExaminedNotErased);

        Result<CovenantSensitivePurgeOutcome> result = await PurgeAsync(ledger, kernel, A);

        Assert.True(result.IsFailure, "A storage failure while re-reading a label must refuse the purge.");

        Assert.Equal(ErrorCodes.Covenant.Unavailable, result.Error.Code);

        Assert.Equal(2, ledger.ReadsOf(A));
    }

    /// <summary>
    /// The write lease is still held when an item the kernel examined but did not erase has its label
    /// read again, so no other writer can move the label between that read and the answer.
    /// </summary>
    /// <remarks>
    /// The re-read checks the authority the coordinator built over its lease: a lease that had already
    /// been released answers stale, so a classification moved after the lease is disposed fails here.
    /// </remarks>
    [Fact]
    public async Task The_write_lease_is_still_held_while_a_moved_label_is_read_again()
    {
        ScriptedLabelLedger ledger = new();

        ScriptedErasureKernel kernel = new();

        Result? leaseAtReread = null;

        ledger.Script(A, LabelRead.Of(Label(A, Guid.NewGuid())), LabelRead.None);

        ledger.AtRead = async (artifactId, read) =>
        {
            if (read == 2)
            {
                leaseAtReread = await kernel.LastAuthority!.RevalidateAsync(CancellationToken.None);
            }
        };

        kernel.Script(A, ExaminedNotErased);

        _ = Succeeded(await PurgeAsync(ledger, kernel, A));

        Assert.NotNull(leaseAtReread);

        Assert.True(leaseAtReread!.IsSuccess, "The lease was already released when the label was read again.");

        Assert.Equal(2, ledger.ReadsOf(A));
    }

    /// <summary>
    /// An item that was examined and found unlabelled on the re-read is the caller's to delete, and it does
    /// not stop the walk: the item after it is still dispatched and purged.
    /// </summary>
    /// <remarks>
    /// Only a blocked item stops the walk. A walk that stopped on anything short of purged would record B
    /// blocked with no blocker, which a route turns into a manual-erasure refusal for an item nothing
    /// blocked.
    /// </remarks>
    [Fact]
    public async Task An_item_reread_as_unlabeled_does_not_stop_the_walk()
    {
        ScriptedLabelLedger ledger = new();

        ledger.Script(A, LabelRead.Of(Label(A, Guid.NewGuid())), LabelRead.None);

        ledger.Script(B, LabelRead.Of(Label(B, Guid.NewGuid())));

        ScriptedErasureKernel kernel = new();

        kernel.Script(A, ExaminedNotErased);

        kernel.Script(B, Erased);

        CovenantSensitivePurgeOutcome outcome = Succeeded(await PurgeAsync(ledger, kernel, A, B));

        Assert.Equal(
            [CovenantSensitivePurgeDisposition.Unlabeled, CovenantSensitivePurgeDisposition.Purged],
            [.. new[] { A, B }.Select(id => outcome.Results.Single(result => result.ArtifactId == id).Disposition)]);

        Assert.True(outcome.RequiresOrdinaryDelete(A));

        Assert.True(outcome.WasPurged(B));

        Assert.False(outcome.IsBlocked);

        Assert.Equal([[A], [B]], kernel.Pages);
    }

    /// <summary>
    /// A managed file after a blocked database item is recorded blocked with that blocker and never
    /// dispatched: nothing reads the managed-file inventory and the managed-file kernel is never called.
    /// </summary>
    /// <remarks>
    /// Database-owned items are walked first whatever order the caller named them in, so the file is
    /// named first here. The connection source and the managed-file kernel both throw if they are
    /// reached, so a dispatch fails the test rather than being recorded.
    /// </remarks>
    [Fact]
    public async Task A_managed_file_after_a_database_block_is_blocked_without_being_dispatched()
    {
        ScriptedLabelLedger ledger = new();

        ledger.Script(A, LabelRead.Of(Label(A, Guid.NewGuid())));

        ledger.Script(
            B,
            LabelRead.Of(CovenantErasureAuthorityFixture.Label(B, Guid.NewGuid(), SensitiveArtifactKind.ManagedWorkspaceFile)));

        ScriptedErasureKernel kernel = new();

        kernel.Script(A, new CovenantArtifactErasureProgress(1, 0, 0, CovenantErasureBlocker.ManualOwnershipMismatch));

        CovenantSensitivePurgeOutcome outcome = Succeeded(await PurgeTargetsAsync(
            ledger,
            kernel,
            new CovenantSensitivePurgeTarget(SensitiveArtifactKind.ManagedWorkspaceFile, B),
            new CovenantSensitivePurgeTarget(SensitiveArtifactKind.Saga, A)));

        Assert.All(
            outcome.Results,
            static result =>
            {
                Assert.Equal(CovenantSensitivePurgeDisposition.Blocked, result.Disposition);

                Assert.Equal(CovenantErasureBlocker.ManualOwnershipMismatch, result.Blocker);
            });

        Assert.Equal(2, outcome.Results.Count);

        Assert.Equal([[A]], kernel.Pages);

        // The file was resolved once and never read again.
        Assert.Equal(1, ledger.ReadsOf(B));
    }

    /// <summary>
    /// A labelled target whose executor is neither of the two the coordinator dispatches is blocked as an
    /// integrity failure, never left unlabelled for the caller's ordinary delete.
    /// </summary>
    /// <remarks>
    /// The policy defines two executors, so the third is reached through the coordinator's test seam. The
    /// target that can be deleted is still dispatched first, and nothing reaches the managed-file kernel.
    /// </remarks>
    [Fact]
    public async Task A_labelled_target_no_executor_can_delete_is_blocked_not_left_for_the_ordinary_delete()
    {
        ScriptedLabelLedger ledger = new();

        ledger.Script(A, LabelRead.Of(Label(A, Guid.NewGuid())));

        ledger.Script(B, LabelRead.Of(CovenantErasureAuthorityFixture.Label(B, Guid.NewGuid(), SensitiveArtifactKind.Embedding)));

        ScriptedErasureKernel kernel = new();

        kernel.Script(A, Erased);

        CovenantSensitivePurgeOutcome outcome = Succeeded(await PurgeTargetsAsync(
            ledger,
            kernel,
            kind => kind is SensitiveArtifactKind.Embedding
                ? (CovenantArtifactPurgeExecutor)byte.MaxValue
                : CovenantSensitiveArtifactPurgePolicy.Resolve(kind).Value.Executor,
            new CovenantSensitivePurgeTarget(SensitiveArtifactKind.Embedding, B),
            new CovenantSensitivePurgeTarget(SensitiveArtifactKind.Saga, A)));

        Assert.True(outcome.WasPurged(A));

        CovenantSensitivePurgeResult unrecognized = outcome.Results.Single(result => result.ArtifactId == B);

        Assert.Equal(CovenantSensitivePurgeDisposition.Blocked, unrecognized.Disposition);

        Assert.Equal(CovenantErasureBlocker.IntegrityFailure, unrecognized.Blocker);

        Assert.False(outcome.RequiresOrdinaryDelete(B));

        Assert.True(outcome.IsBlocked);

        Assert.Equal([[A]], kernel.Pages);
    }

    private static ArtifactSensitivityLabel Label(Guid artifactId, Guid labelId) =>
        CovenantErasureAuthorityFixture.Label(artifactId, labelId, SensitiveArtifactKind.Saga);

    private static CovenantSensitivePurgeOutcome Succeeded(Result<CovenantSensitivePurgeOutcome> result)
    {
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        return result.Value;
    }

    /// <summary>
    /// One purge of Saga targets through a coordinator built from the real gate, a Global write lease,
    /// and operator authority published for a retention purge.
    /// </summary>
    private static Task<Result<CovenantSensitivePurgeOutcome>> PurgeAsync(
        ScriptedLabelLedger ledger,
        ScriptedErasureKernel kernel,
        params Guid[] targets) =>
        PurgeTargetsAsync(
            ledger,
            kernel,
            [.. targets.Select(static id => new CovenantSensitivePurgeTarget(SensitiveArtifactKind.Saga, id))]);

    /// <summary>
    /// One purge of exactly these targets, through a coordinator built from the real gate, a Global write
    /// lease, and operator authority published for a retention purge.
    /// </summary>
    private static Task<Result<CovenantSensitivePurgeOutcome>> PurgeTargetsAsync(
        ScriptedLabelLedger ledger,
        ScriptedErasureKernel kernel,
        params CovenantSensitivePurgeTarget[] targets) =>
        PurgeTargetsAsync(ledger, kernel, executorOf: null, targets);

    private static async Task<Result<CovenantSensitivePurgeOutcome>> PurgeTargetsAsync(
        ScriptedLabelLedger ledger,
        ScriptedErasureKernel kernel,
        Func<SensitiveArtifactKind, CovenantArtifactPurgeExecutor>? executorOf,
        params CovenantSensitivePurgeTarget[] targets)
    {
        FakeCovenantAvailability availability = new();

        FakeCovenantAuthorityProvider authority = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability, authority);

        CovenantSensitivePurgeAuthorityScope scope = new();

        Assert.True(scope.Publish(CovenantErasureAuthorityFixture.OperatorContext(authority)).IsSuccess);

        CovenantSensitiveRetentionPurgeCoordinator coordinator = new(
            ledger,
            new UnreachableConnectionSource(),
            new CovenantManagedFileErasureRequestReader(),
            gate,
            CovenantErasureAuthorityFixture.Issuer(authority),
            availability,
            kernel,
            new UnreachableManagedFileKernel(),
            scope)
        {
            ExecutorForTesting = executorOf,
        };

        return await coordinator.PurgeAsync(targets, CancellationToken.None);
    }

    /// <summary>One scripted answer to a label read.</summary>
    private readonly record struct LabelRead(ArtifactSensitivityLabel? Label, bool Fails)
    {
        internal static LabelRead None => new(null, false);

        internal static LabelRead StorageFailure => new(null, true);

        internal static LabelRead Of(ArtifactSensitivityLabel label) => new(label, false);
    }

    /// <summary>
    /// A label ledger that answers each artifact's reads from a queue, in order, and counts them.
    /// </summary>
    /// <remarks>
    /// A read past the end of an artifact's script throws, so a coordinator that reads more often than a
    /// test expects fails loudly rather than reusing an answer.
    /// </remarks>
    private sealed class ScriptedLabelLedger : IArtifactSensitivityLedger
    {
        private readonly Dictionary<Guid, Queue<LabelRead>> _answers = [];

        private readonly Dictionary<Guid, int> _reads = [];

        /// <summary>Runs on every read, before it is answered, with the artifact and which read this is.</summary>
        internal Func<Guid, int, Task>? AtRead { get; set; }

        internal void Script(Guid artifactId, params LabelRead[] answers) => _answers[artifactId] = new(answers);

        internal int ReadsOf(Guid artifactId) => _reads.GetValueOrDefault(artifactId);

        public async Task<Result<ArtifactSensitivityLabel?>> TryReadLabelAsync(
            SensitiveArtifactKind artifactKind,
            Guid artifactId,
            CancellationToken cancellationToken)
        {
            _reads[artifactId] = ReadsOf(artifactId) + 1;

            if (AtRead is { } observe)
            {
                await observe(artifactId, ReadsOf(artifactId));
            }

            if (!_answers.TryGetValue(artifactId, out Queue<LabelRead>? answers) || answers.Count == 0)
            {
                throw new InvalidOperationException("The coordinator read a label this test did not script.");
            }

            LabelRead answer = answers.Dequeue();

            return answer.Fails
                ? throw new SqliteException("disk I/O error", 10)
                : Result<ArtifactSensitivityLabel?>.Success(answer.Label);
        }

        public Task<Result<LabeledArtifactWriteReceipt>> LabelAsync(
            DerivedArtifactWrite write,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The purge coordinator never writes a label.");

        public Task<Result<SessionSensitivityProjection>> ReadSessionProjectionAsync(
            Guid sessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The purge coordinator never reads a Session projection.");
    }

    /// <summary>
    /// A database erasure kernel that answers each item from a script and records every page it is
    /// handed.
    /// </summary>
    /// <remarks>
    /// A page is walked the way the real kernel walks one: items in order, stopping at the first blocked
    /// one.
    /// </remarks>
    private sealed class ScriptedErasureKernel : ICovenantProtectedArtifactErasureKernel
    {
        private readonly Dictionary<Guid, CovenantArtifactErasureProgress> _answers = [];

        private readonly List<Guid[]> _pages = [];

        internal IReadOnlyList<Guid[]> Pages => _pages;

        /// <summary>The authority the coordinator handed the most recent page.</summary>
        internal CovenantArtifactErasureAuthority? LastAuthority { get; private set; }

        internal void Script(Guid artifactId, CovenantArtifactErasureProgress progress) => _answers[artifactId] = progress;

        public ValueTask<Result<CovenantArtifactErasureProgress>> ErasePageAsync(
            CovenantProtectedArtifactErasurePage page,
            CovenantArtifactErasureAuthority authority,
            CancellationToken cancellationToken = default)
        {
            _pages.Add([.. page.Items.Select(static item => item.ArtifactId)]);

            LastAuthority = authority;

            CovenantArtifactErasureProgress progress = CovenantArtifactErasureProgress.Empty;

            foreach (CovenantProtectedArtifactErasureItem item in page.Items)
            {
                progress = progress.Add(_answers[item.ArtifactId]);

                if (progress.IsBlocked)
                {
                    break;
                }
            }

            return ValueTask.FromResult(Result<CovenantArtifactErasureProgress>.Success(progress));
        }
    }

    /// <summary>The managed-file inventory's connection, which a Saga or Lexicon purge never opens.</summary>
    private sealed class UnreachableConnectionSource : ICovenantConnectionSource
    {
        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A Saga or Lexicon purge never reads the managed-file inventory.");

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A Saga or Lexicon purge never reads the managed-file inventory.");
    }

    /// <summary>The managed-file kernel, which a Saga or Lexicon purge never reaches.</summary>
    private sealed class UnreachableManagedFileKernel : ICovenantManagedFileErasureKernel
    {
        public ValueTask<Result<CovenantArtifactErasureProgress>> EraseAsync(
            CovenantManagedFileErasureRequest request,
            CovenantArtifactErasureAuthority authority,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A Saga or Lexicon purge never reaches the managed-file kernel.");
    }
}
