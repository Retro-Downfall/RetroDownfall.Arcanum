using System.Security.Cryptography;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Serialization;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Configuration;

namespace RetroDownfall.Compendium.Ux.Services;

public sealed class ArcanumConfigurationStore : IArcanumConfigurationStore
{

    public const int MaxConfigurationBytes = ConfigurationBootstrapper.MaxConfigurationBytes;

    private const string ConfigurationFileName = "arcanum.json";

    private const string MissingConfigurationFingerprint = "missing";

    private const string UnreadableConfigurationFingerprintPrefix = "unreadable:";

    private static readonly TimeSpan WriteLockTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ExternalChangeDebounce = TimeSpan.FromMilliseconds(500);

    private readonly ConfigurationValidator _validator;

    private readonly FileSystemWatcher? _watcher;

    private readonly string _directory;

    private readonly string _filePath;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly object _debounceGate = new();

    private readonly object _fingerprintGate = new();

    private readonly Func<
        Func<Task<ConfigurationWriteResult>>,
        CancellationToken,
        Task<ConfigurationWriteResult>> _transactionRunner;

    private CancellationTokenSource? _debounceCts;

    private string? _acknowledgedFingerprint;

    private bool _disposed;

    public ArcanumConfigurationStore()
        : this(enableWatcher: true)
    {

    }

    internal ArcanumConfigurationStore(
        bool enableWatcher,
        Func<
            Func<Task<ConfigurationWriteResult>>,
            CancellationToken,
            Task<ConfigurationWriteResult>>? transactionRunner = null)
    {

        _validator = new ConfigurationValidator();

        _directory = ArcanumPaths.GrimoireDirectory;

        _filePath = Path.Combine(_directory, ConfigurationFileName);

        _transactionRunner = transactionRunner
            ?? RunConfigurationTransactionAsync;

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(_directory);

        if (!enableWatcher)
        {

            return;

        }

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

            AcknowledgeFingerprint(MissingConfigurationFingerprint);

            return new ArcanumSettings();

        }

        try
        {

            await using FileStream stream = OpenConfigurationForRead(_filePath);

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

            stream.Position = 0;

            string fingerprint = await ComputeStreamFingerprintAsync(stream, ct)
                .ConfigureAwait(false);

            AcknowledgeFingerprint(fingerprint);

            return wrapper?.Arcanum ?? new ArcanumSettings();

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (JsonException ex)
        {

            await AcknowledgeUnreadableAsync().ConfigureAwait(false);

            throw new InvalidOperationException(
                $"Failed to parse {_filePath}: {ex.Message}",
                ex);

        }
        catch (Exception)
        {

            await AcknowledgeUnreadableAsync().ConfigureAwait(false);

            throw;

        }

    }

