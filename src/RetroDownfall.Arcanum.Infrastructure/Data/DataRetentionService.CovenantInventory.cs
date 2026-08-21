using System.Data.Common;

using System.Globalization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Issue #116 — the content-free Covenant inventory, and the single read capability every retention
/// surface takes to produce it.
/// </summary>
/// <remarks>
/// The Covenant is the one retention class with no time rule. What an operator can still ask for is a
/// count: how much is there, how many managed files it owns, how many local erasure artifacts are
/// outstanding, how many Sessions it touched, and how many nonrevocable disclosures happened anyway.
/// Those five numbers are the whole read model, and none of them requires opening a single piece of
/// protected content (§10.20.1).
///
/// <para>Every entry point here takes exactly one capability and releases it before returning. That is
/// stricter than it looks: an inventory that acquired per table, or a planner that took an installation
/// read and then a scoped read for the same work, would be holding two generations at once and could
/// report a family half from before a reset and half from after.</para>
/// </remarks>
internal sealed partial class DataRetentionService
{

    /// <summary>
    /// Every Covenant-family table whose rows the inventory counts.
    /// </summary>
    /// <remarks>
    /// The canonical and accelerator arms are read from
    /// <see cref="BackupRestoreProtectedStateInspector"/> rather than restated. Two lists of Covenant
    /// content tables would drift, and the one that drifted would be whichever a new tier forgot to
    /// update — which is exactly the tier a retention report would then fail to mention.
    ///
    /// <para>The three core support tables are added here because they are not part of either
    /// capability tier: sensitivity labels and erasure receipts survive their artifacts on purpose, and
    /// a disclosure receipt is nonrevocable evidence that outlives everything it describes.</para>
    /// </remarks>
    private static readonly string[] CovenantInventoryRowTables =
    [
        .. BackupRestoreProtectedStateInspector.CanonicalContentTables,

        .. BackupRestoreProtectedStateInspector.AcceleratorContentTables,

        "artifact_sensitivity",

        "assistant_entry_erasure_receipts",

        "external_disclosure_receipts",
    ];

    /// <summary>
    /// Takes the one installation-wide read capability, or reports that none was available.
    /// </summary>
    /// <remarks>
    /// A null return is not an error path. An installation with the feature off, the tier uninstalled,
    /// or the family unhealthy has nothing to inventory, and every caller reports absence rather than a
    /// row of zeroes — a zero is a measurement, and the honest answer there is that nothing was
    /// measured (§10.19.11 applies the same rule to a Campaign export's exclusion counts).
    /// </remarks>
    private async ValueTask<CovenantInstallationReadLease?> TryAcquireCovenantInstallationReadAsync(
        CancellationToken cancellationToken)
    {

        if (covenantGate is null)
        {

            return null;

        }

        Result<CovenantInstallationReadLease> lease = await covenantGate
            .AcquireInstallationReadAsync(cancellationToken)
            .ConfigureAwait(false);

        return lease.IsSuccess ? lease.Value : null;

    }

    private async ValueTask<Result<ICovenantSnapshotReadLease?>> AcquireCovenantInstallationPlanningAdmissionAsync(
        CancellationToken cancellationToken)
    {

        if (covenantGate is null)
        {

            return CovenantPlanningCapabilityUnavailable();

        }

        Result<CovenantInstallationReadLease> lease = await covenantGate
            .AcquireInstallationReadAsync(cancellationToken)
            .ConfigureAwait(false);

        return lease.IsFailure
            ? Result<ICovenantSnapshotReadLease?>.Failure(lease.Error)
            : Result<ICovenantSnapshotReadLease?>.Success(lease.Value);

    }

