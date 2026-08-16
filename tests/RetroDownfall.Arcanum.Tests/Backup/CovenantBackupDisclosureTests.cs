using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// The two durability barriers a physical backup crosses before it reads or writes a byte (§10.13).
/// </summary>
/// <remarks>
/// An ordered recorder rather than a real journal for the ordering cases, because what is under test
/// is <em>when</em> the acknowledgement commits relative to the effect. The journal's own durability
/// is covered by its suite; duplicating it here would only prove the fixture agrees with itself.
/// </remarks>
public sealed class CovenantBackupDisclosureTests
{

    private static readonly Guid Installation = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly Guid OperationId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

    [Fact]
    public async Task Each_barrier_commits_its_receipt_before_returning_to_the_caller()
    {

        RecordingJournal journal = new();

        CovenantBackupDisclosureBoundary boundary = Boundary(journal);

        Result<CovenantBackupDisclosureAcknowledgement> snapshot =
            await boundary.BeforeSnapshotReadAsync(
                OperationId,
                Digest(1),
                Digest(2),
                CancellationToken.None);

        Result<CovenantBackupDisclosureAcknowledgement> archive =
            await boundary.BeforeArchiveWriteAsync(
                OperationId,
                Digest(1),
                Digest(2),
                CancellationToken.None);

        Assert.True(snapshot.IsSuccess, Describe(snapshot));
        Assert.True(archive.IsSuccess, Describe(archive));

        Assert.Equal(2, journal.Drafts.Count);

        // Both are encrypted-backup effects, and both are nonrevocable: deleting the installation
        // cannot unmake a file the operator already holds.
        Assert.All(
            journal.Categories,
            static category => Assert.Equal(CovenantDisclosureEffectCategory.EncryptedBackup, category));

        Assert.All(
            journal.Drafts,
            static draft =>
            {

                Assert.Equal(CovenantEgressDestination.EncryptedBackup, draft.Destination);
                Assert.Equal(CovenantDisclosureRevocability.Nonrevocable, draft.Revocability);
                Assert.Equal(CovenantDisclosureSubjectKind.Operation, draft.SubjectKind);
                Assert.Equal(OperationId, draft.SubjectId);

            });

        // Two distinct effect identities. Folding a read and a write into one receipt would report a
        // disclosure that did not happen or miss one that did.
        Assert.NotEqual(
            journal.Drafts[0].EffectIdentityDigest,
            journal.Drafts[1].EffectIdentityDigest);

    }

    [Fact]
    public async Task A_failed_commit_prevents_its_effect_rather_than_being_reported_afterwards()
    {

        RecordingJournal journal = new() { Fail = true };

        CovenantBackupDisclosureBoundary boundary = Boundary(journal);

        Result<CovenantBackupDisclosureAcknowledgement> snapshot =
            await boundary.BeforeSnapshotReadAsync(
                OperationId,
                Digest(1),
                Digest(2),
                CancellationToken.None);

        Assert.True(snapshot.IsFailure);

    }

    [Fact]
    public async Task A_retry_allocates_a_new_physical_attempt_ordinal()
    {

        RecordingJournal journal = new();

        CovenantBackupDisclosureBoundary boundary = Boundary(journal);

        _ = await boundary.BeforeSnapshotReadAsync(
            OperationId,
            Digest(1),
            Digest(2),
            CancellationToken.None);

        _ = await boundary.BeforeSnapshotReadAsync(
            OperationId,
            Digest(1),
            Digest(2),
            CancellationToken.None);

        // A second attempt at the same effect is a second physical disclosure, not a replay of the
        // first. Counting is allowed to overstate and never to understate.
        Assert.NotEqual(
            journal.Drafts[0].EffectIdentityDigest,
            journal.Drafts[1].EffectIdentityDigest);

    }

    [Fact]
    public async Task An_installation_with_no_established_authority_discloses_nothing()
    {

        RecordingJournal journal = new();

        CovenantBackupDisclosureBoundary boundary = new(journal, static () => Guid.Empty, TimeProvider.System);

        Result<CovenantBackupDisclosureAcknowledgement> snapshot =
            await boundary.BeforeSnapshotReadAsync(
                OperationId,
                Digest(1),
                Digest(2),
                CancellationToken.None);

        Assert.True(snapshot.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.OperatorAuthorityUnavailable, snapshot.Error.Code);
        Assert.Empty(journal.Drafts);

    }

