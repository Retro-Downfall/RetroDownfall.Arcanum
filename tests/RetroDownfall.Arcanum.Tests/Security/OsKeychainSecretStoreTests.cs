using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("ProcessEnvironment")]
public sealed class OsKeychainSecretStoreTests : IDisposable
{

    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), $"arcanum-oskey-{Guid.NewGuid():N}");

    private readonly Dictionary<string, string?> _originalEnvironment = new();

    public OsKeychainSecretStoreTests()
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

    [Fact]
    public async Task SaveAndGet_RoundTripThroughOsStore()
    {

        InMemoryOsCredentialStore os = new();

        using OsKeychainSecretStore store = CreateStore(os);

        await store.SaveApiKeyAsync("round-trip-key");

        string? key = await store.GetApiKeyAsync();

        Assert.Equal("round-trip-key", key);

        OsCredentialStoreResult direct = os.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount);

        Assert.Equal("round-trip-key", direct.Value);

    }

    [Fact]
    public async Task Get_MigratesLegacySecurityDatIntoOsStore()
    {

        InMemoryOsCredentialStore os = new();

        using DataProtectionSecretStore legacy = CreateDataProtectionStore();

        await legacy.SaveApiKeyAsync("legacy-dp-key");

        using OsKeychainSecretStore store = CreateStore(os, legacy);

        string? key = await store.GetApiKeyAsync();

        Assert.Equal("legacy-dp-key", key);

        OsCredentialStoreResult migrated = os.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount);

        Assert.Equal(OsCredentialStoreStatus.Ok, migrated.Status);

        Assert.Equal("legacy-dp-key", migrated.Value);

    }

    [Fact]
    public async Task Get_FallsBackToSecurityDatWhenOsUnavailable()
    {

        UnavailableStore os = new();

        using DataProtectionSecretStore legacy = CreateDataProtectionStore();

        await legacy.SaveApiKeyAsync("fallback-key");

        using OsKeychainSecretStore store = CreateStore(os, legacy);

        string? key = await store.GetApiKeyAsync();

        Assert.Equal("fallback-key", key);

    }

    private OsKeychainSecretStore CreateStore(IOsCredentialStore os, DataProtectionSecretStore? legacy = null)
    {

        DataProtectionSecretStore dp = legacy ?? CreateDataProtectionStore();

        return new OsKeychainSecretStore(
            os,
            dp,
            new ApiKeyDigestCache(new FakeTimeProvider()),
            NullLogger<OsKeychainSecretStore>.Instance);

    }

    private DataProtectionSecretStore CreateDataProtectionStore()
    {

        IDataProtectionProvider dataProtectionProvider = DataProtectionProvider.Create(
            new DirectoryInfo(_storeDir),
            _ => { });

        return new DataProtectionSecretStore(dataProtectionProvider, new ApiKeyDigestCache(new FakeTimeProvider()));

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

    private void SetEnvironment(string name, string value)
    {

        _originalEnvironment[name] = global::System.Environment.GetEnvironmentVariable(name);

        global::System.Environment.SetEnvironmentVariable(name, value);

    }

    private sealed class UnavailableStore : IOsCredentialStore
    {

        public bool IsAvailable => false;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            OsCredentialStoreResult.Unavailable("test unavailable");

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            OsCredentialStoreResult.Unavailable("test unavailable");

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Unavailable("test unavailable");

    }

}