    /// <summary>
    /// Records a fingerprint that can never match the on-disk bytes after a failed read, so a write
    /// attempted over a configuration that was never loaded is rejected by the stale-file guard even if
    /// the caller's own fail-closed gate is bypassed.
    /// </summary>
    private async Task AcknowledgeUnreadableAsync()
    {

        try
        {

            string fingerprint = await ReadCurrentFingerprintAsync(CancellationToken.None)
                .ConfigureAwait(false);

            AcknowledgeFingerprint($"{UnreadableConfigurationFingerprintPrefix}{fingerprint}");

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            AcknowledgeFingerprint(UnreadableConfigurationFingerprintPrefix);

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

        try
        {

            string expectedFingerprint = AcknowledgedFingerprint()
                ?? await ReadCurrentFingerprintAsync(ct).ConfigureAwait(false);

            return await _transactionRunner(
                    () => WriteUnderTransactionAsync(
                        settings,
                        expectedFingerprint,
                        ct),
                    ct)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            return new ConfigurationWriteResult(
                false,
                [],
                "The save was cancelled before it could acquire or complete the configuration transaction.");

        }
        catch (Exception ex)
        {

            return new ConfigurationWriteResult(false, [], ex.Message);

        }

    }

    private async Task<ConfigurationWriteResult> WriteUnderTransactionAsync(
        ArcanumSettings settings,
        string expectedFingerprint,
        CancellationToken ct)
    {

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

        string writtenFingerprint;

        try
        {

            ConfigurationWriteResult? changed = await RejectChangedConfigurationAsync(
                    expectedFingerprint,
                    ct)
                .ConfigureAwait(false);

            if (changed is not null)
            {

                return changed;

            }

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

                writtenFingerprint = await ReadFingerprintAsync(tempPath, ct)
                    .ConfigureAwait(false);

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

                changed = await RejectChangedConfigurationAsync(
                        expectedFingerprint,
                        ct)
                    .ConfigureAwait(false);

                if (changed is not null)
                {

                    return changed;

                }

                if (File.Exists(_filePath))
                {

                    File.Replace(tempPath, _filePath, destinationBackupFileName: null);

                }
                else
                {

                    File.Move(tempPath, _filePath);

                }

                // Hardening the staging file is not enough. On Windows ReplaceFile keeps the
                // replaced file's DACL, so a destination that arrived with a loose ACL would survive
                // the save still readable by other principals — the CLI's ConfigurationWriter
                // re-applies owner-only permissions to the destination for exactly this reason.
                SecureFilePermissions.ApplyOwnerOnlyFile(_filePath);

            }
            catch (IOException ioEx)
            {

                return new ConfigurationWriteResult(
                    false,
                    [],
                    $"Could not replace arcanum.json: the file is locked by another application. Close any other editors and try again. ({ioEx.Message})");

            }

            AcknowledgeFingerprint(writtenFingerprint);

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

    private static Task<ConfigurationWriteResult> RunConfigurationTransactionAsync(
        Func<Task<ConfigurationWriteResult>> operation,
        CancellationToken cancellationToken) =>
        ArcanumConfigurationTransaction.RunAsync(
            operation,
            cancellationToken);

    private async Task<ConfigurationWriteResult?> RejectChangedConfigurationAsync(
        string expectedFingerprint,
        CancellationToken cancellationToken)
    {

        if (expectedFingerprint.StartsWith(
            UnreadableConfigurationFingerprintPrefix,
            StringComparison.Ordinal))
        {

            return new ConfigurationWriteResult(
                false,
                [],
                "arcanum.json could not be read when it was loaded, so saving would replace it with default settings. Repair the file and reload the configuration before saving.");

        }

        string currentFingerprint = await ReadCurrentFingerprintAsync(cancellationToken)
            .ConfigureAwait(false);

        return string.Equals(
            currentFingerprint,
            expectedFingerprint,
            StringComparison.Ordinal)
            ? null
            : new ConfigurationWriteResult(
                false,
                [],
                "arcanum.json changed on disk after it was loaded. Reload the configuration, review the newer values, and save again.");

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

        await ProcessObservedChangeAsync(ct).ConfigureAwait(false);

    }

    internal async Task ProcessObservedChangeAsync(CancellationToken ct)
    {

        string observedFingerprint;

        try
        {

            observedFingerprint = await ReadCurrentFingerprintAsync(ct)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {

            return;

        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException)
        {

            RaiseExternalChange();

            return;

        }

        lock (_fingerprintGate)
        {

            if (string.Equals(
                observedFingerprint,
                _acknowledgedFingerprint,
                StringComparison.Ordinal))
            {

                return;

            }

        }

        RaiseExternalChange();

    }

    private async Task<string> ReadCurrentFingerprintAsync(CancellationToken ct)
    {

        if (!File.Exists(_filePath))
        {

            return MissingConfigurationFingerprint;

        }

        try
        {

            return await ReadFingerprintAsync(_filePath, ct).ConfigureAwait(false);

        }
        catch (FileNotFoundException)
        {

            return MissingConfigurationFingerprint;

        }
        catch (DirectoryNotFoundException)
        {

            return MissingConfigurationFingerprint;

        }

    }

    /// <summary>
    /// Opens arcanum.json for reading without denying a concurrent writer. Reads never join the
    /// cross-process configuration mutex, so a share mode that withheld write or delete access would fail
    /// the host's atomic replace on Windows; the fingerprint guard already catches a file that changed
    /// underneath an in-flight reader.
    /// </summary>
    internal static FileStream OpenConfigurationForRead(string path)
    {

        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    }

    private static async Task<string> ReadFingerprintAsync(
        string path,
        CancellationToken ct)
    {

        await using FileStream stream = OpenConfigurationForRead(path);

        if (stream.Length > MaxConfigurationBytes)
        {

            return $"oversized:{stream.Length}";

        }

        return await ComputeStreamFingerprintAsync(stream, ct)
            .ConfigureAwait(false);

    }

    private static async Task<string> ComputeStreamFingerprintAsync(
        Stream stream,
        CancellationToken ct)
    {

        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);

        return $"sha256:{Convert.ToHexString(hash)}";

    }

    private void AcknowledgeFingerprint(string fingerprint)
    {

        lock (_fingerprintGate)
        {

            _acknowledgedFingerprint = fingerprint;

        }

    }

    private string? AcknowledgedFingerprint()
    {

        lock (_fingerprintGate)
        {

            return _acknowledgedFingerprint;

        }

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
