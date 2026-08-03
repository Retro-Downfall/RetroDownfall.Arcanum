using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

/// <summary>
/// Persisted inference audit log (§8.26) — a durable, append-only JSONL trail of completed
/// inference turns, one file per UTC day (<c>{stem}-{yyyyMMdd}.jsonl</c>). Registered as a
/// singleton; a private in-process <see cref="SemaphoreSlim"/> serializes same-family writes, while
/// a shared managed-log gate orders publication against factory reset (Arcanum is a single-process
/// host, so no cross-process locking is needed). A complete no-op — no file I/O at all — when
/// <c>Arcanum:Host:AuditLog:Enabled</c> is <see langword="false"/> (the default).
/// </summary>
public sealed class InferenceAuditLogger : IInferenceAuditLogger, IDisposable
{

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly IOptionsMonitor<ArcanumSettings> _optionsMonitor;

    private readonly ILogger<InferenceAuditLogger> _logger;

    private readonly string? _filePathOverride;

    private readonly IManagedLogMutationGate _managedLogMutationGate;

    private string? _lastPreparedDateStamp;

    private bool _sizeCapWarnedForCurrentDate;

    public InferenceAuditLogger(
        IOptionsMonitor<ArcanumSettings> optionsMonitor,
        ILogger<InferenceAuditLogger> logger,
        string? filePathOverride = null) :
        this(
            optionsMonitor,
            logger,
            filePathOverride,
            new ManagedLogMutationGate())
    {

    }

    internal InferenceAuditLogger(
        IOptionsMonitor<ArcanumSettings> optionsMonitor,
        ILogger<InferenceAuditLogger> logger,
        string? filePathOverride,
        IManagedLogMutationGate managedLogMutationGate)
    {

        _optionsMonitor = optionsMonitor;

        _logger = logger;

        _filePathOverride = filePathOverride;

        _managedLogMutationGate = managedLogMutationGate;

    }

    public async Task LogAsync(InferenceAuditRecord record, CancellationToken cancellationToken)
    {

        HostAuditLogSettings config = ResolveConfig();

        if (!config.Enabled)
        {

            return;

        }

        try
        {

            await using IAsyncDisposable managedLogLease =
                await _managedLogMutationGate.AcquireExclusiveAsync(
                    cancellationToken).ConfigureAwait(false);

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {

                (string directory, string stem) =
                    ResolvePathParts(_filePathOverride ?? config.FilePath);

                string dateStamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

                if (!string.Equals(_lastPreparedDateStamp, dateStamp, StringComparison.Ordinal))
                {

                    PrepareForNewDate(directory, dateStamp);

                }

                string filePath = Path.Combine(directory, $"{stem}-{dateStamp}.jsonl");

                long maxSizeBytes = (long)ArcanumSettingClamps.HostAuditLogMaxSizeMb(config.MaxSizeMb) * 1024L * 1024L;

                if (File.Exists(filePath) && new FileInfo(filePath).Length >= maxSizeBytes)
                {

                    if (!_sizeCapWarnedForCurrentDate)
                    {

                        _logger.LogWarning(
                            "Inference audit log {FilePath} reached its {MaxSizeMb} MB size cap; further entries for today are dropped.",
                            filePath,
                            config.MaxSizeMb);

                        _sizeCapWarnedForCurrentDate = true;

                    }

                    return;

                }

                bool isNewFile = !File.Exists(filePath);

                string json = JsonSerializer.Serialize(record, AuditJsonContext.Default.InferenceAuditRecord);

                await File.AppendAllTextAsync(filePath, json + "\n", cancellationToken).ConfigureAwait(false);

                if (isNewFile)
                {

                    SecureFilePermissions.ApplyOwnerOnlyFile(filePath);

                }

            }
            finally
            {

                _writeLock.Release();

            }

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            _logger.LogWarning(ex, "Failed to write inference audit log entry.");

        }

    }

