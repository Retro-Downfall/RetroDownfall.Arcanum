using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Forwards <see cref="ITheForgeApiKeyProvider"/> and emits a Whisper when a key is session-only.
/// </summary>
internal sealed class SessionOnlyWhisperApiKeyProvider : ITheForgeApiKeyProvider
{

    private readonly TheForgeApiKeyProvider _inner;

    private readonly Lazy<IWhispersService> _whispers;

    public SessionOnlyWhisperApiKeyProvider(TheForgeApiKeyProvider inner, Lazy<IWhispersService> whispers)
    {

        _inner = inner;

        _whispers = whispers;

    }

    public async Task<string?> GetApiKeyAsync(CancellationToken cancellationToken)
    {

        string? key = await _inner.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);

        if (_inner.IsSessionOnlyKey && !string.IsNullOrWhiteSpace(key))
        {

            _whispers.Value.Show(
                WhisperSeverity.Warning,
                TheForgeApiKeyProvider.SessionOnlyPersistWarning);

        }

        return key;

    }

    public Task PersistPastedKeyAsync(string apiKey, CancellationToken cancellationToken) =>
        _inner.PersistPastedKeyAsync(apiKey, cancellationToken);

    public void ClearPasteDecline() => _inner.ClearPasteDecline();

}
