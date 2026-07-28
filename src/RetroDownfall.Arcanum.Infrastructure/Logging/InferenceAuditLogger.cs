using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

/// <summary>
/// Persisted inference audit log (§8.26) — a durable, append-only JSONL trail of completed
/// inference turns, one file per UTC day (<c>{stem}-{yyyyMMdd}.jsonl</c>). Registered as a
/// singleton; a single in-process <see cref="SemaphoreSlim"/> serializes writes (Arcanum is a
/// single-process host, so no cross-process locking is needed). A complete no-op — no file I/O at
/// all — when <c>Arcanum:Host:AuditLog:Enabled</c> is <see langword="false"/> (the default).
/// </summary>
public sealed class InferenceAuditLogger(
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    ILogger<InferenceAuditLogger> logger,
    string? filePathOverride = null) : IInferenceAuditLogger, IDisposable
{

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private string? _lastPreparedDateStamp;

    private bool _sizeCapWarnedForCurrentDate;

    public async Task LogAsync(InferenceAuditRecord record, CancellationToken cancellationToken)
    {

        HostAuditLogSettings config = ResolveConfig();

        if (!config.Enabled)
        {

            return;

        }

        try
        {

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {

                (string directory, string stem) =
                    ResolvePathParts(filePathOverride ?? config.FilePath);

                string dateStamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

                if (!string.Equals(_lastPreparedDateStamp, dateStamp, StringComparison.Ordinal))
                {

                    PrepareForNewDate(directory, stem, dateStamp, config.RetentionDays);

                }

                string filePath = Path.Combine(directory, $"{stem}-{dateStamp}.jsonl");

                long maxSizeBytes = (long)ArcanumSettingClamps.HostAuditLogMaxSizeMb(config.MaxSizeMb) * 1024L * 1024L;

                if (File.Exists(filePath) && new FileInfo(filePath).Length >= maxSizeBytes)
                {

                    if (!_sizeCapWarnedForCurrentDate)
                    {

                        logger.LogWarning(
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

            logger.LogWarning(ex, "Failed to write inference audit log entry.");

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

        HostAuditLogSettings config = ResolveConfig();

        if (!config.Enabled)
        {

            return [];

        }

        (string directory, string stem) =
            ResolvePathParts(filePathOverride ?? config.FilePath);

        if (!Directory.Exists(directory))
        {

            return [];

        }

        DateTimeOffset effectiveTo = to ?? DateTimeOffset.UtcNow;

        DateTimeOffset effectiveFrom = from
            ?? effectiveTo.AddDays(-ArcanumSettingClamps.HostAuditLogRetentionDays(config.RetentionDays));

        string fromStamp = effectiveFrom.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        List<InferenceAuditRecord> results = [];

        DateTimeOffset cursor = effectiveTo;

        int daySafetyCounter = 0;

        while (results.Count < limit && daySafetyCounter < 400)
        {

            string dateStamp = cursor.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

            if (string.CompareOrdinal(dateStamp, fromStamp) < 0)
            {

                break;

            }

            string filePath = Path.Combine(directory, $"{stem}-{dateStamp}.jsonl");

            if (File.Exists(filePath))
            {

                await ReadMatchingRecordsAsync(
                    filePath,
                    effectiveFrom,
                    effectiveTo,
                    model,
                    sessionId,
                    limit,
                    results,
                    cancellationToken).ConfigureAwait(false);

            }

            cursor = cursor.AddDays(-1);

            daySafetyCounter++;

        }

        return results;

    }

    private async Task ReadMatchingRecordsAsync(
        string filePath,
        DateTimeOffset effectiveFrom,
        DateTimeOffset effectiveTo,
        string? model,
        string? sessionId,
        int limit,
        List<InferenceAuditRecord> sink,
        CancellationToken cancellationToken)
    {

        string[] lines;

        try
        {

            lines = await File.ReadAllLinesAsync(filePath, cancellationToken).ConfigureAwait(false);

        }
        catch (IOException ex)
        {

            // Being actively written concurrently (or transient FS issue) — skip this file for this
            // query rather than failing the whole request.
            logger.LogDebug(ex, "Could not read inference audit log file {FilePath} for this query; skipping.", filePath);

            return;

        }

        // Records are appended chronologically; walk backwards for newest-first results.
        for (int i = lines.Length - 1; i >= 0 && sink.Count < limit; i--)
        {

            string line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {

                continue;

            }

            InferenceAuditRecord? record;

            try
            {

                record = JsonSerializer.Deserialize(line, AuditJsonContext.Default.InferenceAuditRecord);

            }
            catch (JsonException)
            {

                // A partial line from a write that raced this read (e.g. mid-flush) — skip it.
                continue;

            }

            if (record is null)
            {

                continue;

            }

            if (!DateTimeOffset.TryParse(
                    record.Timestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset recordTimestamp))
            {

                continue;

            }

            if (recordTimestamp < effectiveFrom || recordTimestamp > effectiveTo)
            {

                continue;

            }

            if (model is not null && !string.Equals(record.Model, model, StringComparison.OrdinalIgnoreCase))
            {

                continue;

            }

            if (sessionId is not null && !string.Equals(record.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {

                continue;

            }

            sink.Add(record);

        }

    }

    private void PrepareForNewDate(string directory, string stem, string dateStamp, int retentionDays)
    {

        _lastPreparedDateStamp = dateStamp;

        _sizeCapWarnedForCurrentDate = false;

        Directory.CreateDirectory(directory);

        SecureFilePermissions.ApplyOwnerOnlyDirectory(directory);

        SweepOldFiles(directory, stem, ArcanumSettingClamps.HostAuditLogRetentionDays(retentionDays));

    }

    private void SweepOldFiles(string directory, string stem, int retentionDays)
    {

        try
        {

            DateTime cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);

            string searchPattern = $"{stem}-????????.jsonl";

            foreach (string file in Directory.EnumerateFiles(directory, searchPattern))
            {

                string fileNameNoExt = Path.GetFileNameWithoutExtension(file);

                if (fileNameNoExt.Length <= stem.Length + 1)
                {

                    continue;

                }

                string datePart = fileNameNoExt[(stem.Length + 1)..];

                if (DateTime.TryParseExact(
                        datePart,
                        "yyyyMMdd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTime fileDateUtc)
                    && fileDateUtc < cutoffUtc)
                {

                    File.Delete(file);

                }

            }

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "Failed to sweep expired inference audit log files in {Directory}.", directory);

        }

    }

    private HostAuditLogSettings ResolveConfig() =>
        optionsMonitor.CurrentValue.ResolveHostAuditLog();

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
