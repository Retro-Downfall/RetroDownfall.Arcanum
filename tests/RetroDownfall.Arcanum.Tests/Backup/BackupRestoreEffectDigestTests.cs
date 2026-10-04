using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// The identity that binds a restore's exclusive owner to one exact destructive plan (§10.12).
/// </summary>
/// <remarks>
/// Every destructive input has to move the digest, because the digest is what a retry is compared
/// against before it may adopt a half-finished sequence. A field that did not change it would let a
/// changed plan finish work the operator authorized under different terms.
/// </remarks>
public sealed class BackupRestoreEffectDigestTests
{

    private static readonly IBackupRestoreEffectDigestCalculator Calculator =
        new BackupRestoreEffectDigestCalculator();

    private static readonly Guid Installation = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void One_domain_preimage_produces_a_stable_digest_for_the_same_plan()
    {

        Result<CovenantDigest> first = Calculator.Compute(Input());

        Result<CovenantDigest> second = Calculator.Compute(Input());

        Assert.True(first.IsSuccess, Describe(first));
        Assert.Equal(first.Value, second.Value);

    }

    [Fact]
    public void Every_destructive_input_changes_the_digest()
    {

        CovenantDigest baseline = Calculator.Compute(Input()).Value;

        Assert.NotEqual(baseline, Calculator.Compute(Input() with { ArchiveManifestDigest = Digest(9) }).Value);

        Assert.NotEqual(
            baseline,
            Calculator.Compute(Input() with { ArchivePhysicalIdentityDigest = Digest(9) }).Value);

        Assert.NotEqual(baseline, Calculator.Compute(Input() with { ProfileNamespaceDigest = Digest(9) }).Value);

        Assert.NotEqual(baseline, Calculator.Compute(Input() with { InstallationId = Guid.NewGuid() }).Value);

        Assert.NotEqual(
            baseline,
            Calculator.Compute(Input() with { DestinationRootIdentityDigest = Digest(9) }).Value);

        Assert.NotEqual(
            baseline,
            Calculator.Compute(Input() with { ConflictMode = BackupRestoreConflictMode.NewProfileRoot }).Value);

        Assert.NotEqual(
            baseline,
            Calculator.Compute(Input() with
            {
                ProtectedStateMode = BackupProtectedStateMode.PurgeProtectedState,
            }).Value);

        Assert.NotEqual(baseline, Calculator.Compute(Input() with { PathMappingVectorDigest = Digest(9) }).Value);

        Assert.NotEqual(baseline, Calculator.Compute(Input() with { RestoreMasterApiKey = true }).Value);

        Assert.NotEqual(baseline, Calculator.Compute(Input() with { CreateSafetyBackup = false }).Value);

    }

    [Fact]
    public void Restore_and_purge_are_never_the_same_authorization()
    {

        CovenantDigest restore = Calculator.Compute(Input() with
        {
            ProtectedStateMode = BackupProtectedStateMode.RestoreProtectedState,
        }).Value;

        CovenantDigest purge = Calculator.Compute(Input() with
        {
            ProtectedStateMode = BackupProtectedStateMode.PurgeProtectedState,
        }).Value;

        // Opposite destructive acts. A shared digest would let an operator who confirmed one have the
        // other finished under the same owner after a crash.
        Assert.NotEqual(restore, purge);

    }

    [Fact]
    public void Null_malformed_and_unsupported_inputs_are_refused_before_hashing()
    {

        Assert.True(Calculator.Compute(null!).IsFailure);

        Assert.True(Calculator.Compute(Input() with { InstallationId = Guid.Empty }).IsFailure);

        Assert.True(Calculator.Compute(Input() with { ArchiveManifestDigest = default }).IsFailure);

        Assert.True(Calculator.Compute(Input() with { DestinationRootIdentityDigest = default }).IsFailure);

        Assert.True(Calculator.Compute(Input() with { ProtectedStateMode = (BackupProtectedStateMode)99 }).IsFailure);

        // Selective import authorizes no installation-level effect and has its own transfer digest.
        Result<CovenantDigest> selective = Calculator.Compute(Input() with
        {
            ConflictMode = BackupRestoreConflictMode.ImportSelectedSessions,
        });

        Assert.True(selective.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.InvalidScope, selective.Error.Code);

    }

