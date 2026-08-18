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

    /// <summary>
    /// 1 once the session-only warning has been raised for the current resolution. <see cref="Interlocked"/>
    /// rather than a plain <see langword="bool"/>: the background health poller races user-initiated requests.
    /// </summary>
    private int _warned;

    public SessionOnlyWhisperApiKeyProvider(TheForgeApiKeyProvider inner, Lazy<IWhispersService> whispers)
    {

        _inner = inner;

        _whispers = whispers;

    }

    public async Task<string?> GetApiKeyAsync(CancellationToken cancellationToken)
    {

        string? key = await _inner.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);

        // IsSessionOnlyKey stays latched for the lifetime of a resolution while this method runs once per
        // outbound HTTP request, so warn only on the transition into a session-only key. Whispers cap at
        // IWhispersService.MaxActive and evict oldest-non-Error first: re-emitting per request would starve
        // every Success/Info notification out of the stack.
        if (_inner.IsSessionOnlyKey
            && !string.IsNullOrWhiteSpace(key)
            && Interlocked.Exchange(ref _warned, 1) == 0)
        {

            _whispers.Value.Show(
                WhisperSeverity.Warning,
                TheForgeApiKeyProvider.SessionOnlyPersistWarning);

        }

        return key;

    }

    public Task PersistPastedKeyAsync(string apiKey, CancellationToken cancellationToken)
    {

        Interlocked.Exchange(ref _warned, 0);

        return _inner.PersistPastedKeyAsync(apiKey, cancellationToken);

    }

    public void ClearPasteDecline()
    {

        Interlocked.Exchange(ref _warned, 0);

        _inner.ClearPasteDecline();

    }

}
