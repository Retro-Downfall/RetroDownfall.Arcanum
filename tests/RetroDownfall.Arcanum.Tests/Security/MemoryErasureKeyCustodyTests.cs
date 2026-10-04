using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Who may name the erasure key's credential account.
/// </summary>
/// <remarks>
/// The keyring is the only reader and writer, and a full installation reset is the only deleter. A
/// backup that carried the key would put it next to the fingerprints it keys, which is an offline
/// confirmation oracle for anything that was erased, so the backup secret reader must never name it.
/// </remarks>
public sealed class MemoryErasureKeyCustodyTests
{
    private const string AccountName = "memory-erasure-fingerprint-key";

    [Fact]
    public void Only_the_keyring_and_the_reset_paths_name_the_erasure_key_account()
    {
        string[] owners = [.. ProductionSourceInventory.Sources()
            .Where(static source =>
                source.Names(nameof(ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount))
                || source.Names(AccountName))
            .Select(static source => source.RelativePath)
            .Order(StringComparer.Ordinal)];

        Assert.Equal(
            [
                "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/FullInstallationResetTerminalContinuation.cs",
                "src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetCredentialCatalog.cs",
                "src/RetroDownfall.Arcanum.Infrastructure/Security/MemoryErasureKeyring.cs",
                "src/RetroDownfall.Arcanum.Secrets/Security/ArcanumCredentialIdentity.cs",
            ],
            owners);
    }

    [Fact]
    public void Backup_secret_snapshot_reader_never_reads_the_erasure_key()
    {
        ProductionSource reader = Assert.Single(
            ProductionSourceInventory.Sources(),
            static source => source.IsExactOwner(
                "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSecretSnapshotReader.cs"));

        Assert.False(reader.Names(nameof(ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount)));

        Assert.False(reader.Names(AccountName));
    }
}
