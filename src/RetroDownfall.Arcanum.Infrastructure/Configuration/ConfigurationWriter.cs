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

        try
        {
            string directory = ArcanumPaths.GrimoireDirectory;

            string path = Path.Combine(directory, "arcanum.json");

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);

            string tempPath = Path.Combine(directory, $".arcanum.{Guid.NewGuid():N}.tmp");

            ArcanumSettings storedSettings = _secretProtector.ProtectSettingsForStorage(settings);

            var wrapper = new ArcanumConfigurationFile { Arcanum = storedSettings };

            AtomicReplaceStatus replaceStatus = await AtomicFile.ReplaceAsync(
                path,
                tempPath,
                (stream, ct) => JsonSerializer.SerializeAsync(
                    stream,
                    wrapper,
                    ConfigurationJsonContext.Default.ArcanumConfigurationFile,
                    ct),
                cancellationToken,
                afterReplace: () =>
                {
                    SecureFilePermissions.ApplyOwnerOnlyFile(path);

                    return true;
                }).ConfigureAwait(false);

            if (replaceStatus != AtomicReplaceStatus.Succeeded)
            {

                throw new IOException(
                    $"Atomic configuration replacement did not succeed ({replaceStatus}).");

            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write configuration to {ConfigPath}", Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json"));

            return Result.Failure(new Error("Configuration.WriteFailed", ex.Message));
        }
        finally
        {
            _writeLock.Release();
        }
    }

}
