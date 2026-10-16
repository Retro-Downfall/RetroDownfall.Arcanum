using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The two pieces of the identity repair that live with the owners of the accounts: the identity
/// provider's removal of a damaged value, and the metadata-only presence lookups of the four stores
/// that own the restore and offline-transition credentials.
/// </summary>
/// <remarks>
/// The repair names no account of its own, because an account name has one owner, so what it may
/// remove and what it may ask about is decided here, in the owners, and these cases pin it.
/// </remarks>
public sealed class InstallationIdentityRemovalTests : IAsyncLifetime
{
    private const string DamagedValue = "c0ffee00-1234-4abc-8def-0123456789ab";

    private const string CanonicalValue = "C0FFEE00-1234-4ABC-8DEF-0123456789AB";

    private const string RepairPath = "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationIdentityRepair.cs";

    private const string ProviderPath =
        "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreJournalInstallationIdentityProvider.cs";

    private readonly TempWorkspace _workspace = new();

    private readonly InMemoryOsCredentialStore _inner = new();

    private string _root = string.Empty;

    private BackupRestoreProfileNamespace _profile = null!;

    public async Task InitializeAsync()
    {
        await _workspace.InitializeAsync();

        _root = _workspace.CreateSubdir("arcanum");

        _profile = BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_root).Value;
    }

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public void A_damaged_value_is_removed_and_nothing_else_is()
    {
        Seed(Account(), DamagedValue);

        Seed("master-api-key", "master");

        Seed(ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(_profile.AccountSuffix), "key");

        using ArcanumMaintenanceLock held = Held();

        Result<bool> removed = Provider(_inner).RemoveDamaged(held, _root, _profile);

        Assert.True(removed.IsSuccess, removed.IsFailure ? removed.Error.Message : string.Empty);

        Assert.True(removed.Value);

        Assert.Equal(OsCredentialStoreStatus.NotFound, _inner.TryGet(ArcanumCredentialIdentity.Service, Account()).Status);

        Assert.Equal(OsCredentialStoreStatus.Ok, _inner.TryGet(ArcanumCredentialIdentity.Service, "master-api-key").Status);

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _inner.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(_profile.AccountSuffix)).Status);
    }

    [Fact]
    public void A_valid_identity_is_refused_and_stays()
    {
        Seed(Account(), CanonicalValue);

        using ArcanumMaintenanceLock held = Held();

        Recording store = new(_inner);

        Result<bool> removed = Provider(store).RemoveDamaged(held, _root, _profile);

        Assert.True(removed.IsFailure);

        Assert.Equal(ErrorCodes.Data.Conflict, removed.Error.Code);

        Assert.Contains("A valid identity is never removed", removed.Error.Message, StringComparison.Ordinal);

        Assert.Empty(store.Deletes);

        Assert.Equal(CanonicalValue, _inner.TryGet(ArcanumCredentialIdentity.Service, Account()).Value);
    }

    [Fact]
    public void An_absent_item_is_reported_as_already_gone_and_nothing_is_deleted()
    {
        using ArcanumMaintenanceLock held = Held();

        Recording store = new(_inner);

        Result<bool> removed = Provider(store).RemoveDamaged(held, _root, _profile);

        Assert.True(removed.IsSuccess);

        Assert.False(removed.Value);

        Assert.Empty(store.Deletes);
    }

    [Fact]
    public void A_store_that_does_not_answer_is_not_a_damaged_value()
    {
        Seed(Account(), DamagedValue);

        using ArcanumMaintenanceLock held = Held();

        Recording store = new(_inner) { ReadsFail = true };

        Result<bool> removed = Provider(store).RemoveDamaged(held, _root, _profile);

        Assert.True(removed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, removed.Error.Code);

        Assert.Empty(store.Deletes);

        Assert.Equal(DamagedValue, _inner.TryGet(ArcanumCredentialIdentity.Service, Account()).Value);
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("fails")]
    public void A_delete_the_store_refuses_is_reported_and_the_item_stays(string mode)
    {
        Seed(Account(), DamagedValue);

        using ArcanumMaintenanceLock held = Held();

        Recording store = new(_inner) { DeleteMode = mode };

        Result<bool> removed = Provider(store).RemoveDamaged(held, _root, _profile);

        Assert.True(removed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, removed.Error.Code);

        Assert.Contains("could not be removed", removed.Error.Message, StringComparison.Ordinal);

        Assert.Equal(DamagedValue, _inner.TryGet(ArcanumCredentialIdentity.Service, Account()).Value);
    }

    [Fact]
    public void A_delete_that_does_not_read_back_as_removed_is_not_reported_as_removed()
    {
        Seed(Account(), DamagedValue);

        using ArcanumMaintenanceLock held = Held();

        Recording store = new(_inner) { DeleteMode = "ignored" };

        Result<bool> removed = Provider(store).RemoveDamaged(held, _root, _profile);

        Assert.True(removed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, removed.Error.Code);

        Assert.Contains("did not read back as removed", removed.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_removal_requires_the_installation_lock_of_the_root_it_names()
    {
        Seed(Account(), DamagedValue);

        string other = _workspace.CreateSubdir("other");

        using ArcanumMaintenanceLock heldForOther = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(other));

        _ = Assert.ThrowsAny<Exception>(
            () => Provider(_inner).RemoveDamaged(heldForOther, _root, _profile));

        Assert.Equal(DamagedValue, _inner.TryGet(ArcanumCredentialIdentity.Service, Account()).Value);
    }

    [Fact]
    public void The_four_owners_report_their_own_account_present_or_absent_by_metadata_alone()
    {
        BackupRestoreJournalKeyProvider restoreKey = new(_inner);

        BackupRestoreJournalAnchorStore restoreAnchor = new(
            _inner,
            restoreKey,
            new BackupRestoreJournalInstallationIdentityProvider(_inner));

        GrimoireOfflineTransitionJournalKeyProvider transitionKey = new(_inner);

        GrimoireOfflineTransitionJournalAnchorStore transitionAnchor = new(_inner);

        Func<OsCredentialStoreStatus>[] probes =
        [
            () => restoreKey.ProbePresence(_profile),
            () => restoreAnchor.ProbeAnchorPresence(_profile),
            () => transitionKey.ProbePresence(_profile),
            () => transitionAnchor.ProbeAnchorPresence(_profile),
        ];

        string[] accounts =
        [
            ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(_profile.AccountSuffix),
            ArcanumCredentialIdentity.BackupRestoreJournalAnchorAccount(_profile.AccountSuffix),
            ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccount(_profile.AccountSuffix),
            ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccount(_profile.AccountSuffix),
        ];

        for (int index = 0; index < probes.Length; index++)
        {
            Assert.Equal(OsCredentialStoreStatus.NotFound, probes[index]());

            Seed(accounts[index], "material");

            Assert.Equal(OsCredentialStoreStatus.Ok, probes[index]());

            // Each owner answers for its own account and for no other.
            for (int other = 0; other < probes.Length; other++)
            {
                if (other != index)
                {
                    Assert.Equal(
                        other < index ? OsCredentialStoreStatus.Ok : OsCredentialStoreStatus.NotFound,
                        probes[other]());
                }
            }
        }
    }

    [Fact]
    public void A_lookup_that_cannot_be_made_is_indeterminate_and_never_a_read()
    {
        Seed(ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(_profile.AccountSuffix), "key");

        NoLookupStore noLookup = new(_inner);

        Assert.Equal(OsCredentialStoreStatus.Unavailable, new BackupRestoreJournalKeyProvider(noLookup).ProbePresence(_profile));

        Assert.Equal(OsCredentialStoreStatus.Unavailable, new GrimoireOfflineTransitionJournalKeyProvider(noLookup).ProbePresence(_profile));

        Assert.Empty(noLookup.Reads);

        Recording throwing = new(_inner) { LookupThrows = true };

        Assert.Equal(OsCredentialStoreStatus.Unavailable, new BackupRestoreJournalKeyProvider(throwing).ProbePresence(_profile));

        Assert.Equal(
            OsCredentialStoreStatus.Unavailable,
            new GrimoireOfflineTransitionJournalAnchorStore(throwing).ProbeAnchorPresence(_profile));
    }

    /// <summary>
    /// The account names keep their single owners: the repair derives none, the provider holds the one
    /// deletion, and only the repair calls it.
    /// </summary>
    [Fact]
    public void Only_the_identity_provider_deletes_the_account_and_only_the_repair_asks_it_to()
    {
        IReadOnlyList<ProductionSource> sources = ProductionSourceInventory.Sources();

        ProductionSource repair = Assert.Single(sources, source => source.IsExactOwner(RepairPath));

        foreach (string derivation in (string[])[
            "InstallationAccount(",
            "JournalKeyAccount(",
            "JournalAnchorAccount(",
            "ActiveKeyAccount(",
            "ActiveAnchorAccount(",
            "_credentials.Delete(",
            "_credentials.Set(",
        ])
        {
            Assert.False(repair.Names(derivation), RepairPath + " names " + derivation);
        }

        ProductionSource provider = Assert.Single(sources, source => source.IsExactOwner(ProviderPath));

        Assert.True(provider.Names("_credentials.Delete("));

        string[] callers =
        [
            .. sources
                .Where(static source => source.Names(".RemoveDamaged("))
                .Select(static source => source.RelativePath.Replace('\\', '/'))
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal([RepairPath], callers);
    }

    private BackupRestoreJournalInstallationIdentityProvider Provider(IOsCredentialStore store) => new(store);

    private ArcanumMaintenanceLock Held() =>
        Assert.IsType<ArcanumMaintenanceLock>(ArcanumMaintenanceLock.TryAcquire(_root));

    private string Account() =>
        ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(_profile.AccountSuffix);

    private void Seed(string account, string value)
    {
        Assert.Equal(OsCredentialStoreStatus.Ok, _inner.Set(ArcanumCredentialIdentity.Service, account, value).Status);
    }

    private sealed class NoLookupStore(InMemoryOsCredentialStore inner) : IOsCredentialStore
    {
        public List<string> Reads { get; } = [];

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            Reads.Add(account);

            return inner.TryGet(service, account);
        }

        public OsCredentialStoreResult Set(string service, string account, string secret) => inner.Set(service, account, secret);

        public OsCredentialStoreResult Delete(string service, string account) => inner.Delete(service, account);
    }

    private sealed class Recording(InMemoryOsCredentialStore inner) : IOsCredentialStore, IOsCredentialPresenceProbe
    {
        public bool ReadsFail { get; set; }

        public string DeleteMode { get; set; } = string.Empty;

        public bool LookupThrows { get; set; }

        public List<string> Deletes { get; } = [];

        public List<string> Reads { get; } = [];

        public bool IsAvailable => true;

        public OsCredentialStoreStatus ProbePresence(string service, string account)
        {
            return LookupThrows ? throw new IOException("The lookup failed.") : inner.ProbePresence(service, account);
        }

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            Reads.Add(account);

            return ReadsFail ? OsCredentialStoreResult.Failed("The store did not answer.") : inner.TryGet(service, account);
        }

        public OsCredentialStoreResult Set(string service, string account, string secret) => inner.Set(service, account, secret);

        public OsCredentialStoreResult Delete(string service, string account)
        {
            Deletes.Add(account);

            return DeleteMode switch
            {
                "throws" => throw new IOException("The delete failed."),
                "fails" => OsCredentialStoreResult.Failed("The store refused the delete."),
                "ignored" => OsCredentialStoreResult.Ok(string.Empty),
                _ => inner.Delete(service, account),
            };
        }
    }
}
