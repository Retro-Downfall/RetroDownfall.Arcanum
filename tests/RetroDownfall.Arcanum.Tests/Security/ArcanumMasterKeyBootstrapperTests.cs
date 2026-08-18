using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class ArcanumMasterKeyBootstrapperTests
{

    [Fact]
    public void Corrupt_key_with_existing_grimoire_throws_sanitized_controlled_failure()
    {

        SecretStoreReadResult result = new(
            SecretStoreReadStatus.Corrupted,
            null,
            "secret-canary-from-data-protection");

        MasterApiKeyUnavailableException exception = Assert.Throws<MasterApiKeyUnavailableException>(
            () => ArcanumMasterKeyBootstrapper.ThrowIfCorruptedWithExistingGrimoire(
                result,
                grimoireExists: true));

        Assert.DoesNotContain("secret-canary", exception.Message, StringComparison.Ordinal);

        Assert.Contains("master API key", exception.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Corrupt_key_without_grimoire_allows_safe_regeneration()
    {

        SecretStoreReadResult result = new(
            SecretStoreReadStatus.Corrupted,
            null,
            "secret-canary-from-data-protection");

        ArcanumMasterKeyBootstrapper.ThrowIfCorruptedWithExistingGrimoire(
            result,
            grimoireExists: false);

    }

    /// <summary>
    /// Minting a replacement master key overwrites the OS credential — and when that write fails too,
    /// deletes it. A probe that failed leaves the credential's existence unknown, and one that
    /// succeeded proves a live credential is there, so neither may authorise regeneration. This holds
    /// whether or not a Grimoire database exists: the surviving key is what every client authenticates
    /// with, and it is unrecoverable once overwritten.
    /// </summary>
    [Theory]
    [InlineData(OsCredentialStoreStatus.Failed)]
    [InlineData(OsCredentialStoreStatus.Ok)]
    public void Regeneration_is_refused_while_os_storage_may_hold_the_live_key(
        OsCredentialStoreStatus status)
    {

        OsCredentialStoreResult probe = new(status, "surviving-key", "probe message");

        MasterApiKeyUnavailableException exception =
            Assert.Throws<MasterApiKeyUnavailableException>(
                () => ArcanumMasterKeyBootstrapper.ThrowIfOsKeyStorageMayHoldTheLiveKey(probe));

        Assert.DoesNotContain("surviving-key", exception.Message, StringComparison.Ordinal);

        Assert.Contains("master API key", exception.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Theory]
    [InlineData(OsCredentialStoreStatus.NotFound)]
    [InlineData(OsCredentialStoreStatus.Unavailable)]
    public void Regeneration_is_allowed_when_os_storage_holds_nothing_of_ours(
        OsCredentialStoreStatus status)
    {

        OsCredentialStoreResult probe = new(status, null, "probe message");

        ArcanumMasterKeyBootstrapper.ThrowIfOsKeyStorageMayHoldTheLiveKey(probe);

    }

}
