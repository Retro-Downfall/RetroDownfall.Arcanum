using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>
/// Resolves the <c>X-Arcanum-Key</c> master API key for The Forge from the shared OS credential store
/// (same identity Arcanum writes at bootstrap). Resolution order:
/// <list type="number">
/// <item>OS keychain (<see cref="ArcanumCredentialIdentity"/>).</item>
/// <item>Legacy plaintext <c>forge.json</c> <see cref="TheForgeSettings.ApiKey"/> — migrated into the keychain then stripped.</item>
/// <item>Optional shell-out to <c>arcanum key show</c> — result persisted into the keychain.</item>
/// <item>Otherwise <see langword="null"/> — caller prompts the user to paste and calls <see cref="PersistAsync"/>.</item>
/// </list>
/// </summary>
public sealed class ApiKeyResolver
{

    private readonly IOsCredentialStore _osStore;

    private readonly ITheForgeSettingsStore _settingsStore;

    private readonly ILogger<ApiKeyResolver> _logger;

    public ApiKeyResolver(
        IOsCredentialStore osStore,
        ITheForgeSettingsStore settingsStore,
        ILogger<ApiKeyResolver> logger)
    {

        _osStore = osStore ?? throw new ArgumentNullException(nameof(osStore));

        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

        _logger = logger;

    }

    /// <summary>
    /// Resolves the API key from the OS store (with legacy forge.json / CLI fallbacks). Returns
    /// <see langword="null"/> when no source yields a key; callers should prompt the user to paste
    /// one and call <see cref="PersistAsync"/>.
    /// </summary>
    public async Task<string?> ResolveAsync(TheForgeSettings currentSettings, CancellationToken cancellationToken)
    {

        OsCredentialStoreResult os = _osStore.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount);

        if (os.Status == OsCredentialStoreStatus.Ok && !string.IsNullOrWhiteSpace(os.Value))
        {

            return os.Value;

        }

        if (os.Status == OsCredentialStoreStatus.Unavailable)
        {

            _logger.LogWarning("OS credential store unavailable: {Message}", os.Message);

        }

        if (!string.IsNullOrWhiteSpace(currentSettings.ApiKey))
        {

            string legacy = currentSettings.ApiKey!;

            OsCredentialStoreResult migrate = _osStore.Set(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.MasterApiKeyAccount,
                legacy);

            if (migrate.Status == OsCredentialStoreStatus.Ok)
            {

                _logger.LogInformation("Migrated forge.json apiKey into the OS credential store; stripping plaintext cache.");

                await StripForgeJsonApiKeyAsync(currentSettings, cancellationToken).ConfigureAwait(false);

            }
            else
            {

                _logger.LogWarning(
                    "Could not migrate forge.json apiKey into the OS store ({Message}); using in-memory value only.",
                    migrate.Message);

            }

            return legacy;

        }

        string? shelledOutKey = await TryShellOutAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(shelledOutKey))
        {

            return null;

        }

        OsCredentialStoreResult persist = _osStore.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            shelledOutKey);

        if (persist.Status != OsCredentialStoreStatus.Ok)
        {

            _logger.LogWarning(
                "`arcanum key show` succeeded but OS store persist failed: {Message}",
                persist.Message);

        }

        return shelledOutKey;

    }

    /// <summary>
    /// Persists a pasted (or otherwise obtained) key into the OS credential store. Also strips any
    /// legacy plaintext <c>apiKey</c> from <c>forge.json</c>.
    /// </summary>
    public async Task PersistAsync(TheForgeSettings currentSettings, string apiKey, CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        OsCredentialStoreResult result = _osStore.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            apiKey.Trim());

        if (result.Status != OsCredentialStoreStatus.Ok)
        {

            throw new InvalidOperationException(
                result.Message ?? "Failed to store the API key in the OS credential store.");

        }

        await StripForgeJsonApiKeyAsync(currentSettings, cancellationToken).ConfigureAwait(false);

    }

    private async Task StripForgeJsonApiKeyAsync(TheForgeSettings currentSettings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(currentSettings.ApiKey)
            && !File.Exists(_settingsStore.SettingsPath))
        {

            return;

        }

        await _settingsStore
            .SavePatchAsync(static s => s with { ApiKey = null }, cancellationToken)
            .ConfigureAwait(false);

    }

    private async Task<string?> TryShellOutAsync(CancellationToken cancellationToken)
    {

        try
        {

            using System.Diagnostics.Process process = new()
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "arcanum",
                    ArgumentList = { "key", "show" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {

                return null;

            }

            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            string stderr = await stderrTask.ConfigureAwait(false);

            _ = await stdoutTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {

                _logger.LogWarning("`arcanum key show` exited with code {ExitCode}.", process.ExitCode);

                return null;

            }

            // CLI writes the key on the first stderr line; muted notes may follow.
            string? firstLine = stderr
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim())
                .FirstOrDefault(static line => line.Length > 0 && !line.StartsWith('('));

            return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;

        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
        {

            _logger.LogInformation(ex, "Could not shell out to `arcanum key show`; the CLI may not be on PATH.");

            return null;

        }

    }

}
