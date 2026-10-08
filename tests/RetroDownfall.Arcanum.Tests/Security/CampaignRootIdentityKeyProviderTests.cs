using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Resolution runs before every session-backed turn, so the OS credential read has to happen once
/// per process — including when it fails. A failure that is not memoised turns a one-time degrade
/// into an unbounded per-turn cost: a warning per turn on headless Linux, and on macOS a repeated
/// user-visible "wants to use your confidential information" prompt the operator cannot escape.
/// </summary>
public sealed class CampaignRootIdentityKeyProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_existing_key_read_returns_the_cached_or_stored_key_without_Set(
        bool primeOrdinaryCache)
    {
        byte[] expected = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

        CountingOsCredentialStore credentials = new(
            OsCredentialStoreResult.Ok(Convert.ToBase64String(expected)));

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        if (primeOrdinaryCache)
        {
            Assert.True(provider.TryCopyRootIdentityKey(new byte[32]));
        }

        byte[] destination = new byte[32];

        Assert.True(provider.TryCopyExistingRootIdentityKey(destination));
        Assert.Equal(expected, destination);
        Assert.Equal(1, credentials.GetCount);
        Assert.Equal(0, credentials.SetCount);
    }

    [Fact]
    public void Recovery_missing_malformed_or_unavailable_key_never_calls_Set()
    {
        OsCredentialStoreResult[] refusedReads =
        [
            OsCredentialStoreResult.NotFound(),
            OsCredentialStoreResult.Failed("test read failure"),
            new OsCredentialStoreResult(
                OsCredentialStoreStatus.Unavailable,
                null,
                "test unavailable"),
            OsCredentialStoreResult.Ok("not-base64"),
            OsCredentialStoreResult.Ok(Convert.ToBase64String(new byte[31])),
        ];

        foreach (OsCredentialStoreResult refusedRead in refusedReads)
        {
            CountingOsCredentialStore credentials = new(refusedRead);

            using CampaignRootIdentityKeyProvider provider = new(credentials);

            byte[] destination = Enumerable.Repeat((byte)0xA5, 32).ToArray();

            Assert.False(provider.TryCopyExistingRootIdentityKey(destination));
            Assert.All(destination, static value => Assert.Equal(0xA5, value));
            Assert.False(provider.TryCopyExistingRootIdentityKey(destination));
            Assert.All(destination, static value => Assert.Equal(0xA5, value));
            Assert.Equal(2, credentials.GetCount);
            Assert.Equal(0, credentials.SetCount);
        }

        CountingOsCredentialStore wrongWidthCredentials = new(
            OsCredentialStoreResult.NotFound());

        using CampaignRootIdentityKeyProvider wrongWidthProvider = new(wrongWidthCredentials);

        byte[] shortDestination = Enumerable.Repeat((byte)0xA5, 31).ToArray();

        byte[] longDestination = Enumerable.Repeat((byte)0xA5, 33).ToArray();

        Assert.False(wrongWidthProvider.TryCopyExistingRootIdentityKey(shortDestination));
        Assert.False(wrongWidthProvider.TryCopyExistingRootIdentityKey(longDestination));
        Assert.All(shortDestination, static value => Assert.Equal(0xA5, value));
        Assert.All(longDestination, static value => Assert.Equal(0xA5, value));
        Assert.Equal(0, wrongWidthCredentials.GetCount);
        Assert.Equal(0, wrongWidthCredentials.SetCount);
    }

    [Fact]
    public void Recovery_disposal_exception_and_cancellation_boundaries_are_fail_closed()
    {
        CountingOsCredentialStore disposedCredentials = new(
            OsCredentialStoreResult.Ok(Convert.ToBase64String(new byte[32])));

        CampaignRootIdentityKeyProvider disposedProvider = new(disposedCredentials);

        disposedProvider.Dispose();

        byte[] disposedDestination = Enumerable.Repeat((byte)0xA5, 32).ToArray();

        Assert.False(disposedProvider.TryCopyExistingRootIdentityKey(disposedDestination));
        Assert.All(disposedDestination, static value => Assert.Equal(0xA5, value));
        Assert.Equal(0, disposedCredentials.GetCount);
        Assert.Equal(0, disposedCredentials.SetCount);

        ThrowingOsCredentialStore faultedCredentials = new(
            new InvalidOperationException("test credential fault"));

        using CampaignRootIdentityKeyProvider faultedProvider = new(faultedCredentials);

        byte[] faultedDestination = Enumerable.Repeat((byte)0xA5, 32).ToArray();

        Assert.False(faultedProvider.TryCopyExistingRootIdentityKey(faultedDestination));
        Assert.False(faultedProvider.TryCopyExistingRootIdentityKey(faultedDestination));
        Assert.All(faultedDestination, static value => Assert.Equal(0xA5, value));
        Assert.Equal(2, faultedCredentials.GetCount);
        Assert.Equal(0, faultedCredentials.SetCount);

        ThrowingOsCredentialStore canceledCredentials = new(
            new OperationCanceledException("test cancellation"));

        using CampaignRootIdentityKeyProvider canceledProvider = new(canceledCredentials);

        byte[] canceledDestination = Enumerable.Repeat((byte)0xA5, 32).ToArray();

        Assert.Throws<OperationCanceledException>(
            () => canceledProvider.TryCopyExistingRootIdentityKey(canceledDestination));
        Assert.All(canceledDestination, static value => Assert.Equal(0xA5, value));
        Assert.Equal(1, canceledCredentials.GetCount);
        Assert.Equal(0, canceledCredentials.SetCount);
    }

    [Fact]
    public void Recovery_not_found_does_not_negative_cache_or_block_later_first_registration()
    {
        InMemoryOsCredentialStore credentials = new();

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        Assert.False(provider.TryCopyExistingRootIdentityKey(new byte[32]));
        Assert.Equal(1, credentials.GetCount);
        Assert.Equal(0, credentials.SetCount);

        Assert.Equal(
            CampaignRootIdentityKeyState.Created,
            provider.OpenOrCreateRootIdentityKey(registeredRootsExist: false));

        // The probe, then the read-back of what was written.
        Assert.Equal(3, credentials.GetCount);
        Assert.Equal(1, credentials.SetCount);

        byte[] ordinary = new byte[32];

        Assert.True(provider.TryCopyRootIdentityKey(ordinary));
        Assert.Contains(ordinary, static value => value != 0);
    }

    /// <summary>
    /// A key that is missing while a root is registered is a lost key, and a new one would orphan every
    /// registered root while the installation kept looking healthy.
    /// </summary>
    [Fact]
    public void A_lost_key_is_not_replaced_while_roots_are_registered()
    {
        InMemoryOsCredentialStore credentials = new();

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        Assert.Equal(
            CampaignRootIdentityKeyState.Lost,
            provider.OpenOrCreateRootIdentityKey(registeredRootsExist: true));

        Assert.Equal(0, credentials.SetCount);

        // Nothing else can mint it either: an ordinary read of the absent account stays a refusal, and
        // so does a recovery read.
        byte[] destination = Enumerable.Repeat((byte)0xA5, 32).ToArray();

        Assert.False(provider.TryCopyRootIdentityKey(destination));
        Assert.False(provider.TryCopyExistingRootIdentityKey(destination));
        Assert.All(destination, static value => Assert.Equal(0xA5, value));
        Assert.Equal(0, credentials.SetCount);

        // The same call with nothing registered is a first registration and does create it.
        Assert.Equal(
            CampaignRootIdentityKeyState.Created,
            provider.OpenOrCreateRootIdentityKey(registeredRootsExist: false));

        Assert.Equal(1, credentials.SetCount);
    }

    /// <summary>
    /// Resolution runs before every session-backed turn, so a reader that minted the key on first use
    /// would mint it on an installation whose key had merely been lost.
    /// </summary>
    [Fact]
    public void An_ordinary_read_never_creates_the_key_and_probes_the_absent_account_once()
    {
        InMemoryOsCredentialStore credentials = new();

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        byte[] destination = new byte[32];

        for (int turn = 0; turn < 5; turn++)
        {
            Assert.False(provider.TryCopyRootIdentityKey(destination));
        }

        Assert.Equal(1, credentials.GetCount);
        Assert.Equal(0, credentials.SetCount);
        Assert.All(destination, static value => Assert.Equal(0, value));
    }

    [Fact]
    public void First_registration_creates_the_key_once_and_every_port_shares_it()
    {
        InMemoryOsCredentialStore credentials = new();

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        // A turn that ran before any registration found the account absent and latched that...
        Assert.False(provider.TryCopyRootIdentityKey(new byte[32]));

        // ...which must not stop the registration that follows from creating the key.
        Assert.Equal(
            CampaignRootIdentityKeyState.Created,
            provider.OpenOrCreateRootIdentityKey(registeredRootsExist: false));

        Assert.Equal(
            CampaignRootIdentityKeyState.Present,
            provider.OpenOrCreateRootIdentityKey(registeredRootsExist: true));

        byte[] first = new byte[32];

        byte[] cached = new byte[32];

        byte[] recovery = new byte[32];

        Assert.True(provider.TryCopyRootIdentityKey(first));
        Assert.True(provider.TryCopyRootIdentityKey(cached));
        Assert.True(provider.TryCopyExistingRootIdentityKey(recovery));
        Assert.Equal(first, cached);
        Assert.Equal(first, recovery);
        Assert.Contains(first, static value => value != 0);
        Assert.Equal(1, credentials.SetCount);
        Assert.Equal(first, Convert.FromBase64String(credentials.StoredValue!));
    }

    /// <summary>
    /// An echo of what was handed in is not proof of persistence, so the key is read back before it is
    /// published.
    /// </summary>
    [Fact]
    public void A_creation_that_cannot_be_read_back_is_not_published()
    {
        InMemoryOsCredentialStore credentials = new() { DropWrites = true };

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        Assert.Equal(
            CampaignRootIdentityKeyState.Unavailable,
            provider.OpenOrCreateRootIdentityKey(registeredRootsExist: false));

        Assert.Equal(1, credentials.SetCount);

        byte[] destination = new byte[32];

        Assert.False(provider.TryCopyRootIdentityKey(destination));
        Assert.All(destination, static value => Assert.Equal(0, value));
    }

    [Fact]
    public void A_failed_creation_write_is_unavailable_and_publishes_no_key()
    {
        InMemoryOsCredentialStore credentials = new()
        {
            WriteResult = OsCredentialStoreResult.Failed("test write failure"),
        };

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        Assert.Equal(
            CampaignRootIdentityKeyState.Unavailable,
            provider.OpenOrCreateRootIdentityKey(registeredRootsExist: false));

        Assert.False(provider.TryCopyRootIdentityKey(new byte[32]));
        Assert.Equal(1, credentials.SetCount);
    }

    /// <summary>
    /// A stored value that is present but not a key is damage, not absence, so it is never overwritten.
    /// </summary>
    [Theory]
    [InlineData("not-base64")]
    [InlineData("AAAA")]
    public void A_present_but_malformed_key_is_never_overwritten(string stored)
    {
        InMemoryOsCredentialStore credentials = new() { StoredValue = stored };

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        Assert.Equal(
            CampaignRootIdentityKeyState.Unavailable,
            provider.OpenOrCreateRootIdentityKey(registeredRootsExist: false));

        Assert.Equal(0, credentials.SetCount);
        Assert.Equal(stored, credentials.StoredValue);
    }

    [Fact]
    public void An_existing_key_is_adopted_not_replaced()
    {
        byte[] existing = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();

        InMemoryOsCredentialStore credentials = new() { StoredValue = Convert.ToBase64String(existing) };

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        Assert.Equal(
            CampaignRootIdentityKeyState.Present,
            provider.OpenOrCreateRootIdentityKey(registeredRootsExist: true));

        byte[] destination = new byte[32];

        Assert.True(provider.TryCopyRootIdentityKey(destination));
        Assert.Equal(existing, destination);
        Assert.Equal(0, credentials.SetCount);
    }

    /// <summary>
    /// The ordinary and recovery root-identity ports are two views of one singleton, so a recovery read
    /// that finds the key populates the same cache the codec and opener later read from.
    /// </summary>
    /// <remarks>
    /// The scope is asynchronous because <see cref="CampaignPathMarkerLifecycle"/> implements
    /// <see cref="IAsyncDisposable"/> and deliberately not <see cref="IDisposable"/>: its disposal
    /// drains the retained no-follow root handles, and every one of those releases is asynchronous.
    /// The container refuses to dispose such a service from a synchronous scope, which is exactly the
    /// contract asserted below — every production site that resolves this lifecycle reaches it through
    /// <c>CreateAsyncScope</c>, so a synchronous scope here would be testing a composition production
    /// never performs.
    /// </remarks>
    [Fact]
    public async Task Infrastructure_graph_shares_one_root_identity_provider_between_ordinary_and_recovery_ports()
    {
        ServiceCollection services = [];

        services.AddSingleton<IOsCredentialStore>(
            new CountingOsCredentialStore(OsCredentialStoreResult.NotFound()));

        services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        await using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<GrimoireDbPassphraseSource>(
            provider.GetRequiredService<IGrimoireDbPassphraseSource>())
            .SetPassphrase("task-6-root-key-provider-composition");

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        ICampaignRootIdentityKeyProvider ordinary =
            scope.ServiceProvider.GetRequiredService<ICampaignRootIdentityKeyProvider>();

        ICampaignRootIdentityRecoveryKeyProvider recovery =
            scope.ServiceProvider.GetRequiredService<ICampaignRootIdentityRecoveryKeyProvider>();

        ICampaignPathMarkerLifecycle lifecycle =
            scope.ServiceProvider.GetRequiredService<ICampaignPathMarkerLifecycle>();

        Assert.Same(ordinary, recovery);

        Assert.Same(
            ordinary,
            scope.ServiceProvider.GetRequiredService<ICampaignRootIdentityKeyCreator>());

        CampaignPathMarkerLifecycle concrete = Assert.IsType<CampaignPathMarkerLifecycle>(lifecycle);

        // Pinned rather than incidental: adding IDisposable would let a synchronous scope release the
        // retained roots on a path that cannot await them.
        Assert.IsAssignableFrom<IAsyncDisposable>(concrete);

        Assert.IsNotAssignableFrom<IDisposable>(concrete);
    }

    [Fact]
    public void Successful_read_is_cached_for_the_process()
    {
        CountingOsCredentialStore credentials = new(
            OsCredentialStoreResult.Ok(Convert.ToBase64String(new byte[32])));

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        byte[] destination = new byte[32];

        for (int turn = 0; turn < 5; turn++)
        {
            Assert.True(provider.TryCopyRootIdentityKey(destination));
        }

        Assert.Equal(1, credentials.GetCount);
    }

    [Theory]
    [InlineData(OsCredentialStoreStatus.Failed)]
    [InlineData(OsCredentialStoreStatus.Unavailable)]
    public void Unreadable_store_is_probed_once_per_process(OsCredentialStoreStatus status)
    {
        CountingOsCredentialStore credentials = new(
            new OsCredentialStoreResult(status, null, "test read failure"));

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        byte[] destination = new byte[32];

        for (int turn = 0; turn < 5; turn++)
        {
            Assert.False(provider.TryCopyRootIdentityKey(destination));
        }

        Assert.Equal(1, credentials.GetCount);
    }

    [Fact]
    public void Malformed_stored_value_is_decoded_once_per_process()
    {
        CountingOsCredentialStore credentials = new(OsCredentialStoreResult.Ok("bm90LWEta2V5"));

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        byte[] destination = new byte[32];

        for (int turn = 0; turn < 5; turn++)
        {
            Assert.False(provider.TryCopyRootIdentityKey(destination));
        }

        Assert.Equal(1, credentials.GetCount);
    }

    /// <summary>
    /// A store that remembers what was written, so a creation can be read back.
    /// </summary>
    private sealed class InMemoryOsCredentialStore : IOsCredentialStore
    {
        public int GetCount { get; private set; }

        public int SetCount { get; private set; }

        public string? StoredValue { get; set; }

        /// <summary>Answers a write as successful without keeping it.</summary>
        public bool DropWrites { get; init; }

        public OsCredentialStoreResult? WriteResult { get; init; }

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            GetCount++;

            return StoredValue is null
                ? OsCredentialStoreResult.NotFound()
                : OsCredentialStoreResult.Ok(StoredValue);
        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {
            SetCount++;

            if (WriteResult is { } refused)
            {
                return refused;
            }

            if (!DropWrites)
            {
                StoredValue = secret;
            }

            return OsCredentialStoreResult.Ok(secret);
        }

        public OsCredentialStoreResult Delete(string service, string account)
        {
            StoredValue = null;

            return OsCredentialStoreResult.Ok(string.Empty);
        }
    }

    private sealed class CountingOsCredentialStore(
        OsCredentialStoreResult readResult,
        OsCredentialStoreResult? writeResult = null) : IOsCredentialStore
    {
        public int GetCount { get; private set; }

        public int SetCount { get; private set; }

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            GetCount++;

            return readResult;
        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {
            SetCount++;

            return writeResult ?? OsCredentialStoreResult.Ok(secret);
        }

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Ok(string.Empty);
    }

    private sealed class ThrowingOsCredentialStore(Exception exception) : IOsCredentialStore
    {
        public int GetCount { get; private set; }

        public int SetCount { get; private set; }

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            GetCount++;

            throw exception;
        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {
            SetCount++;

            return OsCredentialStoreResult.Ok(secret);
        }

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Ok(string.Empty);
    }
}
