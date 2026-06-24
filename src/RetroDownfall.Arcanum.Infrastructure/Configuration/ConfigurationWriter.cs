using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Configuration;

internal sealed class ConfigurationWriter
{

    private readonly ILogger<ConfigurationWriter> _logger;

    private readonly ConfigurationSecretProtector _secretProtector;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ConfigurationWriter(ILogger<ConfigurationWriter> logger, ConfigurationSecretProtector secretProtector)
    {

        _logger = logger;

        _secretProtector = secretProtector;

    }

    public async Task<Result> WriteAsync(ArcanumSettings settings, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        string? tempPath = null;

        try
        {
            string directory = ArcanumPaths.GrimoireDirectory;

            string path = Path.Combine(directory, "arcanum.json");

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);

            tempPath = Path.Combine(directory, $".arcanum.{Guid.NewGuid():N}.tmp");

            ArcanumSettings storedSettings = _secretProtector.ProtectSettingsForStorage(settings);

            var wrapper = new ArcanumConfigurationFile { Arcanum = storedSettings };

            await using (FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    wrapper,
                    ConfigurationJsonContext.Default.ArcanumConfigurationFile,
                    cancellationToken).ConfigureAwait(false);

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);

            SecureFilePermissions.ApplyOwnerOnlyFile(path);

            tempPath = null;

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write configuration to {ConfigPath}", Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json"));

            return Result.Failure(new Error("Configuration.WriteFailed", ex.Message));
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temporary configuration file {TempPath}", tempPath);
                }
            }

            _writeLock.Release();
        }
    }

}