    public async Task<IReadOnlyList<InferenceAuditRecord>> QueryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? model,
        string? sessionId,
        int limit,
        CancellationToken cancellationToken)
    {

        Result<AuditQueryPage<InferenceAuditRecord>> page = await QueryPageAsync(
            from,
            to,
            model,
            sessionId,
            limit,
            cursor: null,
            cancellationToken).ConfigureAwait(false);

        return page.IsSuccess
            ? page.Value.Records
            : [];

    }

    public async Task<Result<AuditQueryPage<InferenceAuditRecord>>> QueryPageAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? model,
        string? sessionId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {

        HostAuditLogSettings config = ResolveConfig();

        if (!config.Enabled)
        {

            return Result<AuditQueryPage<InferenceAuditRecord>>.Success(
                new AuditQueryPage<InferenceAuditRecord>([], null));

        }

        (string directory, string stem) =
            ResolvePathParts(_filePathOverride ?? config.FilePath);

        if (!Directory.Exists(directory))
        {

            return string.IsNullOrWhiteSpace(cursor)
                ? Result<AuditQueryPage<InferenceAuditRecord>>.Success(
                    new AuditQueryPage<InferenceAuditRecord>([], null))
                : Result<AuditQueryPage<InferenceAuditRecord>>.Failure(
                    new Error(
                        ErrorCodes.Validation.InvalidQuery,
                        "The audit cursor no longer references retained log data. Restart without 'cursor'."));

        }

        return await AuditLogPageReader.QueryAsync(
            directory,
            stem,
            family: "inference",
            from,
            to,
            limit,
            cursor,
            AuditJsonContext.Default.InferenceAuditRecord,
            static record => record.Timestamp,
            record =>
                (model is null
                    || string.Equals(record.Model, model, StringComparison.OrdinalIgnoreCase))
                && (sessionId is null
                    || string.Equals(record.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)),
            _logger,
            cancellationToken,
            model,
            sessionId).ConfigureAwait(false);

    }

    private static IEnumerable<string> EnumerateDatedLogFiles(
        string directory,
        string stem,
        DateTimeOffset from,
        DateTimeOffset to)
    {

        string prefix = stem + "-";

        foreach ((string Path, DateTimeOffset Date) candidate in Directory
                     .EnumerateFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly)
                     .Select(path => (Path: path, Date: ParseDatedLogFile(path, prefix)))
                     .Where(static candidate => candidate.Date is not null)
                     .Select(static candidate => (candidate.Path, candidate.Date!.Value))
                     .Where(candidate => candidate.Item2.Date >= from.Date && candidate.Item2.Date <= to.Date)
                     .OrderByDescending(static candidate => candidate.Item2))
        {

            yield return candidate.Path;

        }

    }

    private static DateTimeOffset? ParseDatedLogFile(string path, string prefix)
    {

        string name = Path.GetFileNameWithoutExtension(path);

        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || name.Length != prefix.Length + 8)
        {

            return null;

        }

        return DateTimeOffset.TryParseExact(
            name.AsSpan(prefix.Length),
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out DateTimeOffset date)
            ? date
            : null;

    }

    private void PrepareForNewDate(string directory, string dateStamp)
    {

        _lastPreparedDateStamp = dateStamp;

        _sizeCapWarnedForCurrentDate = false;

        Directory.CreateDirectory(directory);

        SecureFilePermissions.ApplyOwnerOnlyDirectory(directory);

    }

    private HostAuditLogSettings ResolveConfig() =>
        _optionsMonitor.CurrentValue.ResolveHostAuditLog();

    /// <summary>
    /// Splits the configured <c>FilePath</c> into the directory to write dated files into and the
    /// filename stem (default <c>audit</c>) combined with a UTC date to produce each day's file —
    /// honors the documented default (<c>~/.config/arcanum/audit.jsonl</c>) while implementing
    /// date-based rotation rather than one ever-growing file.
    /// </summary>
    private static (string Directory, string Stem) ResolvePathParts(string configuredPath)
    {

        string? directory = Path.GetDirectoryName(configuredPath);

        string stem = Path.GetFileNameWithoutExtension(configuredPath);

        if (string.IsNullOrWhiteSpace(stem))
        {

            stem = "audit";

        }

        return (string.IsNullOrWhiteSpace(directory) ? "." : directory, stem);

    }

    public void Dispose() => _writeLock.Dispose();

}
