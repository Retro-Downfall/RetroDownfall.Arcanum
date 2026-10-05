using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class BackupSecretRewrapperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-secret-rewrap-" + Guid.NewGuid().ToString("N"));

    public BackupSecretRewrapperTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Portable_material_is_rewrapped_into_local_protection_without_the_source_credential_store()
    {
        byte[] first = RandomNumberGenerator.GetBytes(32);

        byte[] second = RandomNumberGenerator.GetBytes(32);

        string path = WriteRecovery(
            "grimoire-secret",
            activeKeyId: KeyId(first),
            keys: [(KeyId(first), first), (KeyId(second), second)],
            masterApiKey: "master-key");

        RecordingSecretStore store = new();

        BackupSecretRewrapResult result = await new BackupSecretRewrapper(store)
            .RewrapAsync(path, restoreMasterApiKey: false, CancellationToken.None);

        Assert.Empty(result.Issues);

        Assert.True(result.GrimoireSecretWritten);

        Assert.Equal(2, result.FileEncryptionKeysWritten);

        Assert.False(result.MasterApiKeyWritten);

        Assert.Equal("grimoire-secret", store.GrimoireSecret);

        Assert.Null(store.ApiKey);

        string ring = Assert.IsType<string>(store.FileEncryptionSecret);

        Assert.StartsWith("ARCANUM-KEYRING-1\n", ring, StringComparison.Ordinal);

        Assert.Contains($"active={KeyId(first)}", ring, StringComparison.Ordinal);

        Assert.Contains($"{KeyId(second)}={Convert.ToBase64String(second)}", ring, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_master_api_key_is_restored_only_when_explicitly_requested()
    {
        string path = WriteRecovery(
            "grimoire-secret",
            activeKeyId: null,
            keys: [],
            masterApiKey: "master-key");

        RecordingSecretStore withoutRequest = new();

        BackupSecretRewrapResult skipped = await new BackupSecretRewrapper(withoutRequest)
            .RewrapAsync(path, restoreMasterApiKey: false, CancellationToken.None);

        Assert.False(skipped.MasterApiKeyWritten);

        Assert.Null(withoutRequest.ApiKey);

        RecordingSecretStore withRequest = new();

        BackupSecretRewrapResult restored = await new BackupSecretRewrapper(withRequest)
            .RewrapAsync(path, restoreMasterApiKey: true, CancellationToken.None);

        Assert.True(restored.MasterApiKeyWritten);

        Assert.Equal("master-key", withRequest.ApiKey);
    }

    [Fact]
    public async Task Requesting_an_absent_master_api_key_is_reported_rather_than_silently_skipped()
    {
        string path = WriteRecovery(
            "grimoire-secret",
            activeKeyId: null,
            keys: [],
            masterApiKey: null);

        RecordingSecretStore store = new();

        BackupSecretRewrapResult result = await new BackupSecretRewrapper(store)
            .RewrapAsync(path, restoreMasterApiKey: true, CancellationToken.None);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.restore_master_api_key_absent");

        Assert.False(result.MasterApiKeyWritten);
    }

    [Fact]
    public async Task Missing_recovery_material_is_a_typed_refusal()
    {
        BackupSecretRewrapResult result = await new BackupSecretRewrapper(new RecordingSecretStore())
            .RewrapAsync(
                Path.Combine(_root, "absent.json"),
                restoreMasterApiKey: false,
                CancellationToken.None);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.restore_recovery_material_missing");
    }

    [Fact]
    public async Task Malformed_recovery_material_is_a_typed_refusal_and_writes_nothing()
    {
        string path = Path.Combine(_root, "malformed.json");

        await File.WriteAllTextAsync(path, "{ not json");

        RecordingSecretStore store = new();

        BackupSecretRewrapResult result = await new BackupSecretRewrapper(store)
            .RewrapAsync(path, restoreMasterApiKey: false, CancellationToken.None);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.restore_recovery_material_invalid");

        Assert.Null(store.GrimoireSecret);
    }

    [Fact]
    public async Task A_key_whose_id_does_not_match_its_bytes_is_refused_before_any_write()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);

        string path = WriteRecovery(
            "grimoire-secret",
            activeKeyId: "deadbeefdeadbeef",
            keys: [("deadbeefdeadbeef", key)],
            masterApiKey: null);

        RecordingSecretStore store = new();

        BackupSecretRewrapResult result = await new BackupSecretRewrapper(store)
            .RewrapAsync(path, restoreMasterApiKey: false, CancellationToken.None);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.restore_recovery_material_invalid");

        Assert.Null(store.GrimoireSecret);

        Assert.Null(store.FileEncryptionSecret);
    }

    [Fact]
    public async Task Recovery_material_without_file_keys_still_rewraps_the_grimoire_secret()
    {
        string path = WriteRecovery(
            "grimoire-secret",
            activeKeyId: null,
            keys: [],
            masterApiKey: null);

        RecordingSecretStore store = new();

        BackupSecretRewrapResult result = await new BackupSecretRewrapper(store)
            .RewrapAsync(path, restoreMasterApiKey: false, CancellationToken.None);

        Assert.Empty(result.Issues);

        Assert.Equal("grimoire-secret", store.GrimoireSecret);

        Assert.Null(store.FileEncryptionSecret);

        Assert.Equal(0, result.FileEncryptionKeysWritten);
    }

    /// <summary>
    /// A key ring an older Windows build persisted with <c>Environment.NewLine</c> still merges.
    /// </summary>
    /// <remarks>
    /// <c>FileEncryptionKeyProvider</c> and <c>BackupSecretSnapshotReader</c> both accept CRLF, so a
    /// machine whose ring carries it is running normally. Refusing that same ring here fails the
    /// import of portable recovery keys as <c>restore_recovery_material_invalid</c> — the operator is
    /// told their intact ring is corrupt on the one path that exists to repair a machine. The
    /// trailing '\r' has to come off every line, not just the header: base64 decoding skips it, so
    /// the keys would load while the active id carried an invisible '\r' and matched none of them.
    /// </remarks>
    [Fact]
    public async Task A_crlf_key_ring_left_by_an_older_windows_build_still_accepts_imported_keys()
    {
        byte[] existing = RandomNumberGenerator.GetBytes(32);

        byte[] imported = RandomNumberGenerator.GetBytes(32);

        string ring = "ARCANUM-KEYRING-1\r\n"
            + $"active={KeyId(existing)}\r\n"
            + $"{KeyId(existing)}={Convert.ToBase64String(existing)}\r\n";

        RecordingSecretStore store = new();

        store.Seed(ring);

        string path = WriteRecovery(
            "grimoire-secret",
            activeKeyId: KeyId(imported),
            keys: [(KeyId(imported), imported)],
            masterApiKey: null);

        BackupSecretRewrapResult result = await new BackupSecretRewrapper(store)
            .MergeFileEncryptionKeysAsync(path, CancellationToken.None);

        Assert.Empty(result.Issues);

        Assert.Equal(1, result.FileEncryptionKeysWritten);

        string merged = Assert.IsType<string>(store.FileEncryptionSecret);

        // Re-emitted in the one canonical LF form, carrying both the pre-existing and imported keys.
        Assert.StartsWith("ARCANUM-KEYRING-1\n", merged, StringComparison.Ordinal);

        Assert.DoesNotContain('\r', merged);

        Assert.Contains($"{KeyId(existing)}={Convert.ToBase64String(existing)}", merged, StringComparison.Ordinal);

        Assert.Contains($"{KeyId(imported)}={Convert.ToBase64String(imported)}", merged, StringComparison.Ordinal);

        // The active id survived the CRLF, so the ring still names a key it actually holds.
        Assert.Contains($"active={KeyId(existing)}\n", merged, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rollback returns a secret that did not exist before the restore to not existing, rather than
    /// leaving the archive's copy behind.
    /// </summary>
    /// <remarks>
    /// Reinstating only the values that were captured cannot undo a write over nothing: a machine with
    /// no master API key that adopted the archive's with <c>--restore-master-api-key</c> would keep
    /// authenticating callers with it after the restore reported a clean rollback. Absence is captured
    /// as a value of its own so the rollback can delete what the restore created.
    /// </remarks>
    [Fact]
    public async Task Restore_removes_secrets_that_were_absent_at_capture()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);

        string path = WriteRecovery(
            "grimoire-secret",
            activeKeyId: KeyId(key),
            keys: [(KeyId(key), key)],
            masterApiKey: "archived-master-key");

        RecordingSecretStore store = new();

        store.SeedGrimoireSecret("the prior grimoire secret");

        BackupSecretRewrapper rewrapper = new(store);

        BackupSecretSnapshot prior = await rewrapper.CaptureAsync();

        BackupSecretRewrapResult rewrap = await rewrapper.RewrapAsync(
            path,
            restoreMasterApiKey: true,
            CancellationToken.None);

        Assert.True(rewrap.MasterApiKeyWritten);

        Assert.Equal("archived-master-key", store.ApiKey);

        _ = await rewrapper.RestoreAsync(prior);

        Assert.Null(store.ApiKey);

        Assert.Null(store.FileEncryptionSecret);

        Assert.Equal("the prior grimoire secret", store.GrimoireSecret);
    }

    /// <summary>
    /// A rollback that cannot put the prior Grimoire secret back says so, rather than letting the
    /// restore report a clean rollback over a database the remaining secret no longer opens.
    /// </summary>
    [Fact]
    public async Task Restore_reports_failure_when_the_prior_grimoire_secret_cannot_be_reinstated()
    {
        string path = WriteRecovery(
            "archived-grimoire-secret",
            activeKeyId: null,
            keys: [],
            masterApiKey: null);

        RecordingSecretStore store = new();

        store.SeedGrimoireSecret("the prior grimoire secret");

        BackupSecretRewrapper rewrapper = new(store);

        BackupSecretSnapshot prior = await rewrapper.CaptureAsync();

        BackupSecretRewrapResult rewrap = await rewrapper.RewrapAsync(
            path,
            restoreMasterApiKey: false,
            CancellationToken.None);

        Assert.True(rewrap.GrimoireSecretWritten);

        store.FailGrimoireSecretWrites = true;

        Result restored = await rewrapper.RestoreAsync(prior);

        Assert.True(restored.IsFailure);

        Assert.Contains("Grimoire", restored.Error.Message, StringComparison.Ordinal);

        Assert.Contains(nameof(IOException), restored.Error.Message, StringComparison.Ordinal);

        Assert.Equal("archived-grimoire-secret", store.GrimoireSecret);
    }

    private static string KeyId(byte[] key) =>
        Convert.ToHexString(SHA256.HashData(key).AsSpan(0, 8)).ToLowerInvariant();

    private string WriteRecovery(
        string grimoireSecret,
        string? activeKeyId,
        (string KeyId, byte[] Key)[] keys,
        string? masterApiKey)
    {
        string path = Path.Combine(_root, "portable-keys-" + Guid.NewGuid().ToString("N") + ".json");

        using MemoryStream buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            writer.WriteNumber("version", 1);

            writer.WriteBase64String(
                "grimoireEncryptionSecretUtf8",
                Encoding.UTF8.GetBytes(grimoireSecret));

            if (activeKeyId is null)
            {
                writer.WriteNull("activeFileEncryptionKeyId");
            }
            else
            {
                writer.WriteString("activeFileEncryptionKeyId", activeKeyId);
            }

            writer.WriteStartArray("fileEncryptionKeys");

            foreach ((string keyId, byte[] key) in keys)
            {
                writer.WriteStartObject();

                writer.WriteString("keyId", keyId);

                writer.WriteBase64String("keyBytes", key);

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (masterApiKey is not null)
            {
                writer.WriteBase64String(
                    "masterApiKeyUtf8",
                    Encoding.UTF8.GetBytes(masterApiKey));
            }

            writer.WriteEndObject();
        }

        File.WriteAllBytes(path, buffer.ToArray());

        return path;
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        public string? ApiKey { get; private set; }

        public string? GrimoireSecret { get; private set; }

        public string? FileEncryptionSecret { get; private set; }

        /// <summary>
        /// Plants a pre-existing ring, so a merge has something to merge into.
        /// </summary>
        public void Seed(string encryptionSecret) => FileEncryptionSecret = encryptionSecret;

        /// <summary>Plants the destination's own Grimoire secret, so a capture has one to keep.</summary>
        public void SeedGrimoireSecret(string encryptionSecret) => GrimoireSecret = encryptionSecret;

        public Task DeleteApiKeyAsync()
        {
            ApiKey = null;

            return Task.CompletedTask;
        }

        public Task DeleteGrimoireEncryptionSecretAsync()
        {
            GrimoireSecret = null;

            return Task.CompletedTask;
        }

        public Task DeleteFileEncryptionSecretAsync()
        {
            FileEncryptionSecret = null;

            return Task.CompletedTask;
        }

        public Task<string?> GetApiKeyAsync() => Task.FromResult(ApiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(
                ApiKey is null
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(ApiKey));

        public Task SaveApiKeyAsync(string apiKey)
        {
            ApiKey = apiKey;

            return Task.CompletedTask;
        }

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult(GrimoireSecret);

        /// <summary>Makes every later Grimoire secret write fail the way an unwritable store does.</summary>
        public bool FailGrimoireSecretWrites { get; set; }

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret)
        {
            if (FailGrimoireSecretWrites)
            {
                throw new IOException("The Grimoire secret store is not writable.");
            }

            GrimoireSecret = encryptionSecret;

            return Task.CompletedTask;
        }

        public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync() =>
            Task.FromResult(
                FileEncryptionSecret is null
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(FileEncryptionSecret));

        public Task SaveFileEncryptionSecretAsync(string encryptionSecret)
        {
            FileEncryptionSecret = encryptionSecret;

            return Task.CompletedTask;
        }
    }
}
