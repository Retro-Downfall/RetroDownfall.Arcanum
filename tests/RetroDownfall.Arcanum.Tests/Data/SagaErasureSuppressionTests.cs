using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The Saga insert chokepoint against erasure evidence.
/// </summary>
/// <remarks>
/// <para>An erased conclusion is withheld in exactly the scope and the bytes it was erased in, and
/// nowhere else. An installation whose key cannot verify its evidence writes nothing at all, and one
/// that has never erased anything never touches the credential store.</para>
///
/// <para>Every fingerprint is seeded over the scope production derived: a probe memory is written in
/// the same Session first and its recorded scope is read back, so no case names a scope the store
/// would not have chosen itself. The erase routes do not exist yet, so the fingerprint itself is
/// seeded through the evidence store's own insert with a real key.</para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SagaErasureSuppressionTests
{
    private const string Conclusion = "The operator prefers dark mode.";

    private const string Probe = "A probe conclusion that fixes the scope.";

    /// <summary>"café" in normalization form C: one precomposed code point for the accented letter.</summary>
    private const string Composed = "café";

    /// <summary>"café" in normalization form D: a plain letter followed by a combining accent.</summary>
    private const string Decomposed = "café";

    private static readonly Guid Campaign = new("A0000000-0000-4000-8000-0000000000C5");

    private static CancellationToken Token => CancellationToken.None;

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fingerprinted_content_in_the_same_scope_is_suppressed_and_nothing_is_written(bool campaign)
    {
        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(credentials);

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true, keys);

        Guid? sessionId = campaign ? await harness.SessionBoundToNewCampaignAsync() : null;

        SagaMemoryDto probe = await WriteProbeAsync(harness, sessionId);

        Assert.Equal(campaign ? SagaMemoryScopeKind.Campaign : SagaMemoryScopeKind.Global, probe.ScopeKind);

        await SeedAsync(harness, key, MemoryErasureIdentity.ForSaga(probe.ScopeKind, probe.ScopeCampaignId, Conclusion));

        SagaMemoryWriteOutcome outcome = await InsertAsync(harness, Conclusion, sessionId);

        Assert.Equal(SagaMemoryWriteOutcome.Suppressed, outcome);

        Assert.Equal(0, await harness.CountAsync("saga_memories", "Content = 'The operator prefers dark mode.'"));

        // The probe only: the refused write left no embedding and opened no claim.
        Assert.Equal(1, await harness.CountAsync("saga_memory_embeddings", "1 = 1"));

        Assert.Equal(1, await harness.CountAsync("annal_claims", "1 = 1"));
    }

    /// <summary>
    /// A fingerprint names exact stored bytes in one exact scope, so text that differs only in its
    /// Unicode normalization form or its trailing whitespace, or the same text in another scope, is a
    /// different identity and is written.
    /// </summary>
    [SkippableTheory]
    [InlineData("global-nfc", SagaMemoryWriteOutcome.Suppressed)]
    [InlineData("global-nfd", SagaMemoryWriteOutcome.Written)]
    [InlineData("global-trailing-space", SagaMemoryWriteOutcome.Written)]
    [InlineData("campaign-nfc", SagaMemoryWriteOutcome.Written)]
    public async Task Only_the_exact_bytes_in_the_exact_scope_are_suppressed(string write, SagaMemoryWriteOutcome expected)
    {
        // The two spellings really are one text under normalization and two under ordinal comparison.
        Assert.Equal(Composed, Decomposed.Normalize(NormalizationForm.FormC));

        Assert.NotEqual(Composed, Decomposed, StringComparer.Ordinal);

        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(credentials);

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true, keys);

        SagaMemoryDto probe = await WriteProbeAsync(harness, sessionId: null);

        Assert.Equal(SagaMemoryScopeKind.Global, probe.ScopeKind);

        await SeedAsync(harness, key, MemoryErasureIdentity.ForSaga(probe.ScopeKind, probe.ScopeCampaignId, Composed));

        (string content, Guid? sessionId) = write switch
        {
            "global-nfc" => (Composed, (Guid?)null),
            "global-nfd" => (Decomposed, null),
            "global-trailing-space" => (Composed + " ", null),
            "campaign-nfc" => (Composed, await harness.SessionBoundToNewCampaignAsync()),
            _ => throw new ArgumentOutOfRangeException(nameof(write), write, "Unknown write case."),
        };

        Assert.Equal(expected, await InsertAsync(harness, content, sessionId));
    }

    /// <summary>
    /// Review Focus 1, through the production composition: the classifier canonicalizes the bound
    /// spelling, and the gate then derives the identity from it.
    /// </summary>
    [Theory]
    [InlineData("a0000000-0000-4000-8000-0000000000c5")]
    [InlineData("A0000000-0000-4000-8000-0000000000C5")]
    [InlineData("A00000000000400080000000000000C5")]
    public void Every_campaign_spelling_a_binding_can_hold_yields_one_fingerprint(string spelling)
    {
        using MemoryErasureKey key = FixedKey();

        (SagaMemoryScopeKind kind, string? campaignId) =
            SagaMemoryScopeClassifier.Classify(hasSession: true, (long)SagaMemoryScopeKind.Campaign, spelling);

        Assert.Equal(
            key.Fingerprint(MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Campaign, Campaign, "x")),
            key.Fingerprint(SagaErasureWriteGate.SagaIdentity(kind, campaignId, "x")));
    }

    [Fact]
    public void An_unparseable_bound_campaign_fails_closed_instead_of_matching_loosely()
    {
        (SagaMemoryScopeKind kind, string? campaignId) =
            SagaMemoryScopeClassifier.Classify(hasSession: true, (long)SagaMemoryScopeKind.Campaign, "not-a-campaign");

        // The classifier keeps an identity it cannot recognize verbatim, so the gate is what refuses it.
        Assert.Equal("not-a-campaign", campaignId);

        Assert.Throws<FormatException>(() => SagaErasureWriteGate.SagaIdentity(kind, campaignId, "x"));
    }

    /// <summary>
    /// A stored pairing no fingerprint can describe is refused as the same exception an unparseable
    /// Campaign is, so every caller handles one exception type and fails closed.
    /// </summary>
    [Theory]
    [InlineData(SagaMemoryScopeKind.Global, "A0000000-0000-4000-8000-0000000000C5")]
    [InlineData(SagaMemoryScopeKind.LegacyUnresolved, "A0000000-0000-4000-8000-0000000000C5")]
    [InlineData(SagaMemoryScopeKind.Campaign, null)]
    [InlineData(SagaMemoryScopeKind.Campaign, "00000000-0000-0000-0000-000000000000")]
    public void An_inconsistent_stored_pairing_fails_closed_as_a_format_error(SagaMemoryScopeKind kind, string? campaignId)
    {
        FormatException refused = Assert.Throws<FormatException>(() => SagaErasureWriteGate.SagaIdentity(kind, campaignId, "x"));

        Assert.IsAssignableFrom<ArgumentException>(refused.InnerException);
    }

    /// <summary>
    /// A fresh keyring, and content that was never erased: evidence the key cannot verify withholds the
    /// whole store, because no answer about this content could be trusted.
    /// </summary>
    [SkippableTheory]
    [InlineData("lost", ErrorCodes.MemoryErasure.KeyLost)]
    [InlineData("overwritten", ErrorCodes.MemoryErasure.KeyLost)]
    [InlineData("unreadable", ErrorCodes.MemoryErasure.KeyUnavailable)]
    [InlineData("malformed", ErrorCodes.MemoryErasure.KeyUnavailable)]
    public async Task A_key_that_cannot_verify_the_evidence_fails_closed_and_writes_nothing(string state, string code)
    {
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

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true, keys);

        await SeedAsync(
            harness,
            original,
            MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, "Something the operator erased."));

        MemoryErasureGuardException refused = await Assert.ThrowsAsync<MemoryErasureGuardException>(
            () => InsertAsync(harness, Conclusion, sessionId: null));

        Assert.Equal(code, refused.Error.Code);

        Assert.Equal(0, await harness.CountAsync("saga_memories", "1 = 1"));

        Assert.Equal(0, await harness.CountAsync("saga_memory_embeddings", "1 = 1"));
    }

    [SkippableFact]
    public async Task No_fingerprints_means_no_keychain_io()
    {
        CountingOsCredentialStore credentials = new(new InMemoryOsCredentialStore());

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(credentials);

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true, keys);

        Guid campaignSession = await harness.SessionBoundToNewCampaignAsync();

        Guid unboundSession = await harness.SessionWithUnresolvedBindingAsync();

        Assert.Equal(SagaMemoryWriteOutcome.Written, await InsertAsync(harness, "A Global conclusion.", sessionId: null));

        Assert.Equal(SagaMemoryWriteOutcome.Written, await InsertAsync(harness, "A Campaign conclusion.", campaignSession));

        Assert.Equal(SagaMemoryWriteOutcome.Written, await InsertAsync(harness, "An unresolved conclusion.", unboundSession));

        Assert.Equal(3, await harness.CountAsync("saga_memories", "1 = 1"));

        Assert.Equal(0, credentials.Calls);
    }

    /// <summary>
    /// A first fingerprint that commits after the insert's probe and before its transaction: the insert
    /// meets evidence with no key in hand, rolls back, resolves the key outside the transaction, and
    /// on its one retry is suppressed.
    /// </summary>
    [SkippableFact]
    public async Task An_insert_that_meets_evidence_committed_after_its_probe_retries_once_and_is_suppressed()
    {
        InMemoryOsCredentialStore credentials = new();

        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(credentials);

        using MemoryErasureKeyring inner = MemoryErasureTestKeys.Isolated(credentials);

        EvidenceOnFirstCopyProvider keys = new(inner);

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true, keys);

        // A write with no Session is Global by the classifier's own rule, so no probe is needed to fix
        // the scope - and a probe would spend the provider's first copy.
        MemoryErasureIdentity identity = MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, Conclusion);

        keys.OnFirstCopy = () =>
        {
            using ArcanumDbContext sibling = harness.CreateSiblingContext();

            MemoryErasureTestKeys
                .SeedFingerprintAsync((SqliteConnection)sibling.Database.GetDbConnection(), key, identity, Token)
                .GetAwaiter()
                .GetResult();
        };

        SagaMemoryWriteOutcome outcome = await InsertAsync(harness, Conclusion, sessionId: null);

        Assert.Equal(SagaMemoryWriteOutcome.Suppressed, outcome);

        Assert.Equal(1, keys.Copies);

        Assert.Equal(1, keys.Opens);

        Assert.Equal(0, await harness.CountAsync("saga_memories", "Content = 'The operator prefers dark mode.'"));

        Assert.Equal(0, await harness.CountAsync("annal_claims", "1 = 1"));
    }

    private static MemoryErasureKey FixedKey() =>
        MemoryErasureKey.FromBytes(Enumerable.Range(0, MemoryErasureDigestGrammar.KeyBytes).Select(static i => (byte)i).ToArray());

    private static Task<SagaMemoryWriteOutcome> InsertAsync(SagaStoreHarness harness, string content, Guid? sessionId) =>
        harness.Store.InsertAsync(
            Guid.NewGuid().ToString(),
            content,
            DateTimeOffset.UtcNow,
            sessionId,
            tags: null,
            source: "extraction",
            harness.Embedding(),
            Token);

    private static async Task<SagaMemoryDto> WriteProbeAsync(SagaStoreHarness harness, Guid? sessionId)
    {
        string id = Guid.NewGuid().ToString();

        Assert.Equal(
            SagaMemoryWriteOutcome.Written,
            await harness.Store.InsertAsync(
                id,
                Probe,
                DateTimeOffset.UtcNow,
                sessionId,
                tags: null,
                source: "extraction",
                harness.Embedding(1),
                Token));

        SagaMemoryCurationRow row = Assert.IsType<SagaMemoryCurationRow>(await harness.Store.ReadCurationRowAsync(id, Token));

        return row.Memory;
    }

    private static Task SeedAsync(SagaStoreHarness harness, MemoryErasureKey key, MemoryErasureIdentity identity) =>
        MemoryErasureTestKeys.SeedFingerprintAsync((SqliteConnection)harness.Connection, key, identity, Token);

    /// <summary>
    /// Wraps a fresh keyring and, on the chokepoint's first latched copy, commits a fingerprint from
    /// another connection before answering that no key is latched.
    /// </summary>
    private sealed class EvidenceOnFirstCopyProvider(IMemoryErasureKeyProvider inner) : IMemoryErasureKeyProvider
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

            return null;
        }
    }
}
