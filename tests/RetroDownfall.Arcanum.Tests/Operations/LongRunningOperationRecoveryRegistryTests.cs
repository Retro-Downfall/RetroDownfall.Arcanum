using System.Reflection;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Issue #40 requires the recovery matrix to be executable rather than prose: every durable
/// operation kind Arcanum can create must carry an explicit, code-owned recovery policy plus the
/// metadata operator surfaces need when automatic recovery cannot finish.
/// </summary>
public sealed class LongRunningOperationRecoveryRegistryTests
{
    private static IReadOnlyList<string> DeclaredKinds() =>
    [
        .. typeof(LongRunningOperationKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(static field => field is { IsLiteral: true, IsInitOnly: false }
                && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!),
    ];

    [Fact]
    public void Every_declared_kind_has_a_registry_descriptor()
    {
        string[] missing =
        [
            .. DeclaredKinds().Where(static kind => !LongRunningOperationRecoveryRegistry.Contains(kind)),
        ];

        Assert.Empty(missing);
    }

    [Fact]
    public void Registry_declares_no_kind_outside_the_bounded_vocabulary()
    {
        HashSet<string> declared = [.. DeclaredKinds()];

        string[] unknown =
        [
            .. LongRunningOperationRecoveryRegistry.Descriptors.Keys.Where(kind => !declared.Contains(kind)),
        ];

        Assert.Empty(unknown);
    }

    [Fact]
    public void Policy_catalog_cannot_drift_from_the_registry()
    {
        Assert.Equal(
            LongRunningOperationRecoveryRegistry.Descriptors.Count,
            LongRunningOperationPolicyCatalog.Registered.Count);

        foreach ((string kind, LongRunningOperationRecoveryDescriptor descriptor)
            in LongRunningOperationRecoveryRegistry.Descriptors)
        {
            Assert.True(LongRunningOperationPolicyCatalog.IsRegistered(kind, descriptor.Policy));
        }
    }

    [Fact]
    public void Every_descriptor_carries_operator_actionable_metadata()
    {
        foreach (LongRunningOperationRecoveryDescriptor descriptor
            in LongRunningOperationRecoveryRegistry.Descriptors.Values)
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Owner), descriptor.Kind);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.RecoveryIntent), descriptor.Kind);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.ManualRepairGuidance), descriptor.Kind);
        }
    }

    [Fact]
    public void Checkpoint_version_windows_are_well_formed()
    {
        foreach (LongRunningOperationRecoveryDescriptor descriptor
            in LongRunningOperationRecoveryRegistry.Descriptors.Values)
        {
            Assert.True(descriptor.MinCheckpointVersion >= 0, descriptor.Kind);
            Assert.True(descriptor.MaxCheckpointVersion >= descriptor.MinCheckpointVersion, descriptor.Kind);
        }
    }

    /// <summary>
    /// Requirement 9: backup/restore recovery must run before ordinary writes reach the target
    /// state root, so those kinds cannot share a startup phase with everyday reconciliation.
    /// </summary>
    [Fact]
    public void Backup_recovery_runs_before_ordinary_state_writes()
    {
        LongRunningOperationRecoveryDescriptor backup =
            LongRunningOperationRecoveryRegistry.Descriptors[LongRunningOperationKinds.BackupCreate];

        Assert.Equal(LongRunningOperationStartupPriority.BeforeStateWrites, backup.StartupPriority);
    }

    /// <summary>
    /// Issue #118: a data-retention mutation carrying an offline-transition launch is a Covenant
    /// reset caught between canonical erasure and its verified reopen, so it cannot share a startup
    /// phase with everyday reconciliation. The priority belongs to the kind rather than to a
    /// checkpoint version, so the legacy V0 arm and every retired payload shape move with it and
    /// stay compatible.
    /// </summary>
    /// <remarks>
    /// The window's upper bound is asserted against the launch record's own constant rather than a
    /// literal, because the defect worth guarding is the two drifting apart. The registry and the
    /// payload shape live in different projects, so a build that raised the launch version without
    /// widening the admitted window would write rows it then refused to read back at startup: an
    /// interrupted reset would be parked as unrecognised — admission left closed behind it — rather
    /// than reconciled. A hand-maintained literal here would go on passing through exactly that
    /// mistake, because the literal is the half a careless edit updates.
    /// </remarks>
    [Fact]
    public void Data_retention_mutation_runs_launch_recovery_before_ordinary_state_writes()
    {
        LongRunningOperationRecoveryDescriptor[] mutation =
        [
            .. LongRunningOperationRecoveryRegistry.Descriptors.Values
                .Where(static descriptor =>
                    descriptor.Kind == LongRunningOperationKinds.DataRetentionMutation),
        ];

        LongRunningOperationRecoveryDescriptor single = Assert.Single(mutation);

        Assert.Equal(LongRunningOperationStartupPriority.BeforeStateWrites, single.StartupPriority);

        Assert.Equal(0, single.MinCheckpointVersion);

        Assert.Equal(CovenantOfflineTransitionLaunchV4.CurrentVersion, single.MaxCheckpointVersion);

        Assert.Equal(LongRunningOperationRecoveryPolicy.ReconcileAndComplete, single.Policy);
    }

    /// <summary>
    /// Issue #118: the factory-reset kind admits its own healthy-catalog erasure launch while
    /// keeping the documented legacy V0 arm, whose rows carry no payload and are restarted
    /// idempotently.
    /// </summary>
    /// <remarks>
    /// The upper bound is pinned to the launch record's constant for the same reason as the
    /// mutation window, and the consequence of drift is worse here: a factory reset whose launch
    /// version fell outside the admitted window would leave a half-erased state root sitting behind
    /// a row startup declines to read, presented to nobody and restarted by nothing. The lower bound
    /// is held at zero just as deliberately — raising it to exclude the retired shapes would strand
    /// the payload-free legacy rows this kind is still obliged to restart.
    /// </remarks>
    [Fact]
    public void Data_retention_factory_reset_pins_its_launch_checkpoint_and_keeps_the_legacy_v0_arm()
    {
        LongRunningOperationRecoveryDescriptor factory =
            LongRunningOperationRecoveryRegistry.Descriptors[
                LongRunningOperationKinds.DataRetentionFactoryReset];

        Assert.Equal(0, factory.MinCheckpointVersion);

        Assert.Equal(DataRetentionFactoryTransitionLaunchV2.CurrentVersion, factory.MaxCheckpointVersion);

        Assert.Equal(LongRunningOperationStartupPriority.BeforeStateWrites, factory.StartupPriority);

        Assert.Equal(LongRunningOperationRecoveryPolicy.RestartIdempotently, factory.Policy);
    }

    /// <summary>
    /// A crashed host can never prove a child process, peer relay, or operator approval survived,
    /// so those kinds must not claim a resumable checkpoint.
    /// </summary>
    [Fact]
    public void Ephemeral_kinds_never_claim_resume_from_checkpoint()
    {
        string[] ephemeral =
        [
            LongRunningOperationKinds.Subagent,
            LongRunningOperationKinds.BackupCreate,
        ];

        foreach (string kind in ephemeral)
        {
            Assert.NotEqual(
                LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
                LongRunningOperationRecoveryRegistry.Descriptors[kind].Policy);
        }
    }

    [Fact]
    public void Lookup_rejects_unknown_kinds_rather_than_guessing_a_policy()
    {
        Assert.False(LongRunningOperationRecoveryRegistry.Contains("not-a-real-kind"));
        Assert.Null(LongRunningOperationRecoveryRegistry.Find("not-a-real-kind"));
    }
}
