using Microsoft.AspNetCore.DataProtection;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class DataProtectionSecretStore(
    IDataProtectionProvider dataProtectionProvider,
    IApiKeyDigestCache apiKeyDigestCache) : ISecretStore, IDisposable
{
    internal const int MaxProtectedSecretBytes = ProtectedCredentialFile.MaxProtectedSecretBytes;

    private const string ProtectorPurpose = "Arcanum.Core.ApiKey";

    private const string GrimoireProtectorPurpose = "Arcanum.Core.GrimoireEncryption";

    private const string FileEncryptionProtectorPurpose = "Arcanum.Core.FileEncryption.v1";

    private const string CorruptApiKeyRecoveryMessage =
        "security.dat is present but could not be decrypted (corrupt or wrong Data Protection key ring). "
        + "Stop the host, then follow the \"Local Grimoire reinstall\" guidance in docs/Arcanum.Engineering.md: remove both security.dat and the Grimoire .db under ~/.config/arcanum/, or restore from backup. "
        + "Do not delete the Grimoire database alone if you need to keep session data.";

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);

    private readonly IDataProtector _grimoireProtector = dataProtectionProvider.CreateProtector(GrimoireProtectorPurpose);

    private readonly IDataProtector _fileEncryptionProtector =
        dataProtectionProvider.CreateProtector(FileEncryptionProtectorPurpose);

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static string StorePath => ArcanumPaths.ApiKeyStoreFile;

    private static string GrimoireStorePath => ArcanumPaths.GrimoireKeyStoreFile;

    private static string FileEncryptionStorePath => ArcanumPaths.FileEncryptionKeyStoreFile;

    public void Dispose() => _fileLock.Dispose();

    public async Task<string?> GetApiKeyAsync()
    {
        SecretStoreReadResult result = await GetApiKeyReadResultAsync().ConfigureAwait(false);

        return result.Status == SecretStoreReadStatus.Ok ? result.Value : null;
    }

    public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
        ReadProtectedResultAsync(StorePath, _protector, corruptMessage: CorruptApiKeyRecoveryMessage);

    public Task SaveApiKeyAsync(string apiKey) =>
        SaveApiKeyCoreAsync(apiKey, invalidateCanonicalDigest: true);

    /// <summary>
    /// Synchronizes the encrypted recovery/client mirror after the canonical OS credential has
    /// already been read. This copy-only write must not invalidate that canonical key's live digest.
    /// </summary>
    internal Task SaveApiKeyMirrorAsync(string apiKey) =>
        SaveApiKeyCoreAsync(apiKey, invalidateCanonicalDigest: false);

    private async Task SaveApiKeyCoreAsync(
        string apiKey,
        bool invalidateCanonicalDigest)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        await _fileLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await WriteProtectedAsync(StorePath, apiKey, _protector).ConfigureAwait(false);

            if (invalidateCanonicalDigest)
            {
                apiKeyDigestCache.Invalidate();
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<string?> GetGrimoireEncryptionSecretAsync()
    {
        SecretStoreReadResult result = await GetGrimoireEncryptionSecretReadResultAsync().ConfigureAwait(false);

        return result.Status == SecretStoreReadStatus.Ok ? result.Value : null;
    }

    /// <summary>
    /// Like <see cref="GetGrimoireEncryptionSecretAsync"/> but preserves
    /// <see cref="SecretStoreReadStatus.Corrupted"/> and <see cref="SecretStoreReadStatus.Unreadable"/>
    /// so callers can refuse a silent API-key fallback when the sealed secret is present but
    /// undecryptable or cannot be read.
    /// </summary>
    public Task<SecretStoreReadResult> GetGrimoireEncryptionSecretReadResultAsync() =>
        ReadProtectedResultAsync(
            GrimoireStorePath,
            _grimoireProtector,
            corruptMessage: "grimoire-key.dat is present but could not be decrypted (corrupt or missing Data Protection key).");

    public async Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret)
    {
        ArgumentNullException.ThrowIfNull(encryptionSecret);

        await _fileLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await WriteProtectedAsync(GrimoireStorePath, encryptionSecret, _grimoireProtector).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync() =>
        ReadProtectedResultAsync(
            FileEncryptionStorePath,
            _fileEncryptionProtector,
            corruptMessage:
                "file-encryption-key.dat is present but could not be decrypted. "
                + "Restore the matching Data Protection key ring and file-encryption-key.dat from backup; "
                + "encrypted attachments, uploads, and batch artifacts are otherwise unrecoverable.");

    public async Task SaveFileEncryptionSecretAsync(string encryptionSecret)
    {
        ArgumentNullException.ThrowIfNull(encryptionSecret);
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await WriteProtectedAsync(
                    FileEncryptionStorePath,
                    encryptionSecret,
                    _fileEncryptionProtector)
                .ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<SecretStoreReadResult> ReadProtectedResultAsync(
        string path,
        IDataProtector protector,
        string corruptMessage)
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);

        try
        {
            // Only undecryptable or empty content is Corrupted and carries the recovery text; a file
            // that could not be read at all (access denied, I/O error, over the ceiling, linked) is
            // Unreadable with retry guidance, never the advice to delete it.
            return await ProtectedCredentialFile
                .ReadAsync(path, protector, corruptMessage, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Test seam for <see cref="WriteProtectedAsync"/>. Production callers only ever pass paths built
    /// by <see cref="ArcanumPaths"/>, which always name a file inside the secret store directory, so
    /// the rooted-path guard on the directory cannot be driven from the public surface.
    /// </summary>
    internal static Task WriteProtectedForTestsAsync(string path, string plainText, IDataProtector protector) =>
        WriteProtectedAsync(path, plainText, protector);

    /// <summary>
    /// grimoire-key.dat has no OS-credential copy, so the rename must not be able to outrun the data:
    /// the shared writer fsyncs the owner-only temp file before the atomic replace, exactly as the
    /// Grimoire <c>.kdf</c> sidecar and every other credential mirror do.
    /// </summary>
    private static Task WriteProtectedAsync(string path, string plainText, IDataProtector protector) =>
        ProtectedCredentialFile.WriteAsync(path, plainText, protector, CancellationToken.None);

    internal static bool GrimoireDatabaseExists() => File.Exists(ArcanumPaths.GrimoireDatabaseFile);
}
