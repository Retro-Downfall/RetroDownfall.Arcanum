using Microsoft.AspNetCore.DataProtection;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// The encrypted file copy of one OS-stored credential, as <see cref="MirroredOsCredential"/> sees it.
/// </summary>
internal abstract class CredentialMirror
{
    /// <summary>The mirror file's current location. Re-evaluated on every access.</summary>
    internal abstract string Path { get; }

    /// <summary>
    /// Reads the mirror. <see cref="SecretStoreReadStatus.Ok"/> always carries a non-blank value; how a
    /// blank mirror is reported is the concrete mirror's decision.
    /// </summary>
    internal abstract Task<SecretStoreReadResult> ReadAsync(CancellationToken cancellationToken);

    internal abstract Task WriteAsync(string value, CancellationToken cancellationToken);
}

/// <summary>
/// A mirror file this process encrypts directly with a purpose-scoped Data Protection protector (the
/// inference-provider and web-research credentials). A blank decrypted value is corrupt: nothing ever
/// writes one.
/// </summary>
internal sealed class ProtectedFileCredentialMirror(
    Func<string> path,
    IDataProtector protector,
    string corruptMessage) : CredentialMirror
{
    internal override string Path => path();

    internal override async Task<SecretStoreReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        SecretStoreReadResult read = await ProtectedCredentialFile
            .ReadAsync(Path, protector, corruptMessage, cancellationToken)
            .ConfigureAwait(false);

        return read.Status == SecretStoreReadStatus.Ok && string.IsNullOrWhiteSpace(read.Value)
            ? SecretStoreReadResult.Corrupted(corruptMessage)
            : read;
    }

    internal override Task WriteAsync(string value, CancellationToken cancellationToken) =>
        ProtectedCredentialFile.WriteAsync(Path, value, protector, cancellationToken);
}

/// <summary>
/// A mirror owned by <see cref="DataProtectionSecretStore"/> (<c>security.dat</c> and
/// <c>file-encryption-key.dat</c>), which serializes its own file access. A blank decrypted value reads
/// as absent, the way those files have always been read.
/// </summary>
internal sealed class DataProtectionStoreCredentialMirror(
    Func<string> path,
    Func<Task<SecretStoreReadResult>> read,
    Func<string, Task> write) : CredentialMirror
{
    internal override string Path => path();

    internal override async Task<SecretStoreReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SecretStoreReadResult result = await read().ConfigureAwait(false);

        return result.Status == SecretStoreReadStatus.Ok && string.IsNullOrWhiteSpace(result.Value)
            ? SecretStoreReadResult.Missing()
            : result;
    }

    internal override Task WriteAsync(string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return write(value);
    }
}
