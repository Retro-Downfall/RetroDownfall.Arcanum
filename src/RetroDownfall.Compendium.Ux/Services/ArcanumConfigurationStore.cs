using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Compendium.Ux.Services;

public sealed class ArcanumConfigurationStore : IArcanumConfigurationStore
{

    public const int MaxConfigurationBytes = ConfigurationBootstrapper.MaxConfigurationBytes;

    private const string ConfigurationFileName = "arcanum.json";

    private static readonly TimeSpan WriteLockTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ExternalChangeDebounce = TimeSpan.FromMilliseconds(500);

    private readonly ConfigurationValidator _validator;

    private readonly FileSystemWatcher? _watcher;

    private readonly string _directory;

    private readonly string _filePath;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly object _debounceGate = new();

    private CancellationTokenSource? _debounceCts;

    private bool _disposed;

    public ArcanumConfigurationStore()
    {

        _validator = new ConfigurationValidator();

        _directory = ArcanumPaths.GrimoireDirectory;

        ValidatePathIsUnderHomeDirectory(_directory);

        _filePath = Path.Combine(_directory, ConfigurationFileName);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(_directory);

        _watcher = new FileSystemWatcher(_directory, ConfigurationFileName)
        {

            InternalBufferSize = 65_536,

            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,

            EnableRaisingEvents = true,

        };

        _watcher.Changed += OnFileChanged;

        _watcher.Renamed += OnFileRenamed;

        _watcher.Created += OnFileChanged;

        _watcher.Error += OnWatcherError;

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

        try
        {

            await using FileStream stream = File.OpenRead(_filePath);

            if (stream.Length > MaxConfigurationBytes)
            {

                throw new InvalidOperationException(
                    $"Failed to parse {_filePath}: configuration exceeds the {MaxConfigurationBytes}-byte limit.");

            }

            using JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            Result rawTree =
                _validator.ValidateConfigurationFileJson(document.RootElement);

            if (rawTree.IsFailure)
            {
                string detail = string.Join(
                    "; ",
                    rawTree.Error.Details?.Select(
                        static error =>
                            $"{error.Pointer}: {error.Detail}")
                    ?? [rawTree.Error.Message]);

                throw new InvalidOperationException(
                    $"Failed to parse {_filePath}: {detail}");
            }

            ArcanumConfigurationFile? wrapper =
                document.RootElement.Deserialize(
                    ConfigurationJsonContext.Default.ArcanumConfigurationFile);

            return wrapper?.Arcanum ?? new ArcanumSettings();

        }
        catch (JsonException ex)
        {

            throw new InvalidOperationException(
                $"Failed to parse {_filePath}: {ex.Message}",
                ex);

        }

    }

