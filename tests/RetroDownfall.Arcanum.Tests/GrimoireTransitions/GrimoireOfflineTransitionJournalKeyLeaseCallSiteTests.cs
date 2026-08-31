using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

public sealed class GrimoireOfflineTransitionJournalKeyLeaseCallSiteTests
{

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

        const string authenticator =
            "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/"
            + "GrimoireOfflineTransitionJournalAuthenticator.cs";

        const string provider =
            "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/"
            + "GrimoireOfflineTransitionJournalKeyProvider.cs";

        List<string> takers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source =>
                    !source.IsExactOwner(authenticator)
                    && source.Names("GrimoireOfflineTransitionJournalKeyLease")
                    && source.Names(".TryTakeKey("))
                .Select(static source => source.RelativePath),
        ];

        List<string> minters =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source =>
                    !source.IsExactOwner(provider)
                    && source.Names("GrimoireOfflineTransitionJournalKeyLease.Mint"))
                .Select(static source => source.RelativePath),
        ];

        Assert.Empty(takers);

        Assert.Empty(minters);

    }

}
