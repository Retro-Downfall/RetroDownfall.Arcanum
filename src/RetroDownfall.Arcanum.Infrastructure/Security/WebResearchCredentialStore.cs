using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Perplexity credential store that prefers the platform credential manager and keeps an
/// owner-only, Data Protection-encrypted fallback for headless hosts. The OS/mirror policy itself
/// lives in <see cref="MirroredOsCredential"/>.
/// </summary>
public sealed class WebResearchCredentialStore : IWebResearchCredentialStore, IDisposable
{
    internal const int MaxProtectedSecretBytes = ProtectedCredentialFile.MaxProtectedSecretBytes;

    private const string ProtectorPurpose = "Arcanum.WebResearch.PerplexityApiKey";

    private const string CorruptCredentialMessage =
        "perplexity-key.dat is present but could not be decrypted "
        + "(corrupt or wrong Data Protection key ring).";

    private readonly MirroredOsCredential _credential;

    private bool _disposed;

    public WebResearchCredentialStore(
        IOsCredentialStore osStore,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<WebResearchCredentialStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(osStore);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        _credential = new MirroredOsCredential(
            osStore,
            ArcanumCredentialIdentity.PerplexityApiKeyAccount,
            new ProtectedFileCredentialMirror(
                static () => ArcanumPaths.PerplexityApiKeyStoreFile,
                dataProtectionProvider.CreateProtector(ProtectorPurpose),
                CorruptCredentialMessage),
            new MirroredCredentialPolicy(
                "the Perplexity provider credential",
                "Restore the credential before retrying."),
            logger);
    }

    public void Dispose() => _disposed = true;

    public Task<SecretStoreReadResult> GetPerplexityApiKeyReadResultAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _credential.GetAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves the Perplexity credential without promoting its encrypted fallback into OS storage.
    /// A failed OS read fails closed because the hidden credential may supersede the fallback.
    /// </summary>
    public Task<SecretStoreReadResult> PeekPerplexityApiKeyReadResultAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _credential.PeekAsync(cancellationToken);
    }

    public Task SavePerplexityApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _credential.SaveAsync(apiKey.Trim(), cancellationToken);
    }

    public Task DeletePerplexityApiKeyAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _credential.DeleteAsync(cancellationToken);
    }
}
