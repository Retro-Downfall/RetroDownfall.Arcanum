using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
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
    public async Task Get_RepairsMissingMirrorWithoutInvalidatingTheCanonicalDigest()
    {
        const string apiKey = "canonical-key-with-missing-mirror";

        InMemoryOsCredentialStore os = new();

        _ = os.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            apiKey);

        ApiKeyDigestCache digestCache = new(new FakeTimeProvider());

        byte[] expectedDigest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(apiKey));

        digestCache.StoreDigest(expectedDigest, ttlSeconds: 600);

        using DataProtectionSecretStore mirror = CreateDataProtectionStore(digestCache);

        using OsKeychainSecretStore store = CreateStore(
            os,
            mirror,
            digestCache);

        SecretStoreReadResult read = await store.GetApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Ok, read.Status);
        Assert.Equal(apiKey, read.Value);
        Assert.Equal(apiKey, (await mirror.GetApiKeyReadResultAsync()).Value);
        Assert.True(digestCache.TryGetDigest(out byte[]? retainedDigest));
        Assert.Equal(expectedDigest, retainedDigest);
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

    /// <summary>
    /// The permissive startup read (DESIGN §11.2 item 4) serves the mirror when the OS read fails —
    /// but only a mirror that is known current. A rotation whose OS write succeeded and whose mirror
    /// write failed leaves the superseded key in the mirror; serving it at a locked-keychain boot
    /// would make the revoked key the active master key.
    /// </summary>
    [Fact]
    public async Task Get_does_not_serve_a_mirror_that_a_failed_mirror_write_left_stale()
    {
        SwitchableReadStore os = new();

        WriteFailingProtectionProvider protection = new(
            DataProtectionProvider.Create(new DirectoryInfo(_storeDir), _ => { }));

        using (OsKeychainSecretStore store = CreateStore(os, CreateDataProtectionStore(protection)))
        {
            await store.SaveApiKeyAsync("superseded-key");

            protection.FailProtect = true;

            await store.SaveApiKeyAsync("current-key");

            protection.FailProtect = false;
        }

        // The next boot finds the keychain locked.
        os.FailReads = true;

        using OsKeychainSecretStore rebooted = CreateStore(os, CreateDataProtectionStore(protection));

        SecretStoreReadResult result = await rebooted.GetApiKeyReadResultAsync();

        Assert.NotEqual(SecretStoreReadStatus.Ok, result.Status);

        Assert.Null(result.Value);

        Assert.Equal(
            SecretStoreReadStatus.Corrupted,
            (await rebooted.PeekApiKeyReadResultAsync()).Status);
    }

    /// <summary>
    /// The backup snapshot reads the mirrors without healing them, but it answers from a mirror over a
    /// failed OS read just as the permissive read does, so the stale marker binds it too. Exporting a
    /// marked mirror would put the superseded (possibly revoked) master key, or a key ring without its
    /// active key, into the archive, and restoring that archive would reinstate it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Backup_snapshot_does_not_export_a_mirror_that_a_failed_mirror_write_left_stale(
        bool masterApiKey)
    {
        SwitchableReadStore os = new();

        WriteFailingProtectionProvider protection = new(
            DataProtectionProvider.Create(new DirectoryInfo(_storeDir), _ => { }));

        Func<OsKeychainSecretStore, string, Task> save = masterApiKey
            ? static (store, value) => store.SaveApiKeyAsync(value)
            : static (store, value) => store.SaveFileEncryptionSecretAsync(value);

        using (OsKeychainSecretStore store = CreateStore(os, CreateDataProtectionStore(protection)))
        {
            await save(store, "superseded-secret");

            protection.FailProtect = true;

            await save(store, "current-secret");

            protection.FailProtect = false;
        }

        // The keychain is locked when the backup runs.
        os.FailReads = true;

        using DataProtectionSecretStore mirrors = CreateDataProtectionStore(protection);

        SecretStoreReadResult mirror = masterApiKey
            ? await mirrors.GetApiKeyReadResultAsync()
            : await mirrors.GetFileEncryptionSecretReadResultAsync();

        // The superseded value still decrypts; only the marker says it must not be used.
        Assert.Equal("superseded-secret", mirror.Value);

        BackupSecretSnapshotReader reader = new(os, mirrors);

        SecretStoreReadResult result = masterApiKey
            ? await reader.ReadMasterApiKeyAsync()
            : await reader.ReadFileEncryptionKeysAsync();

        Assert.Equal(SecretStoreReadStatus.Corrupted, result.Status);

        Assert.Null(result.Value);
    }

    [Fact]
    public async Task A_resynchronized_mirror_is_served_again_after_a_stale_marker()
    {
        SwitchableReadStore os = new();

        WriteFailingProtectionProvider protection = new(
            DataProtectionProvider.Create(new DirectoryInfo(_storeDir), _ => { }));

        using OsKeychainSecretStore store = CreateStore(os, CreateDataProtectionStore(protection));

        await store.SaveApiKeyAsync("superseded-key");

        protection.FailProtect = true;

        await store.SaveApiKeyAsync("current-key");

        protection.FailProtect = false;

        // An ordinary OS-served read re-synchronizes the mirror, which makes it current again.
        Assert.Equal("current-key", (await store.GetApiKeyReadResultAsync()).Value);

        os.FailReads = true;

        using OsKeychainSecretStore rebooted = CreateStore(os, CreateDataProtectionStore(protection));

        SecretStoreReadResult result = await rebooted.GetApiKeyReadResultAsync();

        Assert.Equal(SecretStoreReadStatus.Ok, result.Status);

        Assert.Equal("current-key", result.Value);
    }

    /// <summary>
    /// A Keychain dialog nobody dismisses parks the OS call. The read must be bounded, a second
    /// caller must fail closed within the timeout instead of queueing behind the stuck call, and no
    /// second OS read may be stacked on the first (each would raise another prompt).
    /// </summary>
    [Fact]
    public async Task Peek_does_not_hold_the_gate_past_a_read_timeout()
    {
        using BlockingReadStore os = new();

        TimeSpan readTimeout = TimeSpan.FromMilliseconds(250);

        using OsKeychainSecretStore store = new(
            os,
            CreateDataProtectionStore(),
            new ApiKeyDigestCache(new FakeTimeProvider()),
            NullLogger<OsKeychainSecretStore>.Instance,
            readTimeout);

        Task<SecretStoreReadResult> first = Task.Run(() => store.PeekApiKeyReadResultAsync());

        try
        {
            Assert.True(os.Entered.Wait(TimeSpan.FromSeconds(10)));

            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();

            SecretStoreReadResult second = await store
                .PeekApiKeyReadResultAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));

            // Bounded by one read timeout (plus scheduling slack), not by the parked call.
            Assert.True(elapsed.Elapsed < readTimeout + TimeSpan.FromSeconds(5), elapsed.Elapsed.ToString());

            Assert.Equal(SecretStoreReadStatus.Corrupted, second.Status);

            Assert.Null(second.Value);

            Assert.Equal(
                SecretStoreReadStatus.Corrupted,
                (await first.WaitAsync(TimeSpan.FromSeconds(30))).Status);

            Assert.Equal(1, os.TryGetCallCount);
        }
        finally
        {
            os.Release();
        }
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

    private DataProtectionSecretStore CreateDataProtectionStore(
        IApiKeyDigestCache? apiKeyDigestCache = null)
    {
        IDataProtectionProvider dataProtectionProvider = DataProtectionProvider.Create(
            new DirectoryInfo(_storeDir),
            _ => { });

        return new DataProtectionSecretStore(
            dataProtectionProvider,
            apiKeyDigestCache ?? new ApiKeyDigestCache(new FakeTimeProvider()));
    }

    private static DataProtectionSecretStore CreateDataProtectionStore(
        IDataProtectionProvider dataProtectionProvider) =>
        new(dataProtectionProvider, new ApiKeyDigestCache(new FakeTimeProvider()));

    /// <summary>
    /// The OS store an installation keeps across restarts, whose reads can be made to fail the way a
    /// locked keychain's do while writes keep working.
    /// </summary>
    private sealed class SwitchableReadStore : IOsCredentialStore
    {
        private readonly InMemoryOsCredentialStore _inner = new();

        public bool FailReads { get; set; }

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            FailReads
                ? OsCredentialStoreResult.Failed("test: the keychain is locked")
                : _inner.TryGet(service, account);

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            _inner.Set(service, account, secret);

        public OsCredentialStoreResult Delete(string service, string account) =>
            _inner.Delete(service, account);
    }

    /// <summary>
    /// A real Data Protection provider whose protectors can be made to refuse new ciphertext — the
    /// mirror write fails — while existing ciphertext keeps decrypting.
    /// </summary>
    private sealed class WriteFailingProtectionProvider(IDataProtectionProvider inner) : IDataProtectionProvider
    {
        public bool FailProtect { get; set; }

        public IDataProtector CreateProtector(string purpose) =>
            new Protector(this, inner.CreateProtector(purpose));

        private sealed class Protector(
            WriteFailingProtectionProvider owner,
            IDataProtector inner) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) =>
                new Protector(owner, inner.CreateProtector(purpose));

            public byte[] Protect(byte[] plaintext) =>
                owner.FailProtect
                    ? throw new System.Security.Cryptography.CryptographicException("test: protect refused")
                    : inner.Protect(plaintext);

            public byte[] Unprotect(byte[] protectedData) => inner.Unprotect(protectedData);
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

    /// <summary>
    /// A reachable backend whose read blocks — the Keychain confidential-information dialog that
    /// nobody answers — until the test releases it.
    /// </summary>
    private sealed class BlockingReadStore : IOsCredentialStore, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);

        private readonly CountdownEvent _readsInside = new(1);

        private int _tryGetCallCount;

        public ManualResetEventSlim Entered { get; } = new(false);

        public int TryGetCallCount => Volatile.Read(ref _tryGetCallCount);

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            _readsInside.AddCount();

            try
            {
                _ = Interlocked.Increment(ref _tryGetCallCount);

                Entered.Set();

                _ = _release.Wait(TimeSpan.FromSeconds(60));

                return OsCredentialStoreResult.Ok("released-key");
            }
            finally
            {
                _ = _readsInside.Signal();
            }
        }

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            OsCredentialStoreResult.Ok(secret);

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Ok(string.Empty);

        public void Release() => _release.Set();

        /// <summary>
        /// Releases any parked read and waits for it to leave before disposing the events it uses: a
        /// timed-out read is abandoned by the store, not cancelled, so it is still inside here.
        /// </summary>
        public void Dispose()
        {
            _release.Set();

            _ = _readsInside.Signal();

            _ = _readsInside.Wait(TimeSpan.FromSeconds(10));

            _readsInside.Dispose();

            _release.Dispose();

            Entered.Dispose();
        }
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
