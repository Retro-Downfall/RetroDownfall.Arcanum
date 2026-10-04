using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

/// <summary>
/// The Lexicon scribe chokepoint against erasure evidence.
/// </summary>
/// <remarks>
/// <para>An erased name is refused in exactly the scope it was erased in, whatever spelling the scribe
/// uses and whether or not a row still holds it, and nowhere else. An installation whose key cannot
/// verify its evidence records nothing at all, and one that has never erased anything never touches
/// the credential store.</para>
///
/// <para>The erase routes do not exist yet, so each fingerprint is seeded through the evidence store's
/// own insert with a real key, over the identity production derives.</para>
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconErasureSuppressionTests(GrimoireFixture fixture)
{
    private static CancellationToken Token => CancellationToken.None;

    [SkippableTheory]
    [InlineData("Entity")]
    [InlineData("  entity ")]
    [InlineData("ENTITY")]
    public async Task Scribe_of_an_erased_name_is_refused_and_records_nothing(string name)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(credentials);

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        await using CorrectionFixture owner = new(fixture, annals: true, erasureKeys: keys);

        await MemoryErasureTestKeys.SeedFingerprintAsync(owner.Connection, key, MemoryErasureIdentity.ForLexicon(null, "Entity"), Token);

        Result<LexiconEntryDto> scribed = await owner.Concrete.UpsertAsync(name, "Person", ["alpha"], LexiconScope.Global);

        Assert.Equal(ErrorCodes.Lexicon.SuppressedNameRefused, scribed.Error.Code);

        Assert.Equal(LexiconAgentRefusals.OperatorManaged, scribed.Error.Message);

        Assert.Empty(await owner.SnapshotAsync());
    }

    /// <summary>
    /// A fresh keyring, and a name that was never erased: evidence the key cannot verify withholds the
    /// whole store, because no answer about this name could be trusted.
    /// </summary>
    [SkippableTheory]
    [InlineData("lost", ErrorCodes.MemoryErasure.KeyLost)]
    [InlineData("overwritten", ErrorCodes.MemoryErasure.KeyLost)]
    [InlineData("unreadable", ErrorCodes.MemoryErasure.KeyUnavailable)]
    [InlineData("malformed", ErrorCodes.MemoryErasure.KeyUnavailable)]
    public async Task A_key_that_cannot_verify_the_evidence_fails_closed_before_the_transaction(string state, string code)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        using MemoryErasureKey original = MemoryErasureTestKeys.CreateKey(inner);

        CountingOsCredentialStore credentials = new(inner);

        switch (state)
        {
            case "lost":
                _ = inner.Delete(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount);

                break;

            case "overwritten":
                _ = inner.Delete(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount);

                MemoryErasureTestKeys.CreateKey(inner).Dispose();

                break;

            case "unreadable":
                credentials.FailWith = OsCredentialStoreStatus.Unavailable;

                break;

            case "malformed":
                _ = inner.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount, "not-a-key");

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown key state.");
        }

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        await using CorrectionFixture owner = new(fixture, annals: true, erasureKeys: keys);

        // Written while the store still held no evidence, so it needed no key.
        _ = await owner.SeedAsync();

        await MemoryErasureTestKeys.SeedFingerprintAsync(
            owner.Connection, original, MemoryErasureIdentity.ForLexicon(null, "Something the operator erased"), Token);

        string[] snapshot = await owner.SnapshotAsync();

        Result<LexiconEntryDto> scribed = await owner.Concrete.UpsertAsync("Other", "Person", ["alpha"], LexiconScope.Global);

        Assert.Equal(code, scribed.Error.Code);

        Assert.Equal(snapshot, await owner.SnapshotAsync());
    }

    /// <summary>
    /// A <c>daemon_state:</c> name can never be erased, so it can never be fingerprinted, and a key that is
    /// not there has nothing to verify about it: the Unseen Servant's own state is scribed whatever state the
    /// key is in, while an ordinary name in the same store is still refused.
    /// </summary>
    /// <remarks>
    /// The one refusal that stays is a key that is present but is not the one that recorded the store's
    /// evidence, which is pinned separately: it is the store's own integrity, not the key's availability.
    /// </remarks>
    [SkippableTheory]
    [InlineData("lost", ErrorCodes.MemoryErasure.KeyLost)]
    [InlineData("unreadable", ErrorCodes.MemoryErasure.KeyUnavailable)]
    [InlineData("malformed", ErrorCodes.MemoryErasure.KeyUnavailable)]
    public async Task A_daemon_state_name_is_scribed_whatever_the_key_cannot_do_and_an_ordinary_name_is_still_refused(
        string state,
        string code)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        using MemoryErasureKey original = MemoryErasureTestKeys.CreateKey(inner);

        CountingOsCredentialStore credentials = new(inner);

        switch (state)
        {
            case "lost":
                _ = inner.Delete(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount);

                break;

            case "unreadable":
                credentials.FailWith = OsCredentialStoreStatus.Unavailable;

                break;

            case "malformed":
                _ = inner.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount, "not-a-key");

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown key state.");
        }

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        await using CorrectionFixture owner = new(fixture, annals: true, erasureKeys: keys);

        await MemoryErasureTestKeys.SeedFingerprintAsync(
            owner.Connection, original, MemoryErasureIdentity.ForLexicon(null, "Something the operator erased"), Token);

        Result<LexiconEntryDto> ordinary = await owner.Concrete.UpsertAsync("Other", "Person", ["alpha"], LexiconScope.Global);

        Assert.Equal(code, ordinary.Error.Code);

        Result<LexiconEntryDto> daemon = await owner.Concrete.UpsertAsync(
            "daemon_state:digest-cursor",
            "State",
            ["cursor=42"],
            LexiconScope.Global);

        Assert.True(daemon.IsSuccess, daemon.IsFailure ? $"{daemon.Error.Code}: {daemon.Error.Message}" : null);

        // The prefix is matched whatever its case, as the delete and erase refusals match it.
        Result<LexiconEntryDto> shouting = await owner.Concrete.UpsertAsync(
            "DAEMON_STATE:Digest-Cursor-Two",
            "State",
            ["cursor=43"],
            LexiconScope.Global);

        Assert.True(shouting.IsSuccess, shouting.IsFailure ? $"{shouting.Error.Code}: {shouting.Error.Message}" : null);

        string[] entries = [.. (await owner.SnapshotAsync()).Where(static row => row.StartsWith("lexicon_entries:", StringComparison.Ordinal))];

        Assert.Equal(2, entries.Length);

        Assert.All(entries, static row => Assert.Contains("daemon_state:", row, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A present key that did not record the store's evidence still refuses a <c>daemon_state:</c> name, as it
    /// refuses every other write to the store: the evidence cannot be verified at all, which is the store's
    /// integrity and not the key's availability.
    /// </summary>
    [SkippableFact]
    public async Task A_daemon_state_name_is_still_refused_when_the_key_present_is_not_the_one_that_recorded_the_evidence()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey original = MemoryErasureTestKeys.CreateKey(credentials);

        _ = credentials.Delete(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount);

        MemoryErasureTestKeys.CreateKey(credentials).Dispose();

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        await using CorrectionFixture owner = new(fixture, annals: true, erasureKeys: keys);

        await MemoryErasureTestKeys.SeedFingerprintAsync(
            owner.Connection, original, MemoryErasureIdentity.ForLexicon(null, "Something the operator erased"), Token);

        string[] snapshot = await owner.SnapshotAsync();

        Result<LexiconEntryDto> daemon = await owner.Concrete.UpsertAsync(
            "daemon_state:digest-cursor",
            "State",
            ["cursor=42"],
            LexiconScope.Global);

        Assert.Equal(ErrorCodes.MemoryErasure.KeyLost, daemon.Error.Code);

        Assert.Equal(snapshot, await owner.SnapshotAsync());
    }

    [SkippableFact]
    public async Task An_erased_identity_that_still_has_a_live_row_is_refused()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(credentials);

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        await using CorrectionFixture owner = new(fixture, annals: true, erasureKeys: keys);

        _ = await owner.SeedAsync();

        await MemoryErasureTestKeys.SeedFingerprintAsync(owner.Connection, key, MemoryErasureIdentity.ForLexicon(null, "Entity"), Token);

        string[] snapshot = await owner.SnapshotAsync();

        Result<LexiconEntryDto> scribed = await owner.Concrete.UpsertAsync("Entity", "Person", ["gamma"], LexiconScope.Global);

        Assert.Equal(ErrorCodes.Lexicon.SuppressedNameRefused, scribed.Error.Code);

        Assert.Equal(LexiconAgentRefusals.OperatorManaged, scribed.Error.Message);

        Assert.Equal(snapshot, await owner.SnapshotAsync());
    }

    /// <summary>
    /// A fingerprint names one name in one exact scope: the same name in another scope, and another
    /// name in the same scope, are different identities and are recorded.
    /// </summary>
    [SkippableFact]
    public async Task Fingerprints_bind_the_exact_scope()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(credentials);

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        await using CorrectionFixture owner = new(fixture, annals: true, erasureKeys: keys);

        Guid campaignA = Guid.NewGuid();

        Guid campaignB = Guid.NewGuid();

        await MemoryErasureTestKeys.SeedFingerprintAsync(owner.Connection, key, MemoryErasureIdentity.ForLexicon(campaignA, "Entity"), Token);

        await MemoryErasureTestKeys.SeedFingerprintAsync(owner.Connection, key, MemoryErasureIdentity.ForLexicon(null, "Other"), Token);

        Result<LexiconEntryDto> global = await owner.Concrete.UpsertAsync("Entity", "Person", ["alpha"], LexiconScope.Global);

        Assert.True(global.IsSuccess, global.Error.Message);

        Result<LexiconEntryDto> otherCampaign = await owner.Concrete.UpsertAsync(
            "Entity", "Person", ["alpha"], LexiconScope.ForCampaign(campaignB));

        Assert.True(otherCampaign.IsSuccess, otherCampaign.Error.Message);

        string[] snapshot = await owner.SnapshotAsync();

        Result<LexiconEntryDto> erased = await owner.Concrete.UpsertAsync(
            "Entity", "Person", ["alpha"], LexiconScope.ForCampaign(campaignA));

        Assert.Equal(ErrorCodes.Lexicon.SuppressedNameRefused, erased.Error.Code);

        Assert.Equal(snapshot, await owner.SnapshotAsync());

        Result<LexiconEntryDto> otherName = await owner.Concrete.UpsertAsync(
            "Other", "Person", ["alpha"], LexiconScope.ForCampaign(campaignA));

        Assert.True(otherName.IsSuccess, otherName.Error.Message);
    }

    /// <summary>
    /// A scope no fingerprint can name. While the store holds no evidence the chokepoint never derives
    /// an identity, so the scribe behaves exactly as it did before erasure existed; once it holds
    /// evidence, deriving one fails the scribe closed and nothing is recorded.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unnameable_scope_is_identified_only_when_the_store_holds_evidence(bool evidence)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        LexiconScope unnameable = LexiconScope.ForCampaign(Guid.Empty);

        // No factory can name it: an empty Campaign is refused by the identity itself, not earlier.
        Assert.Throws<ArgumentException>(() => MemoryErasureIdentity.ForLexicon(unnameable.CampaignId, "Entity"));

        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(credentials);

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        await using CorrectionFixture owner = new(fixture, erasureKeys: keys);

        if (evidence)
        {
            await MemoryErasureTestKeys.SeedFingerprintAsync(
                owner.Connection, key, MemoryErasureIdentity.ForLexicon(null, "Something the operator erased"), Token);
        }

        string[] snapshot = await owner.SnapshotAsync();

        Result<LexiconEntryDto> scribed = await owner.Concrete.UpsertAsync("Entity", "Person", ["alpha"], unnameable);

        if (evidence)
        {
            Assert.Equal(ErrorCodes.Lexicon.WriteFailed, scribed.Error.Code);

            Assert.Equal(snapshot, await owner.SnapshotAsync());
        }
        else
        {
            Assert.True(scribed.IsSuccess, scribed.Error.Message);
        }
    }

    [SkippableFact]
    public async Task No_fingerprints_means_no_keychain_io()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CountingOsCredentialStore credentials = new(new InMemoryOsCredentialStore());

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        await using CorrectionFixture owner = new(fixture, annals: true, erasureKeys: keys);

        Result<LexiconEntryDto> global = await owner.Concrete.UpsertAsync("Entity", "Person", ["alpha"], LexiconScope.Global);

        Assert.True(global.IsSuccess, global.Error.Message);

        Result<LexiconEntryDto> campaign = await owner.Concrete.UpsertAsync(
            "Entity", "Person", ["alpha"], LexiconScope.ForCampaign(Guid.NewGuid()));

        Assert.True(campaign.IsSuccess, campaign.Error.Message);

        Assert.Equal(0, credentials.Calls);
    }

    /// <summary>
    /// A first fingerprint that commits after the scribe's probe and before its <c>BEGIN IMMEDIATE</c>:
    /// the scribe meets evidence with no key in hand, rolls back, resolves the key outside the
    /// transaction, and on its one retry is refused.
    /// </summary>
    /// <remarks>
    /// The retry is visible as the single credential open: a mapping that refused at once, without
    /// going back out for the key, would give the same code and open nothing.
    /// </remarks>
    [SkippableFact]
    public async Task A_scribe_that_meets_evidence_committed_after_its_probe_retries_once_and_is_refused()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(credentials);

        using MemoryErasureKeyring inner = MemoryErasureTestKeys.Isolated(credentials);

        HookOnFirstCopyProvider keys = new(inner, withholdFirstCopy: true);

        await using CorrectionFixture owner = new(fixture, annals: true, erasureKeys: keys);

        keys.OnFirstCopy = () => SeedFromSibling(owner, key, MemoryErasureIdentity.ForLexicon(null, "Entity"));

        Result<LexiconEntryDto> scribed = await owner.Concrete.UpsertAsync("Entity", "Person", ["alpha"], LexiconScope.Global);

        Assert.Equal(ErrorCodes.Lexicon.SuppressedNameRefused, scribed.Error.Code);

        Assert.Equal(LexiconAgentRefusals.OperatorManaged, scribed.Error.Message);

        Assert.Equal(1, keys.Copies);

        Assert.Equal(1, keys.Opens);

        Assert.Empty(await owner.SnapshotAsync());
    }

    /// <summary>
    /// Evidence under a key this installation does not hold commits after the scribe's probe, which
    /// found no evidence and carried the latched key in: the check inside the transaction refuses it as
    /// key loss, and the scribe fails closed without going back out for the key.
    /// </summary>
    [SkippableFact]
    public async Task A_scribe_that_meets_evidence_its_key_cannot_verify_inside_the_transaction_fails_closed_as_key_lost()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey own = MemoryErasureTestKeys.CreateKey(credentials);

        using MemoryErasureKey foreign = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        Assert.False(foreign.HasKeyId(own.KeyId));

        using MemoryErasureKeyring inner = MemoryErasureTestKeys.Isolated(credentials);

        // Latched before the scribe, as an earlier erase or status in this process would leave it, so
        // the probe carries the key in although the store holds no evidence yet.
        using (MemoryErasureKey? latched = inner.OpenExisting(MemoryErasureKeyProbe.UseLatched).Key)
        {
            Assert.NotNull(latched);
        }

        HookOnFirstCopyProvider keys = new(inner, withholdFirstCopy: false);

        await using CorrectionFixture owner = new(fixture, annals: true, erasureKeys: keys);

        keys.OnFirstCopy = () => SeedFromSibling(owner, foreign, MemoryErasureIdentity.ForLexicon(null, "Something erased elsewhere"));

        Result<LexiconEntryDto> scribed = await owner.Concrete.UpsertAsync("Entity", "Person", ["alpha"], LexiconScope.Global);

        Assert.Equal(ErrorCodes.MemoryErasure.KeyLost, scribed.Error.Code);

        Assert.Equal(1, keys.Copies);

        Assert.Equal(0, keys.Opens);

        Assert.Empty(await owner.SnapshotAsync());
    }

    private void SeedFromSibling(CorrectionFixture owner, MemoryErasureKey key, MemoryErasureIdentity identity)
    {
        using ArcanumDbContext sibling = fixture.CreateContext(owner.Path);

        MemoryErasureTestKeys
            .SeedFingerprintAsync((SqliteConnection)sibling.Database.GetDbConnection(), key, identity, Token)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Wraps a keyring and, on the chokepoint's first latched copy, commits evidence from another
    /// connection before answering, either with no key or with the latched one.
    /// </summary>
    private sealed class HookOnFirstCopyProvider(IMemoryErasureKeyProvider inner, bool withholdFirstCopy) : IMemoryErasureKeyProvider
    {
        private int _copies;

        private int _opens;

        internal Action? OnFirstCopy { get; set; }

        internal int Copies => Volatile.Read(ref _copies);

        internal int Opens => Volatile.Read(ref _opens);

        public MemoryErasureKeyLatch Latch => inner.Latch;

        public MemoryErasureKeyOpenResult OpenExisting(MemoryErasureKeyProbe probe)
        {
            _ = Interlocked.Increment(ref _opens);

            return inner.OpenExisting(probe);
        }

        public MemoryErasureKey? TryCopyLatched()
        {
            if (Interlocked.Increment(ref _copies) != 1)
            {
                return inner.TryCopyLatched();
            }

            OnFirstCopy?.Invoke();

            return withholdFirstCopy ? null : inner.TryCopyLatched();
        }
    }
}
