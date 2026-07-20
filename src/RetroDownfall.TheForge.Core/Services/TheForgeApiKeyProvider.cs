using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>
/// Callback used when automatic resolution fails and the user must paste a key.
/// Kept as a delegate so TheForge.Core stays free of Avalonia.
/// Returns the trimmed key, <see langword="null"/> when the user declined, or throws
/// <see cref="InvalidOperationException"/> when the UI is not ready to prompt yet.
/// </summary>
public delegate Task<string?> ApiKeyPastePrompt(CancellationToken cancellationToken);

/// <summary>
/// Default <see cref="ITheForgeApiKeyProvider"/>: resolves via <see cref="ApiKeyResolver"/> once and
/// caches a successful key in memory for the process lifetime. Optionally prompts for a paste when empty.
/// A missing key does not permanently suppress future resolution — only an explicit user decline skips
/// re-prompting until <see cref="ClearPasteDecline"/> or <see cref="PersistPastedKeyAsync"/>.
/// </summary>
public sealed class TheForgeApiKeyProvider : ITheForgeApiKeyProvider
{

    public const string SessionOnlyPersistWarning =
        "API key accepted for this session but could not be persisted to the OS credential store.";

    private readonly ApiKeyResolver _resolver;

    private readonly IOptionsMonitor<TheForgeSettings> _settings;

    private readonly ILogger<TheForgeApiKeyProvider> _logger;

    private readonly ApiKeyPastePrompt? _pastePrompt;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _cached;

    private bool _resolved;

    private bool _pasteDeclined;

    private bool _isSessionOnlyKey;

    public TheForgeApiKeyProvider(
        ApiKeyResolver resolver,
        IOptionsMonitor<TheForgeSettings> settings,
        ILogger<TheForgeApiKeyProvider> logger,
        ApiKeyPastePrompt? pastePrompt = null)
    {

        _resolver = resolver;

        _settings = settings;

        _logger = logger;

        _pastePrompt = pastePrompt;

    }

    /// <summary>
    /// True when the cached key is held only in process memory (OS persist failed or env override).
    /// </summary>
    public bool IsSessionOnlyKey => _isSessionOnlyKey;

    public async Task<string?> GetApiKeyAsync(CancellationToken cancellationToken)
    {

        if (_resolved)
        {

            return _cached;

        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            if (_resolved)
            {

                return _cached;

            }

            ApiKeyResolution resolution = await _resolver.ResolveAsync(_settings.CurrentValue, cancellationToken)
                .ConfigureAwait(false);

            string? key = resolution.Key;

            _isSessionOnlyKey = resolution.IsSessionOnly;

            if (string.IsNullOrWhiteSpace(key) && _pastePrompt is not null && !_pasteDeclined)
            {

                try
                {

                    string? pasted = await _pastePrompt(cancellationToken).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(pasted))
                    {

                        string trimmed = pasted.Trim();

                        try
                        {

                            await _resolver.PersistAsync(_settings.CurrentValue, trimmed, cancellationToken)
                                .ConfigureAwait(false);

                            _isSessionOnlyKey = false;

                        }
                        catch (InvalidOperationException ex)
                        {

                            _logger.LogWarning(ex, SessionOnlyPersistWarning);

                            _isSessionOnlyKey = true;

                        }

                        key = trimmed;

                    }
                    else
                    {

                        // User dismissed the dialog — do not spam re-prompts on every health poll.
                        _pasteDeclined = true;

                    }

                }
                catch (InvalidOperationException ex)
                {

                    // UI not ready (e.g. MainWindow not yet assigned). Leave unresolved so the next
                    // call can prompt once the shell is up.
                    _logger.LogDebug(ex, "API key paste prompt unavailable; will retry.");

                    return null;

                }

            }

            if (string.IsNullOrWhiteSpace(key))
            {

                _logger.LogWarning(
                    "No master API key found in the OS credential store, the-forge.json, {EnvVar}, or `arcanum key show`.",
                    ApiKeyResolver.EnvironmentVariableName);

                return null;

            }

            _cached = key.Trim();

            _resolved = true;

            _pasteDeclined = false;

            return _cached;

        }
        finally
        {

            _gate.Release();

        }

    }

    public async Task PersistPastedKeyAsync(string apiKey, CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            string trimmed = apiKey.Trim();

            try
            {

                await _resolver.PersistAsync(_settings.CurrentValue, trimmed, cancellationToken).ConfigureAwait(false);

                _isSessionOnlyKey = false;

            }
            catch (InvalidOperationException ex)
            {

                _logger.LogWarning(ex, SessionOnlyPersistWarning);

                _isSessionOnlyKey = true;

            }

            _cached = trimmed;

            _resolved = true;

            _pasteDeclined = false;

        }
        finally
        {

            _gate.Release();

        }

    }

    public void ClearPasteDecline()
    {

        _pasteDeclined = false;

        // Allow Anvil "Enter API key…" to override a bad cached env/session key via paste.
        _resolved = false;

        _cached = null;

        _isSessionOnlyKey = false;

    }

}
