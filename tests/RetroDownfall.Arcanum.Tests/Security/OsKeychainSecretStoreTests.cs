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

    [Fact]
    public async Task Save_RemovesTheSupersededOsCredentialWhenTheOsWriteFails()
    {

        InMemoryOsCredentialStore backing = new();

        _ = backing.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            "old-key");

        WriteFailingStore os = new(backing);

        using OsKeychainSecretStore store = CreateStore(os);

        await store.SaveApiKeyAsync("new-key");

        Assert.Equal("new-key", await store.GetApiKeyAsync());

        Assert.Equal(
            OsCredentialStoreStatus.NotFound,
            backing.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.MasterApiKeyAccount).Status);

    }

    [Fact]
    public async Task Save_FailsWhenTheSupersededOsCredentialCannotBeRemoved()
    {

        InMemoryOsCredentialStore backing = new();

        _ = backing.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            "old-key");

        WriteFailingStore os = new(backing, deleteFails: true);

        using OsKeychainSecretStore store = CreateStore(os);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveApiKeyAsync("new-key"));

        Assert.Equal("old-key", await store.GetApiKeyAsync());

    }

    /// <summary>
    /// A read that failed says nothing about whether the credential exists, so it must never collapse
    /// to Missing — Missing is the one status that authorises minting a replacement over the live key.
    /// The file-encryption sibling already reports Corrupted for exactly this condition.
    /// </summary>
    [Fact]
    public async Task Get_ReportsCorruptWhenTheOsReadFailsWithNoLegacyMirror()
    {

        ReadFailingStore os = new();

        using OsKeychainSecretStore store = CreateStore(os);

        SecretStoreReadResult result = await store.GetApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);

        Assert.Contains("OS key storage failed", result.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Get_PrefersTheLegacyMirrorOverAFailedOsRead()
    {

        ReadFailingStore os = new();

        using DataProtectionSecretStore legacy = CreateDataProtectionStore();

        await legacy.SaveApiKeyAsync("mirrored-key");

        using OsKeychainSecretStore store = CreateStore(os, legacy);

        SecretStoreReadResult result = await store.GetApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Ok, result.Status);

        Assert.Equal("mirrored-key", result.Value);

    }

    [Fact]
    public async Task PeekApiKey_NotFoundOsCredential_ReturnsMirrorWithoutMigratingOrChangingFiles()
    {

        using DataProtectionSecretStore legacy = CreateDataProtectionStore();

        await legacy.SaveApiKeyAsync("peek-master-key");

        string[] before = SnapshotFileTree();

        RecordingOsCredentialStore os = new(OsCredentialStoreResult.NotFound());

        using OsKeychainSecretStore store = CreateStore(os, legacy);

        ISecretStore contract = store;

        SecretStoreReadResult first = await contract.PeekApiKeyReadResultAsync();

        SecretStoreReadResult second = await contract.PeekApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Ok, first.Status);

        Assert.Equal("peek-master-key", first.Value);

        Assert.Equal(first, second);

        Assert.Equal(0, os.SetCallCount);

        Assert.Equal(0, os.DeleteCallCount);

        Assert.Equal(before, SnapshotFileTree());

    }

    [Fact]
    public async Task PeekApiKey_FailedOsRead_RejectsAnOtherwiseValidMirrorWithoutMutation()
    {

        using DataProtectionSecretStore legacy = CreateDataProtectionStore();

        await legacy.SaveApiKeyAsync("possibly-superseded-key");

        string[] before = SnapshotFileTree();

        RecordingOsCredentialStore os = new(
            OsCredentialStoreResult.Failed("test ambiguous read"));

        using OsKeychainSecretStore store = CreateStore(os, legacy);

        SecretStoreReadResult result = await ((ISecretStore)store)
            .PeekApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);

        Assert.Null(result.Value);

        Assert.Equal(0, os.SetCallCount);

        Assert.Equal(0, os.DeleteCallCount);

        Assert.Equal(before, SnapshotFileTree());

    }

    [Fact]
    public async Task PeekApiKey_MissingAndCorruptMirrorsRemainPureAndFailClosed()
    {

        RecordingOsCredentialStore os = new(OsCredentialStoreResult.NotFound());

        using OsKeychainSecretStore store = CreateStore(os);

        string[] missingBefore = SnapshotFileTree();

        SecretStoreReadResult missing = await ((ISecretStore)store)
            .PeekApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Missing, missing.Status);

        Assert.Equal(missingBefore, SnapshotFileTree());

        Directory.CreateDirectory(Path.GetDirectoryName(ArcanumPaths.ApiKeyStoreFile)!);

        await File.WriteAllBytesAsync(ArcanumPaths.ApiKeyStoreFile, [1, 2, 3, 4]);

        string[] corruptBefore = SnapshotFileTree();

        SecretStoreReadResult corrupt = await ((ISecretStore)store)
            .PeekApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, corrupt.Status);

        Assert.Equal(corruptBefore, SnapshotFileTree());

        Assert.Equal(0, os.SetCallCount);

        Assert.Equal(0, os.DeleteCallCount);

    }

    [Fact]
    public async Task PeekApiKey_WhitespaceMirror_RemainsMissingLikeTheOrdinaryRead()
    {

        using DataProtectionSecretStore legacy = CreateDataProtectionStore();

        await legacy.SaveApiKeyAsync("   ");

        RecordingOsCredentialStore os = new(OsCredentialStoreResult.NotFound());

        using OsKeychainSecretStore store = CreateStore(os, legacy);

        SecretStoreReadResult result = await ((ISecretStore)store)
            .PeekApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Missing, result.Status);

        Assert.Equal(0, os.SetCallCount);

        Assert.Equal(0, os.DeleteCallCount);

    }

    [Fact]
    public async Task FileEncryptionSecret_ReportsCorruptWhenTheOsReadFails()
    {

        ReadFailingStore os = new();

        using OsKeychainSecretStore store = CreateStore(os);

        SecretStoreReadResult result = await store.GetFileEncryptionSecretReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);

    }

    [Fact]
    public async Task FileEncryptionSecret_RoundTripsThroughDedicatedOsCredential()
    {
        InMemoryOsCredentialStore os = new();
        using OsKeychainSecretStore store = CreateStore(os);
        string secret = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        await store.SaveFileEncryptionSecretAsync(secret);
        SecretStoreReadResult loaded = await store.GetFileEncryptionSecretReadResultAsync();
        OsCredentialStoreResult direct = os.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.FileEncryptionKeyAccount);

        Assert.Equal(SecretStoreReadStatus.Ok, loaded.Status);
        Assert.Equal(secret, loaded.Value);
        Assert.Equal(OsCredentialStoreStatus.Ok, direct.Status);
        Assert.Equal(secret, direct.Value);
    }

    [Fact]
    public async Task FileEncryptionSecret_MigratesDataProtectionMirrorIntoOsCredential()
    {
        InMemoryOsCredentialStore os = new();
        using DataProtectionSecretStore dataProtection = CreateDataProtectionStore();
        string secret = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await dataProtection.SaveFileEncryptionSecretAsync(secret);
        using OsKeychainSecretStore store = CreateStore(os, dataProtection);

        SecretStoreReadResult loaded = await store.GetFileEncryptionSecretReadResultAsync();
        OsCredentialStoreResult direct = os.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.FileEncryptionKeyAccount);

        Assert.Equal(secret, loaded.Value);
        Assert.Equal(secret, direct.Value);
    }

    [Fact]
    public async Task PeekFileEncryptionSecret_NotFoundOsCredential_ReturnsMirrorWithoutMigration()
    {

        using DataProtectionSecretStore dataProtection = CreateDataProtectionStore();

        string secret = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        await dataProtection.SaveFileEncryptionSecretAsync(secret);

        string[] before = SnapshotFileTree();

        RecordingOsCredentialStore os = new(OsCredentialStoreResult.NotFound());

        using OsKeychainSecretStore store = CreateStore(os, dataProtection);

        ISecretStore contract = store;

        SecretStoreReadResult first = await contract
            .PeekFileEncryptionSecretReadResultAsync();

        SecretStoreReadResult second = await contract
            .PeekFileEncryptionSecretReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Ok, first.Status);

        Assert.Equal(secret, first.Value);

        Assert.Equal(first, second);

        Assert.Equal(0, os.SetCallCount);

        Assert.Equal(0, os.DeleteCallCount);

        Assert.Equal(before, SnapshotFileTree());

    }

    [Fact]
    public async Task PeekFileEncryptionSecret_FailedOsRead_RejectsMirrorWithoutMutation()
    {

        using DataProtectionSecretStore dataProtection = CreateDataProtectionStore();

        await dataProtection.SaveFileEncryptionSecretAsync("possibly-superseded-file-key");

        string[] before = SnapshotFileTree();

        RecordingOsCredentialStore os = new(
            OsCredentialStoreResult.Failed("test ambiguous read"));

        using OsKeychainSecretStore store = CreateStore(os, dataProtection);

        SecretStoreReadResult result = await ((ISecretStore)store)
            .PeekFileEncryptionSecretReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);

        Assert.Null(result.Value);

        Assert.Equal(0, os.SetCallCount);

        Assert.Equal(0, os.DeleteCallCount);

        Assert.Equal(before, SnapshotFileTree());

    }

    [Fact]
    public async Task PeekFileEncryptionSecret_WhitespaceMirror_RemainsMissingLikeTheOrdinaryRead()
    {

        using DataProtectionSecretStore dataProtection = CreateDataProtectionStore();

        await dataProtection.SaveFileEncryptionSecretAsync("   ");

        RecordingOsCredentialStore os = new(OsCredentialStoreResult.NotFound());

        using OsKeychainSecretStore store = CreateStore(os, dataProtection);

        SecretStoreReadResult result = await ((ISecretStore)store)
            .PeekFileEncryptionSecretReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Missing, result.Status);

        Assert.Equal(0, os.SetCallCount);

        Assert.Equal(0, os.DeleteCallCount);

    }

    // The keychain write owns its own invalidation: the security.dat mirror is best-effort and its
    // failure is swallowed, so a rotation that only reached the OS store must still retire the
    // cached digest of the old key. The store under test gets a digest cache of its own so the
    // mirror's invalidation cannot stand in for the one being asserted.
    [Fact]
    public async Task SaveApiKeyAsync_OsStoreAccepts_InvalidatesDigestCache()
    {

        ApiKeyDigestCache digestCache = new(new FakeTimeProvider());

        InMemoryOsCredentialStore os = new();

        using OsKeychainSecretStore store = CreateStore(os, apiKeyDigestCache: digestCache);

        digestCache.StoreDigest([1, 2, 3, 4], ttlSeconds: 600);

        await store.SaveApiKeyAsync("rotated-key");

        Assert.False(digestCache.TryGetDigest(out byte[]? retiredDigest));

        Assert.Null(retiredDigest);

    }

    [Fact]
    public async Task SaveApiKeyAsync_OsStoreUnavailable_StillInvalidatesDigestCache()
    {

        ApiKeyDigestCache digestCache = new(new FakeTimeProvider());

        UnavailableStore os = new();

        using OsKeychainSecretStore store = CreateStore(os, apiKeyDigestCache: digestCache);

        digestCache.StoreDigest([1, 2, 3, 4], ttlSeconds: 600);

        await store.SaveApiKeyAsync("rotated-key");

        Assert.False(digestCache.TryGetDigest(out byte[]? retiredDigest));

        Assert.Null(retiredDigest);

    }

    private OsKeychainSecretStore CreateStore(
        IOsCredentialStore os,
        DataProtectionSecretStore? legacy = null,
        IApiKeyDigestCache? apiKeyDigestCache = null)
    {

        DataProtectionSecretStore dp = legacy ?? CreateDataProtectionStore();

        return new OsKeychainSecretStore(
            os,
            dp,
            apiKeyDigestCache ?? new ApiKeyDigestCache(new FakeTimeProvider()),
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

    private string[] SnapshotFileTree() => Directory
        .EnumerateFiles(_storeDir, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path =>
            Path.GetRelativePath(_storeDir, path)
            + "|"
            + File.GetLastWriteTimeUtc(path).Ticks
            + "|"
            + Convert.ToBase64String(File.ReadAllBytes(path)))
        .ToArray();

    private sealed class RecordingOsCredentialStore(OsCredentialStoreResult readResult)
        : IOsCredentialStore
    {

        public bool IsAvailable => readResult.Status != OsCredentialStoreStatus.Unavailable;

        public int SetCallCount { get; private set; }

        public int DeleteCallCount { get; private set; }

        public OsCredentialStoreResult TryGet(string service, string account) => readResult;

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {

            SetCallCount++;

            return OsCredentialStoreResult.Ok(secret);

        }

        public OsCredentialStoreResult Delete(string service, string account)
        {

            DeleteCallCount++;

            return OsCredentialStoreResult.Ok(string.Empty);

        }

    }

    /// <summary>
    /// A reachable OS credential backend that refuses writes (locked keychain, transient Secret
    /// Service error) while reads and deletes still work.
    /// </summary>
    private sealed class WriteFailingStore(IOsCredentialStore inner, bool deleteFails = false)
        : IOsCredentialStore
    {

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            inner.TryGet(service, account);

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            OsCredentialStoreResult.Failed("test write failure");

        public OsCredentialStoreResult Delete(string service, string account) =>
            deleteFails
                ? OsCredentialStoreResult.Failed("test delete failure")
                : inner.Delete(service, account);

    }

    /// <summary>
    /// A reachable OS credential backend whose reads fail (locked macOS keychain, ACL denial after a
    /// resign, a transient CredReadW or libsecret error). Distinct from <see cref="UnavailableStore"/>:
    /// the backend is present, so a failed read leaves the credential's existence unknown.
    /// </summary>
    private sealed class ReadFailingStore : IOsCredentialStore
    {

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            OsCredentialStoreResult.Failed("test read failure");

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            OsCredentialStoreResult.Ok(secret);

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Ok(string.Empty);

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
