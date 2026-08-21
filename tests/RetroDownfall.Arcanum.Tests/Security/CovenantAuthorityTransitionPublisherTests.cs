using System.Text;
using System.Reflection;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Publishing a committed transition: fresh keys and a fresh snapshot, or nothing at all.
/// </summary>
public sealed class CovenantAuthorityTransitionPublisherTests
{

    private static readonly Guid Installation = Guid.Parse("2C4A5E3B-9F17-4D0C-8A6E-1B3D5F70921A");

    private static readonly Guid Dataset = Guid.Parse("0D1E2F30-4152-4637-8899-AABBCCDDEEFF");

    private static readonly Guid NextDataset = Guid.Parse("11223344-5566-4778-899A-BBCCDDEEFF00");

    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_committed_reset_switches_dataset_keys_and_rejects_every_old_token()
    {

        using Harness harness = Harness.Create();

        Dictionary<CovenantEnvelopePurpose, string> tokens = Enum
            .GetValues<CovenantEnvelopePurpose>()
            .ToDictionary(
                static purpose => purpose,
                purpose => harness.Codec.Encode(
                    purpose,
                    [(byte)purpose],
                    TimeSpan.FromMinutes(30)).Value);

        OperatorAuthorityContext before = harness.Issuer.Issue(CovenantAuthorityRequirement.CovenantManage).Value;

        CovenantReadAuthorityEpoch readEpoch = harness.Issuer.IssueReadEpoch().Value;

        await using CovenantReadLease readLease = (await harness.Gate.AcquireReadAsync(
            CovenantOperationScope.Global,
            CancellationToken.None)).Value;

        Assert.All(tokens, pair => Assert.True(harness.Codec.Decode(pair.Key, pair.Value).IsSuccess));
        Assert.True(harness.Issuer.Revalidate(before).IsSuccess);

        Result published = await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 11, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
            harness.Lease,
            CancellationToken.None);

        Assert.True(published.IsSuccess);

        Assert.All(tokens, pair => Assert.False(harness.Codec.Decode(pair.Key, pair.Value).IsSuccess));

        Result revalidated = harness.Issuer.Revalidate(before);

        Assert.False(revalidated.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, revalidated.Error.Code);

        Assert.False(readEpoch.Matches(harness.Authority.Current));

        Assert.True((await readLease.RevalidateAsync(CancellationToken.None)).IsFailure);

