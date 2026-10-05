using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class InstallationResetCredentialCatalogTests
{
    [Fact]
    public void Installation_reset_active_accounts_require_one_canonical_profile_suffix()
    {
        string profileSuffix = new string('a', ArcanumCredentialIdentity.ProfileNamespaceSuffixLength);

        string otherProfileSuffix = new string('b', ArcanumCredentialIdentity.ProfileNamespaceSuffixLength);

        string keyAccount = ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(profileSuffix);

        string anchorAccount = ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(profileSuffix);

        string transitionKeyAccount =
            ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccount(profileSuffix);

        string transitionAnchorAccount =
            ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccount(profileSuffix);

        HashSet<string> reservedAccounts = new(StringComparer.Ordinal)
        {
            transitionKeyAccount,
            transitionAnchorAccount,
            ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccount(otherProfileSuffix),
            ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccount(otherProfileSuffix),
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(profileSuffix),
            ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(profileSuffix),
            ArcanumCredentialIdentity.BackupRestoreJournalAnchorAccount(profileSuffix),
            keyAccount,
            anchorAccount,
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            ArcanumCredentialIdentity.FileEncryptionKeyAccount,
            ArcanumCredentialIdentity.CampaignRootIdentityKeyAccount,
            ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount,
        };

        Assert.Equal("installation-reset-active-key-" + profileSuffix, keyAccount);

        Assert.Equal("installation-reset-active-anchor-" + profileSuffix, anchorAccount);

        Assert.True(ArcanumCredentialIdentity.IsInstallationResetActiveAccount(keyAccount));

        Assert.True(ArcanumCredentialIdentity.IsInstallationResetActiveAccount(anchorAccount));

        Assert.Equal("grimoire-transition-journal-key-" + profileSuffix, transitionKeyAccount);

        Assert.Equal("grimoire-transition-journal-anchor-" + profileSuffix, transitionAnchorAccount);

        Assert.True(ArcanumCredentialIdentity.IsGrimoireTransitionJournalAccount(transitionKeyAccount));

        Assert.True(ArcanumCredentialIdentity.IsGrimoireTransitionJournalAccount(transitionAnchorAccount));

        Assert.Equal(13, reservedAccounts.Count);

        Assert.False(
            ArcanumCredentialIdentity.IsInstallationResetActiveAccount(
                ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount));

        Assert.False(
            ArcanumCredentialIdentity.IsGrimoireTransitionJournalAccount(
                ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount));

        Assert.NotEqual(
            keyAccount,
            ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(otherProfileSuffix));

        Assert.NotEqual(
            anchorAccount,
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(otherProfileSuffix));

        foreach (string invalid in (string[])
                 [
                     ArcanumCredentialIdentity.InstallationResetActiveKeyAccountPrefix,
                     "installation-reset-active-key",
                     ArcanumCredentialIdentity.InstallationResetActiveKeyAccountPrefix
                         + profileSuffix.ToUpperInvariant(),
                     ArcanumCredentialIdentity.InstallationResetActiveAnchorAccountPrefix + "too-short",
                     ArcanumCredentialIdentity.InstallationResetActiveAnchorAccountPrefix
                         + new string('g', ArcanumCredentialIdentity.ProfileNamespaceSuffixLength),
                     ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(profileSuffix),
                     ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccountPrefix,
                     ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccountPrefix
                         + profileSuffix.ToUpperInvariant(),
                 ])
        {
            Assert.False(ArcanumCredentialIdentity.IsInstallationResetActiveAccount(invalid));
        }

        _ = Assert.Throws<ArgumentException>(
            () => ArcanumCredentialIdentity.InstallationResetActiveKeyAccount("too-short"));

        _ = Assert.Throws<ArgumentException>(
            () => ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(
                profileSuffix.ToUpperInvariant()));

        _ = Assert.Throws<ArgumentException>(
            () => ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(
                ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(otherProfileSuffix)));

        _ = Assert.Throws<ArgumentException>(
            () => ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccount("too-short"));

        _ = Assert.Throws<ArgumentException>(
            () => ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccount(
                profileSuffix.ToUpperInvariant()));
    }

    /// <summary>
    /// Ordinary credential cleanup keeps the host-tools marker, asserted where the erasure paths do.
    /// </summary>
    /// <remarks>
    /// The closed-catalog test in this suite pins the accounts this path does delete; this one pins
    /// the retention from the other side, in the same place a Covenant reset, a healthy-catalog factory
    /// erasure, and a family reinitialize read it. Four suites each phrasing the rule themselves is
    /// four chances for one of them to drift into asserting something weaker (§10.20.5).
    /// </remarks>
    [Fact]
    public void Ordinary_credential_cleanup_retains_the_marker_set_no_production_path_may_delete() =>
        CovenantRetainedEvidence.AssertNoProductionPathDeletesRetainedEvidence();

    [Fact]
    public void Catalog_is_closed_to_fixed_configured_and_canonical_mirror_identities()
    {
        string mirrorRoot = CreateRoot();

        try
        {
            File.WriteAllText(Path.Combine(mirrorRoot, "provider-OPENAI-key.dat"), "protected");

            File.WriteAllText(Path.Combine(mirrorRoot, "provider-MY_CO-key.dat"), "protected");

            File.WriteAllText(Path.Combine(mirrorRoot, "provider-lowercase-key.dat"), "ignored");

            File.WriteAllText(Path.Combine(mirrorRoot, "not-a-provider-key.dat"), "ignored");

            ArcanumSettings settings = new()
            {
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "OpenAI",
                        Type = AiProviderKind.OpenAICompatible,
                    },
                    new ProviderSettings
                    {
                        Name = "Claude",
                        Type = AiProviderKind.ClaudeCodeCli,
                    },
                ],
            };

            string[] accounts = InstallationResetCredentialCatalog.CollectAccounts(
                settings,
                mirrorRoot);

            // The Campaign root-identity key and the memory-erasure fingerprint key belong here
            // because the documented contract says only a full installation reset ends either one's
            // lifetime, and this catalog is the only thing that can delete them: `IOsCredentialStore`
            // has no enumeration surface, so an account nobody names here is an account nothing on
            // this machine can ever erase.
            Assert.Equal(
                [
                    ArcanumCredentialIdentity.CampaignRootIdentityKeyAccount,
                    ArcanumCredentialIdentity.FileEncryptionKeyAccount,
                    "inference-provider-MY_CO-api-key",
                    "inference-provider-OPENAI-api-key",
                    ArcanumCredentialIdentity.MasterApiKeyAccount,
                    ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount,
                    ArcanumCredentialIdentity.PerplexityApiKeyAccount,
                ],
                accounts);
        }
        finally
        {
            Directory.Delete(mirrorRoot, recursive: true);
        }
    }

    [Fact]
    public void Ordinary_account_filter_rejects_actual_transition_key_and_anchor_accounts()
    {
        string suffix = new('a', ArcanumCredentialIdentity.ProfileNamespaceSuffixLength);

        string[] retained = InstallationResetCredentialCatalog.CollectOrdinaryAccounts(
            [
                ArcanumCredentialIdentity.MasterApiKeyAccount,
                ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccount(suffix),
                ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccount(suffix),
            ]);

        Assert.Equal([ArcanumCredentialIdentity.MasterApiKeyAccount], retained);
    }

    [Fact]
    public void Planning_exposes_status_only_and_never_the_secret_value()
    {
        RecordingCredentialStore store = new();

        store.Values[ArcanumCredentialIdentity.MasterApiKeyAccount] = "sentinel-secret";

        InstallationResetCredentialCatalog catalog = new(store);

        InstallationResetCredentialSummary[] inventory = catalog.Probe(
            [
                ArcanumCredentialIdentity.MasterApiKeyAccount,
                "missing",
            ]);

        Assert.Equal(InstallationResetItemStatus.Pending, inventory[0].Status);

        Assert.Equal(InstallationResetItemStatus.Absent, inventory[1].Status);

        Assert.DoesNotContain(
            "sentinel-secret",
            System.Text.Json.JsonSerializer.Serialize(inventory),
            StringComparison.Ordinal);

        Assert.Empty(store.DeletedAccounts);
    }

    [Fact]
    public void Delete_probes_before_and_after_each_admitted_identity()
    {
        RecordingCredentialStore store = new();

        store.Values[ArcanumCredentialIdentity.MasterApiKeyAccount] = "secret";

        InstallationResetCredentialCatalog catalog = new(store);

        InstallationResetCredentialResult[] result = catalog.DeleteAndVerify(
            [
                ArcanumCredentialIdentity.MasterApiKeyAccount,
                "missing",
            ]);

        Assert.Equal(InstallationResetItemStatus.Deleted, result[0].Status);

        Assert.Equal(InstallationResetItemStatus.Absent, result[1].Status);

        Assert.Equal(
            [
                ArcanumCredentialIdentity.MasterApiKeyAccount,
            ],
            store.DeletedAccounts);

        Assert.Equal(2, store.ProbeCounts[ArcanumCredentialIdentity.MasterApiKeyAccount]);

        Assert.Equal(1, store.ProbeCounts["missing"]);
    }

    [Fact]
    public void DeleteAndVerify_refuses_retained_evidence_accounts()
    {
        // The exclusion is applied when the inventory is built, and a delete that took whatever names
        // it was handed would let any caller that skipped that step remove the only evidence able to
        // finish an interrupted restore or transition. The refusal is per account, so one retained
        // name in a list never costs the ordinary accounts beside it.
        string suffix = new('a', ArcanumCredentialIdentity.ProfileNamespaceSuffixLength);

        string[] retained =
        [
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(suffix),
            ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(suffix),
            ArcanumCredentialIdentity.BackupRestoreJournalAnchorAccount(suffix),
            ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(suffix),
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(suffix),
            ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccount(suffix),
            ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccount(suffix),
            ArcanumCredentialIdentity.HostProcessToolsTaintAccount,
        ];

        RecordingCredentialStore store = new();

        foreach (string account in retained)
        {
            store.Values[account] = "evidence";
        }

        store.Values[ArcanumCredentialIdentity.MasterApiKeyAccount] = "secret";

        InstallationResetCredentialCatalog catalog = new(store);

        InstallationResetCredentialResult[] results = catalog.DeleteAndVerify(
            [.. retained, ArcanumCredentialIdentity.MasterApiKeyAccount]);

        Assert.Equal(retained.Length + 1, results.Length);

        foreach (string account in retained)
        {
            InstallationResetCredentialResult refused = Assert.Single(
                results,
                result => string.Equals(result.Account, account, StringComparison.Ordinal));

            Assert.Equal(InstallationResetItemStatus.Failed, refused.Status);

            Assert.Equal(ErrorCodes.Data.Blocked, refused.ErrorCode);

            Assert.True(store.Values.ContainsKey(account));

            // Refused before the store was touched at all: not read, not deleted.
            Assert.False(store.ProbeCounts.ContainsKey(account));
        }

        Assert.Equal([ArcanumCredentialIdentity.MasterApiKeyAccount], store.DeletedAccounts);

        Assert.Equal(
            InstallationResetItemStatus.Deleted,
            Assert.Single(
                results,
                result => string.Equals(
                    result.Account,
                    ArcanumCredentialIdentity.MasterApiKeyAccount,
                    StringComparison.Ordinal)).Status);
    }

    [Fact]
    public void Unavailable_store_never_aggregates_to_success()
    {
        RecordingCredentialStore store = new() { IsAvailable = false };

        InstallationResetCredentialCatalog catalog = new(store);

        InstallationResetCredentialSummary summary = Assert.Single(
            catalog.Probe([ArcanumCredentialIdentity.MasterApiKeyAccount]));

        InstallationResetCredentialResult result = Assert.Single(
            catalog.DeleteAndVerify([ArcanumCredentialIdentity.MasterApiKeyAccount]));

        Assert.Equal(InstallationResetItemStatus.Unavailable, summary.Status);

        Assert.Equal(InstallationResetItemStatus.Unavailable, result.Status);

        Assert.Empty(store.DeletedAccounts);
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "arcanum-reset-credentials-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        return root;
    }

    private sealed class RecordingCredentialStore : IOsCredentialStore
    {
        public bool IsAvailable { get; set; } = true;

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> ProbeCounts { get; } = new(StringComparer.Ordinal);

        public List<string> DeletedAccounts { get; } = [];

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            Assert.Equal(ArcanumCredentialIdentity.Service, service);

            ProbeCounts[account] = ProbeCounts.GetValueOrDefault(account) + 1;

            if (!IsAvailable)
            {
                return OsCredentialStoreResult.Unavailable("unavailable");
            }

            return Values.TryGetValue(account, out string? value)
                ? OsCredentialStoreResult.Ok(value)
                : OsCredentialStoreResult.NotFound();
        }

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            throw new NotSupportedException();

        public OsCredentialStoreResult Delete(string service, string account)
        {
            Assert.Equal(ArcanumCredentialIdentity.Service, service);

            if (!IsAvailable)
            {
                return OsCredentialStoreResult.Unavailable("unavailable");
            }

            if (!Values.Remove(account))
            {
                return OsCredentialStoreResult.NotFound();
            }

            DeletedAccounts.Add(account);

            return OsCredentialStoreResult.Ok(string.Empty);
        }
    }
}
