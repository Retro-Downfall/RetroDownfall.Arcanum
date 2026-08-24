using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// Payload validation for the nested managed-file reconciliation checkpoint.
/// </summary>
/// <remarks>
/// This is the gate that actually enforces the reconciliation's arithmetic. The reconciler computes
/// the counts, but a durable record can be edited underneath it, so the authenticator recomputes both
/// identity vectors and requires both sums to close before any reader is allowed to believe them.
///
/// <para>The class is a partial of the authentication suite rather than a suite of its own so it can
/// reuse the fixture payload every other checkpoint test is written against. A filter derived from
/// this file's name would therefore match nothing — filter on
/// <c>InstallationResetActiveAuthenticationTests</c> instead.</para>
/// </remarks>
public sealed partial class InstallationResetActiveAuthenticationTests
{

    [Fact]
    public void A_managed_file_checkpoint_is_legal_only_beside_a_terminal_campaign_receipt()
    {

        InstallationResetActivePayloadV2 valid = TerminalReceiptPayload();

        Assert.True(
            InstallationResetActiveRecordAuthenticator.ValidatePayload(valid).IsSuccess);

        HostToolsMarkerPairResetCheckpointV1 marker = valid.HostToolsMarkerPairReset!;

        InstallationResetActivePayloadV2 withManagedFile = valid with
        {
            HostToolsMarkerPairReset = marker with { ManagedFile = ManagedFile() },
        };

        Assert.True(
            InstallationResetActiveRecordAuthenticator.ValidatePayload(withManagedFile).IsSuccess);

        // Managed-file reconciliation runs on an installation whose markers are provably gone and
        // whose Campaign cleanup is already accounted for. A record carrying its progress without that
        // behind it is claiming a position in the sequence it never reached.
        InstallationResetActivePayloadV2[] invalid =
        [
            withManagedFile with
            {
                HostToolsMarkerPairReset = marker with
                {
                    Phase = HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted,
                    ManagedFile = ManagedFile(),
                },
            },
            withManagedFile with
            {
                HostToolsMarkerPairReset = marker with
                {
                    DeletedCount = null,
                    OrphanCount = null,
                    MarkerIntentCount = null,
                    OrderedMarkerIntentIds = null,
                    MarkerIntentVectorDigest = null,
                    ManagedFile = ManagedFile(),
                },
            },
        ];

        Assert.All(
            invalid,
            static candidate => Assert.True(
                InstallationResetActiveRecordAuthenticator.ValidatePayload(candidate).IsFailure));

    }

    [Fact]
    public void A_managed_file_checkpoint_recomputes_both_identity_vectors_rather_than_trusting_them()
    {

        Guid first = Guid.Parse("00000001-0000-0000-0000-000000000000");

        Guid second = Guid.Parse("00000100-0000-0000-0000-000000000000");

        ImmutableArray<Guid> sources = [first, second];

        FullInstallationResetManagedFileCheckpointV1 managedFile = ManagedFile() with
        {
            SourceCount = 2,
            OrderedSourceWriteOperationIds = sources,
            SourceWriteIntentVectorDigest =
                FullInstallationResetManagedFileDigests.SourceWriteIntentVector(sources).Value,
        };

        Assert.True(
            InstallationResetActiveRecordAuthenticator
                .ValidatePayload(WithManagedFile(managedFile))
                .IsSuccess);

        FullInstallationResetManagedFileCheckpointV1[] invalid =
        [
            // A count that disagrees with the vector it counts.
            managedFile with { SourceCount = 3 },

            // A digest that does not commit to the vector beside it.
            managedFile with
            {
                SourceWriteIntentVectorDigest =
                    new CovenantDigest(new byte[CovenantLimits.DigestBytes]),
            },

            // A reordering, which would let two runs over the same inventory authenticate differently.
            managedFile with { OrderedSourceWriteOperationIds = [second, first] },

            // A duplicate, which would let one source be accounted for twice. No digest can be
            // recomputed for it at all, so no digest can make it valid.
            managedFile with
            {
                SourceCount = 2,
                OrderedSourceWriteOperationIds = [first, first],
            },
        ];

        Assert.All(
            invalid,
            candidate => Assert.True(
                InstallationResetActiveRecordAuthenticator
                    .ValidatePayload(WithManagedFile(candidate))
                    .IsFailure));

    }

    [Fact]
    public void The_work_item_head_arrives_at_work_items_reconciled_and_never_earlier_or_later()
    {

        Guid workItem = Guid.Parse("0a0b0c0d-0e0f-1011-1213-141516171819");

        ImmutableArray<Guid> workItems = [workItem];

        FullInstallationResetManagedFileCheckpointV1 withHead = ManagedFile(
            FullInstallationResetManagedFileReconciliationPhase.WorkItemsReconciled) with
        {
            LocalErasureWorkItemCount = 1,
            OrderedLocalErasureWorkItemIds = workItems,
            LocalErasureWorkItemVectorDigest =
                FullInstallationResetManagedFileDigests.LocalErasureWorkItemVector(workItems).Value,
        };

        Assert.True(
            InstallationResetActiveRecordAuthenticator
                .ValidatePayload(WithManagedFile(withHead))
                .IsSuccess);

        FullInstallationResetManagedFileCheckpointV1[] invalid =
        [
            // Present too early: routing an adopted source is what creates its work item, so a vector
            // published before that is predicting identities nothing has committed to.
            withHead with
            {
                Phase = FullInstallationResetManagedFileReconciliationPhase.InventoryPrepared,
            },
            withHead with
            {
                Phase = FullInstallationResetManagedFileReconciliationPhase.WriteIntentsReconciled,
            },

            // Absent too late: a record claiming the work items are reconciled cannot decline to say
            // which ones.
            ManagedFile(FullInstallationResetManagedFileReconciliationPhase.WorkItemsReconciled),

            // Half a head is never authenticated.
            withHead with { LocalErasureWorkItemVectorDigest = null },
            withHead with { LocalErasureWorkItemCount = null },
        ];

        Assert.All(
            invalid,
            candidate => Assert.True(
                InstallationResetActiveRecordAuthenticator
                    .ValidatePayload(WithManagedFile(candidate))
                    .IsFailure));

    }