    /// <summary>
    /// Takes exactly one read capability over one Campaign, or reports that none was available.
    /// </summary>
    private async ValueTask<CovenantReadLease?> TryAcquireCovenantScopedReadAsync(
        Guid campaignId,
        CancellationToken cancellationToken)
    {

        if (covenantGate is null || campaignId == Guid.Empty)
        {

            return null;

        }

        Result<CovenantReadLease> lease = await covenantGate
            .AcquireReadAsync(
                CovenantOperationScope.ForCampaign(campaignId),
                cancellationToken)
            .ConfigureAwait(false);

        return lease.IsSuccess ? lease.Value : null;

    }

    private async ValueTask<Result<ICovenantSnapshotReadLease?>> AcquireCovenantScopedPlanningAdmissionAsync(
        Guid campaignId,
        CancellationToken cancellationToken)
    {

        if (campaignId == Guid.Empty)
        {

            return Result<ICovenantSnapshotReadLease?>.Failure(
                new Error(
                    ErrorCodes.Data.InvalidRequest,
                    "A scoped Covenant planning capability requires a Campaign identity."));

        }

        if (covenantGate is null)
        {

            return CovenantPlanningCapabilityUnavailable();

        }

        Result<CovenantReadLease> lease = await covenantGate
            .AcquireReadAsync(
                CovenantOperationScope.ForCampaign(campaignId),
                cancellationToken)
            .ConfigureAwait(false);

        return lease.IsFailure
            ? Result<ICovenantSnapshotReadLease?>.Failure(lease.Error)
            : Result<ICovenantSnapshotReadLease?>.Success(lease.Value);

    }

    private static Result<ICovenantSnapshotReadLease?> CovenantPlanningCapabilityUnavailable() =>
        Result<ICovenantSnapshotReadLease?>.Failure(
            new Error(
                ErrorCodes.Covenant.MaintenanceFailed,
                "The required Covenant planning capability is unavailable."));

    /// <summary>
    /// Counts the family under a capability the caller already holds.
    /// </summary>
    /// <remarks>
    /// The held lease is a required parameter rather than something this method acquires, so nesting a
    /// second capability inside a planner that already took one is not merely discouraged — there is no
    /// overload that would let it happen.
    ///
    /// <para>Returns null when the capability no longer revalidates, which is reported exactly as an
    /// absent Covenant arm is.</para>
    /// </remarks>
    private async Task<DataRetentionCovenantInventory?> InventoryCovenantAsync(
        ICovenantSnapshotReadLease heldLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldLease);