    [Fact]
    public void The_path_mapping_vector_is_order_independent_and_membership_sensitive()
    {

        BackupPathMapping first = new(BackupPathMappingKind.CampaignRoot, "/a", "/b");

        BackupPathMapping second = new(BackupPathMappingKind.WorkspaceRoot, "/c", "/d");

        CovenantDigest forward = BackupRestoreEffectDigestCalculator.PathMappingVector([first, second]).Value;

        CovenantDigest reversed = BackupRestoreEffectDigestCalculator.PathMappingVector([second, first]).Value;

        // A reordered command line is the same destructive plan, so it must produce the same owner.
        Assert.Equal(forward, reversed);

        Assert.NotEqual(
            forward,
            BackupRestoreEffectDigestCalculator.PathMappingVector([first]).Value);

        Assert.NotEqual(
            forward,
            BackupRestoreEffectDigestCalculator
                .PathMappingVector([first, second with { To = "/e" }])
                .Value);

        Assert.True(
            BackupRestoreEffectDigestCalculator
                .PathMappingVector(default(ImmutableArray<BackupPathMapping>))
                .IsFailure);

    }

    [Fact]
    public void The_destination_root_identity_binds_presence_and_never_path_text()
    {

        CovenantDigest present = BackupRestoreEffectDigestCalculator.DestinationRootIdentity(
            Digest(1),
            "arcanum",
            rootPresent: true);

        CovenantDigest absent = BackupRestoreEffectDigestCalculator.DestinationRootIdentity(
            Digest(1),
            "arcanum",
            rootPresent: false);

        // "The root exists" and "the root is absent" are different destinations. A digest that
        // ignored presence would let a NewProfileRoot owner be replayed over a root that meanwhile
        // came into existence.
        Assert.NotEqual(present, absent);

        Assert.NotEqual(
            present,
            BackupRestoreEffectDigestCalculator.DestinationRootIdentity(Digest(2), "arcanum", true));

        Assert.NotEqual(
            present,
            BackupRestoreEffectDigestCalculator.DestinationRootIdentity(Digest(1), "arcanum2", true));

    }

    /// <summary>
    /// The erasure evidence a restore applies is the destination's own, read at execute time, and is
    /// deliberately outside both the restore effect digest and the authenticated journal: the digest binds
    /// the operator's destructive choice, and the journal names the operation and its topology. Neither
    /// may grow an erasure input, because a digest that moved with the destination's evidence would orphan
    /// every half-finished restore the moment anything was erased or released.
    /// </summary>
    /// <remarks>
    /// A frozen pin: the input's and the payload's members by name, and the digest of one fixed input as
    /// this build computed it before restore staging began applying erasure evidence.
    /// </remarks>
    [Fact]
    public void The_restore_effect_digest_and_the_v2_journal_payload_carry_no_erasure_evidence()
    {

        Assert.Equal(
            [
                "ArchiveManifestDigest",
                "ArchivePhysicalIdentityDigest",
                "ProfileNamespaceDigest",
                "InstallationId",
                "DestinationRootIdentityDigest",
                "ConflictMode",
                "ProtectedStateMode",
                "PathMappingVectorDigest",
                "RestoreMasterApiKey",
                "CreateSafetyBackup",
            ],
            ParameterNames(typeof(BackupRestoreEffectDigestInput)));

        Assert.Equal(
            [
                "OwnerOperationId",
                "OwnerOperation",
                "OwnerEffectDigest",
                "ConflictMode",
                "Phase",
                "RestoreRequestDigest",
                "LiveRoot",
                "StagedRoot",
                "DisplacedRoot",
                "ArchiveSource",
                "SafetyBackup",
                "MarkerCleanup",
            ],
            ParameterNames(typeof(RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreJournalPayloadV2)));

        Result<CovenantDigest> digest = Calculator.Compute(Input());

        Assert.True(digest.IsSuccess, Describe(digest));

        Assert.Equal(
            "ae4e04d965b13ebd33197bc1b5b9a4409dbec1cfadff84dd92d28b4eb656147f",
            Convert.ToHexStringLower(digest.Value.Bytes));

    }

    private static string[] ParameterNames(Type type) =>
        [.. Assert.Single(type.GetConstructors()).GetParameters().Select(static parameter => parameter.Name ?? string.Empty)];

    private static BackupRestoreEffectDigestInput Input() =>
        new(
            Digest(1),
            Digest(2),
            Digest(3),
            Installation,
            Digest(4),
            BackupRestoreConflictMode.ReplaceInstallation,
            BackupProtectedStateMode.Reject,
            Digest(5),
            RestoreMasterApiKey: false,
            CreateSafetyBackup: true);

    private static CovenantDigest Digest(byte seed) => new([.. Enumerable.Repeat(seed, 32)]);

    private static string Describe<T>(Result<T> result) =>
        result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : string.Empty;

}
