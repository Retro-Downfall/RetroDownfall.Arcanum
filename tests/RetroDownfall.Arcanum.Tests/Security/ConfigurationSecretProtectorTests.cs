using Microsoft.AspNetCore.DataProtection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class ConfigurationSecretProtectorTests
{

    [Fact]
    public void ProtectAndUnprotect_round_trips_plaintext()
    {

        IDataProtectionProvider provider = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector protector = new(provider);

        string? restored = protector.Unprotect(protector.Protect("sk-test-key"));

        Assert.Equal("sk-test-key", restored);

    }

    [Fact]
    public void ProtectSettingsForStorage_encrypts_provider_api_keys()
    {

        IDataProtectionProvider provider = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector protector = new(provider);

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "openai", ApiKey = "sk-live" },
            ],
        };

        ArcanumSettings stored = protector.ProtectSettingsForStorage(settings);

        Assert.StartsWith("dp:v1:", stored.Providers![0].ApiKey, StringComparison.Ordinal);

        Assert.Equal("sk-live", protector.ResolveApiKey(stored.Providers[0].ApiKey));

    }

}
