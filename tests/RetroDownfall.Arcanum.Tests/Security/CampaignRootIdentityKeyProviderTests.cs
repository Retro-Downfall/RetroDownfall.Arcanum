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
    public void Recovery_not_found_does_not_negative_cache_or_block_later_ordinary_first_registration()
    {

        SequencedOsCredentialStore credentials = new(
            OsCredentialStoreResult.NotFound(),
            OsCredentialStoreResult.NotFound());

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        Assert.False(provider.TryCopyExistingRootIdentityKey(new byte[32]));
        Assert.Equal(1, credentials.GetCount);
        Assert.Equal(0, credentials.SetCount);

        byte[] ordinary = new byte[32];

        Assert.True(provider.TryCopyRootIdentityKey(ordinary));
        Assert.Equal(2, credentials.GetCount);
        Assert.Equal(1, credentials.SetCount);
        Assert.Contains(ordinary, static value => value != 0);

    }

    [Fact]
    public void Ordinary_first_registration_still_creates_the_key_on_NotFound()
    {

        CountingOsCredentialStore credentials = new(OsCredentialStoreResult.NotFound());

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        byte[] first = new byte[32];

        byte[] cached = new byte[32];

        Assert.True(provider.TryCopyRootIdentityKey(first));
        Assert.True(provider.TryCopyRootIdentityKey(cached));
        Assert.Equal(first, cached);
        Assert.Contains(first, static value => value != 0);
        Assert.Equal(1, credentials.GetCount);
        Assert.Equal(1, credentials.SetCount);

        byte[] recovery = new byte[32];

        Assert.True(provider.TryCopyExistingRootIdentityKey(recovery));
        Assert.Equal(first, recovery);
        Assert.Equal(1, credentials.GetCount);
        Assert.Equal(1, credentials.SetCount);

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

    [Fact]
    public void Failed_creation_is_attempted_once_per_process()
    {

        CountingOsCredentialStore credentials = new(
            OsCredentialStoreResult.NotFound(),
            OsCredentialStoreResult.Failed("test write failure"));

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        byte[] destination = new byte[32];

        for (int turn = 0; turn < 5; turn++)
        {

            Assert.False(provider.TryCopyRootIdentityKey(destination));

        }

        Assert.Equal(1, credentials.GetCount);

        Assert.Equal(1, credentials.SetCount);

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

    private sealed class SequencedOsCredentialStore(
        params OsCredentialStoreResult[] readResults) : IOsCredentialStore
    {

        private int _readIndex;

        public int GetCount { get; private set; }

        public int SetCount { get; private set; }

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account)
        {

            GetCount++;

            int index = Math.Min(_readIndex, readResults.Length - 1);

            _readIndex++;

            return readResults[index];

        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {

            SetCount++;

            return OsCredentialStoreResult.Ok(secret);

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