    [Fact]
    public void The_effect_identity_changes_with_every_field_and_names_no_path()
    {

        CovenantDigest baseline = CovenantBackupDisclosureBoundary.EffectIdentity(
            Installation,
            OperationId,
            CovenantBackupDisclosureEffect.SnapshotRead,
            1,
            Digest(1),
            Digest(2));

        Assert.NotEqual(
            baseline,
            CovenantBackupDisclosureBoundary.EffectIdentity(
                Guid.NewGuid(),
                OperationId,
                CovenantBackupDisclosureEffect.SnapshotRead,
                1,
                Digest(1),
                Digest(2)));

        Assert.NotEqual(
            baseline,
            CovenantBackupDisclosureBoundary.EffectIdentity(
                Installation,
                Guid.NewGuid(),
                CovenantBackupDisclosureEffect.SnapshotRead,
                1,
                Digest(1),
                Digest(2)));

        Assert.NotEqual(
            baseline,
            CovenantBackupDisclosureBoundary.EffectIdentity(
                Installation,
                OperationId,
                CovenantBackupDisclosureEffect.ArchiveWrite,
                1,
                Digest(1),
                Digest(2)));

        Assert.NotEqual(
            baseline,
            CovenantBackupDisclosureBoundary.EffectIdentity(
                Installation,
                OperationId,
                CovenantBackupDisclosureEffect.SnapshotRead,
                2,
                Digest(1),
                Digest(2)));

        Assert.NotEqual(
            baseline,
            CovenantBackupDisclosureBoundary.EffectIdentity(
                Installation,
                OperationId,
                CovenantBackupDisclosureEffect.SnapshotRead,
                1,
                Digest(9),
                Digest(2)));

        Assert.NotEqual(
            baseline,
            CovenantBackupDisclosureBoundary.EffectIdentity(
                Installation,
                OperationId,
                CovenantBackupDisclosureEffect.SnapshotRead,
                1,
                Digest(1),
                Digest(9)));

        // Deterministic: the same inputs always produce the same identity, which is what lets a retry
        // that can prove it never dispatched reuse the acknowledged identity.
        Assert.Equal(
            baseline,
            CovenantBackupDisclosureBoundary.EffectIdentity(
                Installation,
                OperationId,
                CovenantBackupDisclosureEffect.SnapshotRead,
                1,
                Digest(1),
                Digest(2)));

    }

    private static CovenantBackupDisclosureBoundary Boundary(RecordingJournal journal) =>
        new(journal, static () => Installation, TimeProvider.System);

    private static CovenantDigest Digest(byte seed) => new([.. Enumerable.Repeat(seed, 32)]);

    private static string Describe<T>(Result<T> result) =>
        result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : string.Empty;

    /// <summary>
    /// Records what was acknowledged and in what order, and can refuse on demand.
    /// </summary>
    private sealed class RecordingJournal : ICovenantDisclosureJournal
    {

        private ulong _ordinal;

        internal List<CovenantDisclosureDraft> Drafts { get; } = [];

        internal List<CovenantDisclosureEffectCategory> Categories { get; } = [];

        internal bool Fail { get; init; }

        public ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(
            CovenantDisclosureDraft draft,
            CovenantDisclosureEffectCategory category,
            ProviderCallSensitivity sensitivity,
            CancellationToken cancellationToken)
        {

            if (Fail)
            {

                return ValueTask.FromResult(
                    Result<CovenantDisclosureReceipt>.Failure(
                        new Error(ErrorCodes.Covenant.MaintenanceFailed, "The journal refused.")));

            }

            Drafts.Add(draft);

            Categories.Add(category);

            return ValueTask.FromResult(
                Result<CovenantDisclosureReceipt>.Success(
                    new CovenantDisclosureReceipt(draft, ++_ordinal)));

        }

    }

}
