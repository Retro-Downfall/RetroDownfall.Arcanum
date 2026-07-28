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

    [Fact]
    public void ProtectSettingsForStorage_encrypts_https_certificate_password()
    {

        IDataProtectionProvider provider = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector protector = new(provider);

        ArcanumSettings settings = new()
        {
            Host = new HostSettings
            {
                Https = new HttpsSettings
                {
                    Enabled = true,
                    CertificatePath = "/certs/localhost.pfx",
                    CertificatePassword = "pfx-secret",
                },
            },
        };

        ArcanumSettings stored = protector.ProtectSettingsForStorage(settings);

        Assert.StartsWith("dp:v1:", stored.Host.Https.CertificatePassword, StringComparison.Ordinal);

        Assert.Equal("pfx-secret", protector.Unprotect(stored.Host.Https.CertificatePassword));

    }

    [Fact]
    public void ProtectSettingsForStorage_NoProvidersNoHttpsPassword_ReturnsSameInstance()
    {

        IDataProtectionProvider provider = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector protector = new(provider);

        ArcanumSettings settings = new()
        {
            Host = new HostSettings { Https = new HttpsSettings { Enabled = true, CertificatePath = "/certs/x.pfx" } },
        };

        ArcanumSettings stored = protector.ProtectSettingsForStorage(settings);

        Assert.Same(settings, stored);

    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Protect_NullOrEmpty_ReturnsUnchanged(string? plaintext)
    {

        IDataProtectionProvider provider = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector protector = new(provider);

        Assert.Equal(plaintext, protector.Protect(plaintext));

    }

    [Fact]
    public void Protect_AlreadyProtected_ReturnsUnchanged()
    {

        IDataProtectionProvider provider = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector protector = new(provider);

        const string alreadyProtected = "dp:v1:already-encoded";

        Assert.Equal(alreadyProtected, protector.Protect(alreadyProtected));

    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unprotect_NullOrEmpty_ReturnsUnchanged(string? stored)
    {

        IDataProtectionProvider provider = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector protector = new(provider);

        Assert.Equal(stored, protector.Unprotect(stored));

    }

    [Fact]
    public void Unprotect_PlaintextWithoutPrefix_ReturnsUnchanged()
    {

        IDataProtectionProvider provider = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector protector = new(provider);

        const string legacyPlaintext = "legacy-plain-api-key";

        Assert.Equal(legacyPlaintext, protector.Unprotect(legacyPlaintext));

    }

    [Fact]
    public void ProtectSettingsForStorage_NoProviders_ReturnsSameInstance()
    {

        IDataProtectionProvider provider = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector protector = new(provider);

        ArcanumSettings settings = new()
        {
            Providers = [],
        };

        ArcanumSettings stored = protector.ProtectSettingsForStorage(settings);

        Assert.Same(settings, stored);

    }

    [Fact]
    public void ProtectSettingsForStorage_NullProvidersArray_ReturnsSameInstance()
    {

        IDataProtectionProvider provider = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector protector = new(provider);

        ArcanumSettings settings = new()
        {
            Providers = null!,
        };

        ArcanumSettings stored = protector.ProtectSettingsForStorage(settings);

        Assert.Same(settings, stored);

    }

    [Fact]
    public void ResolveApiKey_DelegatesToUnprotect()
    {

        IDataProtectionProvider provider = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector protector = new(provider);

        string? protectedKey = protector.Protect("resolve-me");

        Assert.Equal("resolve-me", protector.ResolveApiKey(protectedKey));

    }

}