        // The new generation is fully usable, which is what "publish before reopening admission" buys.
        Assert.True(harness.Codec.Encode(CovenantEnvelopePurpose.Cursor, [1], TimeSpan.FromMinutes(1)).IsSuccess);
        Assert.True(harness.Issuer.Issue(CovenantAuthorityRequirement.CovenantManage).IsSuccess);

    }

    [Fact]
    public async Task Final_publication_exposes_one_entire_predecessor_then_one_entire_successor()
    {

        using BlockingCovenantRuntimePublicationCheckpoint checkpoint = new(
            CovenantRuntimePublicationStep.CommittedBeforeSwap,
            CovenantRuntimePublicationStep.CommittedAfterSwap);

        using Harness harness = Harness.Create(publicationCheckpoint: checkpoint);

        CovenantRuntimeGenerationState predecessor = harness.Runtime.Current;

        OperatorAuthorityContext predecessorContext = harness.Issuer
            .Issue(CovenantAuthorityRequirement.CovenantManage).Value;

        string predecessorToken = harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [0x41],
            TimeSpan.FromMinutes(30)).Value;

        CovenantExclusiveRecoveryOwner owner = harness.Lease.Snapshot.RecoveryOwner!.Value;

        await using CovenantExclusiveLease lease = (await harness.Gate.AcquireExclusiveAsync(
            owner,
            CancellationToken.None)).Value;

        checkpoint.Arm();

        Task<Result> publishing = Task.Run(async () => await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 12, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
            lease,
            CancellationToken.None));

        checkpoint.WaitForBeforeSwap();

        Assert.False(publishing.IsCompleted);

        Assert.Same(predecessor, harness.Runtime.Current);

        Assert.Same(predecessor.Availability, harness.Availability.Current);

        Assert.Same(predecessor.ActiveAuthority, harness.Authority.Current);

        Assert.Same(predecessor.Keys, harness.Keys.Current);

        OperatorAuthorityContext during = harness.Issuer
            .Issue(CovenantAuthorityRequirement.CovenantManage).Value;

        Assert.Equal(predecessor.RuntimeAuthorityGeneration, during.RuntimeAuthorityGeneration);

        Assert.True(harness.Issuer.Revalidate(predecessorContext).IsSuccess);

        using ManualResetEventSlim codecStarted = new(initialState: false);

        Task<Result<CovenantEnvelopeBody>> codecReader = Task.Run(() =>
        {

            codecStarted.Set();

            return harness.Codec.Decode(
                CovenantEnvelopePurpose.Cursor,
                predecessorToken);

        });

        using ManualResetEventSlim gateStarted = new(initialState: false);

        using ManualResetEventSlim firstGateAttemptRefused = new(initialState: false);

        using ManualResetEventSlim retryGateReader = new(initialState: false);

        Task<CovenantReadLease> gateReader = Task.Run(async () =>
        {

            gateStarted.Set();

            Result<CovenantReadLease> whileClosed = await harness.Gate.AcquireReadAsync(
                CovenantOperationScope.Global,
                CancellationToken.None);

            Assert.True(whileClosed.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.Unavailable, whileClosed.Error.Code);

            firstGateAttemptRefused.Set();

            Assert.True(retryGateReader.Wait(TimeSpan.FromSeconds(10)));

            return (await harness.Gate.AcquireReadAsync(
                CovenantOperationScope.Global,
                CancellationToken.None)).Value;

        });

        Assert.True(codecStarted.Wait(TimeSpan.FromSeconds(10)));

        Assert.True(gateStarted.Wait(TimeSpan.FromSeconds(10)));

        Assert.False(codecReader.IsCompleted);

        Assert.False(gateReader.IsCompleted);

        checkpoint.AdvanceToAfterSwap();

        Assert.False(publishing.IsCompleted);

        Assert.False(codecReader.IsCompleted);

        Assert.False(gateReader.IsCompleted);

        CovenantRuntimeGenerationState successor = harness.Runtime.Current;

        Assert.NotSame(predecessor, successor);

        Assert.Same(successor.Availability, harness.Availability.Current);

        Assert.Same(successor.ActiveAuthority, harness.Authority.Current);

        Assert.Same(successor.Keys, harness.Keys.Current);

        Assert.Equal(NextDataset, successor.Availability.DatasetGeneration);

        Assert.Equal(successor.RuntimeAuthorityGeneration, successor.ActiveAuthority!.RuntimeAuthorityGeneration);

        OperatorAuthorityContext afterSwap = harness.Issuer
            .Issue(CovenantAuthorityRequirement.CovenantManage).Value;

        Assert.Equal(successor.RuntimeAuthorityGeneration, afterSwap.RuntimeAuthorityGeneration);

        Assert.False(harness.Issuer.Revalidate(predecessorContext).IsSuccess);

        checkpoint.ReleaseAfterSwap();

        Result<CovenantEnvelopeBody> decoded = await codecReader;

        Result published = await publishing;

        checkpoint.AssertNoFailure();

        Assert.True(published.IsSuccess);

        Assert.True(decoded.IsFailure);

        Assert.True(firstGateAttemptRefused.Wait(TimeSpan.FromSeconds(10)));

        Assert.True((await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CancellationToken.None)).IsSuccess);

        retryGateReader.Set();

        await using CovenantReadLease afterRead = await gateReader;

        Assert.Equal(successor.RuntimeAuthorityGeneration, afterRead.Snapshot.RuntimeAuthorityGeneration);

        Assert.Equal(successor.Availability.DatasetGeneration, afterRead.Snapshot.DatasetGeneration);

        Assert.Equal(successor.Availability.Generation, afterRead.Snapshot.CapabilityGeneration);

        Assert.Equal(successor.ActiveAuthority.AuthorityEpoch, afterRead.Snapshot.AuthorityEpoch);

        string successorToken = harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [0x42],
            TimeSpan.FromMinutes(30)).Value;

        Assert.True(harness.Codec.Decode(
            CovenantEnvelopePurpose.Cursor,
            successorToken).IsSuccess);

        OperatorAuthorityContext after = harness.Issuer
            .Issue(CovenantAuthorityRequirement.CovenantManage).Value;

        Assert.Equal(successor.RuntimeAuthorityGeneration, after.RuntimeAuthorityGeneration);

        Assert.Equal(successor.ActiveAuthority!.AuthorityEpoch, after.AuthorityEpoch);

        Assert.False(harness.Issuer.Revalidate(predecessorContext).IsSuccess);

    }

    [Theory]
    [InlineData(CovenantExclusiveOperation.CovenantReset, CovenantHealthTransition.Reset)]
    [InlineData(CovenantExclusiveOperation.HealthyCatalogFactoryErasure, CovenantHealthTransition.Reset)]
    [InlineData(CovenantExclusiveOperation.BackupRestore, CovenantHealthTransition.Restore)]
    [InlineData(CovenantExclusiveOperation.CovenantFamilyReinitialize, CovenantHealthTransition.FamilyReinitialize)]
    [InlineData(CovenantExclusiveOperation.SchemaRepair, CovenantHealthTransition.SchemaRepair)]
    public async Task Publication_records_the_exact_exclusive_health_transition(
        CovenantExclusiveOperation operation,
        CovenantHealthTransition expectedHealthTransition)
    {

        using Harness harness = Harness.Create(operation: operation);

        Result published = await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 11, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
            harness.Lease,
            CancellationToken.None);

        Assert.True(published.IsSuccess);

        Assert.Equal(expectedHealthTransition, harness.Availability.Current.LastHealthTransition);

    }

    [Fact]
    public async Task A_revoked_lease_publishes_nothing()
    {

        using Harness harness = Harness.Create();

        harness.Lease.Live = false;

        string cursor = harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [1],
            TimeSpan.FromMinutes(30)).Value;

        Result published = await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 12, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
            harness.Lease,
            CancellationToken.None);

        Assert.False(published.IsSuccess);

        Assert.False(harness.Codec.Decode(CovenantEnvelopePurpose.Cursor, cursor).IsSuccess);
        Assert.Null(harness.Authority.Current);
        Assert.True(harness.Runtime.Current.AuthorityRetired);
        Assert.Equal(harness.Lease.Snapshot.RecoveryOwner, harness.Runtime.Current.RecoveryOwner);

    }

    [Fact]
    public async Task A_stale_lease_invoked_after_a_committed_winner_cannot_retire_that_winner()
    {

        using Harness harness = Harness.Create();

        CovenantRuntimeGenerationState expected = harness.Runtime.Current;

        CovenantCommittedAuthorityTransition competing = Transition(
            authorityEpoch: 12,
            canonicalEnvelopeEpoch: 4,
            dataset: NextDataset);

        Result<CovenantAvailabilitySnapshot> built = harness.Availability.BuildCommittedTransition(
            expected.Availability,
            competing.Capability,
            CovenantHealthTransition.Reset);

        Assert.True(built.IsSuccess);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = harness.Keys.PrepareRekey(competing);

        Assert.True(prepared.IsSuccess);

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        Assert.True(harness.Runtime.PublishCommitted(
            expected,
            owned,
            competing,
            built.Value).IsSuccess);

        CovenantRuntimeGenerationState winner = harness.Runtime.Current;

        harness.Lease.Live = false;

        harness.Lease.RevalidationException = new InvalidOperationException(
            "A generation-mismatched lease must be rejected before revalidation.");

        Result published = await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 12, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
            harness.Lease,
            CancellationToken.None);

        Assert.True(published.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, published.Error.Code);

        Assert.Same(winner, harness.Runtime.Current);

        Assert.Same(winner.Keys, harness.Keys.Current);

        Assert.Same(winner.ActiveAuthority, harness.Authority.Current);

        Assert.Same(winner.Availability, harness.Availability.Current);

        Assert.False(winner.AuthorityRetired);

        Assert.Null(winner.RecoveryOwner);

    }

    [Fact]
    public async Task A_transition_cannot_move_an_authority_counter_backwards()
    {

        foreach (CovenantCommittedAuthorityTransition regression in new[]
        {
            Transition(authorityEpoch: 10),
            Transition(masterKeyVersion: 3),
            Transition(canonicalEnvelopeEpoch: 2),
            Transition(recoveryEnvelopeEpoch: 1),
        })
        {

            using Harness harness = Harness.Create();

            Result published = await harness.Publisher.PublishCommittedAsync(
                regression,
                harness.Lease,
                CancellationToken.None);

            Assert.False(published.IsSuccess);
            Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, published.Error.Code);
            Assert.Null(harness.Authority.Current);
            Assert.True(harness.Runtime.Current.AuthorityRetired);

        }

    }

    [Fact]
    public async Task A_retired_exact_owner_cannot_publish_a_lower_canonical_envelope_epoch()
    {

        using Harness harness = Harness.Create();

        CovenantExclusiveRecoveryOwner owner = harness.Lease.Snapshot.RecoveryOwner!.Value;

        CovenantExclusiveLease failedLease = (await harness.Gate.AcquireExclusiveAsync(
            owner,
            CancellationToken.None)).Value;

        Result failed = await harness.Publisher.PublishCommittedAsync(
            new CovenantCommittedAuthorityTransition(
                Guid.NewGuid().ToString().ToUpperInvariant(),
                authorityEpoch: 11,
                masterKeyVersion: 4,
                canonicalEnvelopeEpoch: 4,
                recoveryEnvelopeEpoch: 2,
                CovenantHostToolsState.Clean,
                transitionId: null,
                Capability(NextDataset)),
            failedLease,
            CancellationToken.None);

        Assert.True(failed.IsFailure);

        await failedLease.DisposeAsync();

        await using CovenantExclusiveLease resumed = (await harness.Gate.ResumeExclusiveAsync(
            owner,
            CancellationToken.None)).Value;

        Result rollback = await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 11, canonicalEnvelopeEpoch: 2, dataset: NextDataset),
            resumed,
            CancellationToken.None);

        Assert.True(rollback.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, rollback.Error.Code);

        Assert.True(harness.Runtime.Current.AuthorityRetired);

        Assert.Null(harness.Runtime.Current.Keys);

        Assert.Equal(3, harness.Runtime.Current.CanonicalEnvelopeEpoch);

        Assert.Null(harness.Authority.Current);

    }

    [Fact]
    public async Task A_transition_cannot_change_the_installation_identity()
    {

        using Harness harness = Harness.Create();

        Result published = await harness.Publisher.PublishCommittedAsync(
            new CovenantCommittedAuthorityTransition(
                Guid.NewGuid().ToString().ToUpperInvariant(),
                authorityEpoch: 12,
                masterKeyVersion: 4,
                canonicalEnvelopeEpoch: 4,
                recoveryEnvelopeEpoch: 2,
                CovenantHostToolsState.Clean,
                transitionId: null,
                Capability(NextDataset)),
            harness.Lease,
            CancellationToken.None);

        Assert.False(published.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, published.Error.Code);

    }

    [Fact]
    public async Task An_availability_winner_makes_final_publication_stale_and_survives_retirement()
    {

        using Harness harness = Harness.Create();

        CovenantAvailabilitySnapshot? winner = null;

        harness.Lease.BeforeExecute = () =>
        {

            winner = harness.Availability.PublishFeatureEnabled(featureEnabled: false);

        };

        Result published = await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 12, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
            harness.Lease,
            CancellationToken.None);

        Assert.True(published.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, published.Error.Code);

        Assert.NotNull(winner);

        Assert.True(harness.Runtime.Current.AuthorityRetired);

        Assert.Null(harness.Runtime.Current.Keys);

        Assert.Same(winner, harness.Runtime.Current.Availability);

        Assert.False(harness.Runtime.Current.Availability.FeatureEnabled);

        Assert.Equal("sha256-canonical", harness.Runtime.Current.Availability.CanonicalInstalledFingerprint);

        Assert.Equal("sha256-accelerator", harness.Runtime.Current.Availability.AcceleratorInstalledFingerprint);

    }

    [Fact]
    public async Task An_erasure_capture_before_a_feature_race_is_stale_and_retires_the_captured_generation()
    {

        using Harness harness = Harness.Create();

        CovenantRuntimeGenerationState captured = harness.Runtime.Current;

        CovenantAvailabilitySnapshot winner =
            harness.Availability.PublishFeatureEnabled(featureEnabled: false);

        Result published = await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 12, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
            harness.Lease,
            captured,
            CancellationToken.None);

        Assert.True(published.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, published.Error.Code);

        Assert.True(harness.Runtime.Current.AuthorityRetired);

        Assert.Null(harness.Runtime.Current.Keys);

        Assert.Same(winner, harness.Runtime.Current.Availability);

        Assert.Equal(harness.Lease.Snapshot.RecoveryOwner, harness.Runtime.Current.RecoveryOwner);

    }

    [Fact]
    public async Task A_different_authority_generation_winner_survives_the_losing_publishers_retirement()
    {

        using Harness harness = Harness.Create();

        CovenantRuntimeGenerationState? winner = null;

        harness.Lease.BeforeExecute = () =>
        {

            CovenantRuntimeGenerationState expected = harness.Runtime.Current;

            CovenantCommittedAuthorityTransition competing = Transition(
                authorityEpoch: 13,
                canonicalEnvelopeEpoch: 5,
                dataset: NextDataset);

            Result<CovenantAvailabilitySnapshot> built = harness.Availability.BuildCommittedTransition(
                expected.Availability,
                competing.Capability,
                CovenantHealthTransition.Reset);

            Assert.True(built.IsSuccess);

            Result<CovenantPreparedEnvelopeKeyGeneration> prepared = harness.Keys.PrepareRekey(competing);

            Assert.True(prepared.IsSuccess);

            using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

            Assert.True(harness.Runtime.PublishCommitted(
                expected,
                owned,
                competing,
                built.Value).IsSuccess);

            winner = harness.Runtime.Current;

        };

        Result published = await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 12, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
            harness.Lease,
            CancellationToken.None);

        Assert.True(published.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, published.Error.Code);

        Assert.NotNull(winner);

        Assert.Same(winner, harness.Runtime.Current);

        Assert.False(harness.Runtime.Current.AuthorityRetired);

        Assert.NotNull(harness.Authority.Current);

        Assert.Equal(13, harness.Authority.Current!.AuthorityEpoch);

        Assert.Equal(NextDataset, harness.Runtime.Current.Availability.DatasetGeneration);

        Assert.True(harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [1],
            TimeSpan.FromMinutes(1)).IsSuccess);

    }

    [Fact]
    public async Task The_exact_retired_owner_resumes_and_republishes_from_the_resident_root()
    {

        using Harness harness = Harness.Create();

        CovenantExclusiveRecoveryOwner owner = harness.Lease.Snapshot.RecoveryOwner!.Value;

        CovenantExclusiveLease failedLease = (await harness.Gate.AcquireExclusiveAsync(
            owner,
            CancellationToken.None)).Value;

        Result failed = await harness.Publisher.PublishCommittedAsync(
            new CovenantCommittedAuthorityTransition(
                Guid.NewGuid().ToString().ToUpperInvariant(),
                authorityEpoch: 11,
                masterKeyVersion: 4,
                canonicalEnvelopeEpoch: 4,
                recoveryEnvelopeEpoch: 2,
                CovenantHostToolsState.Clean,
                transitionId: null,
                Capability(NextDataset)),
            failedLease,
            CancellationToken.None);

        Assert.True(failed.IsFailure);

        Assert.True(harness.Runtime.Current.AuthorityRetired);

        Assert.Null(harness.Authority.Current);

        Assert.Null(harness.Keys.Current);

        Assert.Equal(
            ErrorCodes.Covenant.ForbiddenAuthority,
            (await failedLease.RevalidateAsync(CancellationToken.None)).Error.Code);

        await failedLease.DisposeAsync();

        CovenantExclusiveRecoveryOwner wrongOwner = new(
            Guid.NewGuid(),
            owner.Operation,
            owner.EffectDigest);

        Assert.True((await harness.Gate.ResumeExclusiveAsync(
            wrongOwner,
            CancellationToken.None)).IsFailure);

        Assert.True((await harness.Gate.AcquireReadAsync(
            CovenantOperationScope.Global,
            CancellationToken.None)).IsFailure);

        CovenantExclusiveLease resumed = (await harness.Gate.ResumeExclusiveAsync(
            owner,
            CancellationToken.None)).Value;

        Assert.Equal(
            harness.Runtime.Current.RuntimeAuthorityGeneration,
            resumed.Snapshot.RuntimeAuthorityGeneration);

        Assert.True((await resumed.RevalidateAsync(CancellationToken.None)).IsSuccess);

        long firstRetiredGeneration = resumed.Snapshot.RuntimeAuthorityGeneration;

        Result retryFailed = await harness.Publisher.PublishCommittedAsync(
            new CovenantCommittedAuthorityTransition(
                Guid.NewGuid().ToString().ToUpperInvariant(),
                authorityEpoch: 11,
                masterKeyVersion: 4,
                canonicalEnvelopeEpoch: 4,
                recoveryEnvelopeEpoch: 2,
                CovenantHostToolsState.Clean,
                transitionId: null,
                Capability(NextDataset)),
            resumed,
            CancellationToken.None);

        Assert.True(retryFailed.IsFailure);

        Assert.True(harness.Runtime.Current.AuthorityRetired);

        Assert.True(harness.Runtime.Current.RuntimeAuthorityGeneration > firstRetiredGeneration);

        await resumed.DisposeAsync();

        Assert.True((await harness.Gate.ResumeExclusiveAsync(
            wrongOwner,
            CancellationToken.None)).IsFailure);

        await using CovenantExclusiveLease retry = (await harness.Gate.ResumeExclusiveAsync(
            owner,
            CancellationToken.None)).Value;

        Result recovered = await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 11, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
            retry,
            CancellationToken.None);

        Assert.True(recovered.IsSuccess);

        Assert.False(harness.Runtime.Current.AuthorityRetired);

        Assert.Null(harness.Runtime.Current.RecoveryOwner);

        Assert.NotNull(harness.Authority.Current);

        Assert.NotNull(harness.Keys.Current);

        Assert.Equal(
            ErrorCodes.Covenant.ForbiddenAuthority,
            (await retry.RevalidateAsync(CancellationToken.None)).Error.Code);

        Assert.True((await retry.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CancellationToken.None)).IsSuccess);

        await using CovenantReadLease read = (await harness.Gate.AcquireReadAsync(
            CovenantOperationScope.Global,
            CancellationToken.None)).Value;

        Assert.Equal(
            harness.Runtime.Current.RuntimeAuthorityGeneration,
            read.Snapshot.RuntimeAuthorityGeneration);

    }

    [Fact]
    public async Task Already_cancelled_publication_retires_the_observed_generation()
    {

        using Harness harness = Harness.Create();

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Publisher.PublishCommittedAsync(
                Transition(authorityEpoch: 12, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
                harness.Lease,
                cancellation.Token).AsTask());

        AssertRetired(harness);

    }

    [Fact]
    public async Task A_thrown_lease_revalidation_retires_the_observed_generation()
    {

        using Harness harness = Harness.Create();

        harness.Lease.RevalidationException = new InvalidOperationException("Injected lease proof failure.");

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Publisher.PublishCommittedAsync(
                Transition(authorityEpoch: 12, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
                harness.Lease,
                CancellationToken.None).AsTask());

        AssertRetired(harness);

    }

    [Fact]
    public async Task A_thrown_exact_held_proof_retires_the_observed_generation()
    {

        using Harness harness = Harness.Create();

        harness.Lease.ExecutionException = new InvalidOperationException("Injected exact-held proof failure.");

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Publisher.PublishCommittedAsync(
                Transition(authorityEpoch: 12, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
                harness.Lease,
                CancellationToken.None).AsTask());

        AssertRetired(harness);

    }

    [Fact]
    public async Task Derivation_failure_retires_the_observed_generation()
    {

        ThrowingDerivationCheckpoint checkpoint = new();

        using Harness harness = Harness.Create(checkpoint);

        checkpoint.Enabled = true;

        Result published = await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 12, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
            harness.Lease,
            CancellationToken.None);

        Assert.True(published.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, published.Error.Code);

        AssertRetired(harness);

    }

    [Fact]
    public async Task A_post_disposition_lease_cannot_publish_and_retires_the_observed_generation()
    {

        using Harness harness = Harness.Create();

        CovenantExclusiveLease lease = (await harness.Gate.AcquireExclusiveAsync(
            harness.Lease.Snapshot.RecoveryOwner!.Value,
            CancellationToken.None)).Value;

        Assert.True((await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CancellationToken.None)).IsSuccess);

        Result published = await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 12, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
            lease,
            CancellationToken.None);

        Assert.True(published.IsFailure);

        AssertRetired(harness);

        await lease.DisposeAsync();

    }

    [Fact]
    public async Task A_replaced_registration_cannot_publish_and_retires_the_observed_generation()
    {

        using Harness harness = Harness.Create();

        CovenantExclusiveRecoveryOwner owner = harness.Lease.Snapshot.RecoveryOwner!.Value;

        CovenantExclusiveLease original = (await harness.Gate.AcquireExclusiveAsync(
            owner,
            CancellationToken.None)).Value;

        await original.DisposeAsync();

        await using CovenantExclusiveLease replacement = (await harness.Gate.ResumeExclusiveAsync(
            owner,
            CancellationToken.None)).Value;

        Result published = await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 12, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
            original,
            CancellationToken.None);

        Assert.True(published.IsFailure);

        AssertRetired(harness);

    }

    [Fact]
    public void A_committed_transition_validates_its_own_shape()
    {

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Transition(authorityEpoch: 0));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Transition(masterKeyVersion: 0));

        _ = Assert.Throws<ArgumentException>(() => Transition(dataset: Guid.Empty));

    }

    [Fact]
    public void Committed_capability_validated_fields_have_no_mutation_setters()
    {

        PropertyInfo[] properties = typeof(CovenantCommittedCapabilityTransition)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.NotEmpty(properties);

        Assert.All(properties, property => Assert.Null(property.GetSetMethod(nonPublic: true)));

    }

    [Fact]
    public void Committed_capability_constructor_rejects_malformed_state()
    {

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Capability(
            Dataset,
            canonical: (CovenantCapabilityState)0));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Capability(
            Dataset,
            ftsSynchronization: (CovenantFtsSynchronizationState)0));

        _ = Assert.Throws<ArgumentException>(() => Capability(
            Dataset,
            canonicalInstalledFingerprint: null));

        _ = Assert.Throws<ArgumentException>(() => Capability(
            Dataset,
            appliedDatasetGeneration: Dataset,
            appliedSequence: null,
            appliedCampaignDeletionSequence: 0));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Capability(
            Dataset,
            canonicalAppliedCampaignDeletionSequence: -1));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Capability(
            Dataset,
            canonicalAppliedSessionDeletionSequence: -1));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Capability(
            Dataset,
            cleanupAppliedCampaignSequence: -1));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Capability(
            Dataset,
            cleanupAppliedSessionSequence: -1));

    }

    private static CovenantCommittedAuthorityTransition Transition(
        long authorityEpoch = 11,
        uint masterKeyVersion = 4,
        long canonicalEnvelopeEpoch = 3,
        long recoveryEnvelopeEpoch = 2,
        Guid? dataset = null) =>
        new(
            Installation.ToString().ToUpperInvariant(),
            authorityEpoch,
            masterKeyVersion,
            canonicalEnvelopeEpoch,
            recoveryEnvelopeEpoch,
            CovenantHostToolsState.Clean,
            transitionId: null,
            Capability(dataset ?? Dataset));

    private static CovenantCommittedCapabilityTransition Capability(
        Guid dataset,
        CovenantCapabilityState canonical = CovenantCapabilityState.Healthy,
        string? canonicalInstalledFingerprint = "sha256-canonical",
        Guid? appliedDatasetGeneration = null,
        long? appliedSequence = null,
        long? appliedCampaignDeletionSequence = null,
        long canonicalAppliedCampaignDeletionSequence = 0,
        long canonicalAppliedSessionDeletionSequence = 0,
        CovenantFtsSynchronizationState ftsSynchronization = CovenantFtsSynchronizationState.Dirty,
        long cleanupAppliedCampaignSequence = 0,
        long cleanupAppliedSessionSequence = 0) =>
        new(
            ExpectedGeneration: 2,
            Generation: 3,
            FeatureEnabled: true,
            canonical,
            CanonicalSchemaVersion: 1,
            canonicalInstalledFingerprint,
            CovenantCapabilityState.Healthy,
            AcceleratorSchemaVersion: 1,
            AcceleratorInstalledFingerprint: "sha256-accelerator",
            dataset,
            CanonicalSequence: 0,
            CoreCampaignDeletionSequence: 0,
            canonicalAppliedCampaignDeletionSequence,
            canonicalAppliedSessionDeletionSequence,
            appliedDatasetGeneration,
            appliedSequence,
            appliedCampaignDeletionSequence,
            AcceleratorEpoch: 1,
            ftsSynchronization,
            RebuildRequired: true,
            cleanupAppliedCampaignSequence,
            cleanupAppliedSessionSequence,
            CleanupFullSweepRequired: false,
            CanonicalDiagnosticCode: null,
            AcceleratorDiagnosticCode: null);

    private static void AssertRetired(Harness harness)
    {

        Assert.True(harness.Runtime.Current.AuthorityRetired);

        Assert.Null(harness.Runtime.Current.Keys);

        Assert.Null(harness.Authority.Current);

        Assert.Equal(harness.Lease.Snapshot.RecoveryOwner, harness.Runtime.Current.RecoveryOwner);

    }

    private sealed class Harness : IDisposable
    {

        private Harness(
            CovenantRuntimeGenerationProvider runtime,
            CovenantEnvelopeMasterKeyProvider keys,
            CovenantAuthoritySnapshotProvider authority,
            CovenantAvailability availability,
            CovenantOperationGate gate,
            CovenantEnvelopeCodec codec,
            OperatorAuthorityContextIssuer issuer,
            CovenantAuthorityTransitionPublisher publisher,
            StubExclusiveLease lease)
        {

            Runtime = runtime;

            Keys = keys;

            Authority = authority;

            Availability = availability;

            Gate = gate;

            Codec = codec;

            Issuer = issuer;

            Publisher = publisher;

            Lease = lease;

        }

        public CovenantRuntimeGenerationProvider Runtime { get; }

        public CovenantEnvelopeMasterKeyProvider Keys { get; }

        public CovenantAuthoritySnapshotProvider Authority { get; }

        public CovenantAvailability Availability { get; }

        public CovenantOperationGate Gate { get; }

        public CovenantEnvelopeCodec Codec { get; }

        public OperatorAuthorityContextIssuer Issuer { get; }

        public CovenantAuthorityTransitionPublisher Publisher { get; }

        public StubExclusiveLease Lease { get; }

        public static Harness Create(
            ICovenantEnvelopeDerivationCheckpoint? derivationCheckpoint = null,
            CovenantExclusiveOperation operation = CovenantExclusiveOperation.CovenantReset,
            ICovenantRuntimePublicationCheckpoint? publicationCheckpoint = null)
        {

            CovenantRuntimeGenerationProvider runtime = publicationCheckpoint is null
                ? new CovenantRuntimeGenerationProvider()
                : new CovenantRuntimeGenerationProvider(publicationCheckpoint);

            CovenantEnvelopeMasterKeyProvider keys = derivationCheckpoint is null
                ? new CovenantEnvelopeMasterKeyProvider(runtime)
                : new CovenantEnvelopeMasterKeyProvider(
                    runtime,
                    derivationCheckpoint,
                    CovenantEnvelopeKeyAccessCheckpoint.None);

            Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareInitial(
                Encoding.UTF8.GetBytes("master-key-material"),
                new CovenantEnvelopeBootstrapKeyInput(
                    Installation.ToString().ToUpperInvariant(),
                    masterKeyVersion: 4,
                    canonicalEnvelopeEpoch: 3,
                    recoveryEnvelopeEpoch: 2,
                    Dataset));

            using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

            CovenantAvailabilitySnapshot bootAvailability = runtime.PublishAvailability(
                _ => InitialAvailability());

            CovenantRuntimeGenerationState expected = runtime.Current;

            _ = runtime.Initialize(
                expected,
                owned,
                new CovenantAuthoritySnapshot(
                    RuntimeAuthorityGeneration: 1,
                    Installation.ToString().ToUpperInvariant(),
                    AuthorityEpoch: 11,
                    MasterKeyVersion: 4,
                    RecoveryEnvelopeEpoch: 2,
                    CovenantHostToolsState.Clean,
                    null),
                bootAvailability);

            CovenantAuthoritySnapshotProvider authority = new(runtime);

            CovenantAvailability availability = new(runtime);

            CovenantOperationGate gate = new(runtime, new NoCampaignProbe());

            return new Harness(
                runtime,
                keys,
                authority,
                availability,
                gate,
                new CovenantEnvelopeCodec(keys, FakeClock(Now)),
                new OperatorAuthorityContextIssuer(authority),
                new CovenantAuthorityTransitionPublisher(runtime, keys, availability),
                new StubExclusiveLease(operation));

        }

        public void Dispose()
        {

            Keys.Dispose();

            Runtime.Dispose();

        }

        private static CovenantAvailabilitySnapshot InitialAvailability() =>
            new(
                Generation: 1,
                FeatureEnabled: true,
                Canonical: CovenantCapabilityState.Healthy,
                CanonicalSchemaVersion: 1,
                CanonicalInstalledFingerprint: "sha256-canonical",
                Accelerator: CovenantCapabilityState.Healthy,
                AcceleratorSchemaVersion: 1,
                AcceleratorInstalledFingerprint: "sha256-accelerator",
                Dataset,
                CanonicalSequence: 0,
                CoreCampaignDeletionSequence: 0,
                AppliedDatasetGeneration: Dataset,
                AppliedSequence: 0,
                AppliedCampaignDeletionSequence: 0,
                AcceleratorEpoch: 1,
                CovenantFtsSynchronizationState.Synchronized,
                RebuildRequired: false,
                LastHealthTransition: CovenantHealthTransition.Bootstrap,
                CanonicalDiagnosticCode: null,
                AcceleratorDiagnosticCode: null);

    }

    private sealed class StubExclusiveLease : ICovenantExclusiveOperationLease
    {

        public StubExclusiveLease(CovenantExclusiveOperation operation)
        {

            Snapshot = new CovenantOperationLeaseSnapshot(
                RegistrationId: Guid.Parse("5F6E7D8C-9B0A-4132-8455-667788990011"),
                RuntimeAuthorityGeneration: 1,
                Kind: CovenantLeaseKind.Exclusive,
                Coverage: CovenantLeaseCoverage.Installation,
                Scope: null,
                DatasetGeneration: Dataset,
                CapabilityGeneration: 1,
                AuthorityEpoch: 11,
                CanonicalSequence: 0,
                CampaignAvailabilityGeneration: null,
                CampaignPathRevision: null,
                AcceleratorEpoch: null,
                AppliedCampaignDeletionSequence: null,
                RecoveryOwner: new CovenantExclusiveRecoveryOwner(
                    Guid.Parse("77777777-8888-4999-8AAA-BBBBBBBBBBBB"),
                    operation,
                    new CovenantDigest([.. Enumerable.Repeat((byte)0x44, CovenantLimits.DigestBytes)])),
                CleanupOnlyHistoricalCampaign: false);

        }

        public bool Live { get; set; } = true;

        public Action? BeforeExecute { get; set; }

        public Exception? RevalidationException { get; set; }

        public Exception? ExecutionException { get; set; }

        public CovenantOperationLeaseSnapshot Snapshot { get; }

        public CancellationToken Revocation => CancellationToken.None;

        public Result ExecuteWhileHeld(Func<Result> callback)
        {

            if (ExecutionException is { } executionException)
            {

                throw executionException;

            }

            if (!Live)
            {

                return Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "This lease was revoked."));

            }

            BeforeExecute?.Invoke();

            return callback();

        }

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken)
        {

            if (RevalidationException is { } revalidationException)
            {

                throw revalidationException;

            }

            return ValueTask.FromResult(
                Live
                    ? Result.Success()
                    : Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "This lease was revoked.")));

        }

        public ValueTask<Result> CompleteAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

    private sealed class NoCampaignProbe : ICovenantCampaignScopeProbe
    {

        public ValueTask<Result<CovenantCampaignScopeState>> ResolveAsync(
            Guid campaignId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<Result<CovenantCampaignScopeState>>(CovenantCampaignScopeState.Live);

    }

    private sealed class ThrowingDerivationCheckpoint : ICovenantEnvelopeDerivationCheckpoint
    {

        internal bool Enabled { get; set; }

        public void Reached(CovenantEnvelopeDerivationStep step, int purposeKeysDerived)
        {

            if (Enabled && step == CovenantEnvelopeDerivationStep.PurposeKeyDerived)
            {

                throw new InvalidOperationException("Injected key derivation failure.");

            }

        }

        public void Zeroized(CovenantEnvelopeSensitiveBufferKind kind, bool isZero)
        {
        }

    }


    /// <summary>A fixed clock, so envelope timestamps and expiry are exact rather than approximate.</summary>
    private static FakeTimeProvider FakeClock(DateTimeOffset now)
    {

        FakeTimeProvider provider = new();

        provider.SetUtcNow(now);

        return provider;

    }

}
