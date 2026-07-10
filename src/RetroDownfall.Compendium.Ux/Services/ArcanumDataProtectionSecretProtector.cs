using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Compendium.Ux.Services;

public sealed class ArcanumDataProtectionSecretProtector : IArcanumSecretProtector, IDisposable
{

    private const string Prefix = "dp:v1:";

    private readonly ServiceProvider _serviceProvider;

    private readonly IDataProtector _protector;

    public ArcanumDataProtectionSecretProtector()
    {

        ServiceCollection services = new();

        string keyRingPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "keys");

        _ = Directory.CreateDirectory(keyRingPath);

        services.AddDataProtection()

            .SetApplicationName("ArcanumCore")

            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));

        _serviceProvider = services.BuildServiceProvider();

        IDataProtectionProvider dataProtectionProvider = _serviceProvider.GetRequiredService<IDataProtectionProvider>();

        _protector = dataProtectionProvider.CreateProtector("Arcanum.Configuration.ProviderSecrets");

    }

    public string? Protect(string? plaintext)
    {

        if (string.IsNullOrEmpty(plaintext))
        {

            return plaintext;

        }

        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal))
        {

            return plaintext;

        }

        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);

        byte[] protectedBytes = _protector.Protect(plainBytes);

        CryptographicOperations.ZeroMemory(plainBytes);

        return Prefix + Convert.ToBase64String(protectedBytes);

    }

    public string? Unprotect(string? stored)
    {

        if (string.IsNullOrEmpty(stored))
        {

            return stored;

        }

        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {

            return stored;

        }

        string payload = stored[Prefix.Length..];

        byte[] protectedBytes = Convert.FromBase64String(payload);

        byte[] plain = _protector.Unprotect(protectedBytes);

        string restored = Encoding.UTF8.GetString(plain);

        CryptographicOperations.ZeroMemory(plain);

        return restored;

    }

    public ArcanumSettings DecryptProviderKeys(ArcanumSettings settings) =>
        TransformSecrets(settings, Unprotect);

    public ArcanumSettings EncryptProviderKeys(ArcanumSettings settings) =>
        TransformSecrets(settings, Protect);

    private ArcanumSettings TransformSecrets(ArcanumSettings settings, Func<string?, string?> transform)
    {

        ArcanumSettings result = settings;

        if (settings.Providers is { Length: > 0 })
        {

            ProviderSettings[] providers = new ProviderSettings[settings.Providers.Length];

            for (int i = 0; i < settings.Providers.Length; i++)
            {

                ProviderSettings provider = settings.Providers[i];

                providers[i] = provider with { ApiKey = transform(provider.ApiKey) };

            }

            result = result with { Providers = providers };

        }

        if (!string.IsNullOrEmpty(settings.Host.Https.CertificatePassword))
        {

            result = result with
            {
                Host = result.Host with
                {
                    Https = result.Host.Https with
                    {
                        CertificatePassword = transform(settings.Host.Https.CertificatePassword),
                    },
                },
            };

        }

        return result;

    }

    public void Dispose()
    {

        _serviceProvider.Dispose();

    }

}
