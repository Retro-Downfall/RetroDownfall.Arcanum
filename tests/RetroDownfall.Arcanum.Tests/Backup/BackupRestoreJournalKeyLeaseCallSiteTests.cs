using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// Source-level guards on who may touch the restore journal's key and account names.
/// </summary>
/// <remarks>
/// C# has no friend classes, so "only the authenticator may take the key" and "only
/// <c>ArcanumCredentialIdentity</c> may spell a journal account" are properties of the call graph
/// rather than of a visibility modifier. These are inventory assertions over production source rather
/// than behavior tests, because the failure they prevent is a new call site, not a wrong result — a
/// second taker would be a second place that decides how long the key lives, and a hand-spelled
/// account name would be one that skipped the profile-namespace validation entirely (§10.19.6).
/// </remarks>
public sealed class BackupRestoreJournalKeyLeaseCallSiteTests
{

    private const string AuthenticatorPath =
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalAuthenticator.cs";

    private const string KeyProviderPath =
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalKeyProvider.cs";

    private const string ActiveAuthenticatorPath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "InstallationResetActiveRecordAuthenticator.cs";

    private const string ActiveKeyProviderPath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "InstallationResetActiveRecordKeyProvider.cs";

    private const string CredentialIdentityPath =
        "src/RetroDownfall.Arcanum.Secrets/Security/ArcanumCredentialIdentity.cs";

    // The credential layer itself, which only forwards the account name it is handed.
    private static readonly string[] CredentialBackendOwners =
    [
        "src/RetroDownfall.Arcanum.Secrets/Security/IOsCredentialStore.cs",
        "src/RetroDownfall.Arcanum.Secrets/Security/OsCredentialStore.cs",
        "src/RetroDownfall.Arcanum.Secrets/Security/WindowsOsCredentialStore.cs",
        "src/RetroDownfall.Arcanum.Secrets/Security/MacOsCredentialStore.cs",
        "src/RetroDownfall.Arcanum.Secrets/Security/LinuxOsCredentialStore.cs",
        "src/RetroDownfall.Arcanum.Secrets/Security/InMemoryOsCredentialStore.cs",
    ];

    // The callers that decide *which* account goes away. Each is here with its reason.
    private static readonly string[] CredentialDeletionDeciderOwners =
    [
        // The closed reset catalog. The three journal accounts are absent from CollectAccounts.
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "InstallationResetCredentialCatalog.cs",

        // Removes only one profile's reset-active key under the concrete installation lock.
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "InstallationResetActiveRecordKeyProvider.cs",

        // Removes only one profile's reset-active anchor under that same concrete lock.
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "InstallationResetActiveAnchorStore.cs",

        // Compare-deletes only host-process-tools-taint, and only against its exact digest.
        "src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsMarkerStore.cs",

        // Purges only the superseded master-api-key after a failed OS write.
        "src/RetroDownfall.Arcanum.Infrastructure/Security/OsKeychainSecretStore.cs",

        // Only inference-provider-{NAME}-api-key.
        "src/RetroDownfall.Arcanum.Infrastructure/Security/ProviderCredentialStore.cs",

        // Only provider-perplexity-api-key.
        "src/RetroDownfall.Arcanum.Infrastructure/Security/WebResearchCredentialStore.cs",

        // The one path that may remove the three restore-journal accounts, and the only entry in this
        // inventory allowed to name them. It removes nothing else, it removes them only in the anchor,
        // journal-key, installation-identity order, and it removes each one only when the account's
        // current value reproduces the digest a terminal-state proof projected for that exact account
        // name. Without that proof it deletes nothing at all, which is what keeps every ordinary
        // cleanup, Covenant reset, family reinitialize, and restore retaining them byte-for-byte.
        FullResetRestoreCredentialRemover,
    ];

    /// <summary>
    /// The single declared exception to the rule below, named once so both rules refer to the same file.
    /// </summary>
    private const string FullResetRestoreCredentialRemover =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "InstallationResetRestoreCredentialCleanup.cs";