        // Revalidated immediately before the first read, not merely accepted. The gate's whole purpose
        // is to notice a generation moving underneath a live capability, and a lease that is only
        // required as a parameter would prove the caller took one rather than that it is still good.
        Result current = await heldLease
            .RevalidateAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current.IsFailure)
        {

            // Null rather than zeroes, and rather than a throw. The capability went stale mid-read, so
            // this installation was not measured — which is the same fact as having no Covenant arm and
            // is reported the same way. Failing the whole status call instead would let a Covenant
            // generation change take down an inventory of seventeen unrelated classes.
            return null;

        }

        long rows = 0;

        foreach (string table in CovenantInventoryRowTables)
        {

            rows += await CountTableAsync(
                table,
                null,
                cancellationToken).ConfigureAwait(false);

        }

        long managedFiles = await CountTableAsync(
            "managed_file_write_intents",
            null,
            cancellationToken).ConfigureAwait(false);

        long localArtifacts = await CountTableAsync(
            "local_erasure_work_items",
            null,
            cancellationToken).ConfigureAwait(false);

        long affectedSessions = await CountTableAsync(
            "session_sensitivity_state",
            "TaintedArtifactCount > 0",
            cancellationToken).ConfigureAwait(false);

        CovenantDisclosureExposure? exposure =
            await ReadNonrevocableDisclosureExposureAsync(cancellationToken).ConfigureAwait(false);

        if (exposure is null)
        {

            return null;

        }

        return new DataRetentionCovenantInventory(
            rows,
            managedFiles,
            localArtifacts,
            affectedSessions,
            exposure.PossibleAttempts,
            exposure.CountKind);

    }

    /// <summary>
    /// Attaches Covenant inventory and makes it part of the identity only for an explicit Covenant
    /// erasure decision.
    /// </summary>
    /// <remarks>
    /// Ordinary prune and workspace plans report the same inventory, but do not select it. Binding
    /// that report to their identity would invalidate a preview when unrelated Covenant evidence
    /// changes. Covenant reset and factory reset do select the family, so every content-free aggregate
    /// must instead be bound to the preview the operator confirmed.
    /// </remarks>
    private static DataRetentionPlan BindCovenantErasurePlanIdentity(
        DataRetentionPlan plan,
        DataRetentionCovenantInventory inventory)
    {

        ArgumentNullException.ThrowIfNull(plan);

        ArgumentNullException.ThrowIfNull(inventory);

        DataRetentionPlan attached = plan with { Covenant = inventory };

        bool bindsInventory = plan.Request is
            {
                Operation: DataRetentionOperation.ResetMemory,
                MemoryScope: MemoryResetScope.Covenant,
            }
            or { Operation: DataRetentionOperation.FactoryReset };

        if (!bindsInventory)
        {

            return attached;

        }

        return attached with
        {

            PlanId = ComputePlanId(
                attached.Request,
                attached.Items,
                attached.Blockers,
                attached.Conflicts,
                attached.CandidateIds,
                CovenantErasureInventoryAuthority(inventory)),

        };

    }

    private static string CovenantErasureInventoryAuthority(
        DataRetentionCovenantInventory inventory) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"covenant-erasure-inventory:v1:{inventory.Rows}:{inventory.ManagedFiles}:{inventory.LocalArtifacts}:{inventory.AffectedSessions}:{inventory.PossibleDisclosures}:{(int)inventory.DisclosureCountKind}");

    /// <summary>
    /// Folds the installation's nonrevocable disclosure buckets into one possible-attempt count.
    /// </summary>
    /// <remarks>
    /// Only nonrevocable buckets are folded, because the destructive-operation copy this number sits
    /// beside is a statement about what local removal <em>cannot</em> revoke. Including a locally
    /// revocable effect would inflate the figure with exactly the disclosures Arcanum can still undo.
    ///
    /// <para>The count kind is the join of the buckets, not the maximum of their counts: one lower-bound
    /// bucket makes the total a lower bound. This is the same fold
    /// <see cref="BackupRestoreProtectedStateInspector"/> performs over an archive, applied to the live
    /// installation instead.</para>
    /// </remarks>
    private async Task<CovenantDisclosureExposure?>
        ReadNonrevocableDisclosureExposureAsync(CancellationToken cancellationToken)
    {

        if (!await TableExistsAsync(
                "external_disclosure_state",
                cancellationToken).ConfigureAwait(false))
        {

            return new CovenantDisclosureExposure(0, CovenantDisclosureCountKind.Exact);

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        if (connection is not Microsoft.Data.Sqlite.SqliteConnection sqlite)
        {

            return null;

        }

        Result<CovenantDisclosureExposure> exposure = await _covenantExposureReader
            .ReadWithinAsync(sqlite, transaction: null, cancellationToken)
            .ConfigureAwait(false);

        return exposure.IsSuccess ? exposure.Value : null;

    }

    /// <summary>
    /// The status row an operator sees for a family that has no rule and never will.
    /// </summary>
    private static DataRetentionStatusItem CovenantStatusItem(
        DataRetentionCovenantInventory inventory) =>
        new(
            RetentionDataClass.Covenant,
            inventory.Rows,
            inventory.ManagedFiles,
            0,
            PolicyEnabled: false,
            RetentionDays: null,
            "Covenant canonical, accelerator, sensitivity, and disclosure evidence",
            "Immutable operator-and-agent agreement state. Inventoried, never aged out; erasure is an "
                + "explicit reset, not a retention rule.");

}
