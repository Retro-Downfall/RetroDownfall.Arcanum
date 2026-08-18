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
/// <item>Legacy plaintext <c>the-forge.json</c> <see cref="TheForgeSettings.ApiKey"/> — migrated into the keychain then stripped.</item>
/// <item><c>THEFORGE_ARCANUM_KEY</c> environment variable (trim; empty/whitespace = absent; never persisted).</item>
/// <item>Optional shell-out to <c>arcanum key show</c> — result persisted into the keychain when possible.</item>
/// <item>Otherwise <see langword="null"/> — caller prompts the user to paste and calls <see cref="PersistAsync"/>.</item>
/// </list>
/// </summary>
public sealed class ApiKeyResolver
{

    public const string EnvironmentVariableName = "THEFORGE_ARCANUM_KEY";

    /// <summary>Shortest value accepted from <c>arcanum key show</c> before it is written to the OS store.</summary>
    internal const int MinRecoveredKeyLength = 8;

    /// <summary>Longest value accepted from <c>arcanum key show</c> before it is written to the OS store.</summary>
    internal const int MaxRecoveredKeyLength = 512;

    private readonly IOsCredentialStore _osStore;

    private readonly ITheForgeSettingsStore _settingsStore;

    private readonly ILogger<ApiKeyResolver> _logger;

    private readonly Func<string, string?> _getEnvironmentVariable;

    private readonly Func<CancellationToken, Task<string?>> _shellOut;

    public ApiKeyResolver(
        IOsCredentialStore osStore,
        ITheForgeSettingsStore settingsStore,
        ILogger<ApiKeyResolver> logger,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<CancellationToken, Task<string?>>? shellOut = null)
    {

        _osStore = osStore ?? throw new ArgumentNullException(nameof(osStore));

        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

        _logger = logger;

        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

        _shellOut = shellOut ?? TryShellOutAsync;

    }

    /// <summary>
    /// Resolves the API key from the OS store (with legacy the-forge.json / env / CLI fallbacks). Returns
    /// a null <see cref="ApiKeyResolution.Key"/> when no source yields a key; callers should prompt
    /// the user to paste one and call <see cref="PersistAsync"/>.
    /// </summary>
    public async Task<ApiKeyResolution> ResolveAsync(TheForgeSettings currentSettings, CancellationToken cancellationToken)
    {

        OsCredentialStoreResult os = _osStore.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount);

        if (os.Status == OsCredentialStoreStatus.Ok && !string.IsNullOrWhiteSpace(os.Value))
        {

            return new ApiKeyResolution(os.Value, IsSessionOnly: false);

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

                _logger.LogInformation("Migrated the-forge.json apiKey into the OS credential store; stripping plaintext cache.");

                await StripForgeJsonApiKeyAsync(currentSettings, cancellationToken).ConfigureAwait(false);

                return new ApiKeyResolution(legacy, IsSessionOnly: false);

            }

            _logger.LogWarning(
                "Could not migrate the-forge.json apiKey into the OS store ({Message}); using in-memory value only.",
                migrate.Message);

            return new ApiKeyResolution(legacy, IsSessionOnly: true);

        }

        string? envKey = ReadEnvironmentKey();

        if (!string.IsNullOrWhiteSpace(envKey))
        {

            _logger.LogInformation(
                "{EnvVar} is set; using process-only API key override (not persisted).",
                EnvironmentVariableName);

            return new ApiKeyResolution(envKey, IsSessionOnly: true);

        }

        string? shelledOutKey = await _shellOut(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(shelledOutKey))
        {

            return new ApiKeyResolution(null, IsSessionOnly: false);

        }

        string recovered = shelledOutKey.Trim();

        if (!IsPlausibleApiKey(recovered))
        {

            _logger.LogWarning(
                "`arcanum key show` returned a value that does not look like a master API key; it was not persisted.");

            return new ApiKeyResolution(null, IsSessionOnly: false);

        }

        OsCredentialStoreResult persist = _osStore.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            recovered);

        if (persist.Status != OsCredentialStoreStatus.Ok)
        {

            _logger.LogWarning(
                "`arcanum key show` succeeded but OS store persist failed: {Message}",
                persist.Message);

            return new ApiKeyResolution(recovered, IsSessionOnly: true);

        }

        return new ApiKeyResolution(recovered, IsSessionOnly: false);

    }

    /// <summary>
    /// Persists a pasted (or otherwise obtained) key into the OS credential store. Also strips any
    /// legacy plaintext <c>apiKey</c> from <c>the-forge.json</c>.
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

    private string? ReadEnvironmentKey()
    {

        string? raw = _getEnvironmentVariable(EnvironmentVariableName);

        if (string.IsNullOrWhiteSpace(raw))
        {

            return null;

        }

        string trimmed = raw.Trim();

        return trimmed.Length == 0 ? null : trimmed;

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

    /// <summary>
    /// Resolves the <c>arcanum</c> CLI to a fully qualified path by walking <c>PATH</c> itself. A bare
    /// file name is never handed to <see cref="System.Diagnostics.Process"/>: on Windows
    /// <c>CreateProcess</c> searches the loading application's directory and the process working
    /// directory ahead of <c>PATH</c>, so a planted <c>arcanum.exe</c> in whatever directory The Forge
    /// happened to inherit would run with the operator's privileges (CWE-426 untrusted search path).
    /// Relative <c>PATH</c> entries are rejected for the same reason. Returns <see langword="null"/>
    /// when no absolute candidate exists, in which case the shell-out step is skipped.
    /// </summary>
    internal static string? ResolveCliExecutablePath(
        Func<string, string?> getEnvironmentVariable,
        Func<string, bool> fileExists)
    {

        string? path = getEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(path))
        {

            return null;

        }

        string fileName = OperatingSystem.IsWindows() ? "arcanum.exe" : "arcanum";

        foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {

            string directory = entry.Trim();

            if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
            {

                continue;

            }

            string candidate = Path.Combine(directory, fileName);

            if (fileExists(candidate))
            {

                return candidate;

            }

        }

        return null;

    }

    /// <summary>
    /// Rejects anything that does not look like a master API key before it reaches the shared
    /// <c>arcanum</c>/<c>master-api-key</c> credential slot. Keys are single-line printable ASCII
    /// (the bootstrapper writes base64 of 32 random bytes), so diagnostics, prose, and anything
    /// carrying whitespace or control characters never get persisted.
    /// </summary>
    internal static bool IsPlausibleApiKey(string? value)
    {

        if (string.IsNullOrWhiteSpace(value))
        {

            return false;

        }

        if (value.Length is < MinRecoveredKeyLength or > MaxRecoveredKeyLength)
        {

            return false;

        }

        foreach (char character in value)
        {

            if (character is < ' ' or > '~')
            {

                return false;

            }

            if (character == ' ')
            {

                return false;

            }

        }

        return true;

    }

    private async Task<string?> TryShellOutAsync(CancellationToken cancellationToken)
    {

        string? executable = ResolveCliExecutablePath(_getEnvironmentVariable, File.Exists);

        if (executable is null)
        {

            _logger.LogInformation(
                "No `arcanum` executable was found on an absolute PATH entry; skipping the CLI key hand-off.");

            return null;

        }

        try
        {

            using System.Diagnostics.Process process = new()
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = executable,
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
