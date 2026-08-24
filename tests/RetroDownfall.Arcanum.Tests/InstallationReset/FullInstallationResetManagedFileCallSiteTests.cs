using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The closed set of production files that may name the stopped-host managed-file surface.
/// </summary>
/// <remarks>
/// Private constructors and internal factories stop being guarantees the moment a second type can name
/// what they produce, so the guarantee is asserted over the production source set instead. These are
/// whole-file inventories rather than "does anything look wrong" checks: a new caller has to be added
/// here deliberately, and adding it is the moment somebody has to justify it.
/// </remarks>
public sealed class FullInstallationResetManagedFileCallSiteTests
{

    private const string ReconcilerPath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "FullInstallationResetManagedFileReconciler.cs";

    private const string KernelOverloadPath =
        "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/"
        + "CovenantManagedFileErasureKernel.FullInstallationReset.cs";

    private const string CoordinatorPath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "HostToolsMarkerPairResetCoordinator.cs";

    private const string CompositionRootPath =
        "src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/"
        + "ServiceCollectionExtensions.cs";

    private const string CredentialCleanupPath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "InstallationResetRestoreCredentialCleanup.cs";

    private const string AnchorTerminalPath =
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/"
        + "BackupRestoreJournalAnchorStore.FullResetTerminal.cs";

    private const string IdentityPath =
        "src/RetroDownfall.Arcanum.Secrets/Security/ArcanumCredentialIdentity.cs";

    private const string AnchorStorePath =
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalAnchorStore.cs";

    private const string KeyProviderPath =
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalKeyProvider.cs";

    private const string TerminalContinuationPath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "FullInstallationResetTerminalContinuation.cs";

    private const string ServicePath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs";

    private const string InstallationIdentityProviderPath =
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/"
        + "BackupRestoreJournalInstallationIdentityProvider.cs";

    [Fact]
    public void The_reconciler_is_the_only_caller_of_the_stopped_host_kernel_overloads()
    {

        // The leading dot matches invocations only, so the file that declares the overloads does not
        // trip its own rule.
        AssertNamedOnlyBy(
            ".ReconcileSourceForFullInstallationResetAsync(",
            ReconcilerPath);

        AssertNamedOnlyBy(
            ".ResumeWorkItemForFullInstallationResetAsync(",
            ReconcilerPath);

        // And the overloads exist where they are supposed to exist, so a rename cannot make both
        // assertions vacuously true.
        Assert.True(
            ProductionSourceInventory.Sources()
                .Single(source => source.IsExactOwner(KernelOverloadPath))
                .Names("internal async Task<Result<CovenantArtifactErasureProgress>> ReconcileSourceForFullInstallationResetAsync("));

    }

    [Fact]
    public void The_managed_file_journal_proof_is_named_by_nothing_outside_the_reconciler()
    {

        AssertNamedOnlyBy(
            "AuthenticatedFullInstallationResetManagedFileJournalProof",
            ReconcilerPath);

    }

    [Fact]
    public void The_managed_file_erasure_authority_is_named_only_by_its_owner_and_the_kernel_it_authorizes()
    {

        AssertNamedOnlyBy(
            "FullInstallationResetManagedFileErasureAuthority",
            ReconcilerPath,
            KernelOverloadPath);

    }

    [Fact]
    public void The_marker_pair_coordinator_is_the_only_production_caller_of_the_reconciler()
    {

        AssertNamedOnlyBy(
            "IFullInstallationResetManagedFileReconciler",
            ReconcilerPath,
            CoordinatorPath,
            CompositionRootPath);

        AssertNamedOnlyBy(
            "new FullInstallationResetManagedFileReconciler(",
            CompositionRootPath);

        // Asserted positively as well: a coordinator that stopped calling it would satisfy every
        // negative rule above while quietly reverting the boundary.
        Assert.True(
            ProductionSourceInventory.Sources()
                .Single(source => source.IsExactOwner(CoordinatorPath))
                .Names("_managedFiles.ReconcileAsync("));

    }

