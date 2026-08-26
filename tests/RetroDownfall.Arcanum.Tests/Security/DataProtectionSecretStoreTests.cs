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

    private DataProtectionSecretStore CreateStore() =>
        CreateStore(new ApiKeyDigestCache(new FakeTimeProvider()));

    private DataProtectionSecretStore CreateStore(IApiKeyDigestCache apiKeyDigestCache)
    {

        IDataProtectionProvider dataProtectionProvider = DataProtectionProvider.Create(
            new DirectoryInfo(_storeDir),
            _ => { });

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
    public async Task GetApiKeyReadResultAsync_OversizedFile_FailsClosed()
    {

        using DataProtectionSecretStore store = CreateStore();

        string path = ArcanumPaths.ApiKeyStoreFile;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {

            stream.SetLength(DataProtectionSecretStore.MaxProtectedSecretBytes + 1L);

        }

        SecretStoreReadResult result = await store.GetApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);

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
    public async Task SaveFileEncryptionSecretAsync_RoundTrip_UsesDedicatedProtectedFile()
    {
        using DataProtectionSecretStore store = CreateStore();
        string secret = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        await store.SaveFileEncryptionSecretAsync(secret);
        SecretStoreReadResult result = await store.GetFileEncryptionSecretReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Ok, result.Status);
        Assert.Equal(secret, result.Value);
        Assert.True(File.Exists(ArcanumPaths.FileEncryptionKeyStoreFile));
        Assert.NotEqual(
            ArcanumPaths.GrimoireKeyStoreFile,
            ArcanumPaths.FileEncryptionKeyStoreFile);
        Assert.NotEqual(
            ArcanumPaths.ApiKeyStoreFile,
            ArcanumPaths.FileEncryptionKeyStoreFile);
    }

    [Fact]
    public async Task GetFileEncryptionSecretReadResultAsync_CorruptFile_FailsClosedWithRecovery()
    {
        using DataProtectionSecretStore store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(ArcanumPaths.FileEncryptionKeyStoreFile)!);
        await File.WriteAllBytesAsync(ArcanumPaths.FileEncryptionKeyStoreFile, [1, 2, 3]);

        SecretStoreReadResult result = await store.GetFileEncryptionSecretReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);
        Assert.Contains("file-encryption-key.dat", result.Message, StringComparison.Ordinal);
        Assert.Contains("restore", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetApiKeyAsync_CorruptStore_ReturnsNullRatherThanRawCiphertext()
    {

        using DataProtectionSecretStore store = CreateStore();

        string path = ArcanumPaths.ApiKeyStoreFile;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllBytesAsync(path, [9, 8, 7, 6, 5]);

        string? result = await store.GetApiKeyAsync();

        Assert.Null(result);

        Assert.Equal(SecretStoreReadStatus.Corrupted, (await store.GetApiKeyReadResultAsync()).Status);

    }

    [Fact]
    public async Task GetApiKeyAsync_MissingStore_ReturnsNull()
    {

        using DataProtectionSecretStore store = CreateStore();

        string? result = await store.GetApiKeyAsync();

        Assert.Null(result);

    }

    [Fact]
    public async Task GetGrimoireEncryptionSecretAsync_CorruptStore_ReturnsNullRatherThanRawCiphertext()
    {

        using DataProtectionSecretStore store = CreateStore();

        string path = ArcanumPaths.GrimoireKeyStoreFile;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllBytesAsync(path, [4, 3, 2, 1]);

        string? result = await store.GetGrimoireEncryptionSecretAsync();

        Assert.Null(result);

        Assert.Equal(
            SecretStoreReadStatus.Corrupted,
            (await store.GetGrimoireEncryptionSecretReadResultAsync()).Status);

    }

    [Fact]
    public async Task GetGrimoireEncryptionSecretAsync_MissingStore_ReturnsNull()
    {

        using DataProtectionSecretStore store = CreateStore();

        string? result = await store.GetGrimoireEncryptionSecretAsync();

        Assert.Null(result);

    }

    [Fact]
    public async Task GetApiKeyReadResultAsync_ZeroLengthFile_FailsClosedAsCorrupted()
    {

        using DataProtectionSecretStore store = CreateStore();

        string path = ArcanumPaths.ApiKeyStoreFile;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllBytesAsync(path, []);

        SecretStoreReadResult result = await store.GetApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);

        Assert.Contains("security.dat", result.Message, StringComparison.Ordinal);

        Assert.Null(result.Value);

        Assert.Null(await store.GetApiKeyAsync());

    }

    [Fact]
    public async Task GetGrimoireEncryptionSecretReadResultAsync_ZeroLengthFile_FailsClosedAsCorrupted()
    {

        using DataProtectionSecretStore store = CreateStore();

        string path = ArcanumPaths.GrimoireKeyStoreFile;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllBytesAsync(path, []);

        SecretStoreReadResult result = await store.GetGrimoireEncryptionSecretReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);

        Assert.Null(result.Value);

    }

    [Fact]
    public async Task WriteProtectedAsync_PathWithNoParentDirectory_RefusesToWrite()
    {

        IDataProtector protector = DataProtectionProvider
            .Create(new DirectoryInfo(_storeDir), _ => { })
            .CreateProtector("Arcanum.Tests.RootPathGuard");

        string rootPath = Path.GetPathRoot(Path.GetFullPath(_storeDir))!;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DataProtectionSecretStore.WriteProtectedForTestsAsync(rootPath, "secret", protector));

        Assert.Contains("Invalid secret store path", exception.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task SaveApiKeyAsync_WhenAtomicReplaceFails_LeavesNoStagedCiphertextBehind()
    {

        using DataProtectionSecretStore store = CreateStore();

        string path = ArcanumPaths.ApiKeyStoreFile;

        // A directory squatting on the store path lets the temp file be staged and written, then
        // fails the atomic replace, which is the only way the cleanup arm of the finally runs.
        Directory.CreateDirectory(path);

        await FilesystemRefusal.ThrowsAsync(() => store.SaveApiKeyAsync("super-secret-key"));

        string[] leftovers = Directory.GetFiles(
            ArcanumPaths.SecretStoreDirectory,
            "security.dat.tmp.*");

        Assert.Empty(leftovers);

        Assert.True(Directory.Exists(path));

    }

    // Rotation must drop the cached digest of the retired key. Without the invalidation the
    // ApiKeyEndpointFilter keeps authenticating the OLD key for the remainder of the cache TTL.
    [Fact]
    public async Task SaveApiKeyAsync_InvalidatesDigestCache()
    {

        ApiKeyDigestCache digestCache = new(new FakeTimeProvider());

        using DataProtectionSecretStore store = CreateStore(digestCache);

        digestCache.StoreDigest([1, 2, 3, 4], ttlSeconds: 600);

        Assert.True(digestCache.TryGetDigest(out _));

        await store.SaveApiKeyAsync(Guid.NewGuid().ToString("N"));

        Assert.False(digestCache.TryGetDigest(out byte[]? retiredDigest));

        Assert.Null(retiredDigest);

    }

    private void SetEnvironment(string name, string value)
    {

        _originalEnvironment[name] = global::System.Environment.GetEnvironmentVariable(name);

        global::System.Environment.SetEnvironmentVariable(name, value);

    }

}
