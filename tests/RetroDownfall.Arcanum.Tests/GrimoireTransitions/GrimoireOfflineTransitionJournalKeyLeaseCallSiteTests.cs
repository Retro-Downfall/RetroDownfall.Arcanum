using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

public sealed class GrimoireOfflineTransitionJournalKeyLeaseCallSiteTests
{

    private const string AuthenticatorPath =
        "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/"
        + "GrimoireOfflineTransitionJournalAuthenticator.cs";

    private const string ProviderPath =
        "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/"
        + "GrimoireOfflineTransitionJournalKeyProvider.cs";

    private const string BackupAuthenticatorPath =
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalAuthenticator.cs";

    private const string BackupProviderPath =
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalKeyProvider.cs";

    private const string ActiveAuthenticatorPath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "InstallationResetActiveRecordAuthenticator.cs";

    private const string ActiveProviderPath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "InstallationResetActiveRecordKeyProvider.cs";

    private const string EncryptBrokerCall =
        "EncryptGrimoireOfflineTransitionJournal(";

    private const string DecryptBrokerCall =
        "DecryptGrimoireOfflineTransitionJournal(";

    [Fact]
    public void Transition_credentials_are_profile_scoped_and_excluded_from_ordinary_cleanup()
    {

        const string credentials = "src/RetroDownfall.Arcanum.Secrets/Security/ArcanumCredentialIdentity.cs";

        List<string> offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source =>
                    !source.IsExactOwner(credentials)
                    && source.Names("\"grimoire-transition-journal-"))
                .Select(static source => source.RelativePath),
        ];

        Assert.Empty(offenders);

        string keyProvider =
            "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/"
            + "GrimoireOfflineTransitionJournalKeyProvider.cs";

        string anchorStore =
            "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/"
            + "GrimoireOfflineTransitionJournalAnchorStore.cs";

        List<string> keyFactoryCallers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source =>
                    !source.IsExactOwner(credentials)
                    && !source.IsExactOwner(keyProvider)
                    && source.Names("ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccount("))
                .Select(static source => source.RelativePath),
        ];

        List<string> anchorFactoryCallers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source =>
                    !source.IsExactOwner(credentials)
                    && !source.IsExactOwner(anchorStore)
                    && source.Names("ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccount("))
                .Select(static source => source.RelativePath),
        ];

        List<string> deletionFactoryCallers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source =>
                    source.Names(".Delete(")
                    && (source.Names("GrimoireTransitionJournalKeyAccount(")
                        || source.Names("GrimoireTransitionJournalAnchorAccount(")))
                .Select(static source => source.RelativePath),
        ];

        Assert.Empty(keyFactoryCallers);

        Assert.Empty(anchorFactoryCallers);

        Assert.Empty(deletionFactoryCallers);

    }

    [Fact]
    public void Key_lease_call_sites_are_limited_to_the_authenticator_and_provider()
    {

        IReadOnlyList<ProductionSource> sources = ProductionSourceInventory.Sources();

        List<string> takers = FindUnauthorizedRawKeyTakers(sources);

        List<string> minters = FindUnauthorizedTransitionLeaseMinters(sources);

        List<string> brokerCallers = FindUnauthorizedTransitionBrokerCallers(sources);

        Assert.Empty(takers);

        Assert.Empty(minters);

        Assert.Empty(brokerCallers);

        ProductionSource authenticator = sources.Single(source => source.IsExactOwner(AuthenticatorPath));

        Assert.False(authenticator.Names(".TryTakeKey("));

        Assert.False(authenticator.Names("AesGcm"));

        Assert.Equal(1, authenticator.Occurrences(EncryptBrokerCall));

        Assert.Equal(1, authenticator.Occurrences(DecryptBrokerCall));

        ProductionSource backupProvider = sources.Single(source => source.IsExactOwner(BackupProviderPath));

        ProductionSource transitionProvider = sources.Single(source => source.IsExactOwner(ProviderPath));

        Assert.True(backupProvider.Names("internal abstract class StableJournalKeyLease"));

        Assert.True(backupProvider.Names("BackupRestoreJournalKeyLease : StableJournalKeyLease"));

        Assert.True(transitionProvider.Names(
            "GrimoireOfflineTransitionJournalKeyLease : StableJournalKeyLease"));

    }

    [Fact]
    public void Key_architecture_guards_reject_inferred_receivers_and_duplicate_basenames()
    {

        ProductionSource[] sources =
        [
            new(BackupAuthenticatorPath, "lease.TryTakeKey(out _);"),
            new(BackupProviderPath, "lease.TryTakeKey(out _);"),
            new(
                "src/Adversarial/BackupRestoreJournalAuthenticator.cs",
                "lease.TryTakeKey(out _);"),
            new(
                "src/Adversarial/TransitionService.cs",
                "var lease = provider.OpenExisting(profile).Value; lease.TryTakeKey(out _);"),
            new(AuthenticatorPath, EncryptBrokerCall + DecryptBrokerCall),
            new(
                "src/Adversarial/GrimoireOfflineTransitionJournalAuthenticator.cs",
                EncryptBrokerCall),
            new(
                "src/Adversarial/TransitionBrokerCaller.cs",
                DecryptBrokerCall),
            new(ProviderPath, "GrimoireOfflineTransitionJournalKeyLease.Mint(material)"),
            new(
                "src/Adversarial/GrimoireOfflineTransitionJournalKeyProvider.cs",
                "GrimoireOfflineTransitionJournalKeyLease.Mint(material)"),
        ];

        Assert.Equal(
            [
                "src/Adversarial/BackupRestoreJournalAuthenticator.cs",
                "src/Adversarial/TransitionService.cs",
            ],
            FindUnauthorizedRawKeyTakers(sources));

        Assert.Equal(
            [
                "src/Adversarial/GrimoireOfflineTransitionJournalAuthenticator.cs",
                "src/Adversarial/TransitionBrokerCaller.cs",
            ],
            FindUnauthorizedTransitionBrokerCallers(sources));

        Assert.Equal(
            ["src/Adversarial/GrimoireOfflineTransitionJournalKeyProvider.cs"],
            FindUnauthorizedTransitionLeaseMinters(sources));

    }

    private static List<string> FindUnauthorizedRawKeyTakers(
        IEnumerable<ProductionSource> sources) =>
        [
            .. sources
                .Where(static source =>
                    !source.IsExactOwner(BackupAuthenticatorPath)
                    && !source.IsExactOwner(BackupProviderPath)
                    && !source.IsExactOwner(ActiveAuthenticatorPath)
                    && !source.IsExactOwner(ActiveProviderPath)
                    && source.Names(".TryTakeKey("))
                .Select(static source => source.RelativePath),
        ];

    private static List<string> FindUnauthorizedTransitionBrokerCallers(
        IEnumerable<ProductionSource> sources) =>
        [
            .. sources
                .Where(static source =>
                    !source.IsExactOwner(AuthenticatorPath)
                    && !source.IsExactOwner(BackupAuthenticatorPath)
                    && (source.Names(EncryptBrokerCall) || source.Names(DecryptBrokerCall)))
                .Select(static source => source.RelativePath),
        ];

    private static List<string> FindUnauthorizedTransitionLeaseMinters(
        IEnumerable<ProductionSource> sources) =>
        [
            .. sources
                .Where(static source =>
                    !source.IsExactOwner(ProviderPath)
                    && source.Names("GrimoireOfflineTransitionJournalKeyLease.Mint"))
                .Select(static source => source.RelativePath),
        ];

}
