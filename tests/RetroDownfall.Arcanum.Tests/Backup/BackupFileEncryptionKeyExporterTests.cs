using System.Security.Cryptography;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class BackupFileEncryptionKeyExporterTests
{

    [Fact]
    public void Export_includes_only_referenced_keys_and_the_active_key_when_requested()
    {

        byte[] active = Enumerable.Repeat((byte)0x11, 32).ToArray();

        byte[] required = Enumerable.Repeat((byte)0x22, 32).ToArray();

        byte[] unrelated = Enumerable.Repeat((byte)0x33, 32).ToArray();

        string activeId = ComputeKeyId(active);

        string requiredId = ComputeKeyId(required);

        string unrelatedId = ComputeKeyId(unrelated);

        string encoded = EncodeRing(
            activeId,
            (unrelatedId, unrelated),
            (requiredId, required),
            (activeId, active));

        BackupRecoveryKeySnapshot snapshot = BackupFileEncryptionKeyExporter.Export(
            encoded,
            new HashSet<string>([requiredId], StringComparer.Ordinal),
            includeActiveKey: true);

        byte[][] exportedBuffers = snapshot.Keys
            .Select(static key => key.KeyBytes)
            .ToArray();

        Assert.Equal(activeId, snapshot.ActiveKeyId);

        Assert.Equal(
            new[] { activeId, requiredId }.Order(StringComparer.Ordinal),
            snapshot.Keys.Select(static key => key.KeyId));

        Assert.DoesNotContain(snapshot.Keys, key => key.KeyId == unrelatedId);

        Assert.Equal(
            required,
            Assert.Single(snapshot.Keys, key => key.KeyId == requiredId).KeyBytes);

        snapshot.Dispose();

        Assert.All(
            exportedBuffers,
            buffer => Assert.All(buffer, value => Assert.Equal((byte)0, value)));

    }

    [Fact]
    public void Export_without_active_key_exports_only_referenced_keys()
    {

        byte[] active = Enumerable.Repeat((byte)0x44, 32).ToArray();

        byte[] required = Enumerable.Repeat((byte)0x55, 32).ToArray();

        string activeId = ComputeKeyId(active);

        string requiredId = ComputeKeyId(required);

        string encoded = EncodeRing(
            activeId,
            (activeId, active),
            (requiredId, required));

        using BackupRecoveryKeySnapshot snapshot = BackupFileEncryptionKeyExporter.Export(
            encoded,
            new HashSet<string>([requiredId], StringComparer.Ordinal),
            includeActiveKey: false);

        PortableBackupFileKey exported = Assert.Single(snapshot.Keys);

        Assert.Equal(requiredId, exported.KeyId);

        Assert.Equal(requiredId, snapshot.ActiveKeyId);

    }

    [Fact]
    public void Export_rejects_a_missing_referenced_key()
    {

        byte[] active = Enumerable.Repeat((byte)0x66, 32).ToArray();

        string activeId = ComputeKeyId(active);

        string encoded = EncodeRing(activeId, (activeId, active));

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => BackupFileEncryptionKeyExporter.Export(
                encoded,
                new HashSet<string>(["missing-key"], StringComparer.Ordinal),
                includeActiveKey: false));

        Assert.Contains("missing-key", error.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// A ring persisted by a pre-LF Windows build is CRLF-delimited, and
    /// <c>FileEncryptionKeyProvider</c> deliberately still loads it — so the installation's encrypted
    /// blobs keep working while every backup of it would refuse. The exporter has to hold exactly the
    /// same tolerance, or `arcanum backup create` reports an intact key ring as malformed and
    /// produces no archive at all.
    /// </summary>
    [Fact]
    public void Export_accepts_a_carriage_return_delimited_ring_the_runtime_still_loads()
    {

        byte[] active = Enumerable.Repeat((byte)0x77, 32).ToArray();

        byte[] required = Enumerable.Repeat((byte)0x88, 32).ToArray();

        string activeId = ComputeKeyId(active);

        string requiredId = ComputeKeyId(required);

        string encoded = EncodeRing(activeId, (activeId, active), (requiredId, required))
            .Replace("\n", "\r\n", StringComparison.Ordinal);

        using BackupRecoveryKeySnapshot snapshot = BackupFileEncryptionKeyExporter.Export(
            encoded,
            new HashSet<string>([requiredId], StringComparer.Ordinal),
            includeActiveKey: true);

        Assert.Equal(activeId, snapshot.ActiveKeyId);

        Assert.Equal(
            new[] { activeId, requiredId }.Order(StringComparer.Ordinal),
            snapshot.Keys.Select(static key => key.KeyId));

        Assert.Equal(
            required,
            Assert.Single(snapshot.Keys, key => key.KeyId == requiredId).KeyBytes);

    }

    private static string EncodeRing(
        string activeKeyId,
        params (string Id, byte[] Key)[] keys) =>
        string.Join(
            '\n',
            new[]
            {
                "ARCANUM-KEYRING-1",
                "active=" + activeKeyId,
            }.Concat(
                keys.Select(
                    static key => key.Id + "=" + Convert.ToBase64String(key.Key))));

    private static string ComputeKeyId(ReadOnlySpan<byte> key)
    {

        Span<byte> digest = stackalloc byte[32];

        SHA256.HashData(key, digest);

        string id = Convert.ToHexString(digest[..8]).ToLowerInvariant();

        CryptographicOperations.ZeroMemory(digest);

        return id;

    }

}
