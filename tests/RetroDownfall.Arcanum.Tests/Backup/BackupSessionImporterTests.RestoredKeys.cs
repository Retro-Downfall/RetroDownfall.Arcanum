using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupSessionImporterTests
{
    [Fact]
    public async Task Protected_import_authenticates_newly_restored_keys_after_the_destination_ring_was_cached()
    {
        string sourceSecret = await SeedFinalizedSourceAsync();

        await EncryptArchivedAttachmentAsync(sourceSecret);

        string destinationSecret = await SeedDestinationAsync();

        byte[] existingKey = Enumerable.Repeat((byte)99, 32).ToArray();

        byte[] importedKey = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

        string existingId = Convert.ToHexString(SHA256.HashData(existingKey).AsSpan(0, 8)).ToLowerInvariant();

        string importedId = Convert.ToHexString(SHA256.HashData(importedKey).AsSpan(0, 8)).ToLowerInvariant();

        TestApiKeySecretStore secrets = new("unused");

        await secrets.SaveFileEncryptionSecretAsync(Convert.ToBase64String(existingKey));

        using FileEncryptionKeyProvider cachedKeys = new(secrets);

        Assert.Equal(existingId, (await cachedKeys.GetForReadAsync(existingId)).KeyId);

        EncryptedBlobStore destinationBlobs = new(cachedKeys);

        string recoveryPath = Path.Combine(_root, "restored-file-keys.json");

        using (MemoryStream buffer = new())
        {
            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartObject();

                writer.WriteNumber("version", 1);

                writer.WriteBase64String("grimoireEncryptionSecretUtf8", Encoding.UTF8.GetBytes(sourceSecret));

                writer.WriteString("activeFileEncryptionKeyId", importedId);

                writer.WriteStartArray("fileEncryptionKeys");

                writer.WriteStartObject();

                writer.WriteString("keyId", importedId);

                writer.WriteBase64String("keyBytes", importedKey);

                writer.WriteEndObject();

                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            await File.WriteAllBytesAsync(recoveryPath, buffer.ToArray());
        }

        BackupSecretRewrapResult merged = await new BackupSecretRewrapper(secrets, cachedKeys)
            .MergeFileEncryptionKeysAsync(recoveryPath, CancellationToken.None);

        Assert.Empty(merged.Issues);

        Assert.Equal(1, merged.FileEncryptionKeysWritten);

        BackupSessionImportResult result = await ImportProtectedFixtureAsync(sourceSecret, destinationSecret,
            store: new ProtectedArtifactTransferStore(CovenantSqliteConnectionInitializer.Instance,
                TimeProvider.System, destinationBlobs));

        Assert.Empty(result.Issues);

        BackupImportedSession committed = Assert.Single(result.Committed);

        string relative = await ReadAttachmentRelativePathAsync(destinationSecret, committed.DestinationSessionId);

        await using Stream plaintext = await destinationBlobs.OpenReadAsync(
            Path.Combine(_destinationRoot, "attachments", relative), EncryptedBlobPurpose.SessionAttachment);

        using StreamReader reader = new(plaintext);

        Assert.Equal("attachment bytes", await reader.ReadToEndAsync());

        Assert.Equal(1, result.Attachments);

        Assert.Equal(existingId, (await cachedKeys.GetForWriteAsync()).KeyId);
    }
}
