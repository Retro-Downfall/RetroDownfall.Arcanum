using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>
/// Resolves the <c>X-Arcanum-Key</c> API key The Forge uses to authenticate against Arcanum's HTTP
/// API. The key itself is Data-Protection-encrypted at <c>security.dat</c> and cannot be decrypted by
/// a separate process (The Forge is not Arcanum), so resolution goes through a CLI shell-out rather
/// than reading the encrypted store directly:
///
/// 1. A previously-resolved key cached in <c>forge.json</c> (<see cref="ForgeSettings.ApiKey"/>).
/// 2. Shell out to <c>arcanum key show</c>, which writes the raw key to stderr.
/// 3. Otherwise <see langword="null"/> — the caller (Compendium / Whispers toast) prompts the user to paste one.
/// </summary>
public sealed class ApiKeyResolver
{

    private readonly ILogger<ApiKeyResolver> _logger;

    public ApiKeyResolver(ILogger<ApiKeyResolver> logger)
    {

        _logger = logger;

    }

    /// <summary>
    /// Resolves the API key, preferring the cached value in <c>forge.json</c> when present, then
    /// shelling out to <c>arcanum key show</c>. Returns <see langword="null"/> when neither source
    /// yields a key; callers should prompt the user to paste one and call
    /// <see cref="PersistAsync"/> afterward.
    /// </summary>
    public async Task<string?> ResolveAsync(ForgeSettings currentSettings, CancellationToken cancellationToken)
    {

        if (!string.IsNullOrWhiteSpace(currentSettings.ApiKey))
        {

            return currentSettings.ApiKey;

        }

        string? shelledOutKey = await TryShellOutAsync(cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(shelledOutKey) ? null : shelledOutKey;

    }

    /// <summary>
    /// Runs <c>arcanum key show</c> and captures the key from stderr (the CLI intentionally avoids
    /// stdout so the key is not accidentally captured by shell piping). Returns <see langword="null"/>
    /// on any failure (CLI not on PATH, non-zero exit, empty output) — never throws.
    /// </summary>
    private async Task<string?> TryShellOutAsync(CancellationToken cancellationToken)
    {

        try
        {

            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
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

            string trimmed = stderr.Trim();

            return trimmed.Length == 0 ? null : trimmed;

        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
        {

            _logger.LogInformation(ex, "Could not shell out to `arcanum key show`; the CLI may not be on PATH.");

            return null;

        }

    }

    /// <summary>
    /// Persists a resolved (or manually-pasted) key into <c>forge.json</c>
    /// (<see cref="ArcanumPaths.GrimoireDirectory"/>) with file mode <c>0600</c> on Unix.
    /// </summary>
    public async Task PersistAsync(ForgeSettings currentSettings, string apiKey, CancellationToken cancellationToken)
    {

        string directory = ArcanumPaths.GrimoireDirectory;

        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, "forge.json");

        ForgeSettings updated = currentSettings with { ApiKey = apiKey };

        string json = JsonSerializer.Serialize(updated, ForgeSettingsJsonContext.Default.ForgeSettings);

        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);

        TrySetUnixFileMode(path);

    }

    private static void TrySetUnixFileMode(string path)
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        try
        {

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        }
        catch (IOException)
        {

            // Best-effort — some filesystems (e.g. certain network mounts) reject chmod.
        }

    }

}