    [Fact]
    public void Terminal_inventory_verification_requires_both_sums_to_close_against_the_inventories()
    {

        Guid source = Guid.Parse("00000001-0000-0000-0000-000000000000");

        Guid workItem = Guid.Parse("0a0b0c0d-0e0f-1011-1213-141516171819");

        ImmutableArray<Guid> sources = [source];

        ImmutableArray<Guid> workItems = [workItem];

        FullInstallationResetManagedFileCheckpointV1 terminal = new(
            Version: 1,
            FullInstallationResetManagedFileReconciliationPhase.TerminalInventoryVerified,
            SourceCount: 1,
            sources,
            FullInstallationResetManagedFileDigests.SourceWriteIntentVector(sources).Value,
            LocalErasureWorkItemCount: 1,
            workItems,
            FullInstallationResetManagedFileDigests.LocalErasureWorkItemVector(workItems).Value,
            SafeTerminalWriteIntentCount: 1,
            ManualWriteOrphanCount: 0,
            CompletedWorkItemCount: 0,
            ManualWorkItemOrphanCount: 1,
            TerminalClassificationDigest: new CovenantDigest(new byte[CovenantLimits.DigestBytes]));

        Assert.True(
            InstallationResetActiveRecordAuthenticator
                .ValidatePayload(WithManagedFile(terminal))
                .IsSuccess);

        FullInstallationResetManagedFileCheckpointV1[] invalid =
        [
            // The write sum overshoots its inventory, so something was counted that is not there.
            terminal with { SafeTerminalWriteIntentCount = 2 },

            // The write sum undershoots, so a source is unaccounted for and the Grimoire must stay.
            terminal with { SafeTerminalWriteIntentCount = 0 },

            // The work-item sum overshoots.
            terminal with { ManualWorkItemOrphanCount = 2 },

            // The work-item sum undershoots.
            terminal with { CompletedWorkItemCount = 0, ManualWorkItemOrphanCount = 0 },

            // A partially filled tail is never authenticated: a record that could publish three of the
            // four counters could report an inventory that does not add up.
            terminal with { ManualWriteOrphanCount = null },
            terminal with { TerminalClassificationDigest = null },

            // The terminal phase without any tail at all.
            terminal with
            {
                SafeTerminalWriteIntentCount = null,
                ManualWriteOrphanCount = null,
                CompletedWorkItemCount = null,
                ManualWorkItemOrphanCount = null,
                TerminalClassificationDigest = null,
            },
        ];

        Assert.All(
            invalid,
            candidate => Assert.True(
                InstallationResetActiveRecordAuthenticator
                    .ValidatePayload(WithManagedFile(candidate))
                    .IsFailure));

    }

    private static InstallationResetActivePayloadV2 WithManagedFile(
        FullInstallationResetManagedFileCheckpointV1 managedFile)
    {

        InstallationResetActivePayloadV2 payload = TerminalReceiptPayload();

        return payload with
        {
            HostToolsMarkerPairReset =
                payload.HostToolsMarkerPairReset! with { ManagedFile = managedFile },
        };

    }

    /// <summary>
    /// The fixture checkpoint advanced to pair absence with an empty terminal Campaign receipt.
    /// </summary>
    /// <remarks>
    /// An empty inventory reaches its terminal shape with an intent count of zero and both effect
    /// counts at zero, and the sum still has to close — which is exactly the predicate the nested
    /// checkpoint is gated on.
    /// </remarks>
    private static InstallationResetActivePayloadV2 TerminalReceiptPayload()
    {

        InstallationResetActivePayloadV2 payload = CheckpointPayload();

        ImmutableArray<Guid> intents = [];

        return payload with
        {
            HostToolsMarkerPairReset = payload.HostToolsMarkerPairReset! with
            {
                Phase = HostToolsMarkerPairResetPhase.PairAbsenceVerified,
                MarkerIntentCount = 0,
                OrderedMarkerIntentIds = intents,
                MarkerIntentVectorDigest =
                    FullInstallationResetMarkerPairResetDigests
                        .FullResetIntentVector(intents)
                        .Value,
                DeletedCount = 0,
                OrphanCount = 0,
            },
        };

    }

    private static FullInstallationResetManagedFileCheckpointV1 ManagedFile(
        FullInstallationResetManagedFileReconciliationPhase phase =
            FullInstallationResetManagedFileReconciliationPhase.InventoryPrepared)
    {

        ImmutableArray<Guid> empty = [];

        return new FullInstallationResetManagedFileCheckpointV1(
            Version: 1,
            phase,
            SourceCount: 0,
            empty,
            FullInstallationResetManagedFileDigests.SourceWriteIntentVector(empty).Value,
            LocalErasureWorkItemCount: null,
            OrderedLocalErasureWorkItemIds: null,
            LocalErasureWorkItemVectorDigest: null,
            SafeTerminalWriteIntentCount: null,
            ManualWriteOrphanCount: null,
            CompletedWorkItemCount: null,
            ManualWorkItemOrphanCount: null,
            TerminalClassificationDigest: null);

    }

}
