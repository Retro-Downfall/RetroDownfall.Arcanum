using System.Reflection;
using RetroDownfall.Arcanum.Core.Operations;

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
    /// Issue #118: a V3 data-retention mutation is a Covenant reset caught between canonical erasure
    /// and its verified reopen, so it cannot share a startup phase with everyday reconciliation. The
    /// priority belongs to the kind rather than to a checkpoint version, so the legacy V0 and V2
    /// arms move with it and stay compatible.
    /// </summary>
    [Fact]
    public void Data_retention_mutation_runs_v3_recovery_before_ordinary_state_writes()
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

        Assert.Equal(3, single.MaxCheckpointVersion);

        Assert.Equal(LongRunningOperationRecoveryPolicy.ReconcileAndComplete, single.Policy);
    }

    /// <summary>
    /// Issue #118: the factory-reset kind gains its own V1 erasure checkpoint while keeping the
    /// documented legacy V0 arm, whose rows carry no payload and are restarted idempotently.
    /// </summary>
    [Fact]
    public void Data_retention_factory_reset_pins_its_v1_checkpoint_and_keeps_the_legacy_v0_arm()
    {
        LongRunningOperationRecoveryDescriptor factory =
            LongRunningOperationRecoveryRegistry.Descriptors[
                LongRunningOperationKinds.DataRetentionFactoryReset];

        Assert.Equal(0, factory.MinCheckpointVersion);

        Assert.Equal(1, factory.MaxCheckpointVersion);

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
