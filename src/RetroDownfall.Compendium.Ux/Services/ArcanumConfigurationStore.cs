using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Compendium.Ux.Services;

public sealed class ArcanumConfigurationStore : IArcanumConfigurationStore
{

    private const string ConfigurationFileName = "arcanum.json";

    private readonly IArcanumSecretProtector _secretProtector;

    private readonly ConfigurationValidator _validator;

    private readonly FileSystemWatcher? _watcher;

    private readonly string _directory;

    private readonly string _filePath;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private bool _disposed;

    public ArcanumConfigurationStore(IArcanumSecretProtector secretProtector)
    {

        _secretProtector = secretProtector;

        _validator = new ConfigurationValidator();

        _directory = ArcanumPaths.GrimoireDirectory;

        _filePath = Path.Combine(_directory, ConfigurationFileName);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(_directory);

        _watcher = new FileSystemWatcher(_directory, ConfigurationFileName)
        {

            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,

            EnableRaisingEvents = true,

        };

        _watcher.Changed += OnFileChanged;

        _watcher.Renamed += OnFileRenamed;

        _watcher.Created += OnFileChanged;

    }

    public string ConfigurationFilePath => _filePath;

    public DateTimeOffset? GetLastWriteTimeUtc()
    {

        if (!File.Exists(_filePath))
        {

            return null;

        }

        return File.GetLastWriteTimeUtc(_filePath);

    }

    public async Task<ArcanumSettings> ReadAsync(CancellationToken ct = default)
    {

        if (!File.Exists(_filePath))
        {

            return new ArcanumSettings();

        }

        await using FileStream stream = File.OpenRead(_filePath);

        ArcanumConfigurationFile? wrapper = await JsonSerializer.DeserializeAsync(
            stream,
            ConfigurationJsonContext.Default.ArcanumConfigurationFile,
            ct).ConfigureAwait(false);

        ArcanumSettings settings = wrapper?.Arcanum ?? new ArcanumSettings();

        return _secretProtector.DecryptProviderKeys(settings);

    }

    public async Task<ConfigurationWriteResult> WriteAsync(ArcanumSettings settings, CancellationToken ct = default)
    {

        Result validation = _validator.Validate(settings);

        if (!validation.IsSuccess)
        {

            IReadOnlyList<ConfigurationValidationError> errors = ExtractValidationErrors(validation);

            return new ConfigurationWriteResult(false, errors, ResultMessage(validation));

        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(_directory);

            string tempPath = Path.Combine(_directory, $".arcanum.{Guid.NewGuid():N}.tmp");

            ArcanumSettings encrypted = _secretProtector.EncryptProviderKeys(settings);

            var wrapper = new ArcanumConfigurationFile { Arcanum = encrypted };

            await using (FileStream tempStream = File.Create(tempPath))

            {

                await JsonSerializer.SerializeAsync(
                    tempStream,
                    wrapper,
                    ConfigurationJsonContext.Default.ArcanumConfigurationFile,
                    ct).ConfigureAwait(false);

            }

            SecureFilePermissions.ApplyOwnerOnlyFile(tempPath);

            File.Replace(tempPath, _filePath, destinationBackupFileName: null);

            return new ConfigurationWriteResult(true, [], null);

        }
        catch (Exception ex)

        {

            return new ConfigurationWriteResult(false, [], ex.Message);

        }
        finally

        {

            _writeLock.Release();

        }

    }

    private static string? ResultMessage(Result result)
    {

        if (result.IsSuccess)

        {

            return null;

        }

        return result.Error.Message;

    }

    public event EventHandler? ExternalChange;

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {

        ExternalChange?.Invoke(this, EventArgs.Empty);

    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {

        ExternalChange?.Invoke(this, EventArgs.Empty);

    }

    private static IReadOnlyList<ConfigurationValidationError> ExtractValidationErrors(Result result)
    {

        return result.Error.Details ?? [];

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        _watcher?.Dispose();

        _writeLock.Dispose();

    }

}
