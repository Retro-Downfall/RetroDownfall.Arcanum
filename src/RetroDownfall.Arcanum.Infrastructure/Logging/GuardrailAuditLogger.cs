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
/// Persisted guardrails audit log (Tier 3 Phase 4, §8.x) — a durable, append-only JSONL trail of
/// guardrail violations that blocked an inference turn, one file per UTC day
/// (<c>{stem}-{yyyyMMdd}.jsonl</c>). Registered as a singleton; a private in-process
/// <see cref="SemaphoreSlim"/> serializes same-family writes, while a shared managed-log gate orders
/// publication against factory reset. A complete no-op — no file I/O at all — when
/// <c>Arcanum:Security:Guardrails:AuditLog:Enabled</c> is <see langword="false"/> (the default).
/// Independent of <see cref="InferenceAuditLogger"/> (which records completed turns): this records
/// only the violations that rejected a turn, and only when <c>Arcanum:Features:Guardrails</c> is
/// also <see langword="true"/>.
/// </summary>
public sealed class GuardrailAuditLogger : IGuardrailAuditLogger, IDisposable
{

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly IOptionsMonitor<ArcanumSettings> _optionsMonitor;

    private readonly ILogger<GuardrailAuditLogger> _logger;

    private readonly string? _filePathOverride;

    private readonly IManagedLogMutationGate _managedLogMutationGate;

    private string? _lastPreparedDateStamp;

    private bool _sizeCapWarnedForCurrentDate;

    public GuardrailAuditLogger(
        IOptionsMonitor<ArcanumSettings> optionsMonitor,
        ILogger<GuardrailAuditLogger> logger,
        string? filePathOverride = null) :
        this(
            optionsMonitor,
            logger,
            filePathOverride,
            new ManagedLogMutationGate())
    {

    }

    internal GuardrailAuditLogger(
        IOptionsMonitor<ArcanumSettings> optionsMonitor,
        ILogger<GuardrailAuditLogger> logger,
        string? filePathOverride,
        IManagedLogMutationGate managedLogMutationGate)
    {

        _optionsMonitor = optionsMonitor;

        _logger = logger;

        _filePathOverride = filePathOverride;

        _managedLogMutationGate = managedLogMutationGate;

    }

    public async Task LogAsync(GuardrailAuditRecord record, CancellationToken cancellationToken)
    {

        GuardrailsAuditLogSettings config =
            _optionsMonitor.CurrentValue.ResolveGuardrails().AuditLog;

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
                            "Guardrails audit log {FilePath} reached its {MaxSizeMb} MB size cap; further entries for today are dropped.",
                            filePath,
                            config.MaxSizeMb);

                        _sizeCapWarnedForCurrentDate = true;
                    }

                    return;
                }

                string json = JsonSerializer.Serialize(record, AuditJsonContext.Default.GuardrailAuditRecord);

                await File.AppendAllTextAsync(filePath, json + "\n", cancellationToken).ConfigureAwait(false);

                SecureFilePermissions.ApplyOwnerOnlyFile(filePath);

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
            _logger.LogWarning(ex, "Failed to write guardrails audit log entry.");

        }

    }

    public async Task<IReadOnlyList<GuardrailAuditRecord>> QueryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? stage,
        string? violationType,
        string? sessionId,
        int limit,
        CancellationToken cancellationToken)
    {

        Result<AuditQueryPage<GuardrailAuditRecord>> page = await QueryPageAsync(
            from,
            to,
            stage,
            violationType,
            sessionId,
            limit,
            cursor: null,
            cancellationToken).ConfigureAwait(false);

        return page.IsSuccess
            ? page.Value.Records
            : [];

    }

    public async Task<Result<AuditQueryPage<GuardrailAuditRecord>>> QueryPageAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? stage,
        string? violationType,
        string? sessionId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {

        GuardrailsAuditLogSettings config =
            _optionsMonitor.CurrentValue.ResolveGuardrails().AuditLog;

        if (!config.Enabled)
        {

            return Result<AuditQueryPage<GuardrailAuditRecord>>.Success(
                new AuditQueryPage<GuardrailAuditRecord>([], null));

        }

        (string directory, string stem) =
            ResolvePathParts(_filePathOverride ?? config.FilePath);

        if (!Directory.Exists(directory))
        {

            return string.IsNullOrWhiteSpace(cursor)
                ? Result<AuditQueryPage<GuardrailAuditRecord>>.Success(
                    new AuditQueryPage<GuardrailAuditRecord>([], null))
                : Result<AuditQueryPage<GuardrailAuditRecord>>.Failure(
                    new Error(
                        ErrorCodes.Validation.InvalidQuery,
                        "The audit cursor no longer references retained log data. Restart without 'cursor'."));

        }

        return await AuditLogPageReader.QueryAsync(
            directory,
            stem,
            family: "guardrail",
            from,
            to,
            limit,
            cursor,
            AuditJsonContext.Default.GuardrailAuditRecord,
            static record => record.Timestamp,
            record =>
                (stage is null
                    || string.Equals(record.Stage, stage, StringComparison.OrdinalIgnoreCase))
                && (violationType is null
                    || string.Equals(record.ViolationType, violationType, StringComparison.OrdinalIgnoreCase))
                && (sessionId is null
                    || string.Equals(record.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)),
            _logger,
            cancellationToken,
            stage,
            violationType,
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

        try
        {

            Directory.CreateDirectory(directory);

            SecureFilePermissions.ApplyOwnerOnlyDirectory(directory);

        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Failed to create or secure guardrails audit log directory {Directory}; audit entries for {DateStamp} will be dropped.", directory, dateStamp);

            return;

        }

        _lastPreparedDateStamp = dateStamp;

        _sizeCapWarnedForCurrentDate = false;

    }

    private static (string Directory, string Stem) ResolvePathParts(string configuredPath)
    {

        string? directory = Path.GetDirectoryName(configuredPath);

        string stem = Path.GetFileNameWithoutExtension(configuredPath);

        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "guardrails";
        }

        return (string.IsNullOrWhiteSpace(directory) ? "." : directory, stem);

    }

    public void Dispose() => _writeLock.Dispose();

}
