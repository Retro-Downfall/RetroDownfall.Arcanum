using System.Buffers.Text;

using System.Collections.Immutable;

using System.Runtime.InteropServices;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

[Collection("WorkspacePathPolicy")]
public sealed class InstallationResetActiveStoreTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public async Task DisposeAsync()
    {

        SecureFileReader.AfterOpenForTests = null;

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task New_v2_publication_writes_revision_zero_anchor_before_revision_one_envelope()
    {

        string guardedRoot = _workspace.CreateSubdir("arcanum-v2-begin");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        List<string> events = [];

        RecordingCredentialStore credentials = new(events);

        InstallationResetActiveFilePersistence files = new(events.Add);

        InstallationResetActiveStore store = new(guardedRoot, credentials, files);

        Guid installationId = Guid.Parse("11111111-2222-4333-8444-555555555555");

        InstallationResetActiveRecord record = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            OperationId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
        };

        Result<InstallationResetActivePublication> result = await store.BeginAsync(
            heldLock,
            installationId,
            record,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(1UL, result.Value.Envelope.Revision);

        Assert.Equal(InstallationResetActiveRecordAuthenticator.ZeroDigest,
            result.Value.Envelope.PreviousEnvelopeDigest);

        Assert.Equal(1UL, result.Value.Anchor.Revision);

        Assert.Equal(result.Value.EnvelopeDigest, result.Value.Anchor.EnvelopeDigest);

        AssertOrdered(
            events,
            "key:readback",
            "anchor:set:Active:0",
            "anchor:readback:Active:0",
            "file:temporary-flushed",
            "file:atomic-replace",
            "file:parent-flushed");

    }

    [Fact]
    public async Task New_v2_publication_rereads_authenticates_and_then_verifies_the_anchor()
    {

        string guardedRoot = _workspace.CreateSubdir("arcanum-v2-reread");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        List<string> events = [];

        RecordingCredentialStore credentials = new(events);

        InstallationResetActiveStore store = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(events.Add));

        Result<InstallationResetActivePublication> result = await store.BeginAsync(
            heldLock,
            Guid.Parse("21111111-2222-4333-8444-555555555555"),
            CreateRecord(InstallationResetPhase.Prepared),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        AssertOrdered(
            events,
            "file:parent-flushed",
            "file:secure-reread",
            "key:open-existing",
            "anchor:compare-read:Active:0",
            "anchor:set:Active:1",
            "anchor:readback:Active:1");

        Result<InstallationResetActiveRecoveryState> inspected = await store.InspectAsync(
            CancellationToken.None);

        Assert.True(inspected.IsSuccess, inspected.Error.Message);

        Assert.Equal(
            InstallationResetActiveRecoveryOutcome.AuthenticatedV2,
            inspected.Value.Outcome);

        Assert.Equal(result.Value.EnvelopeDigest, inspected.Value.Publication!.EnvelopeDigest);

    }

    [Fact]
    public async Task Begin_maps_oversized_checkpoint_copy_failure_to_content_free_integrity()
    {

        // Mutation caught: allowing the bounded-copy ArgumentException to escape reveals the
        // rejected projection shape instead of returning the store's content-free integrity error.
        string guardedRoot = _workspace.CreateSubdir("checkpoint-oversized-copy");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        InstallationResetActiveStore store = new(
            guardedRoot,
            new RecordingCredentialStore([]));

        Guid installationId = Guid.Parse("2b111111-2222-4333-8444-555555555555");

        InstallationResetActiveRecord record = CreateCheckpointRecord(
            installationId,
            HostToolsMarkerPairResetPhase.PairJournaled);

        HostToolsMarkerPairResetCheckpointV1 oneEntry = WithCampaignInventory(
            record.HostToolsMarkerPairReset!);

        record = record with
        {
            HostToolsMarkerPairReset = oneEntry with
            {
                CampaignInventory = Enumerable.Repeat(
                        oneEntry.CampaignInventory[0],
                        4097)
                    .ToImmutableArray(),
            },
        };

        Assert.Throws<ArgumentException>(() =>
            InstallationResetActivePayloadV3.FromRecord(record));

        Result<InstallationResetActivePublication> result = await store.BeginAsync(
            heldLock,
            installationId,
            record,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.Equal(
            "The installation-reset active evidence did not authenticate.",
            result.Error.Message);

        Assert.False(File.Exists(store.ActivePath));

    }

    [Fact]
    public async Task Publication_cancellation_after_atomic_replace_finishes_the_bounded_checkpoint()
    {

        string guardedRoot = _workspace.CreateSubdir("arcanum-v2-commit-cancellation");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        using CancellationTokenSource cancellation = new();

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(step =>
            {

                if (string.Equals(step, "file:atomic-replace", StringComparison.Ordinal))
                {

                    cancellation.Cancel();

                }

            }));

        InstallationResetActivePublication publication = Value(await store.BeginAsync(
            heldLock,
            Guid.Parse("2a111111-2222-4333-8444-555555555555"),
            CreateRecord(InstallationResetPhase.Prepared),
            cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);

        Assert.Equal(1UL, publication.Anchor.Revision);

        Assert.Equal(
            InstallationResetActiveRecoveryOutcome.AuthenticatedV2,
            Value(await store.InspectAsync(CancellationToken.None)).Outcome);

    }

    [Fact]
    public async Task Publication_cancellation_before_atomic_replace_preserves_the_opening_anchor()
    {

        string guardedRoot = _workspace.CreateSubdir("arcanum-v2-precommit-cancellation");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        using CancellationTokenSource cancellation = new();

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(step =>
            {

                if (string.Equals(step, "file:temporary-flushed", StringComparison.Ordinal))
                {

                    cancellation.Cancel();

                }

            }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.BeginAsync(
            heldLock,
            Guid.Parse("2b111111-2222-4333-8444-555555555555"),
            CreateRecord(InstallationResetPhase.Prepared),
            cancellation.Token));

        Assert.False(File.Exists(store.ActivePath));

        InstallationResetActiveAnchorV1 opening = CredentialAnchor(credentials);

        Assert.Equal(InstallationResetActiveAnchorState.Active, opening.State);

        Assert.Equal(0UL, opening.Revision);

        Assert.Equal(
            InstallationResetActiveRecordAuthenticator.ZeroDigest,
            opening.EnvelopeDigest);

        Assert.True((await store.RecoverAsync(
            heldLock,
            CancellationToken.None)).IsFailure);

    }

    [Fact]
    public async Task Advance_allows_only_null_to_pair_journaled_then_same_or_next_proven_pair_phase()
    {

        // Mutation caught: treating the typed checkpoint as an ordinary nullable payload member
        // permits introduction after a skipped destructive effect, removal, or phase jumps.
        string guardedRoot = _workspace.CreateSubdir("checkpoint-pair-phases");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        List<string> events = [];

        RecordingCredentialStore credentials = new(events);

        InstallationResetActiveStore store = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(events.Add));

        Guid installationId = Guid.Parse("11111111-2222-4333-8444-555555555555");

        InstallationResetActiveRecord initial = CreateCheckpointRecord(
            installationId,
            checkpointPhase: null);

        InstallationResetActivePublication publication = Value(await store.BeginAsync(
            heldLock,
            installationId,
            initial,
            CancellationToken.None));

        InstallationResetActiveRecord skippedIntroduction = CreateCheckpointRecord(
            installationId,
            HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted,
            initial.OperationId);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            publication,
            skippedIntroduction,
            CancellationToken.None)).IsFailure);

        InstallationResetActiveRecord journaled = CreateCheckpointRecord(
            installationId,
            HostToolsMarkerPairResetPhase.PairJournaled,
            initial.OperationId);

        publication = Value(await store.AdvanceAsync(
            heldLock,
            publication,
            journaled,
            CancellationToken.None));

        publication = Value(await store.AdvanceAsync(
            heldLock,
            publication,
            journaled,
            CancellationToken.None));

        InstallationResetActiveRecord next = CreateCheckpointRecord(
            installationId,
            HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted,
            initial.OperationId);

        publication = Value(await store.AdvanceAsync(
            heldLock,
            publication,
            next,
            CancellationToken.None));

        InstallationResetActiveRecord skipped = CreateCheckpointRecord(
            installationId,
            HostToolsMarkerPairResetPhase.PairAbsenceVerified,
            initial.OperationId);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            publication,
            skipped,
            CancellationToken.None)).IsFailure);

    }

    [Fact]
    public async Task Advance_cannot_remove_regress_skip_or_substitute_restart_or_inventory_evidence()
    {

        // Mutation caught: record/reference equality or phase-only comparison lets a caller replace
        // the restart proof or campaign inventory while retaining a valid checkpoint shape.
        string guardedRoot = _workspace.CreateSubdir("checkpoint-immutable-evidence");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        InstallationResetActiveStore store = new(
            guardedRoot,
            new RecordingCredentialStore([]));

        Guid installationId = Guid.Parse("21111111-2222-4333-8444-555555555555");

        InstallationResetActiveRecord journaled = CreateCheckpointRecord(
            installationId,
            HostToolsMarkerPairResetPhase.PairJournaled);

        InstallationResetActivePublication publication = Value(await store.BeginAsync(
            heldLock,
            installationId,
            journaled,
            CancellationToken.None));

        HostToolsMarkerPairResetCheckpointV1 checkpoint =
            journaled.HostToolsMarkerPairReset!;

        HostProcessToolsOsMarkerEvidence substitutedOs = new(
            checkpoint.RestartProof.OsMarkerEvidence.InstallationIdentity,
            checkpoint.RestartProof.OsMarkerEvidence.TransitionId,
            checkpoint.RestartProof.OsMarkerEvidence.TaintMasterKeyVersion,
            checkpoint.RestartProof.OsMarkerEvidence.TaintFingerprint,
            checkpoint.RestartProof.OsMarkerEvidence.MarkerBytesDigest,
            Digest(0xD1));

        HostProcessToolsMatchedPair substitutedPair = new(
            checkpoint.RestartProof.DatabaseMarkerEvidence,
            substitutedOs);

        HostToolsMarkerPairResetCheckpointV1 substitutedRestart = checkpoint with
        {
            RestartProof = checkpoint.RestartProof with
            {
                OsMarkerEvidence = substitutedOs,
                PairEvidenceDigest = Value(
                    FullInstallationResetMarkerPairResetDigests.PairEvidence(
                        substitutedPair)),
            },
        };

        ImmutableArray<CampaignMarkerInventoryEntryV1> substitutedInventory =
        [
            new CampaignMarkerInventoryEntryV1(
                Guid.Parse("31111111-2222-4333-8444-555555555555"),
                PriorPathRevision: 1,
                Digest(0x12),
                Digest(0x32),
                Digest(0x52),
                Digest(0x72)),
        ];

        CovenantDigest substitutedInventoryDigest = Value(
            FullInstallationResetMarkerPairResetDigests.CampaignInventory(
                substitutedInventory));

        FullInstallationResetSignedAttestationProjectionV1 signed =
            checkpoint.RestartProof.SignedAttestation;

        HostToolsMarkerPairResetCheckpointV1 substitutedCampaigns = checkpoint with
        {
            CampaignInventory = substitutedInventory,
            CampaignMarkerInventoryDigest = substitutedInventoryDigest,
            OwnerEffectDigest = Value(
                FullInstallationResetMarkerPairResetDigests.FullResetEffect(
                    signed.OperationId,
                    signed.InstallationId,
                    signed.HostToolsTransitionId,
                    signed.TaintMasterKeyVersion,
                    signed.AuthorityFingerprint,
                    signed.DatabaseMarkerDigest,
                    signed.OsMarkerDigest,
                    signed.RemediationActionDigest,
                    substitutedInventoryDigest)),
        };

        InstallationResetActiveRecord[] invalid =
        [
            journaled with { HostToolsMarkerPairReset = null },
            journaled with
            {
                HostToolsMarkerPairReset = checkpoint with
                {
                    Phase = HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted,
                },
            },
            journaled with { HostToolsMarkerPairReset = substitutedRestart },
            journaled with { HostToolsMarkerPairReset = substitutedCampaigns },
        ];

        foreach (InstallationResetActiveRecord candidate in invalid)
        {
            Assert.True((await store.AdvanceAsync(
                heldLock,
                publication,
                candidate,
                CancellationToken.None)).IsFailure);
        }

        InstallationResetActiveRecord databaseDeleted = journaled with
        {
            HostToolsMarkerPairReset = checkpoint with
            {
                Phase = HostToolsMarkerPairResetPhase.DatabaseMarkerCompareDeleted,
            },
        };

        publication = Value(await store.AdvanceAsync(
            heldLock,
            publication,
            databaseDeleted,
            CancellationToken.None));

        Assert.True((await store.AdvanceAsync(
            heldLock,
            publication,
            journaled,
            CancellationToken.None)).IsFailure);

    }

    [Fact]
    public async Task Advance_allows_pair_absent_null_receipt_to_exact_prepared_receipt()
    {

        // Mutation caught: requiring checkpoint equality after pair absence prevents the one
        // durable publication that freezes the ordered cleanup intent vector before deletion.
        string guardedRoot = _workspace.CreateSubdir("checkpoint-prepare-receipt");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        InstallationResetActiveStore store = new(
            guardedRoot,
            new RecordingCredentialStore([]));

        Guid installationId = Guid.Parse("41111111-2222-4333-8444-555555555555");

        InstallationResetActiveRecord pairAbsent = CreateCheckpointRecord(
            installationId,
            HostToolsMarkerPairResetPhase.PairAbsenceVerified);

        ImmutableArray<Guid> intentIds =
        [
            Guid.Parse("51111111-2222-4333-8444-555555555555"),
            Guid.Parse("61111111-2222-4333-8444-555555555555"),
        ];

        pairAbsent = pairAbsent with
        {
            HostToolsMarkerPairReset = WithCampaignInventory(
                pairAbsent.HostToolsMarkerPairReset!,
                intentIds.Length),
        };

        InstallationResetActivePublication publication = Value(await store.BeginAsync(
            heldLock,
            installationId,
            pairAbsent,
            CancellationToken.None));

        InstallationResetActiveRecord prepared = pairAbsent with
        {
            HostToolsMarkerPairReset = PreparedCheckpoint(
                pairAbsent.HostToolsMarkerPairReset!,
                intentIds),
        };

        publication = Value(await store.AdvanceAsync(
            heldLock,
            publication,
            prepared,
            CancellationToken.None));

        Assert.Equal(
            intentIds,
            publication.Payload.HostToolsMarkerPairReset!.OrderedMarkerIntentIds);

        Assert.Equal(0UL, publication.Payload.HostToolsMarkerPairReset.DeletedCount);

        Assert.Equal(0UL, publication.Payload.HostToolsMarkerPairReset.OrphanCount);

    }

    [Fact]
    public async Task Advance_allows_only_fixed_vector_prepared_zero_counts_then_one_terminal_count_publication()
    {

        // Mutation caught: allowing record equality or any all-present receipt transition permits
        // vector substitution, repeated terminal publication, or a second zero-campaign receipt.
        string guardedRoot = _workspace.CreateSubdir("checkpoint-terminal-receipt");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        InstallationResetActiveStore store = new(
            guardedRoot,
            new RecordingCredentialStore([]));

        Guid installationId = Guid.Parse("71111111-2222-4333-8444-555555555555");

        InstallationResetActiveRecord pairAbsent = CreateCheckpointRecord(
            installationId,
            HostToolsMarkerPairResetPhase.PairAbsenceVerified);

        ImmutableArray<Guid> intentIds =
        [
            Guid.Parse("81111111-2222-4333-8444-555555555555"),
            Guid.Parse("91111111-2222-4333-8444-555555555555"),
        ];

        pairAbsent = pairAbsent with
        {
            HostToolsMarkerPairReset = WithCampaignInventory(
                pairAbsent.HostToolsMarkerPairReset!,
                intentIds.Length),
        };

        InstallationResetActivePublication publication = Value(await store.BeginAsync(
            heldLock,
            installationId,
            pairAbsent,
            CancellationToken.None));

        HostToolsMarkerPairResetCheckpointV1 preparedCheckpoint = PreparedCheckpoint(
            pairAbsent.HostToolsMarkerPairReset!,
            intentIds);

        InstallationResetActiveRecord prepared = pairAbsent with
        {
            HostToolsMarkerPairReset = preparedCheckpoint,
        };

        publication = Value(await store.AdvanceAsync(
            heldLock,
            publication,
            prepared,
            CancellationToken.None));

        ImmutableArray<Guid> reordered = [intentIds[1], intentIds[0]];

        InstallationResetActiveRecord substitutedVector = prepared with
        {
            HostToolsMarkerPairReset = preparedCheckpoint with
            {
                OrderedMarkerIntentIds = reordered,
                MarkerIntentVectorDigest = Value(
                    FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(
                        reordered)),
            },
        };

        Assert.True((await store.AdvanceAsync(
            heldLock,
            publication,
            substitutedVector,
            CancellationToken.None)).IsFailure);

        InstallationResetActiveRecord terminal = prepared with
        {
            HostToolsMarkerPairReset = preparedCheckpoint with
            {
                DeletedCount = 1,
                OrphanCount = 1,
            },
        };

        publication = Value(await store.AdvanceAsync(
            heldLock,
            publication,
            terminal,
            CancellationToken.None));

        Assert.True((await store.AdvanceAsync(
            heldLock,
            publication,
            terminal,
            CancellationToken.None)).IsFailure);

        InstallationResetActiveRecord substitutedCounts = terminal with
        {
            HostToolsMarkerPairReset = terminal.HostToolsMarkerPairReset! with
            {
                DeletedCount = 2,
                OrphanCount = 0,
            },
        };

        Assert.True((await store.AdvanceAsync(
            heldLock,
            publication,
            substitutedCounts,
            CancellationToken.None)).IsFailure);

        string zeroRoot = _workspace.CreateSubdir("checkpoint-zero-terminal");

        using ArcanumMaintenanceLock zeroLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(zeroRoot));

        InstallationResetActiveStore zeroStore = new(
            zeroRoot,
            new RecordingCredentialStore([]));

        Guid zeroInstallationId = Guid.Parse("a1111111-2222-4333-8444-555555555555");

        InstallationResetActiveRecord zeroPairAbsent = CreateCheckpointRecord(
            zeroInstallationId,
            HostToolsMarkerPairResetPhase.PairAbsenceVerified);

        InstallationResetActivePublication zeroPublication = Value(
            await zeroStore.BeginAsync(
                zeroLock,
                zeroInstallationId,
                zeroPairAbsent,
                CancellationToken.None));

        InstallationResetActiveRecord zeroTerminal = zeroPairAbsent with
        {
            HostToolsMarkerPairReset = PreparedCheckpoint(
                zeroPairAbsent.HostToolsMarkerPairReset!,
                []),
        };

        zeroPublication = Value(await zeroStore.AdvanceAsync(
            zeroLock,
            zeroPublication,
            zeroTerminal,
            CancellationToken.None));

        Assert.True((await zeroStore.AdvanceAsync(
            zeroLock,
            zeroPublication,
            zeroTerminal,
            CancellationToken.None)).IsFailure);

    }

    [Fact]
    public async Task Recovery_round_trips_structurally_equal_immutable_checkpoint_vectors()
    {

        // Mutation caught: reference/record equality rejects recovered evidence, while retained
        // ImmutableArray backing stores let callers rewrite a later recovery projection.
        string guardedRoot = _workspace.CreateSubdir("checkpoint-structural-recovery");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        InstallationResetActiveStore store = new(
            guardedRoot,
            new RecordingCredentialStore([]));

        Guid installationId = Guid.Parse("b1111111-2222-4333-8444-555555555555");

        InstallationResetActiveRecord pairAbsent = CreateCheckpointRecord(
            installationId,
            HostToolsMarkerPairResetPhase.PairAbsenceVerified);

        HostToolsMarkerPairResetCheckpointV1 withInventory = WithCampaignInventory(
            pairAbsent.HostToolsMarkerPairReset!);

        ImmutableArray<Guid> intents =
        [
            Guid.Parse("c1111111-2222-4333-8444-555555555555"),
        ];

        InstallationResetActiveRecord prepared = pairAbsent with
        {
            HostToolsMarkerPairReset = PreparedCheckpoint(withInventory, intents),
        };

        InstallationResetActivePublication published = Value(await store.BeginAsync(
            heldLock,
            installationId,
            prepared,
            CancellationToken.None));

        InstallationResetActiveRecoveryState recovered = Value(await store.RecoverAsync(
            heldLock,
            CancellationToken.None));

        Assert.Equal(
            InstallationResetActiveRecoveryOutcome.AuthenticatedV2,
            recovered.Outcome);

        HostToolsMarkerPairResetCheckpointV1 expected =
            published.Payload.HostToolsMarkerPairReset!;

        HostToolsMarkerPairResetCheckpointV1 actual =
            recovered.Publication!.Payload.HostToolsMarkerPairReset!;

        Assert.NotSame(expected, actual);

        Assert.Equal(expected.Phase, actual.Phase);

        Assert.Equal(
            expected.CampaignMarkerInventoryDigest,
            actual.CampaignMarkerInventoryDigest);

        Assert.Equal(expected.OwnerEffectDigest, actual.OwnerEffectDigest);

        Assert.Equal(
            expected.CampaignInventory.Select(static entry => entry.CampaignId),
            actual.CampaignInventory.Select(static entry => entry.CampaignId));

        Assert.Equal(expected.OrderedMarkerIntentIds, actual.OrderedMarkerIntentIds);

        Assert.NotSame(
            ImmutableCollectionsMarshal.AsArray(expected.CampaignInventory),
            ImmutableCollectionsMarshal.AsArray(actual.CampaignInventory));

        Assert.NotSame(
            ImmutableCollectionsMarshal.AsArray(expected.OrderedMarkerIntentIds!.Value),
            ImmutableCollectionsMarshal.AsArray(actual.OrderedMarkerIntentIds!.Value));

    }

    [Fact]
    public async Task One_ahead_anchor_recovery_preserves_the_exact_typed_checkpoint()
    {

        // Mutation caught: one-ahead recovery that drops the checkpoint or compares nested
        // evidence by reference cannot advance the anchor to the authenticated landed envelope.
        string guardedRoot = _workspace.CreateSubdir("checkpoint-one-ahead");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(guardedRoot, credentials);

        Guid installationId = Guid.Parse("e1111111-2222-4333-8444-555555555555");

        InstallationResetActiveRecord pairAbsent = CreateCheckpointRecord(
            installationId,
            HostToolsMarkerPairResetPhase.PairAbsenceVerified);

        ImmutableArray<Guid> intents =
        [
            Guid.Parse("f1111111-2222-4333-8444-555555555555"),
        ];

        InstallationResetActiveRecord prepared = pairAbsent with
        {
            HostToolsMarkerPairReset = PreparedCheckpoint(
                WithCampaignInventory(pairAbsent.HostToolsMarkerPairReset!),
                intents),
        };

        InstallationResetActivePublication publication = Value(await store.BeginAsync(
            heldLock,
            installationId,
            prepared,
            CancellationToken.None));

        InstallationResetActiveRecord terminal = prepared with
        {
            Version = 2,
            HostToolsMarkerPairReset = prepared.HostToolsMarkerPairReset! with
            {
                DeletedCount = 1,
                OrphanCount = 0,
            },
        };

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guardedRoot));

        using InstallationResetActiveRecordKeyLease key = Value(
            new InstallationResetActiveRecordKeyProvider(credentials)
                .OpenExisting(profile));

        InstallationResetActiveEnvelopeV2 ahead = Value(
            InstallationResetActiveRecordAuthenticator.Seal(
                key,
                publication.Location,
                installationId,
                revision: 2,
                publication.EnvelopeDigest,
                InstallationResetActivePayloadV3.FromRecord(terminal)));

        WriteEnvelope(store.ActivePath, ahead);

        InstallationResetActiveRecoveryState recovered = Value(await store.RecoverAsync(
            heldLock,
            CancellationToken.None));

        Assert.Equal(2UL, recovered.Publication!.Anchor.Revision);

        HostToolsMarkerPairResetCheckpointV1 checkpoint =
            recovered.Publication.Payload.HostToolsMarkerPairReset!;

        Assert.Equal(terminal.HostToolsMarkerPairReset!.Phase, checkpoint.Phase);

        Assert.Equal(
            terminal.HostToolsMarkerPairReset.CampaignMarkerInventoryDigest,
            checkpoint.CampaignMarkerInventoryDigest);

        Assert.Equal(
            terminal.HostToolsMarkerPairReset.OrderedMarkerIntentIds,
            checkpoint.OrderedMarkerIntentIds);

        Assert.Equal(1UL, checkpoint.DeletedCount);

        Assert.Equal(0UL, checkpoint.OrphanCount);

    }

    [Fact]
    public async Task Advance_chains_exactly_one_revision_and_rejects_regression_skip_overflow_or_changed_binding()
    {

        string guardedRoot = _workspace.CreateSubdir("arcanum-v2-advance");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence());

        InstallationResetActiveRecord record = CreateRecord(InstallationResetPhase.Prepared) with
        {
            CredentialResults =
            [
                new InstallationResetCredentialResult(
                    "master-api-key",
                    InstallationResetItemStatus.Pending),
            ],
            DataHandoff = InstallationResetDataHandoff.HostFactoryErasure,
        };

        InstallationResetActivePublication begun = Value(await store.BeginAsync(
            heldLock,
            Guid.Parse("31111111-2222-4333-8444-555555555555"),
            record,
            CancellationToken.None));

        InstallationResetActiveRecord next = record with
        {
            Version = 2,
            Phase = InstallationResetPhase.DataResetComplete,
            PointOfNoReturn = true,
            RowsDeleted = 5,
            FilesDeleted = 3,
            EstimatedBytesDeleted = 11,
            CredentialResults =
            [
                new InstallationResetCredentialResult(
                    "master-api-key",
                    InstallationResetItemStatus.Deleted),
            ],
            OnlineDataCompletion = new InstallationResetOnlineDataCompletion(
                Guid.Parse("3a111111-2222-4333-8444-555555555555"),
                record.OperationId,
                "data-plan",
                RowsDeleted: 5,
                FilesDeleted: 3,
                EstimatedBytesDeleted: 11,
                DerivedRecordsDeleted: 2),
        };

        InstallationResetActivePublication advanced = Value(await store.AdvanceAsync(
            heldLock,
            begun,
            next,
            CancellationToken.None));

        Assert.Equal(2UL, advanced.Envelope.Revision);

        Assert.Equal(begun.EnvelopeDigest, advanced.Envelope.PreviousEnvelopeDigest);

        Assert.Equal(advanced.EnvelopeDigest, advanced.Anchor.EnvelopeDigest);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced,
            next with { RowsDeleted = 4 },
            CancellationToken.None)).IsFailure);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced,
            next with { Phase = InstallationResetPhase.Prepared },
            CancellationToken.None)).IsFailure);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced,
            next with { PointOfNoReturn = false },
            CancellationToken.None)).IsFailure);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced,
            next with { OnlineDataCompletion = null },
            CancellationToken.None)).IsFailure);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced,
            next with
            {
                CredentialResults =
                [
                    new InstallationResetCredentialResult(
                        "master-api-key",
                        InstallationResetItemStatus.Pending),
                ],
            },
            CancellationToken.None)).IsFailure);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced,
            next with
            {
                AcceptedBinding = next.AcceptedBinding with { BindingId = "substituted" },
            },
            CancellationToken.None)).IsFailure);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced with
            {
                Envelope = advanced.Envelope with { Revision = 4 },
            },
            next,
            CancellationToken.None)).IsFailure);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced with
            {
                Envelope = advanced.Envelope with
                {
                    Revision = InstallationResetActiveRecordAuthenticator.MaxRevision,
                },
                Anchor = advanced.Anchor with
                {
                    Revision = InstallationResetActiveRecordAuthenticator.MaxRevision,
                },
            },
            next,
            CancellationToken.None)).IsFailure);

        InstallationResetActiveRecoveryState recovered = Value(
            await store.RecoverAsync(heldLock, CancellationToken.None));

        Assert.Equal(2UL, recovered.Publication!.Envelope.Revision);

        Assert.Equal(5, recovered.Publication.Payload.RowsDeleted);

    }

    [Theory]
    [InlineData("removed")]
    [InlineData("version")]
    [InlineData("operation")]
    [InlineData("installation")]
    [InlineData("attestation-digest")]
    [InlineData("nonce-digest")]
    [InlineData("issuer-digest")]
    [InlineData("accepted-at")]
    public async Task Advance_cannot_remove_or_substitute_an_authenticated_full_claim(
        string mutation)
    {

        string guardedRoot = _workspace.CreateSubdir(
            "arcanum-v2-claim-" + mutation);

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(guardedRoot, credentials);

        Guid installationId = Guid.Parse(
            "32111111-2222-4333-8444-555555555555");

        Guid operationId = Guid.Parse(
            "33111111-2222-4333-8444-555555555555");

        FullInstallationResetRemediationClaimV1 claim = new(
            Version: 1,
            operationId,
            installationId,
            ClaimDigest(0x10),
            ClaimDigest(0x20),
            ClaimDigest(0x30),
            new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero));

        InstallationResetActiveRecord record = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            OperationId = operationId,
            Scope = InstallationResetScope.All,
            Workspace = new DataRetentionWorkspaceBinding(
                Guid.Parse("34111111-2222-4333-8444-555555555555"),
                "/selected/workspace"),
            LastErrorCode = ErrorCodes.Data.RecoveryRequired,
            FullInstallationResetRemediationClaim = claim,
        };

        InstallationResetActivePublication begun = Value(await store.BeginAsync(
            heldLock,
            installationId,
            record,
            CancellationToken.None));

        FullInstallationResetRemediationClaimV1? changed = mutation switch
        {
            "removed" => null,
            "version" => claim with { Version = 2 },
            "operation" => claim with { OperationId = Guid.NewGuid() },
            "installation" => claim with { InstallationId = Guid.NewGuid() },
            "attestation-digest" => claim with
            {
                AttestationDigest = ClaimDigest(0x40),
            },
            "nonce-digest" => claim with { NonceDigest = ClaimDigest(0x50) },
            "issuer-digest" => claim with { IssuerDigest = ClaimDigest(0x60) },
            "accepted-at" => claim with
            {
                AcceptedAtUtc = claim.AcceptedAtUtc.AddSeconds(1),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        Result<InstallationResetActivePublication> advanced = await store.AdvanceAsync(
            heldLock,
            begun,
            record with { FullInstallationResetRemediationClaim = changed },
            CancellationToken.None);

        Assert.True(advanced.IsFailure);

        InstallationResetActiveRecoveryState recovered = Value(
            await store.RecoverAsync(heldLock, CancellationToken.None));

        Assert.Equal(1UL, recovered.Publication!.Envelope.Revision);

        Assert.Equal(
            claim,
            recovered.Publication.Payload.FullInstallationResetRemediationClaim);

    }

    [Fact]
    public async Task Recovery_accepts_only_an_exact_anchor_envelope_pair_or_one_authenticated_envelope_ahead()
    {

        using AuthenticatedFixture fixture = await BeginAuthenticatedAsync("recovery-exact-ahead");

        InstallationResetActiveRecoveryState exact = Value(await fixture.Store.RecoverAsync(
            fixture.Lock,
            CancellationToken.None));

        Assert.Equal(InstallationResetActiveRecoveryOutcome.AuthenticatedV2, exact.Outcome);

        Assert.Equal(fixture.Publication.EnvelopeDigest, exact.Publication!.EnvelopeDigest);

        InstallationResetActiveRecord next = fixture.Record with
        {
            Version = 2,
            Phase = InstallationResetPhase.DataResetComplete,
            PointOfNoReturn = true,
            RowsDeleted = 7,
        };

        InstallationResetActiveEnvelopeV2 ahead = SealEnvelope(
            fixture,
            revision: 2,
            fixture.Publication.EnvelopeDigest,
            InstallationResetActivePayloadV3.FromRecord(next));

        WriteEnvelope(fixture.Store.ActivePath, ahead);

        Result<InstallationResetActiveRecoveryState> readOnly = await fixture.Store.InspectAsync(
            CancellationToken.None);

        Assert.True(readOnly.IsFailure);

        Assert.Equal(1UL, CredentialAnchor(fixture.Credentials).Revision);

        InstallationResetActiveRecoveryState recovered = Value(await fixture.Store.RecoverAsync(
            fixture.Lock,
            CancellationToken.None));

        Assert.Equal(2UL, recovered.Publication!.Anchor.Revision);

        Assert.Equal(
            Value(InstallationResetActiveRecordAuthenticator.EnvelopeDigest(ahead)),
            recovered.Publication.Anchor.EnvelopeDigest);

        InstallationResetActiveRecoveryState readback = Value(await fixture.Store.RecoverAsync(
            fixture.Lock,
            CancellationToken.None));

        Assert.Equal(recovered.Publication.Anchor, readback.Publication!.Anchor);

    }

    [Fact]
    public async Task Recovery_rejects_rollback_skipped_revision_cross_profile_cross_operation_and_location_substitution()
    {

        using (AuthenticatedFixture rollback = await BeginAuthenticatedAsync("recovery-rollback"))
        {

            InstallationResetActivePublication second = Value(await rollback.Store.AdvanceAsync(
                rollback.Lock,
                rollback.Publication,
                rollback.Record with
                {
                    Version = 2,
                    Phase = InstallationResetPhase.DataResetComplete,
                    PointOfNoReturn = true,
                },
                CancellationToken.None));

            Assert.Equal(2UL, second.Anchor.Revision);

            WriteEnvelope(rollback.Store.ActivePath, rollback.Publication.Envelope);

            Assert.True((await rollback.Store.RecoverAsync(
                rollback.Lock,
                CancellationToken.None)).IsFailure);

        }

        using (AuthenticatedFixture skipped = await BeginAuthenticatedAsync("recovery-skipped"))
        {

            InstallationResetActiveEnvelopeV2 jump = SealEnvelope(
                skipped,
                revision: 3,
                skipped.Publication.EnvelopeDigest,
                skipped.Publication.Payload);

            WriteEnvelope(skipped.Store.ActivePath, jump);

            Assert.True((await skipped.Store.RecoverAsync(
                skipped.Lock,
                CancellationToken.None)).IsFailure);

        }

        using (AuthenticatedFixture operation = await BeginAuthenticatedAsync("recovery-operation"))
        {

            InstallationResetActivePayloadV3 substituted =
                InstallationResetActivePayloadV3.FromRecord(operation.Record with
                {
                    OperationId = Guid.Parse("99999999-8888-4777-8666-555555555555"),
                });

            InstallationResetActiveEnvelopeV2 crossOperation = SealEnvelope(
                operation,
                revision: 2,
                operation.Publication.EnvelopeDigest,
                substituted);

            WriteEnvelope(operation.Store.ActivePath, crossOperation);

            Assert.True((await operation.Store.RecoverAsync(
                operation.Lock,
                CancellationToken.None)).IsFailure);

        }

        using (AuthenticatedFixture location = await BeginAuthenticatedAsync("recovery-location"))
        {

            BackupRestoreProfileNamespace profile = Value(
                BackupRestoreJournalAuthenticator.ResolveProfileNamespace(location.GuardedRoot));

            string account = ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(
                profile.AccountSuffix);

            InstallationResetActiveAnchorV1 substituted = location.Publication.Anchor with
            {
                ActiveLocationDigest = Digest(0x91),
            };

            location.Credentials.Values[account] = Value(
                InstallationResetActiveRecordAuthenticator.EncodeAnchor(substituted));

            Assert.True((await location.Store.RecoverAsync(
                location.Lock,
                CancellationToken.None)).IsFailure);

        }

        using AuthenticatedFixture source = await BeginAuthenticatedAsync("recovery-profile-source");

        string targetRoot = _workspace.CreateSubdir("recovery-profile-target");

        using ArcanumMaintenanceLock targetLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(targetRoot));

        BackupRestoreProfileNamespace sourceProfile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(source.GuardedRoot));

        BackupRestoreProfileNamespace targetProfile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(targetRoot));

        CopyCredential(
            source.Credentials,
            ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(sourceProfile.AccountSuffix),
            ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(targetProfile.AccountSuffix));

        CopyCredential(
            source.Credentials,
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(sourceProfile.AccountSuffix),
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(targetProfile.AccountSuffix));

        CopyCredential(
            source.Credentials,
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(sourceProfile.AccountSuffix),
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(targetProfile.AccountSuffix));

        InstallationResetActiveStore target = new(targetRoot, source.Credentials);

        File.Copy(source.Store.ActivePath, target.ActivePath);

        Assert.True((await target.RecoverAsync(targetLock, CancellationToken.None)).IsFailure);

    }

    [Fact]
    public async Task Recovery_treats_file_key_anchor_partial_combinations_and_lookalikes_as_blocking_evidence()
    {

        using AuthenticatedFixture source = await BeginAuthenticatedAsync("recovery-partial-source");

        string anchorOnlyRoot = _workspace.CreateSubdir("recovery-anchor-only");

        using ArcanumMaintenanceLock anchorOnlyLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(anchorOnlyRoot));

        BackupRestoreProfileNamespace sourceProfile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(source.GuardedRoot));

        BackupRestoreProfileNamespace anchorOnlyProfile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(anchorOnlyRoot));

        RecordingCredentialStore anchorOnlyCredentials = new([]);

        anchorOnlyCredentials.Values[
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(
                anchorOnlyProfile.AccountSuffix)] = Value(
                    InstallationResetActiveRecordAuthenticator.EncodeAnchor(
                        source.Publication.Anchor with
                        {
                            ProfileNamespaceDigest = anchorOnlyProfile.Digest,
                            ActiveLocationDigest = Value(
                                InstallationResetActiveRecordAuthenticator.ResolveLocation(
                                    anchorOnlyRoot,
                                    anchorOnlyProfile)).Digest,
                        }));

        InstallationResetActiveStore anchorOnly = new(anchorOnlyRoot, anchorOnlyCredentials);

        Assert.True((await anchorOnly.RecoverAsync(
            anchorOnlyLock,
            CancellationToken.None)).IsFailure);

        string fileOnlyRoot = _workspace.CreateSubdir("recovery-file-only");

        using ArcanumMaintenanceLock fileOnlyLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(fileOnlyRoot));

        InstallationResetActiveStore fileOnly = new(fileOnlyRoot, new RecordingCredentialStore([]));

        File.Copy(source.Store.ActivePath, fileOnly.ActivePath);

        Assert.True((await fileOnly.RecoverAsync(
            fileOnlyLock,
            CancellationToken.None)).IsFailure);

        string keyOnlyRoot = _workspace.CreateSubdir("recovery-key-only");

        using ArcanumMaintenanceLock keyOnlyLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(keyOnlyRoot));

        RecordingCredentialStore keyOnlyCredentials = new([]);

        BackupRestoreProfileNamespace keyOnlyProfile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(keyOnlyRoot));

        Value(new InstallationResetActiveRecordKeyProvider(keyOnlyCredentials).CreateOrOpen(
            keyOnlyLock,
            keyOnlyRoot,
            keyOnlyProfile)).Dispose();

        InstallationResetActiveStore keyOnly = new(keyOnlyRoot, keyOnlyCredentials);

        Assert.True((await keyOnly.RecoverAsync(
            keyOnlyLock,
            CancellationToken.None)).IsFailure);

        string lookalikeRoot = _workspace.CreateSubdir("recovery-lookalike");

        using ArcanumMaintenanceLock lookalikeLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(lookalikeRoot));

        InstallationResetActiveStore lookalike = new(
            lookalikeRoot,
            new RecordingCredentialStore([]));

        await File.WriteAllTextAsync(lookalike.ActivePath + ".tmp", "ambiguous");

        Assert.True((await lookalike.RecoverAsync(
            lookalikeLock,
            CancellationToken.None)).IsFailure);

        string symlinkRoot = _workspace.CreateSubdir("recovery-symlink");

        using ArcanumMaintenanceLock symlinkLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(symlinkRoot));

        InstallationResetActiveStore symlink = new(
            symlinkRoot,
            new RecordingCredentialStore([]));

        string outside = _workspace.WriteFile("recovery-outside.json", "{}");

        File.CreateSymbolicLink(symlink.ActivePath, outside);

        Assert.True((await symlink.RecoverAsync(
            symlinkLock,
            CancellationToken.None)).IsFailure);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Case_variant_evidence_blocks_read_only_inspection_and_locked_recovery(
        bool temporary)
    {

        string guardedRoot = _workspace.CreateSubdir(
            temporary
                ? "case-variant-temporary-recovery"
                : "case-variant-canonical-recovery");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        InstallationResetActiveStore store = new(
            guardedRoot,
            new RecordingCredentialStore([]));

        string variant = CaseVariantEvidencePath(store.ActivePath, temporary);

        await File.WriteAllTextAsync(variant, "ambiguous");

        Assert.True((await store.InspectAsync(CancellationToken.None)).IsFailure);

        Assert.True((await store.RecoverAsync(
            heldLock,
            CancellationToken.None)).IsFailure);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Case_variant_evidence_refuses_begin_without_creating_credentials(
        bool temporary)
    {

        string guardedRoot = _workspace.CreateSubdir(
            temporary
                ? "case-variant-temporary-begin"
                : "case-variant-canonical-begin");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(guardedRoot, credentials);

        string variant = CaseVariantEvidencePath(store.ActivePath, temporary);

        await File.WriteAllTextAsync(variant, "ambiguous");

        Result<InstallationResetActivePublication> begun = await store.BeginAsync(
            heldLock,
            Guid.Parse("b1111111-2222-4333-8444-555555555555"),
            CreateRecord(InstallationResetPhase.Prepared),
            CancellationToken.None);

        Assert.True(begun.IsFailure);

        Assert.Equal(0, credentials.SetCount);

        Assert.Equal("ambiguous", await File.ReadAllTextAsync(variant));

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Case_variant_evidence_blocks_retirement_absence_proof(
        bool temporary)
    {

        string guardedRoot = _workspace.CreateSubdir(
            temporary
                ? "case-variant-temporary-retirement"
                : "case-variant-canonical-retirement");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(guardedRoot, credentials);

        InstallationResetActiveRecord record = CreateRecord(
            InstallationResetPhase.Completed);

        _ = Value(await store.BeginAsync(
            heldLock,
            Guid.Parse("c1111111-2222-4333-8444-555555555555"),
            record,
            CancellationToken.None));

        credentials.FailDelete = account => account.StartsWith(
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccountPrefix,
            StringComparison.Ordinal);

        Assert.True((await store.RetireAsync(
            heldLock,
            record.OperationId,
            CancellationToken.None)).IsFailure);

        Assert.False(File.Exists(store.ActivePath));

        credentials.FailDelete = null;

        string variant = CaseVariantEvidencePath(store.ActivePath, temporary);

        bool injected = false;

        InstallationResetActiveStore resumed = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(step =>
            {

                if (!injected
                    && string.Equals(
                        step,
                        "file:absence-parent-flushed",
                        StringComparison.Ordinal))
                {

                    File.WriteAllText(variant, "ambiguous");

                    injected = true;

                }

            }));

        Assert.True((await resumed.RetireAsync(
            heldLock,
            record.OperationId,
            CancellationToken.None)).IsFailure);

        Assert.True(injected);

        Assert.Equal(
            InstallationResetActiveAnchorState.Closed,
            CredentialAnchor(credentials).State);

        Assert.True(File.Exists(variant));

    }

    [Fact]
    public async Task File_mutation_primitives_reject_a_wrong_root_lock_before_any_side_effect()
    {

        string guardedRoot = _workspace.CreateSubdir("file-mutation-wrong-root-target");

        string otherRoot = _workspace.CreateSubdir("file-mutation-wrong-root-lock");

        using ArcanumMaintenanceLock wrongLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(otherRoot));

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guardedRoot));

        InstallationResetActiveLocation location = Value(
            InstallationResetActiveRecordAuthenticator.ResolveLocation(
                guardedRoot,
                profile));

        await File.WriteAllTextAsync(location.ActivePath, "owned");

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
            location.ActivePath,
            out FileHandleMetadata metadata));

        List<string> events = [];

        InstallationResetActiveFilePersistence files = new(events.Add);

        await Assert.ThrowsAsync<InvalidOperationException>(() => files.ReplaceDurablyAsync(
            wrongLock,
            guardedRoot,
            location,
            new byte[] { 1, 2, 3 },
            CancellationToken.None));

        Assert.Throws<InvalidOperationException>(() => files.DeleteDurably(
            wrongLock,
            guardedRoot,
            location,
            metadata));

        Assert.Throws<InvalidOperationException>(() => files.ProveAbsentDurably(
            wrongLock,
            guardedRoot,
            location));

        Assert.Empty(events);

        Assert.Equal("owned", await File.ReadAllTextAsync(location.ActivePath));

        AssertNoTemporaryEvidence(location);

    }

    [Fact]
    public async Task File_mutation_primitives_reject_a_disposed_lock_before_any_side_effect()
    {

        string guardedRoot = _workspace.CreateSubdir("file-mutation-disposed-lock");

        ArcanumMaintenanceLock disposedLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        disposedLock.Dispose();

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guardedRoot));

        InstallationResetActiveLocation location = Value(
            InstallationResetActiveRecordAuthenticator.ResolveLocation(
                guardedRoot,
                profile));

        await File.WriteAllTextAsync(location.ActivePath, "owned");

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
            location.ActivePath,
            out FileHandleMetadata metadata));

        List<string> events = [];

        InstallationResetActiveFilePersistence files = new(events.Add);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => files.ReplaceDurablyAsync(
            disposedLock,
            guardedRoot,
            location,
            new byte[] { 1, 2, 3 },
            CancellationToken.None));

        Assert.Throws<ObjectDisposedException>(() => files.DeleteDurably(
            disposedLock,
            guardedRoot,
            location,
            metadata));

        Assert.Throws<ObjectDisposedException>(() => files.ProveAbsentDurably(
            disposedLock,
            guardedRoot,
            location));

        Assert.Empty(events);

        Assert.Equal("owned", await File.ReadAllTextAsync(location.ActivePath));

        AssertNoTemporaryEvidence(location);

    }

    [Fact]
    public async Task Recovery_never_creates_or_repairs_missing_authentication_material()
    {

        using AuthenticatedFixture missingKey = await BeginAuthenticatedAsync("recovery-no-repair");

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(missingKey.GuardedRoot));

        string keyAccount = ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(
            profile.AccountSuffix);

        _ = missingKey.Credentials.Values.Remove(keyAccount);

        int writesBefore = missingKey.Credentials.SetCount;

        Assert.True((await missingKey.Store.RecoverAsync(
            missingKey.Lock,
            CancellationToken.None)).IsFailure);

        Assert.Equal(writesBefore, missingKey.Credentials.SetCount);

        Assert.False(missingKey.Credentials.Values.ContainsKey(keyAccount));

        missingKey.Credentials.Values[keyAccount] = "not-canonical";

        Assert.True((await missingKey.Store.RecoverAsync(
            missingKey.Lock,
            CancellationToken.None)).IsFailure);

        Assert.Equal("not-canonical", missingKey.Credentials.Values[keyAccount]);

        Assert.Equal(writesBefore, missingKey.Credentials.SetCount);

        missingKey.Credentials.IsAvailable = false;

        Assert.True((await missingKey.Store.RecoverAsync(
            missingKey.Lock,
            CancellationToken.None)).IsFailure);

        Assert.Equal(writesBefore, missingKey.Credentials.SetCount);

    }

    [Fact]
    public async Task V1_ordinary_record_migrates_to_authenticated_v2_before_the_next_effect()
    {

        string guardedRoot = _workspace.CreateSubdir("legacy-migration");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        List<string> events = [];

        RecordingCredentialStore credentials = new(events);

        InstallationResetActiveStore store = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(events.Add));

        InstallationResetActiveRecord legacy = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            OperationId = Guid.Parse("41111111-2222-4333-8444-555555555555"),
            DataHandoff = InstallationResetDataHandoff.HostFactoryErasure,
        };

        Assert.True((await store.WriteLegacyV1ForTestsAsync(legacy, CancellationToken.None)).IsSuccess);

        InstallationResetActiveRecoveryState inspection = Value(await store.InspectAsync(
            CancellationToken.None));

        Assert.Equal(InstallationResetActiveRecoveryOutcome.LegacyV1, inspection.Outcome);

        Assert.Equivalent(legacy, inspection.LegacyRecord, strict: true);

        Guid installationId = Guid.Parse("51111111-2222-4333-8444-555555555555");

        events.Clear();

        InstallationResetActivePublication migrated = Value(await store.MigrateLegacyV1ForTestsAsync(
            heldLock,
            installationId,
            CancellationToken.None));

        Assert.Equal(1UL, migrated.Envelope.Revision);

        Assert.Equal(
            InstallationResetActiveRecordAuthenticator.ZeroDigest,
            migrated.Envelope.PreviousEnvelopeDigest);

        Assert.Equal(legacy.OperationId, migrated.Payload.OperationId);

        Assert.Equal(legacy.AcceptedBinding.BindingId, migrated.Payload.AcceptedBinding.BindingId);

        Assert.Equal(legacy.DataHandoff, migrated.Payload.DataHandoff);

        AssertOrdered(
            events,
            "key:readback",
            "anchor:set:Active:0",
            "anchor:readback:Active:0",
            "file:temporary-flushed",
            "file:atomic-replace",
            "file:parent-flushed",
            "file:secure-reread",
            "anchor:set:Active:1");

        InstallationResetActiveRecoveryState recovered = Value(await store.RecoverAsync(
            heldLock,
            CancellationToken.None));

        Assert.Equal(InstallationResetActiveRecoveryOutcome.AuthenticatedV2, recovered.Outcome);

        Assert.Equal(migrated.EnvelopeDigest, recovered.Publication!.EnvelopeDigest);

    }

    [Fact]
    public async Task V1_record_with_full_reset_authority_or_nonnull_reserved_slot_is_refused()
    {

        foreach (string forbiddenMember in (string[])
                 [
                     "\"fullResetAuthority\":true",
                     "\"hostToolsMarkerPairReset\":{\"revision\":1}",
                 ])
        {

            string guardedRoot = _workspace.CreateSubdir(
                "legacy-forbidden-" + Guid.NewGuid().ToString("N"));

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            RecordingCredentialStore credentials = new([]);

            InstallationResetActiveStore store = new(guardedRoot, credentials);

            const string prefix =
                "{\"version\":1,\"operationId\":\"61111111-2222-4333-8444-555555555555\","
                + "\"planId\":\"composite-plan\",\"scope\":\"Global\",\"workspace\":null,"
                + "\"acceptedBinding\":{\"bindingId\":\"binding\",\"selectedRoots\":[],"
                + "\"excludedRoots\":[],\"preservedBackups\":[],\"credentialAccounts\":[],"
                + "\"dataPlanIds\":[]},\"phase\":\"Prepared\",\"pointOfNoReturn\":false,"
                + "\"rowsDeleted\":0,\"filesDeleted\":0,\"estimatedBytesDeleted\":0,"
                + "\"credentialResults\":[],\"lastErrorCode\":null,\"dataHandoff\":null,"
                + "\"onlineDataCompletion\":null,";

            await File.WriteAllTextAsync(store.ActivePath, prefix + forbiddenMember + "}");

            Assert.True((await store.InspectAsync(CancellationToken.None)).IsFailure);

            Assert.True((await store.MigrateLegacyV1ForTestsAsync(
                heldLock,
                Guid.NewGuid(),
                CancellationToken.None)).IsFailure);

            Assert.Equal(0, credentials.SetCount);

        }

    }

    [Fact]
    public async Task V1_revision_zero_anchor_crash_resumes_only_the_same_ordinary_operation()
    {

        string guardedRoot = _workspace.CreateSubdir("legacy-revision-zero-resume");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore failingStore = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(
                failBeforeStep: step => string.Equals(
                    step,
                    "file:temporary-flushed",
                    StringComparison.Ordinal)));

        Guid operationId = Guid.Parse("6a111111-2222-4333-8444-555555555555");

        InstallationResetActiveRecord legacy = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            OperationId = operationId,
        };

        Assert.True((await failingStore.WriteLegacyV1ForTestsAsync(
            legacy,
            CancellationToken.None)).IsSuccess);

        string exactLegacy = await File.ReadAllTextAsync(failingStore.ActivePath);

        Guid installationId = Guid.Parse("6b111111-2222-4333-8444-555555555555");

        Assert.True((await failingStore.MigrateLegacyV1ForTestsAsync(
            heldLock,
            installationId,
            CancellationToken.None)).IsFailure);

        InstallationResetActiveStore resumedStore = new(guardedRoot, credentials);

        Assert.Equal(
            InstallationResetActiveRecoveryOutcome.LegacyV1,
            Value(await resumedStore.InspectAsync(CancellationToken.None)).Outcome);

        await File.WriteAllTextAsync(
            resumedStore.ActivePath,
            exactLegacy.Replace(
                operationId.ToString("D"),
                Guid.Parse("6c111111-2222-4333-8444-555555555555").ToString("D"),
                StringComparison.Ordinal));

        Assert.True((await resumedStore.InspectAsync(CancellationToken.None)).IsFailure);

        Assert.True((await resumedStore.MigrateLegacyV1ForTestsAsync(
            heldLock,
            installationId,
            CancellationToken.None)).IsFailure);

        await File.WriteAllTextAsync(resumedStore.ActivePath, exactLegacy);

        InstallationResetActivePublication migrated = Value(
            await resumedStore.MigrateLegacyV1ForTestsAsync(
                heldLock,
                installationId,
                CancellationToken.None));

        Assert.Equal(operationId, migrated.Envelope.OperationId);

        Assert.Equal(1UL, migrated.Anchor.Revision);

    }

    [Fact]
    public async Task V1_semantically_invalid_binding_is_not_a_migration_candidate()
    {

        string guardedRoot = _workspace.CreateSubdir("legacy-invalid-binding");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(guardedRoot, credentials);

        InstallationResetActiveRecord invalid = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            AcceptedBinding = CreateRecord(InstallationResetPhase.Prepared)
                .AcceptedBinding with
            {
                SelectedRoots = [" "],
            },
        };

        await File.WriteAllBytesAsync(
            store.ActivePath,
            JsonSerializer.SerializeToUtf8Bytes(
                invalid,
                InstallationResetActiveLegacyJsonContext.Default.InstallationResetActiveRecord));

        Assert.True((await store.InspectAsync(CancellationToken.None)).IsFailure);

        Assert.True((await store.MigrateLegacyV1ForTestsAsync(
            heldLock,
            Guid.NewGuid(),
            CancellationToken.None)).IsFailure);

        Assert.Equal(0, credentials.SetCount);

    }

    [Fact]
    public async Task Closed_anchor_retirement_deletes_file_then_anchor_then_key_idempotently()
    {

        string guardedRoot = _workspace.CreateSubdir("closed-retirement");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        List<string> events = [];

        RecordingCredentialStore credentials = new(events);

        InstallationResetActiveStore store = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(events.Add));

        InstallationResetActiveRecord record = CreateRecord(
            InstallationResetPhase.Completed);

        InstallationResetActivePublication publication = Value(await store.BeginAsync(
            heldLock,
            Guid.Parse("71111111-2222-4333-8444-555555555555"),
            record,
            CancellationToken.None));

        events.Clear();

        Result retired = await store.RetireAsync(
            heldLock,
            record.OperationId,
            CancellationToken.None);

        Assert.True(retired.IsSuccess, retired.Error.Message);

        AssertOrdered(
            events,
            "anchor:set:Closed:1",
            "anchor:readback:Closed:1",
            "file:secure-reread",
            "file:delete",
            "file:delete-parent-flushed",
            "file:absence-proved",
            "anchor:delete",
            "anchor:absence-readback",
            "key:delete",
            "key:absence-readback");

        Assert.False(File.Exists(store.ActivePath));

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guardedRoot));

        Assert.DoesNotContain(
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(
                profile.AccountSuffix),
            credentials.Values.Keys);

        Assert.DoesNotContain(
            ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(
                profile.AccountSuffix),
            credentials.Values.Keys);

        Assert.Equal(
            InstallationResetActiveRecoveryOutcome.NoActiveRecord,
            Value(await store.InspectAsync(CancellationToken.None)).Outcome);

        Assert.True((await store.RetireAsync(
            heldLock,
            publication.Envelope.OperationId,
            CancellationToken.None)).IsSuccess);

    }

    [Fact]
    public async Task Exact_operation_retirement_cannot_claim_key_only_evidence_but_startup_cleanup_can()
    {

        string guardedRoot = _workspace.CreateSubdir("key-only-retirement-authority");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guardedRoot));

        using (Value(new InstallationResetActiveRecordKeyProvider(credentials).CreateOrOpen(
                   heldLock,
                   guardedRoot,
                   profile)))
        {
        }

        string keyAccount = ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(
            profile.AccountSuffix);

        InstallationResetActiveStore store = new(guardedRoot, credentials);

        Result retired = await store.RetireAsync(
            heldLock,
            Guid.Parse("d1111111-2222-4333-8444-555555555555"),
            CancellationToken.None);

        Assert.True(retired.IsFailure);

        Assert.True(credentials.Values.ContainsKey(keyAccount));

        Assert.True((await store.CompleteStartupCleanupAsync(
            heldLock,
            CancellationToken.None)).IsSuccess);

        Assert.False(credentials.Values.ContainsKey(keyAccount));

    }

    [Fact]
    public async Task Startup_cleanup_removes_only_closed_or_orphaned_key_evidence_and_never_active_evidence()
    {

        string closedRoot = _workspace.CreateSubdir("closed-cleanup");

        using ArcanumMaintenanceLock closedLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(closedRoot));

        List<string> closedEvents = [];

        RecordingCredentialStore closedCredentials = new(closedEvents);

        InstallationResetActiveStore closedStore = new(
            closedRoot,
            closedCredentials,
            new InstallationResetActiveFilePersistence(closedEvents.Add));

        InstallationResetActiveRecord closedRecord = CreateRecord(
            InstallationResetPhase.Completed);

        _ = Value(await closedStore.BeginAsync(
            closedLock,
            Guid.Parse("81111111-2222-4333-8444-555555555555"),
            closedRecord,
            CancellationToken.None));

        closedCredentials.FailDelete = account => account.StartsWith(
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccountPrefix,
            StringComparison.Ordinal);

        Assert.True((await closedStore.RetireAsync(
            closedLock,
            closedRecord.OperationId,
            CancellationToken.None)).IsFailure);

        Assert.False(File.Exists(closedStore.ActivePath));

        Assert.Equal(
            InstallationResetActiveAnchorState.Closed,
            CredentialAnchor(closedCredentials).State);

        closedCredentials.FailDelete = null;

        Assert.True((await closedStore.CompleteStartupCleanupAsync(
            closedLock,
            CancellationToken.None)).IsSuccess);

        Assert.Equal(
            InstallationResetActiveRecoveryOutcome.NoActiveRecord,
            Value(await closedStore.InspectAsync(CancellationToken.None)).Outcome);

        string filePresentRoot = _workspace.CreateSubdir("closed-file-cleanup");

        using ArcanumMaintenanceLock filePresentLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(filePresentRoot));

        RecordingCredentialStore filePresentCredentials = new([]);

        InstallationResetActiveFilePersistence failingDelete = new(
            failBeforeStep: step => string.Equals(
                step,
                "file:delete",
                StringComparison.Ordinal));

        InstallationResetActiveStore filePresentStore = new(
            filePresentRoot,
            filePresentCredentials,
            failingDelete);

        InstallationResetActiveRecord filePresentRecord = CreateRecord(
            InstallationResetPhase.Completed);

        _ = Value(await filePresentStore.BeginAsync(
            filePresentLock,
            Guid.Parse("91111111-2222-4333-8444-555555555555"),
            filePresentRecord,
            CancellationToken.None));

        Assert.True((await filePresentStore.RetireAsync(
            filePresentLock,
            filePresentRecord.OperationId,
            CancellationToken.None)).IsFailure);

        Assert.True(File.Exists(filePresentStore.ActivePath));

        InstallationResetActiveStore resumedClosedStore = new(
            filePresentRoot,
            filePresentCredentials);

        Assert.True((await resumedClosedStore.CompleteStartupCleanupAsync(
            filePresentLock,
            CancellationToken.None)).IsSuccess);

        Assert.False(File.Exists(filePresentStore.ActivePath));

        string keyOnlyRoot = _workspace.CreateSubdir("key-only-cleanup");

        using ArcanumMaintenanceLock keyOnlyLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(keyOnlyRoot));

        RecordingCredentialStore keyOnlyCredentials = new([]);

        BackupRestoreProfileNamespace keyOnlyProfile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(keyOnlyRoot));

        using (Value(new InstallationResetActiveRecordKeyProvider(keyOnlyCredentials).CreateOrOpen(
                   keyOnlyLock,
                   keyOnlyRoot,
                   keyOnlyProfile)))
        {
        }

        InstallationResetActiveStore keyOnlyStore = new(keyOnlyRoot, keyOnlyCredentials);

        Assert.True((await keyOnlyStore.CompleteStartupCleanupAsync(
            keyOnlyLock,
            CancellationToken.None)).IsSuccess);

        Assert.Empty(keyOnlyCredentials.Values);

        string activeRoot = _workspace.CreateSubdir("active-missing-file");

        using ArcanumMaintenanceLock activeLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(activeRoot));

        RecordingCredentialStore activeCredentials = new([]);

        InstallationResetActiveStore activeStore = new(
            activeRoot,
            activeCredentials,
            new InstallationResetActiveFilePersistence(
                failBeforeStep: step => string.Equals(
                    step,
                    "file:temporary-flushed",
                    StringComparison.Ordinal)));

        Assert.True((await activeStore.BeginAsync(
            activeLock,
            Guid.Parse("a1111111-2222-4333-8444-555555555555"),
            CreateRecord(InstallationResetPhase.Prepared),
            CancellationToken.None)).IsFailure);

        int activeCredentialCount = activeCredentials.Values.Count;

        Assert.True((await activeStore.CompleteStartupCleanupAsync(
            activeLock,
            CancellationToken.None)).IsFailure);

        Assert.Equal(activeCredentialCount, activeCredentials.Values.Count);

    }

    [Fact]
    public async Task Advance_carries_a_nested_receipt_forward_and_refuses_to_undo_it()
    {

        // Mutation caught: letting a nested receipt be removed, regressed, or renamed lets a reset
        // forget that it started a database transition it can no longer prove anything about.
        string guardedRoot = _workspace.CreateSubdir("nested-receipt-monotonic");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        InstallationResetActiveStore store = new(
            guardedRoot,
            new RecordingCredentialStore([]));

        Guid installationId = Guid.Parse("71111111-2222-4333-8444-555555555555");

        InstallationResetActiveRecord absent = CreateRecord(InstallationResetPhase.Prepared);

        InstallationResetActivePublication opening = Value(await store.BeginAsync(
            heldLock,
            installationId,
            absent,
            CancellationToken.None));

        Assert.Null(opening.Payload.NestedTransitionReceipt);

        Guid nested = Guid.Parse("81111111-2222-4333-8444-555555555555");

        InstallationResetNestedTransitionReceiptV1 claimed = new(
            Version: 1,
            nested,
            InstallationResetNestedTransitionPhase.Claimed,
            NestedEffectDigest: null,
            TerminalWinnerDigest: null);

        InstallationResetNestedTransitionReceiptV1 completed = claimed with
        {
            Phase = InstallationResetNestedTransitionPhase.Completed,
            NestedEffectDigest = Digest(0x51),
            TerminalWinnerDigest = Digest(0x61),
        };

        InstallationResetActivePublication claimPublication = Value(await store.AdvanceAsync(
            heldLock,
            opening,
            absent with { NestedTransitionReceipt = claimed },
            CancellationToken.None));

        Assert.Equal(claimed, claimPublication.Payload.NestedTransitionReceipt);

        // A claim may only become the completion of the same nested operation. Dropping it, moving it
        // back, or pointing it at a different operation each describe a transition this reset did not
        // launch.
        InstallationResetActiveRecord[] refused =
        [
            absent,
            absent with
            {
                NestedTransitionReceipt = completed with
                {
                    NestedOperationId = Guid.Parse("91111111-2222-4333-8444-555555555555"),
                },
            },
        ];

        foreach (InstallationResetActiveRecord candidate in refused)
        {

            Assert.True(
                (await store.AdvanceAsync(
                    heldLock,
                    claimPublication,
                    candidate,
                    CancellationToken.None)).IsFailure);

        }

        InstallationResetActivePublication completion = Value(await store.AdvanceAsync(
            heldLock,
            claimPublication,
            absent with { NestedTransitionReceipt = completed },
            CancellationToken.None));

        Assert.Equal(completed, completion.Payload.NestedTransitionReceipt);

        Assert.True(
            (await store.AdvanceAsync(
                heldLock,
                completion,
                absent with { NestedTransitionReceipt = claimed },
                CancellationToken.None)).IsFailure);

    }

    private static InstallationResetActiveRecord CreateRecord(
        InstallationResetPhase phase)
    {

        InstallationResetAcceptedBinding binding = new(
            "binding",
            ["/selected"],
            ["/excluded"],
            [],
            ["master-api-key"],
            ["data-plan"]);

        return new InstallationResetActiveRecord(
            InstallationResetActiveStore.CurrentVersion,
            Guid.NewGuid(),
            "composite-plan",
            InstallationResetScope.Global,
            Workspace: null,
            binding,
            phase,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: null);

    }

    private static InstallationResetActiveRecord CreateCheckpointRecord(
        Guid installationId,
        HostToolsMarkerPairResetPhase? checkpointPhase,
        Guid? operationId = null)
    {

        Guid operation = operationId ?? Guid.NewGuid();

        DateTimeOffset acceptedAtUtc = new(
            2026,
            8,
            22,
            12,
            0,
            0,
            TimeSpan.Zero);

        HostProcessToolsMatchedPair pair = CheckpointPair(installationId);

        FullInstallationResetExternalRemediationAttestation attestation = new(
            Version: 1,
            operation,
            installationId,
            pair.Database.TransitionId!.Value,
            pair.Database.TaintMasterKeyVersion!.Value,
            pair.Database.TaintFingerprint!.Value,
            pair.Database.DatabaseMarkerDigest,
            pair.OsMarker.MarkerBytesDigest,
            new CovenantDigest(Convert.FromHexString(
                "761e8536128080d5936070524da90a6558b8901ea46d93194646b413bb27a1d9")),
            "oKGio6SlpqeoqaqrrK2urw",
            "RetroDownfall.Remediation.v1",
            acceptedAtUtc.AddMinutes(-5),
            acceptedAtUtc.AddMinutes(55),
            Base64Url.EncodeToString(Enumerable.Repeat((byte)0x44, 64).ToArray()));

        CovenantDigest signedDigest = Value(
            FullInstallationResetRemediationAttestationDigest.Calculate(attestation));

        ImmutableArray<CampaignMarkerInventoryEntryV1> inventory = [];

        CovenantDigest inventoryDigest = Value(
            FullInstallationResetMarkerPairResetDigests.CampaignInventory(inventory));

        CovenantDigest ownerEffect = Value(
            FullInstallationResetMarkerPairResetDigests.FullResetEffect(
                operation,
                installationId,
                attestation.HostToolsTransitionId,
                attestation.TaintMasterKeyVersion,
                attestation.AuthorityFingerprint,
                attestation.DatabaseMarkerDigest,
                attestation.OsMarkerDigest,
                attestation.RemediationActionDigest,
                inventoryDigest));

        FullInstallationResetRemediationClaimV1 claim = new(
            Version: 1,
            operation,
            installationId,
            signedDigest,
            Digest(0x91),
            Digest(0xB1),
            acceptedAtUtc);

        HostToolsMarkerPairResetCheckpointV1? checkpoint = checkpointPhase is { } phase
            ? new HostToolsMarkerPairResetCheckpointV1(
                Version: 1,
                phase,
                new FullInstallationResetRestartProofV1(
                    Version: 1,
                    FullInstallationResetSignedAttestationProjectionV1.FromAttestation(
                        attestation),
                    acceptedAtUtc,
                    signedDigest,
                    pair.Database,
                    pair.OsMarker,
                    Value(FullInstallationResetMarkerPairResetDigests.PairEvidence(pair))),
                inventory,
                inventoryDigest,
                ownerEffect,
                MarkerIntentCount: null,
                OrderedMarkerIntentIds: null,
                MarkerIntentVectorDigest: null,
                DeletedCount: null,
                OrphanCount: null)
            : null;

        return CreateRecord(InstallationResetPhase.Prepared) with
        {
            OperationId = operation,
            Scope = InstallationResetScope.All,
            Workspace = new DataRetentionWorkspaceBinding(
                Guid.Parse("12345678-1234-4234-8234-123456789abc"),
                "/workspace"),
            AcceptedBinding = new InstallationResetAcceptedBinding(
                "binding",
                [],
                [],
                [],
                [],
                []),
            LastErrorCode = ErrorCodes.Data.RecoveryRequired,
            FullInstallationResetRemediationClaim = claim,
            HostToolsMarkerPairReset = checkpoint,
        };

    }

    private static HostProcessToolsMatchedPair CheckpointPair(Guid installationId)
    {

        Guid transition = Guid.Parse("ffeeddcc-bbaa-4988-b766-554433221100");

        CovenantDigest fingerprint = Digest(0x11);

        HostProcessToolsDatabaseMarkerEvidence database = new(
            installationId.ToString("D"),
            RetroDownfall.Arcanum.Core.Security.CovenantHostToolsState.HostToolsTainted,
            transition,
            0x0102030405060708,
            fingerprint);

        HostProcessToolsOsMarkerEvidence marker = new(
            installationId.ToString("D"),
            transition,
            0x0102030405060708,
            fingerprint,
            Digest(0x31),
            Digest(0x51));

        return new HostProcessToolsMatchedPair(database, marker);

    }

    private static HostToolsMarkerPairResetCheckpointV1 PreparedCheckpoint(
        HostToolsMarkerPairResetCheckpointV1 checkpoint,
        ImmutableArray<Guid> intentIds) =>
        ReceiptCheckpoint(
            WithCampaignInventory(checkpoint, intentIds.Length),
            intentIds);

    private static HostToolsMarkerPairResetCheckpointV1 ReceiptCheckpoint(
        HostToolsMarkerPairResetCheckpointV1 checkpoint,
        ImmutableArray<Guid> intentIds) =>
        checkpoint with
        {
            Phase = HostToolsMarkerPairResetPhase.PairAbsenceVerified,
            MarkerIntentCount = checked((ulong)intentIds.Length),
            OrderedMarkerIntentIds = intentIds,
            MarkerIntentVectorDigest = Value(
                FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(
                    intentIds)),
            DeletedCount = 0,
            OrphanCount = 0,
        };

    private static HostToolsMarkerPairResetCheckpointV1 WithCampaignInventory(
        HostToolsMarkerPairResetCheckpointV1 checkpoint,
        int count = 1)
    {

        ImmutableArray<CampaignMarkerInventoryEntryV1> inventory =
            ImmutableArray.CreateRange(
                Enumerable.Range(1, count).Select(static value =>
                    new CampaignMarkerInventoryEntryV1(
                        new Guid(value, 0, 0, new byte[8]),
                        PriorPathRevision: value,
                        Digest(checked((byte)(0x13 + value))),
                        Digest(checked((byte)(0x33 + value))),
                        Digest(checked((byte)(0x53 + value))),
                        Digest(checked((byte)(0x73 + value))))));

        CovenantDigest inventoryDigest = Value(
            FullInstallationResetMarkerPairResetDigests.CampaignInventory(inventory));

        FullInstallationResetSignedAttestationProjectionV1 signed =
            checkpoint.RestartProof.SignedAttestation;

        return checkpoint with
        {
            CampaignInventory = inventory,
            CampaignMarkerInventoryDigest = inventoryDigest,
            OwnerEffectDigest = Value(
                FullInstallationResetMarkerPairResetDigests.FullResetEffect(
                    signed.OperationId,
                    signed.InstallationId,
                    signed.HostToolsTransitionId,
                    signed.TaintMasterKeyVersion,
                    signed.AuthorityFingerprint,
                    signed.DatabaseMarkerDigest,
                    signed.OsMarkerDigest,
                    signed.RemediationActionDigest,
                    inventoryDigest)),
        };

    }

    private static CovenantDigest ClaimDigest(byte value) =>
        new(Enumerable.Repeat(value, 32).ToArray());

    private static void AssertOrdered(List<string> events, params string[] expected)
    {

        int prior = -1;

        foreach (string value in expected)
        {

            int current = events.FindIndex(
                prior + 1,
                candidate => string.Equals(candidate, value, StringComparison.Ordinal));

            Assert.True(
                current > prior,
                $"Expected '{value}' after event index {prior}. Actual: {string.Join(", ", events)}");

            prior = current;

        }

    }

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;

    }

    private async Task<AuthenticatedFixture> BeginAuthenticatedAsync(string name)
    {

        string guardedRoot = _workspace.CreateSubdir(name);

        ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(guardedRoot, credentials);

        InstallationResetActiveRecord record = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            OperationId = Guid.NewGuid(),
        };

        InstallationResetActivePublication publication = Value(await store.BeginAsync(
            heldLock,
            Guid.NewGuid(),
            record,
            CancellationToken.None));

        return new AuthenticatedFixture(
            guardedRoot,
            heldLock,
            credentials,
            store,
            record,
            publication);

    }

    private static InstallationResetActiveEnvelopeV2 SealEnvelope(
        AuthenticatedFixture fixture,
        ulong revision,
        CovenantDigest previousDigest,
        InstallationResetActivePayloadV3 payload)
    {

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(fixture.GuardedRoot));

        using InstallationResetActiveRecordKeyLease key = Value(
            new InstallationResetActiveRecordKeyProvider(fixture.Credentials)
                .OpenExisting(profile));

        return Value(InstallationResetActiveRecordAuthenticator.Seal(
            key,
            fixture.Publication.Location,
            fixture.Publication.Anchor.InstallationId,
            revision,
            previousDigest,
            payload));

    }

    private static void WriteEnvelope(
        string path,
        InstallationResetActiveEnvelopeV2 envelope) =>
        File.WriteAllBytes(
            path,
            Value(InstallationResetActiveRecordAuthenticator.EncodeEnvelope(envelope)));

    private static CovenantDigest Digest(byte first) =>
        new(Enumerable.Range(first, 32).Select(static value => (byte)value).ToArray());

    private static string CaseVariantPath(string path)
    {

        string leaf = Path.GetFileName(path);

        int letterIndex = leaf.Index().First(pair => char.IsLetter(pair.Item)).Index;

        char variant = char.IsLower(leaf[letterIndex])
            ? char.ToUpperInvariant(leaf[letterIndex])
            : char.ToLowerInvariant(leaf[letterIndex]);

        string variantLeaf = leaf[..letterIndex]
            + variant
            + leaf[(letterIndex + 1)..];

        return Path.Combine(
            Path.GetDirectoryName(path)!,
            variantLeaf);

    }

    private static string CaseVariantEvidencePath(string activePath, bool temporary) =>
        CaseVariantPath(activePath)
        + (temporary ? ".TMP.interrupted" : string.Empty);

    private static void AssertNoTemporaryEvidence(
        InstallationResetActiveLocation location) =>
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(
                Path.GetDirectoryName(location.ActivePath)!),
            entry => Path.GetFileName(entry).StartsWith(
                location.ActiveLeaf + ".tmp",
                StringComparison.OrdinalIgnoreCase));

    private static void CopyCredential(
        RecordingCredentialStore credentials,
        string source,
        string destination) =>
        credentials.Values[destination] = credentials.Values[source];

    private static InstallationResetActiveAnchorV1 CredentialAnchor(
        RecordingCredentialStore credentials)
    {

        KeyValuePair<string, string> stored = Assert.Single(
            credentials.Values,
            pair => pair.Key.StartsWith(
                ArcanumCredentialIdentity.InstallationResetActiveAnchorAccountPrefix,
                StringComparison.Ordinal));

        return Value(InstallationResetActiveRecordAuthenticator.DecodeAnchor(
            stored.Value));

    }

    private sealed record AuthenticatedFixture(
        string GuardedRoot,
        ArcanumMaintenanceLock Lock,
        RecordingCredentialStore Credentials,
        InstallationResetActiveStore Store,
        InstallationResetActiveRecord Record,
        InstallationResetActivePublication Publication) : IDisposable
    {

        public void Dispose() => Lock.Dispose();

    }

    private sealed class RecordingCredentialStore(List<string> events) : IOsCredentialStore
    {

        public bool IsAvailable { get; set; } = true;

        public Func<string, bool>? FailDelete { get; set; }

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        private readonly HashSet<string> _pendingReadbacks = new(StringComparer.Ordinal);

        public OsCredentialStoreResult TryGet(string service, string account)
        {

            Assert.Equal(ArcanumCredentialIdentity.Service, service);

            if (!IsAvailable)
            {

                return OsCredentialStoreResult.Unavailable("unavailable");

            }

            if (account.StartsWith(
                    ArcanumCredentialIdentity.InstallationResetActiveKeyAccountPrefix,
                    StringComparison.Ordinal))
            {

                if (Values.ContainsKey(account))
                {

                    events.Add(_pendingReadbacks.Remove(account)
                        ? "key:readback"
                        : "key:open-existing");

                }
                else
                {

                    events.Add(_pendingDeletions.Remove(account)
                        ? "key:absence-readback"
                        : "key:probe");

                }

            }
            else if (account.StartsWith(
                         ArcanumCredentialIdentity.InstallationResetActiveAnchorAccountPrefix,
                         StringComparison.Ordinal))
            {

                if (Values.TryGetValue(account, out string? encoded))
                {

                    InstallationResetActiveAnchorV1 anchor = Value(
                        InstallationResetActiveRecordAuthenticator.DecodeAnchor(encoded));

                    events.Add(_pendingReadbacks.Remove(account)
                        ? $"anchor:readback:{anchor.State}:{anchor.Revision}"
                        : $"anchor:compare-read:{anchor.State}:{anchor.Revision}");

                }
                else
                {

                    events.Add(_pendingDeletions.Remove(account)
                        ? "anchor:absence-readback"
                        : "anchor:probe");

                }

            }

            return Values.TryGetValue(account, out string? value)
                ? OsCredentialStoreResult.Ok(value)
                : OsCredentialStoreResult.NotFound();

        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {

            Assert.Equal(ArcanumCredentialIdentity.Service, service);

            if (!IsAvailable)
            {

                return OsCredentialStoreResult.Unavailable("unavailable");

            }

            Values[account] = secret;

            SetCount++;

            _pendingReadbacks.Add(account);

            if (account.StartsWith(
                    ArcanumCredentialIdentity.InstallationResetActiveAnchorAccountPrefix,
                    StringComparison.Ordinal))
            {

                InstallationResetActiveAnchorV1 anchor = Value(
                    InstallationResetActiveRecordAuthenticator.DecodeAnchor(secret));

                events.Add($"anchor:set:{anchor.State}:{anchor.Revision}");

            }

            return OsCredentialStoreResult.Ok(secret);

        }

        public OsCredentialStoreResult Delete(string service, string account)
        {

            Assert.Equal(ArcanumCredentialIdentity.Service, service);

            if (!IsAvailable)
            {

                return OsCredentialStoreResult.Unavailable("unavailable");

            }

            if (FailDelete?.Invoke(account) is true)
            {

                return OsCredentialStoreResult.Failed("injected");

            }

            if (account.StartsWith(
                    ArcanumCredentialIdentity.InstallationResetActiveAnchorAccountPrefix,
                    StringComparison.Ordinal))
            {

                events.Add("anchor:delete");

            }
            else if (account.StartsWith(
                         ArcanumCredentialIdentity.InstallationResetActiveKeyAccountPrefix,
                         StringComparison.Ordinal))
            {

                events.Add("key:delete");

            }

            _ = Values.Remove(account);

            _pendingDeletions.Add(account);

            return OsCredentialStoreResult.Ok(string.Empty);

        }

        public int SetCount { get; private set; }

        private readonly HashSet<string> _pendingDeletions = new(StringComparer.Ordinal);

    }

}
