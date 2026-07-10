using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>
/// Atomic, owner-only writer for <c>forge.json</c>. Uses <see cref="ForgeSettingsJsonContext"/> only.
/// </summary>
public sealed class ForgeSettingsStore : IForgeSettingsStore
{

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly ILogger<ForgeSettingsStore>? _logger;

    public ForgeSettingsStore(string settingsPath, ILogger<ForgeSettingsStore>? logger = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);

        SettingsPath = settingsPath;

        _logger = logger;

    }

    public string SettingsPath { get; }

    public async Task<ForgeSettings> LoadAsync(CancellationToken cancellationToken = default)
    {

        if (!File.Exists(SettingsPath))
        {

            return new ForgeSettings();

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

            ForgeSettings? settings = await JsonSerializer
                .DeserializeAsync(stream, ForgeSettingsJsonContext.Default.ForgeSettings, cancellationToken)
                .ConfigureAwait(false);

            return settings ?? new ForgeSettings();

        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {

            _logger?.LogWarning(ex, "Corrupt or unreadable forge.json at {Path}; using defaults.", SettingsPath);

            return new ForgeSettings();

        }

    }

    public async Task SaveAsync(ForgeSettings settings, CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(settings);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
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
                        .SerializeAsync(stream, settings, ForgeSettingsJsonContext.Default.ForgeSettings, cancellationToken)
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
        finally
        {

            _writeLock.Release();

        }

    }

    public async Task SavePatchAsync(Func<ForgeSettings, ForgeSettings> patch, CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(patch);

        ForgeSettings current = await LoadAsync(cancellationToken).ConfigureAwait(false);

        ForgeSettings updated = patch(current);

        await SaveAsync(updated, cancellationToken).ConfigureAwait(false);

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
