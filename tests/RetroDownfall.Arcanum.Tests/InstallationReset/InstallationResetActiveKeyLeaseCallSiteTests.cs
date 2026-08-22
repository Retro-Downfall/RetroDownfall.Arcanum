using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>Source-level guards on who may take or retire reset-active credential authority.</summary>
public sealed class InstallationResetActiveKeyLeaseCallSiteTests
{

    private const string ActiveAuthenticatorPath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "InstallationResetActiveRecordAuthenticator.cs";

    private const string BackupRestoreAuthenticatorPath =
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalAuthenticator.cs";

    private const string ActiveStorePath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "InstallationResetActiveStore.cs";

    private const string AnchorStorePath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "InstallationResetActiveAnchorStore.cs";

    private const string KeyProviderPath =
        "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/"
        + "InstallationResetActiveRecordKeyProvider.cs";

    private const string CredentialIdentityPath =
        "src/RetroDownfall.Arcanum.Secrets/Security/ArcanumCredentialIdentity.cs";

    [Fact]
    public void Only_the_active_authenticator_can_take_an_active_record_key_lease()
    {

        List<string> takers = FindUnauthorizedTryTakeKeyCallers(
            ProductionSourceInventory.Sources());

        Assert.True(
            takers.Count == 0,
            "The only caller that may spend an installation-reset active-record key lease is "
            + "InstallationResetActiveRecordAuthenticator, which must zero the taken array in a "
            + "finally. Route these callers through it: "
            + string.Join(", ", takers));

        List<string> minters =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    !source.IsExactOwner(KeyProviderPath)
                    && source.Names("InstallationResetActiveRecordKeyLease.Mint"))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            minters.Count == 0,
            "Only InstallationResetActiveRecordKeyProvider may mint a lease from canonical OS "
            + "credential material: "
            + string.Join(", ", minters));

    }

    [Fact]
    public void Active_credential_owner_guard_rejects_duplicate_basenames_in_wrong_directories()
    {

        ProductionSource[] sources =
        [
            new(
                "src/Wrong/InstallationResetActiveStore.cs",
                "keys.RemoveAndVerifyAbsent(heldLock, root, profile);"),
            new(
                "src/Wrong/InstallationResetActiveRecordKeyProvider.cs",
                "InstallationResetActiveKeyAccount(profile); credentials.Delete(service, account);"),
            new(
                "src/Wrong/InstallationResetActiveAnchorStore.cs",
                "InstallationResetActiveAnchorAccount(profile); credentials.Delete(service, account);"),
            new(
                "src/Wrong/ArcanumCredentialIdentity.cs",
                "const string value = \"installation-reset-active-key-\";"),
        ];

        Assert.Equal(
            ["src/Wrong/InstallationResetActiveStore.cs"],
            FindUnauthorizedRemovalCallers(sources));

        Assert.Equal(
            [
                "src/Wrong/InstallationResetActiveRecordKeyProvider.cs",
                "src/Wrong/InstallationResetActiveAnchorStore.cs",
            ],
            FindUnauthorizedDirectDeletionCallers(sources));

        Assert.Equal(
            ["src/Wrong/InstallationResetActiveRecordKeyProvider.cs"],
            FindUnauthorizedKeyNamingCallers(sources));

        Assert.Equal(
            ["src/Wrong/InstallationResetActiveAnchorStore.cs"],
            FindUnauthorizedAnchorNamingCallers(sources));

        Assert.Equal(
            ["src/Wrong/ArcanumCredentialIdentity.cs"],
            FindUnauthorizedHandSpelledAccounts(sources));

    }

    [Fact]
    public void Active_key_taker_guard_rejects_an_inferred_receiver_outside_known_authenticators()
    {

        ProductionSource[] sources =
        [
            new(
                "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs",
                "var lease = provider.OpenExisting(profile).Value; lease.TryTakeKey(out _);"),
            new(
                "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalAuthenticator.cs",
                "lease.TryTakeKey(out _);"),
            new(
                "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveRecordAuthenticator.cs",
                "lease.TryTakeKey(out _);"),
        ];

        Assert.Equal(
            ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs"],
            FindUnauthorizedTryTakeKeyCallers(sources));

    }

    [Fact]
    public void Active_key_taker_guard_requires_exact_authenticator_repository_paths()
    {

        ProductionSource[] sources =
        [
            new(
                "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalAuthenticator.cs",
                "lease.TryTakeKey(out _);"),
            new(
                "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetActiveRecordAuthenticator.cs",
                "lease.TryTakeKey(out _);"),
            new(
                "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/BackupRestoreJournalAuthenticator.cs",
                "lease.TryTakeKey(out _);"),
            new(
                "src/RetroDownfall.Arcanum.Infrastructure/Backup/InstallationResetActiveRecordAuthenticator.cs",
                "lease.TryTakeKey(out _);"),
        ];

        Assert.Equal(
            [
                "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/BackupRestoreJournalAuthenticator.cs",
                "src/RetroDownfall.Arcanum.Infrastructure/Backup/InstallationResetActiveRecordAuthenticator.cs",
            ],
            FindUnauthorizedTryTakeKeyCallers(sources));

    }

    [Fact]
    public void Only_the_active_store_retirement_path_can_delete_active_record_credentials()
    {

        IReadOnlyList<ProductionSource> sources = ProductionSourceInventory.Sources();

        List<string> keyRemovalCallers = FindUnauthorizedRemovalCallers(sources);

        Assert.True(
            keyRemovalCallers.Count == 0,
            "Only InstallationResetActiveStore's locked retirement path may ask the physical key "
            + "provider to remove the reset-active key: "
            + string.Join(", ", keyRemovalCallers));

        List<string> directCredentialDeletion = FindUnauthorizedDirectDeletionCallers(sources);

        Assert.True(
            directCredentialDeletion.Count == 0,
            "The key provider is the physical key-deletion primitive and the active store owns "
            + "locked pair retirement. No other production path may derive and delete either "
            + "account: "
            + string.Join(", ", directCredentialDeletion));

        List<string> keyNaming = FindUnauthorizedKeyNamingCallers(sources);

        Assert.True(
            keyNaming.Count == 0,
            "Only the credential identity and the physical key provider may derive the reset-active "
            + "key account; retirement must call the provider rather than bypassing its lock and "
            + "verification boundary: "
            + string.Join(", ", keyNaming));

        List<string> anchorNaming = FindUnauthorizedAnchorNamingCallers(sources);

        Assert.True(
            anchorNaming.Count == 0,
            "Only the credential identity and the active store's retirement/anchor implementation "
            + "may derive the reset-active anchor account: "
            + string.Join(", ", anchorNaming));

        List<string> handSpelledAccounts = FindUnauthorizedHandSpelledAccounts(sources);

        Assert.True(
            handSpelledAccounts.Count == 0,
            "Reset-active credential accounts must be spelled only by ArcanumCredentialIdentity, "
            + "which enforces the canonical profile suffix: "
            + string.Join(", ", handSpelledAccounts));

    }

    private static List<string> FindUnauthorizedTryTakeKeyCallers(
        IEnumerable<ProductionSource> sources) =>
        [
            .. sources
                .Where(static source =>
                    !IsKnownAuthenticatorPath(source)
                    && source.Names(".TryTakeKey("))
                .Select(static source => source.RelativePath),
        ];

    private static List<string> FindUnauthorizedRemovalCallers(
        IEnumerable<ProductionSource> sources) =>
        [
            .. sources
                .Where(static source =>
                    !source.IsExactOwner(ActiveStorePath)
                    && source.Names(".RemoveAndVerifyAbsent("))
                .Select(static source => source.RelativePath),
        ];

    private static List<string> FindUnauthorizedDirectDeletionCallers(
        IEnumerable<ProductionSource> sources) =>
        [
            .. sources
                .Where(static source =>
                    !source.IsExactOwner(KeyProviderPath)
                    && !source.IsExactOwner(AnchorStorePath)
                    && (source.Names("InstallationResetActiveKeyAccount(")
                        || source.Names("InstallationResetActiveAnchorAccount("))
                    && source.Names(".Delete("))
                .Select(static source => source.RelativePath),
        ];

    private static List<string> FindUnauthorizedKeyNamingCallers(
        IEnumerable<ProductionSource> sources) =>
        [
            .. sources
                .Where(static source =>
                    !source.IsExactOwner(CredentialIdentityPath)
                    && !source.IsExactOwner(KeyProviderPath)
                    && source.Names("InstallationResetActiveKeyAccount("))
                .Select(static source => source.RelativePath),
        ];

    private static List<string> FindUnauthorizedAnchorNamingCallers(
        IEnumerable<ProductionSource> sources) =>
        [
            .. sources
                .Where(static source =>
                    !source.IsExactOwner(CredentialIdentityPath)
                    && !source.IsExactOwner(AnchorStorePath)
                    && source.Names("InstallationResetActiveAnchorAccount("))
                .Select(static source => source.RelativePath),
        ];

    private static List<string> FindUnauthorizedHandSpelledAccounts(
        IEnumerable<ProductionSource> sources) =>
        [
            .. sources
                .Where(static source =>
                    !source.IsExactOwner(CredentialIdentityPath)
                    && source.Names("\"installation-reset-active-"))
                .Select(static source => source.RelativePath),
        ];

    private static bool IsKnownAuthenticatorPath(ProductionSource source) =>
        source.IsExactOwner(ActiveAuthenticatorPath)
        || source.IsExactOwner(BackupRestoreAuthenticatorPath);

}