    [Fact]
    public void Only_the_restore_credential_cleanup_derives_the_three_profile_accounts_for_removal()
    {

        // Each account name has exactly one owner that reads or writes it, plus the two full-reset
        // files that read all three: the terminal proof, which digests their current values, and the
        // cleanup, which removes them. A caller outside this set would be a second opinion about which
        // accounts these are, and the credential store cannot be enumerated to catch a wrong answer.
        AssertNamedOnlyBy(
            "BackupRestoreJournalAnchorAccount(",
            IdentityPath,
            AnchorStorePath,
            AnchorTerminalPath,
            CredentialCleanupPath);

        AssertNamedOnlyBy(
            "BackupRestoreJournalKeyAccount(",
            IdentityPath,
            KeyProviderPath,
            AnchorTerminalPath,
            CredentialCleanupPath);

        AssertNamedOnlyBy(
            "BackupRestoreJournalInstallationAccount(",
            IdentityPath,
            InstallationIdentityProviderPath,
            AnchorTerminalPath,
            CredentialCleanupPath);

        // Removal is narrower still: only the cleanup deletes anything from the credential store on
        // the full-reset path, and the ordinary catalog is the only other production deleter.
        AssertNamedOnlyBy(
            "new InstallationResetRestoreCredentialCleanup(",
            CompositionRootPath);

        // And the removal itself is invoked from exactly one place: the step that has already proven
        // the managed-file inventory verified and the database file gone. Both the per-step removal
        // and the final absence check are named, because a caller that took the steps without the
        // check would publish VerifiedAbsent over an observation nobody made.
        AssertNamedOnlyBy("_credentials.RemoveStep(", TerminalContinuationPath);

        AssertNamedOnlyBy("_credentials.VerifyAllAbsent(", TerminalContinuationPath);

    }

    [Fact]
    public void The_locked_reset_service_is_the_only_production_caller_of_the_terminal_continuation()
    {

        AssertNamedOnlyBy(
            "IFullInstallationResetTerminalContinuation",
            TerminalContinuationPath,
            ServicePath,
            CompositionRootPath);

        AssertNamedOnlyBy(
            "new FullInstallationResetTerminalContinuation(",
            CompositionRootPath);

        // Asserted positively too: a service that stopped calling it would satisfy every negative
        // rule above while silently restoring an ending that never removed the restore credentials.
        ProductionSource service = ProductionSourceInventory.Sources()
            .Single(source => source.IsExactOwner(ServicePath));

        Assert.True(service.Names("terminal.CompleteAsync("));

    }

    [Fact]
    public void Only_the_terminal_continuation_gates_the_ending_on_a_verified_managed_file_inventory()
    {

        // Two gates, in two files, and both have to stay. The service refuses to continue past
        // admission without a terminal reconciliation; the continuation refuses to remove a restore
        // credential without one. Either alone would let a partially reconciled installation lose the
        // evidence that could have finished its interrupted restore.
        ProductionSource service = ProductionSourceInventory.Sources()
            .Single(source => source.IsExactOwner(ServicePath));

        ProductionSource continuation = ProductionSourceInventory.Sources()
            .Single(source => source.IsExactOwner(TerminalContinuationPath));

        Assert.All(
            new[] { service, continuation },
            static source => Assert.True(
                source.Names(
                    "Phase: FullInstallationResetManagedFileReconciliationPhase.TerminalInventoryVerified,")));

        // And the continuation observes the database rather than trusting a report of its removal.
        Assert.True(continuation.Names("File.Exists(_grimoireDatabaseFile)"));

    }

    [Fact]
    public void The_reconciler_resolves_no_filesystem_credential_or_path_primitive_of_its_own()
    {

        // It owns the order, the authentication, and the arithmetic. Every effect belongs to the one
        // shared erasure machine or to the write-intent recovery, and a reconciler that could open a
        // file or read a credential would be a second answer to "is this file Arcanum's".
        string[] forbidden =
        [
            "IOsCredentialStore",
            "IManagedFileCapabilityOpener",
            "IManagedFileOwnershipVerifier",
            "ManagedFileCapabilityOpener",
            "ManagedFileOwnershipVerifier",
            "File.",
            "Directory.",
            "FileStream",
            "SafeFileHandle",
            "ArcanumPaths",
        ];

        ProductionSource reconciler = ProductionSourceInventory.Sources()
            .Single(source => source.IsExactOwner(ReconcilerPath));

        Assert.DoesNotContain(forbidden, construct => reconciler.Names(construct));

    }

    private static void AssertNamedOnlyBy(string construct, params string[] allowed)
    {

        string[] namers = [.. ProductionSourceInventory.Sources()
            .Where(source => source.Names(construct))
            .Select(static source => NormalizeSeparators(source.RelativePath))
            .Order(StringComparer.Ordinal)];

        Assert.Equal(
            [.. allowed.Select(NormalizeSeparators).Order(StringComparer.Ordinal)],
            namers);

    }

    private static string NormalizeSeparators(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

}