    [Fact]
    public void The_authenticator_is_the_only_production_caller_that_takes_a_journal_key()
    {

        List<string> offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    !source.IsExactOwner(AuthenticatorPath)
                    && !source.IsExactOwner(KeyProviderPath)
                    && !source.IsExactOwner(ActiveAuthenticatorPath)
                    && !source.IsExactOwner(ActiveKeyProviderPath)
                    && source.Names("TryTakeKey"))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            offenders.Count == 0,
            "The single take of a restore journal key belongs to BackupRestoreJournalAuthenticator, "
            + "which spends it on one bounded AES-GCM operation and zeroes the buffer in a finally. "
            + "Route these through it: "
            + string.Join(", ", offenders));

    }

    [Fact]
    public void The_key_provider_is_the_only_production_minter_of_a_journal_key_lease()
    {

        List<string> offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    !source.IsExactOwner(KeyProviderPath)
                    && source.Names("BackupRestoreJournalKeyLease.Mint"))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            offenders.Count == 0,
            "A restore journal key lease is minted only from the namespaced credential account, after "
            + "the stored material has been proven canonical: "
            + string.Join(", ", offenders));

    }

    [Fact]
    public void Journal_credential_accounts_are_spelled_only_by_the_credential_identity()
    {

        List<string> offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    !source.IsExactOwner(CredentialIdentityPath)
                    && source.Names("\"backup-restore-journal-"))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            offenders.Count == 0,
            "A restore journal account name is derived only through ArcanumCredentialIdentity, which "
            + "refuses any suffix that is not a canonical profile-namespace digest. A hand-spelled "
            + "name would be one that skipped that check: "
            + string.Join(", ", offenders));

    }

    /// <summary>
    /// The complete inventory of production code that can remove an OS credential.
    /// </summary>
    /// <remarks>
    /// This is what actually makes "ordinary credential cleanup, Covenant reset, family reinitialize,
    /// and restore never remove the three restore-journal accounts" true. `IOsCredentialStore` has no
    /// enumeration surface, so no path can discover an account it was not handed by name; every
    /// deletion is therefore either the closed reset catalog — which the journal accounts are absent
    /// from — or one of three sites hard-coded to a single unrelated account. A new deletion site is
    /// the only way that could stop being true, so a new one has to be justified here.
    /// </remarks>
    [Fact]
    public void Credential_deletion_stays_inside_its_known_inventory()
    {

        List<string> offenders = CredentialDeletionOffenders(
            ProductionSourceInventory.Sources());

        Assert.True(
            offenders.Count == 0,
            "A new OS credential deletion site changes which accounts survive an ordinary cleanup. "
            + "Add it to this inventory only with a reason, and prove the three restore-journal "
            + "accounts still survive it: "
            + string.Join(", ", offenders));

        // And no decider except the attested full-reset remover can name a restore-journal account at
        // all, which is what turns "absent from the catalog" into "unreachable by every deletion path".
        List<string> naming =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source =>
                    CredentialDeletionDeciderOwners.Any(source.IsExactOwner)
                    && !source.IsExactOwner(FullResetRestoreCredentialRemover)
                    && source.Names("ArcanumCredentialIdentity.BackupRestoreJournal"))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            naming.Count == 0,
            "A credential deletion site that can derive a restore-journal account name can remove one. "
            + "Ordinary cleanup, Covenant reset, family reinitialize, and restore must all retain the "
            + "three byte-for-byte: "
            + string.Join(", ", naming));

        // The exception is only safe because it cannot act without a terminal-state proof. If the
        // remover ever stops taking one, it becomes an unconditional deleter of the three accounts and
        // this exemption has to be withdrawn.
        ProductionSource remover = ProductionSourceInventory.Sources()
            .Single(source => source.IsExactOwner(FullResetRestoreCredentialRemover));

        Assert.True(remover.Names("BackupRestoreFullResetTerminalProjectionV1 terminal"));

        Assert.True(remover.Names("AccountValueDigest(account, value) != expected"));

    }

    [Fact]
    public void Credential_deletion_inventory_rejects_duplicate_basenames_outside_owner_directories()
    {

        const string wrongBackend =
            "src/Adversarial/Security/IOsCredentialStore.cs";

        const string wrongDecider =
            "src/Adversarial/InstallationReset/InstallationResetActiveAnchorStore.cs";

        ProductionSource[] sources =
        [
            new(
                "src/RetroDownfall.Arcanum.Secrets/Security/IOsCredentialStore.cs",
                "credentials.Delete("),
            new(wrongBackend, "credentials.Delete("),
            new(
                "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
                + "InstallationResetActiveAnchorStore.cs",
                "credentials.Delete("),
            new(wrongDecider, "credentials.Delete("),
        ];

        Assert.Equal(
            [wrongBackend, wrongDecider],
            CredentialDeletionOffenders(sources));

    }

    private static List<string> CredentialDeletionOffenders(
        IEnumerable<ProductionSource> sources) =>
    [
        .. sources
            .Where(source =>
                !CredentialBackendOwners.Any(source.IsExactOwner)
                && !CredentialDeletionDeciderOwners.Any(source.IsExactOwner)
                && (source.Names(".Delete(ArcanumCredentialIdentity.Service")
                    || source.Names("credentials.Delete(")
                    || source.Names("credentialStore.Delete(")
                    || source.Names("_credentials.Delete(")
                    || source.Names("_osStore.Delete(")))
            .Select(static source => source.RelativePath),
    ];

}
