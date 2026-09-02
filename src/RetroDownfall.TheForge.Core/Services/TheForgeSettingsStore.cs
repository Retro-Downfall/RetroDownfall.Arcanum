using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroDownfall.TheForge.Core.IO;
using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>
/// Atomic, owner-only writer for <c>the-forge.json</c>. Uses <see cref="TheForgeSettingsJsonContext"/> only.
/// </summary>
public sealed class TheForgeSettingsStore : ITheForgeSettingsStore
{

    /// <summary>Settings filename under <c>~/.config/arcanum/</c>.</summary>
    public const string FileName = "the-forge.json";

    /// <summary>Pre-rename filename; read as a compatibility fallback when <see cref="FileName"/> is absent.</summary>
    public const string LegacyFileName = "forge.json";

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly ITheForgeLocalMutationRunner _mutationRunner;

    private readonly ILogger<TheForgeSettingsStore>? _logger;

    public TheForgeSettingsStore(
        string settingsPath,
        ITheForgeLocalMutationRunner mutationRunner,
        ILogger<TheForgeSettingsStore>? logger = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);

        SettingsPath = settingsPath;

        _mutationRunner = mutationRunner ?? throw new ArgumentNullException(nameof(mutationRunner));

        _logger = logger;

    }

    public string SettingsPath { get; }

    internal ITheForgeLocalMutationRunner MutationRunner => _mutationRunner;

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

            await _mutationRunner
                .RunAsync(
                    SettingsPath,
                    admittedCancellationToken => SaveCoreAsync(
                        settings,
                        admittedCancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

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

            await _mutationRunner
                .RunAsync(
                    SettingsPath,
                    async admittedCancellationToken =>
                    {

                        TheForgeSettings current = await LoadCoreAsync(admittedCancellationToken)
                            .ConfigureAwait(false);

                        TheForgeSettings updated = patch(current);

                        await SaveCoreAsync(updated, admittedCancellationToken)
                            .ConfigureAwait(false);

                    },
                    cancellationToken)
                .ConfigureAwait(false);

        }
        finally
        {

            _writeLock.Release();

        }

    }

    private async Task<TheForgeSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {

        string readPath = ReadPath();

        if (!File.Exists(readPath))
        {

            return new TheForgeSettings();

        }

        try
        {

            await using FileStream stream = new(
                readPath,
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

            _logger?.LogWarning(ex, "Corrupt or unreadable The Forge settings at {Path}; using defaults.", readPath);

            return new TheForgeSettings();

        }

    }

    private string ReadPath()
    {

        if (File.Exists(SettingsPath))
        {

            return SettingsPath;

        }

        string? directory = Path.GetDirectoryName(SettingsPath);

        return string.IsNullOrEmpty(directory)
            ? SettingsPath
            : Path.Combine(directory, LegacyFileName);

    }

    private async Task SaveCoreAsync(TheForgeSettings settings, CancellationToken cancellationToken)
    {

        string? directory = Path.GetDirectoryName(SettingsPath);

        if (!string.IsNullOrEmpty(directory))
        {

            Directory.CreateDirectory(directory);

            TheForgeOwnerOnlyPermissions.TrySetDirectory(directory);

        }

        string tempPath = SettingsPath + $".{Guid.NewGuid():N}.tmp";

        try
        {

            // The temp file can carry the full settings payload, including a legacy plaintext
            // ApiKey (SavePatchAsync round-trips it). Setting UnixCreateMode makes the owner-only
            // mode part of the file's creation syscall instead of a chmod applied after the write —
            // create-then-chmod leaves a window where the default (umask-controlled) mode is
            // group/other-readable for the entire write duration.
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            };

            // Windows has no UnixCreateMode equivalent; its owner-only restriction is the ACL
            // TheForgeOwnerOnlyPermissions.TrySetFile applies below, after the file exists (its own
            // OperatingSystem.IsWindows() branch calls TryApplyFileAcl there).
            if (!OperatingSystem.IsWindows())
            {

                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            }

            await using (FileStream stream = new(tempPath, options))
            {

                await JsonSerializer
                    .SerializeAsync(stream, settings, TheForgeSettingsJsonContext.Default.TheForgeSettings, cancellationToken)
                    .ConfigureAwait(false);

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            }

            TheForgeOwnerOnlyPermissions.TrySetFile(tempPath);

            File.Move(tempPath, SettingsPath, overwrite: true);

            TheForgeOwnerOnlyPermissions.TrySetFile(SettingsPath);

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

}
