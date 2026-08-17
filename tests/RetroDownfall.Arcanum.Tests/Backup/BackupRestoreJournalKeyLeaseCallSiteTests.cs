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

    private const string AuthenticatorFileName = "BackupRestoreJournalAuthenticator.cs";

    private const string KeyProviderFileName = "BackupRestoreJournalKeyProvider.cs";

    private const string CredentialIdentityFileName = "ArcanumCredentialIdentity.cs";

    [Fact]
    public void The_authenticator_is_the_only_production_caller_that_takes_a_journal_key()
    {

        List<string> offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    !source.Is(AuthenticatorFileName)
                    && !source.Is(KeyProviderFileName)
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
                    !source.Is(KeyProviderFileName)
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
                    !source.Is(CredentialIdentityFileName)
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

        // The credential layer itself, which only forwards the account name it is handed.
        string[] backends =
        [
            "IOsCredentialStore.cs",
            "OsCredentialStore.cs",
            "WindowsOsCredentialStore.cs",
            "MacOsCredentialStore.cs",
            "LinuxOsCredentialStore.cs",
            "InMemoryOsCredentialStore.cs",
        ];

        // The callers that decide *which* account goes away. Each is here with its reason.
        string[] deciders =
        [
            // The closed reset catalog. The three journal accounts are absent from CollectAccounts.
            "InstallationResetCredentialCatalog.cs",

            // Compare-deletes only host-process-tools-taint, and only against its exact digest.
            "HostProcessToolsMarkerStore.cs",

            // Purges only the superseded master-api-key after a failed OS write.
            "OsKeychainSecretStore.cs",

            // Only inference-provider-{NAME}-api-key.
            "ProviderCredentialStore.cs",

            // Only provider-perplexity-api-key.
            "WebResearchCredentialStore.cs",
        ];

        List<string> offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source =>
                    !backends.Any(name => source.Is(name))
                    && !deciders.Any(name => source.Is(name))
                    && (source.Names(".Delete(ArcanumCredentialIdentity.Service")
                        || source.Names("credentials.Delete(")
                        || source.Names("credentialStore.Delete(")
                        || source.Names("_credentials.Delete(")
                        || source.Names("_osStore.Delete(")))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            offenders.Count == 0,
            "A new OS credential deletion site changes which accounts survive an ordinary cleanup. "
            + "Add it to this inventory only with a reason, and prove the three restore-journal "
            + "accounts still survive it: "
            + string.Join(", ", offenders));

        // And no decider can name a restore-journal account at all, which is what turns "absent from
        // the catalog" into "unreachable by every deletion path".
        List<string> naming =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source =>
                    deciders.Any(name => source.Is(name))
                    && source.Names("ArcanumCredentialIdentity.BackupRestoreJournal"))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            naming.Count == 0,
            "A credential deletion site that can derive a restore-journal account name can remove one. "
            + "Ordinary cleanup, Covenant reset, family reinitialize, and restore must all retain the "
            + "three byte-for-byte: "
            + string.Join(", ", naming));

    }

}
