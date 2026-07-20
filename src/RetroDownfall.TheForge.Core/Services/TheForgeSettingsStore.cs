using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>
/// Atomic, owner-only writer for <c>the-forge.json</c>. Uses <see cref="TheForgeSettingsJsonContext"/> only.
/// </summary>
public sealed class TheForgeSettingsStore : ITheForgeSettingsStore
{

    /// <summary>Settings filename under <c>~/.config/arcanum/</c>.</summary>
    public const string FileName = "the-forge.json";

    /// <summary>Pre-rename filename; moved to <see cref="FileName"/> on first launch when present.</summary>
    public const string LegacyFileName = "forge.json";

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly ILogger<TheForgeSettingsStore>? _logger;

    public TheForgeSettingsStore(string settingsPath, ILogger<TheForgeSettingsStore>? logger = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);

        SettingsPath = settingsPath;

        _logger = logger;

    }

    public string SettingsPath { get; }

    /// <summary>
    /// If <paramref name="settingsPath"/> is missing and <c>forge.json</c> exists beside it, renames
    /// the legacy file into place. Returns true when a rename occurred.
    /// </summary>
    public static bool TryMigrateLegacyFile(string settingsPath)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);

        if (File.Exists(settingsPath))
        {

            return false;

        }

        string? directory = Path.GetDirectoryName(settingsPath);

        if (string.IsNullOrEmpty(directory))
        {

            return false;

        }

        string legacyPath = Path.Combine(directory, LegacyFileName);

        if (!File.Exists(legacyPath))
        {

            return false;

        }

        try
        {

            Directory.CreateDirectory(directory);

            File.Move(legacyPath, settingsPath);

            return true;

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            return false;

        }

    }

    public async Task<TheForgeSettings> LoadAsync(CancellationToken cancellationToken = default)
    {

        return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);

    }

    public async Task SaveAsync(TheForgeSettings settings, CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(settings);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _writeLock.Release();

        }

    }

    public async Task SavePatchAsync(Func<TheForgeSettings, TheForgeSettings> patch, CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(patch);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            TheForgeSettings current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);

            TheForgeSettings updated = patch(current);

            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _writeLock.Release();

        }

    }

    private async Task<TheForgeSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {

        if (!File.Exists(SettingsPath))
        {

            return new TheForgeSettings();

        }

        try
        {

            await using FileStream stream = new(
                SettingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous);

            TheForgeSettings? settings = await JsonSerializer
                .DeserializeAsync(stream, TheForgeSettingsJsonContext.Default.TheForgeSettings, cancellationToken)
                .ConfigureAwait(false);

            return settings ?? new TheForgeSettings();

        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {

            _logger?.LogWarning(ex, "Corrupt or unreadable the-forge.json at {Path}; using defaults.", SettingsPath);

            return new TheForgeSettings();

        }

    }

    private async Task SaveCoreAsync(TheForgeSettings settings, CancellationToken cancellationToken)
    {

        string? directory = Path.GetDirectoryName(SettingsPath);

        if (!string.IsNullOrEmpty(directory))
        {

            Directory.CreateDirectory(directory);

            TrySetUnixDirectoryMode(directory);

        }

        string tempPath = SettingsPath + $".{Guid.NewGuid():N}.tmp";

        try
        {

            await using (FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {

                await JsonSerializer
                    .SerializeAsync(stream, settings, TheForgeSettingsJsonContext.Default.TheForgeSettings, cancellationToken)
                    .ConfigureAwait(false);

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            }

            TrySetUnixFileMode(tempPath);

            File.Move(tempPath, SettingsPath, overwrite: true);

            TrySetUnixFileMode(SettingsPath);

        }
        catch
        {

            TryDelete(tempPath);

            throw;

        }

    }

    private static void TryDelete(string path)
    {

        try
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }
        catch (IOException)
        {

            // Best-effort cleanup of temp file.
        }

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            // Best-effort — some filesystems reject chmod.
        }

    }

    private static void TrySetUnixDirectoryMode(string path)
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        try
        {

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            // Best-effort.
        }

    }

}
