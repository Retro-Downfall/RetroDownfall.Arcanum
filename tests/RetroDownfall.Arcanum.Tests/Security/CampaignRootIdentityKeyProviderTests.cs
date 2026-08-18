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

}
