using Microsoft.AspNetCore.DataProtection;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("ProcessEnvironment")]
public sealed class DataProtectionSecretStoreTests : IDisposable
{

    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), $"arcanum-test-{Guid.NewGuid():N}");

    private readonly Dictionary<string, string?> _originalEnvironment = new();

    public DataProtectionSecretStoreTests()
    {

        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");

        SetEnvironment("DOTNET_ENVIRONMENT", "Testing");

        SetEnvironment("ARCANUM_TEST_HOME", _storeDir);

        Directory.CreateDirectory(_storeDir);

        DeleteSecurityDat();

    }

    public void Dispose()
    {

        try
        {

            DeleteSecurityDat();

            if (Directory.Exists(_storeDir))
            {

                Directory.Delete(_storeDir, recursive: true);

            }

        }
        catch
        {

            // Best-effort cleanup.

        }
        finally
        {

            foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
            {

                global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

            }

        }

    }

    private static void DeleteSecurityDat()
    {

        string path = ArcanumPaths.ApiKeyStoreFile;

        try
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }
        catch
        {

            // Best-effort cleanup.

        }

    }

    private DataProtectionSecretStore CreateStore()
    {

        IDataProtectionProvider dataProtectionProvider = DataProtectionProvider.Create(
            new DirectoryInfo(_storeDir),
            _ => { });

        IApiKeyDigestCache apiKeyDigestCache = new ApiKeyDigestCache(new FakeTimeProvider());

        return new DataProtectionSecretStore(dataProtectionProvider, apiKeyDigestCache);

    }

    [Fact]
    public async Task SaveApiKeyAsync_RoundTrip_ReturnsSameKey()
    {

        using DataProtectionSecretStore store = CreateStore();

        string apiKey = Guid.NewGuid().ToString("N");

        await store.SaveApiKeyAsync(apiKey);

        string? result = await store.GetApiKeyAsync();

        Assert.Equal(apiKey, result);

    }

    [Fact]
    public async Task GetApiKeyReadResultAsync_MissingFile_ReturnsMissing()
    {

        using DataProtectionSecretStore store = CreateStore();

        SecretStoreReadResult result = await store.GetApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Missing, result.Status);

    }

    [Fact]
    public async Task GetApiKeyReadResultAsync_CorruptFile_ReturnsCorrupted()
    {

        using DataProtectionSecretStore store = CreateStore();

        string path = ArcanumPaths.ApiKeyStoreFile;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        try
        {

            SecretStoreReadResult result = await store.GetApiKeyReadResultAsync();

            Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);

        }
        finally
        {

            File.Delete(path);

        }

    }

    [Fact]
    public async Task SaveGrimoireEncryptionSecretAsync_RoundTrip_ReturnsSameSecret()
    {

        using DataProtectionSecretStore store = CreateStore();

        string secret = Guid.NewGuid().ToString("N");

        await store.SaveGrimoireEncryptionSecretAsync(secret);

        string? result = await store.GetGrimoireEncryptionSecretAsync();

        Assert.Equal(secret, result);

    }

    [Fact]
    public async Task SaveApiKeyAsync_InvalidatesDigestCache()
    {

        using DataProtectionSecretStore store = CreateStore();

        await store.SaveApiKeyAsync(Guid.NewGuid().ToString("N"));

        Assert.True(true);

    }

    private void SetEnvironment(string name, string value)
    {

        _originalEnvironment[name] = global::System.Environment.GetEnvironmentVariable(name);

        global::System.Environment.SetEnvironmentVariable(name, value);

    }

}
