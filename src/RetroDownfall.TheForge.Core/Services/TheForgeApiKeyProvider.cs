using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>
/// Callback used when automatic resolution fails and the user must paste a key.
/// Kept as a delegate so TheForge.Core stays free of Avalonia.
/// </summary>
public delegate Task<string?> ApiKeyPastePrompt(CancellationToken cancellationToken);

/// <summary>
/// Default <see cref="ITheForgeApiKeyProvider"/>: resolves via <see cref="ApiKeyResolver"/> once and
/// caches the result in memory for the process lifetime. Optionally prompts for a paste when empty.
/// </summary>
public sealed class TheForgeApiKeyProvider : ITheForgeApiKeyProvider
{

    private readonly ApiKeyResolver _resolver;

    private readonly IOptionsMonitor<TheForgeSettings> _settings;

    private readonly ILogger<TheForgeApiKeyProvider> _logger;

    private readonly ApiKeyPastePrompt? _pastePrompt;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _cached;

    private bool _resolved;

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

            string? key = await _resolver.ResolveAsync(_settings.CurrentValue, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(key) && _pastePrompt is not null)
            {

                string? pasted = await _pastePrompt(cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(pasted))
                {

                    await _resolver.PersistAsync(_settings.CurrentValue, pasted.Trim(), cancellationToken)
                        .ConfigureAwait(false);

                    key = pasted.Trim();

                }

            }

            _cached = string.IsNullOrWhiteSpace(key) ? null : key.Trim();

            _resolved = true;

            if (_cached is null)
            {

                _logger.LogWarning(
                    "No master API key found in the OS credential store, forge.json, or `arcanum key show`.");

            }

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

            await _resolver.PersistAsync(_settings.CurrentValue, trimmed, cancellationToken).ConfigureAwait(false);

            _cached = trimmed;

            _resolved = true;

        }
        finally
        {

            _gate.Release();

        }

    }

}
