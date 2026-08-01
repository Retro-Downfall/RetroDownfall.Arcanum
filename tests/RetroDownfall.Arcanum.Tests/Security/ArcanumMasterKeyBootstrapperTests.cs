using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Security;

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

}
