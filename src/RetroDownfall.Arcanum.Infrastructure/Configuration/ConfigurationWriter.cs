using System.Text.Json;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Serialization;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Configuration;

internal sealed class ConfigurationWriter
{

    private readonly ILogger<ConfigurationWriter> _logger;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private ArcanumSettings? _latest;

    public ConfigurationWriter(ILogger<ConfigurationWriter> logger)
    {

        _logger = logger;

    }

    internal ArcanumSettings? Latest => Volatile.Read(ref _latest);

    public async Task<Result> WriteAsync(
        ArcanumSettings settings,
        CancellationToken cancellationToken)
    {

        try
        {

            return await ArcanumConfigurationTransaction.RunAsync(
                    () => WriteUnderTransactionAsync(settings, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

        }
        catch (ArcanumConfigurationLockException exception)
        {

            return LockUnavailable(exception);

        }

    }

    private async Task<Result> WriteUnderTransactionAsync(
        ArcanumSettings settings,
        CancellationToken cancellationToken)
    {

        await _writeLock
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {

            await WriteLockedAsync(settings, cancellationToken)
                .ConfigureAwait(false);

            return Result.Success();

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception exception)
        {

            return WriteFailure(exception);

        }
        finally
        {

            _writeLock.Release();

        }

    }

    /// <summary>
    /// Serializes a partial read-modify-write with full configuration replacement. The updater sees
    /// the latest successfully persisted file, or <paramref name="fallback"/> when no file exists.
    /// </summary>
    internal async Task<Result<ArcanumSettings>> UpdateAsync(
        ArcanumSettings fallback,
        Func<ArcanumSettings, Result<ArcanumSettings>> update,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(update);

        return await UpdateAsync(
                fallback,
                (current, _) => Task.FromResult(update(current)),
                cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Serializes an asynchronous read-validate-write transaction with full configuration
    /// replacement. The updater and its validation run while the configuration writer lock is held,
    /// so another partial writer cannot commit between the current snapshot and replacement.
    /// </summary>
    internal async Task<Result<ArcanumSettings>> UpdateAsync(
        ArcanumSettings fallback,
        Func<ArcanumSettings, CancellationToken, Task<Result<ArcanumSettings>>> update,
        CancellationToken cancellationToken)
    {

        try
        {

            return await ArcanumConfigurationTransaction.RunAsync(
                    () => UpdateUnderTransactionAsync(
                        fallback,
                        update,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

        }
        catch (ArcanumConfigurationLockException exception)
        {

            return Result<ArcanumSettings>.Failure(LockUnavailable(exception).Error);

        }

    }

    private async Task<Result<ArcanumSettings>> UpdateUnderTransactionAsync(
        ArcanumSettings fallback,
        Func<ArcanumSettings, CancellationToken, Task<Result<ArcanumSettings>>> update,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(fallback);

        ArgumentNullException.ThrowIfNull(update);

        await _writeLock
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {

            ArcanumSettings current = await ReadLockedAsync(
                    fallback,
                    cancellationToken)
                .ConfigureAwait(false);

            Result<ArcanumSettings> updated = await update(
                    current,
                    cancellationToken)
                .ConfigureAwait(false);

            if (updated.IsFailure)
            {

                return updated;

            }

            await WriteLockedAsync(updated.Value, cancellationToken)
                .ConfigureAwait(false);

            return updated;

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception exception)
        {

            Result failed = WriteFailure(exception);

            return Result<ArcanumSettings>.Failure(failed.Error);

        }
        finally
        {

            _writeLock.Release();

        }

    }

    private static Task<ArcanumSettings> ReadLockedAsync(
        ArcanumSettings fallback,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        ArcanumSettings settings = ConfigurationBootstrapper
            .LoadPersistedArcanumSettings(() => RemoveEnvironmentOverrides(fallback));

        return Task.FromResult(settings);

    }

    private static ArcanumSettings RemoveEnvironmentOverrides(ArcanumSettings fallback)
    {

        ArcanumSettings raw = ConfigurationPathAccessor.Clone(fallback);

        ArcanumSettings defaults = new();

        ConfigurationEnvironmentSnapshot environment =
            ConfigurationEnvironmentResolver.Resolve(fallback);

        foreach (ConfigurationEnvironmentOverride item in environment.Overrides)
        {

            if (!item.IsEffective || !ConfigurationPathAccessor.Exists(defaults, item.Path))
            {

                continue;

            }

            ConfigurationPathUpdate update = ConfigurationPathAccessor.SetCanonicalValue(
                raw,
                item.Path,
                ConfigurationPathAccessor.GetCanonicalValue(defaults, item.Path));

            if (update.IsSuccess)
            {

                raw = update.Settings!;

            }

        }

        return raw;

    }

    private async Task WriteLockedAsync(
        ArcanumSettings settings,
        CancellationToken cancellationToken)
    {

        string directory = ArcanumPaths.GrimoireDirectory;

        string path = ConfigurationPath();

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);

        string tempPath = Path.Combine(
            directory,
            $".arcanum.{Guid.NewGuid():N}.tmp");

        ArcanumConfigurationFile wrapper = new() { Arcanum = settings };

        AtomicReplaceStatus replaceStatus = await AtomicFile.ReplaceAsync(
            path,
            tempPath,
            (stream, cancellation) => JsonSerializer.SerializeAsync(
                stream,
                wrapper,
                ConfigurationJsonContext.Default.ArcanumConfigurationFile,
                cancellation),
            cancellationToken,
            afterReplace: () =>
            {

                try
                {

                    SecureFilePermissions.ApplyOwnerOnlyFile(path);

                    return true;

                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or PlatformNotSupportedException)
                {

                    return false;

                }

            }).ConfigureAwait(false);

        if (replaceStatus != AtomicReplaceStatus.Succeeded)
        {

            throw new ConfigurationAtomicWriteException(replaceStatus);

        }

        Volatile.Write(ref _latest, settings);

    }

    private Result LockUnavailable(ArcanumConfigurationLockException exception)
    {

        _logger.LogWarning(
            exception,
            "Timed out acquiring the configuration transaction for {ConfigPath}",
            ConfigurationPath());

        return Result.Failure(
            new Error("Configuration.LockUnavailable", exception.Message));

    }

    private Result WriteFailure(Exception exception)
    {

        _logger.LogError(
            exception,
            "Failed to write configuration to {ConfigPath}",
            ConfigurationPath());

        string code = exception is ConfigurationAtomicWriteException atomic
            ? atomic.Status switch
            {
                AtomicReplaceStatus.Aborted => "Configuration.WriteAborted",
                AtomicReplaceStatus.RolledBack => "Configuration.WriteFailed.RolledBack",
                AtomicReplaceStatus.ReplacedButUnverified =>
                    "Configuration.WriteFailed.Unverified",
                _ => "Configuration.WriteFailed",
            }
            : "Configuration.WriteFailed";

        return Result.Failure(new Error(code, exception.Message));

    }

    private static string ConfigurationPath() =>
        Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

    private sealed class ConfigurationAtomicWriteException(AtomicReplaceStatus status)
        : IOException($"Atomic configuration replacement did not succeed ({status}).")
    {

        public AtomicReplaceStatus Status { get; } = status;

    }

}
