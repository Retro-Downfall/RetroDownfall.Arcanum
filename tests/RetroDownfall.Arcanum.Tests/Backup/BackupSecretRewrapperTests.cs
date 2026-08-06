using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Backup;

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

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret)
        {

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