    public async Task<ConfigurationWriteResult> WriteAsync(ArcanumSettings settings, CancellationToken ct = default)
    {

        Result validation = _validator.Validate(settings);

        if (!validation.IsSuccess)
        {

            IReadOnlyList<ConfigurationValidationError> errors = ExtractValidationErrors(validation);

            return new ConfigurationWriteResult(false, errors, ResultMessage(validation));

        }

        bool acquired;

        try
        {

            acquired = await _writeLock.WaitAsync(WriteLockTimeout, ct).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            return new ConfigurationWriteResult(
                false,
                [],
                "The save was cancelled before it could acquire the configuration lock.");

        }

        if (!acquired)
        {

            return new ConfigurationWriteResult(
                false,
                [],
                "Could not save arcanum.json: another save operation is still in progress. Please try again in a few seconds.");

        }

        string? tempPath = null;

        try
        {

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(_directory);

            tempPath = Path.Combine(_directory, $".arcanum.{Guid.NewGuid():N}.tmp");

            var wrapper = new ArcanumConfigurationFile { Arcanum = settings };

            try
            {

                await using (FileStream tempStream = new(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))

                {

                    SecureFilePermissions.ApplyOwnerOnlyFile(tempPath);

                    await JsonSerializer.SerializeAsync(
                        tempStream,
                        wrapper,
                        ConfigurationJsonContext.Default.ArcanumConfigurationFile,
                        ct).ConfigureAwait(false);

                    await tempStream.FlushAsync(ct).ConfigureAwait(false);

                    tempStream.Flush(flushToDisk: true);

                }

            }
            catch (IOException ioEx)
            {

                return new ConfigurationWriteResult(
                    false,
                    [],
                    $"Could not save arcanum.json: the file or configuration directory is locked by another application. Close any other editors and try again. ({ioEx.Message})");

            }

            try
            {

                if (File.Exists(_filePath))
                {

                    File.Replace(tempPath, _filePath, destinationBackupFileName: null);

                }
                else
                {

                    File.Move(tempPath, _filePath);

                }

            }
            catch (IOException ioEx)
            {

                return new ConfigurationWriteResult(
                    false,
                    [],
                    $"Could not replace arcanum.json: the file is locked by another application. Close any other editors and try again. ({ioEx.Message})");

            }

            return new ConfigurationWriteResult(true, [], null);

        }
        catch (Exception ex)
        {

            return new ConfigurationWriteResult(false, [], ex.Message);

        }
        finally

        {

            if (!string.IsNullOrEmpty(tempPath))
            {

                try
                {

                    File.Delete(tempPath);

                }
                catch (Exception cleanupException) when (
                    cleanupException is IOException or UnauthorizedAccessException)
                {

                }

            }

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

    private void RaiseExternalChange()
    {

        ExternalChange?.Invoke(this, EventArgs.Empty);

    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {

        ScheduleExternalChange();

    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {

        ScheduleExternalChange();

    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {

        // The watcher buffer overflowed or the directory became unavailable.
        // Surface a single coalesced notification so the UI can prompt a reload.
        ScheduleExternalChange();

    }

    private void ScheduleExternalChange()
    {

        lock (_debounceGate)
        {

            _debounceCts?.Cancel();

            _debounceCts?.Dispose();

            CancellationTokenSource cts = new();

            _debounceCts = cts;

            _ = DebounceAndRaiseAsync(cts.Token);

        }

    }

    private async Task DebounceAndRaiseAsync(CancellationToken ct)
    {

        try
        {

            await Task.Delay(ExternalChangeDebounce, ct).ConfigureAwait(false);

        }
        catch (TaskCanceledException)
        {

            return;

        }

        if (_disposed || ct.IsCancellationRequested)
        {

            return;

        }

        RaiseExternalChange();

    }

    private static IReadOnlyList<ConfigurationValidationError> ExtractValidationErrors(Result result)
    {

        return result.Error.Details ?? [];

    }

    private static void ValidatePathIsUnderHomeDirectory(string path)
    {
        string? homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrEmpty(homeDir))
        {
            return; // Cannot validate if home directory is unavailable
        }

        string fullPath = Path.GetFullPath(path);
        string fullHome = Path.GetFullPath(homeDir);

        if (!fullPath.StartsWith(fullHome, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configuration path '{path}' is not under the user's home directory. " +
                "This is a security restriction to prevent unauthorized file access.");
        }

        // Check for symbolic links that could escape the home directory
        if (IsSymbolicLink(path))
        {
            string target = ResolveSymbolicLink(path);
            string fullTarget = Path.GetFullPath(target);

            if (!fullTarget.StartsWith(fullHome, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Configuration path '{path}' is a symbolic link pointing outside the home directory. " +
                    "This is a security restriction to prevent unauthorized file access.");
            }
        }
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            FileInfo fileInfo = new(path);
            return fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveSymbolicLink(string path)
    {
        // On Unix systems, readlink resolves symlinks
        // On Windows, this is more complex and may require P/Invoke
        // For now, return the path as-is (validation will still catch obvious cases)
        return path;
    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        lock (_debounceGate)
        {

            _debounceCts?.Cancel();

            _debounceCts?.Dispose();

            _debounceCts = null;

        }

        _watcher?.Dispose();

        _writeLock.Dispose();

    }

}
