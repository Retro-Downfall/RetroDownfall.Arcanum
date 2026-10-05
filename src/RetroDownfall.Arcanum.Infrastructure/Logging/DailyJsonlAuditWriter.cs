using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

/// <summary>
/// Append-only writer shared by the inference and guardrails audit logs: one JSONL file per UTC day
/// (<c>{stem}-{yyyyMMdd}.jsonl</c>) with a soft size cap. A private in-process <see cref="SemaphoreSlim"/>
/// serializes same-family writes, while the shared managed-log gate orders publication against factory
/// reset.
///
/// Permissions are applied only to what the writer creates: a new day file is owner-only, and so is
/// every directory the writer had to create, intermediate parents included, each created with that
/// posture rather than chmod-ed afterwards. A directory that already exists, or that another process
/// creates first, belongs to whoever configured the <c>FilePath</c> (it may be a shared log directory),
/// so its mode is never changed.
/// </summary>
internal sealed class DailyJsonlAuditWriter(
    string logName,
    string defaultStem,
    ILogger logger,
    IManagedLogMutationGate managedLogMutationGate) : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private string? _lastPreparedDateStamp;

    private bool _sizeCapWarnedForCurrentDate;

    /// <summary>
    /// Splits the configured <c>FilePath</c> into the directory to write dated files into and the
    /// filename stem combined with a UTC date to produce each day's file — honors the documented default
    /// (for example <c>~/.config/arcanum/audit.jsonl</c>) while implementing date-based rotation rather
    /// than one ever-growing file.
    /// </summary>
    internal static (string Directory, string Stem) ResolvePathParts(string configuredPath, string defaultStem)
    {
        string? directory = Path.GetDirectoryName(configuredPath);

        string stem = Path.GetFileNameWithoutExtension(configuredPath);

        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = defaultStem;
        }

        return (string.IsNullOrWhiteSpace(directory) ? "." : directory, stem);
    }

    internal (string Directory, string Stem) ResolvePathParts(string configuredPath) =>
        ResolvePathParts(configuredPath, defaultStem);

    /// <summary>
    /// Appends one record to today's file. Failures other than cancellation are logged and swallowed:
    /// an audit write never fails the turn it describes.
    /// </summary>
    internal async Task AppendAsync<TRecord>(
        TRecord record,
        JsonTypeInfo<TRecord> typeInfo,
        string configuredFilePath,
        int configuredMaxSizeMb,
        CancellationToken cancellationToken)
    {
        try
        {
            await using IAsyncDisposable managedLogLease =
                await managedLogMutationGate.AcquireExclusiveAsync(
                    cancellationToken).ConfigureAwait(false);

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                (string directory, string stem) = ResolvePathParts(configuredFilePath);

                string dateStamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

                if (!string.Equals(_lastPreparedDateStamp, dateStamp, StringComparison.Ordinal))
                {
                    PrepareForNewDate(directory, dateStamp);
                }

                string filePath = Path.Combine(directory, $"{stem}-{dateStamp}.jsonl");

                long maxSizeBytes = (long)ArcanumSettingClamps.HostAuditLogMaxSizeMb(configuredMaxSizeMb) * 1024L * 1024L;

                bool fileExists = File.Exists(filePath);

                if (fileExists && new FileInfo(filePath).Length >= maxSizeBytes)
                {
                    if (!_sizeCapWarnedForCurrentDate)
                    {
                        logger.LogWarning(
                            "The {LogName} audit log {FilePath} reached its {MaxSizeMb} MB size cap; further entries for today are dropped.",
                            logName,
                            filePath,
                            configuredMaxSizeMb);

                        _sizeCapWarnedForCurrentDate = true;
                    }

                    return;
                }

                string json = JsonSerializer.Serialize(record, typeInfo);

                await SecureFilePermissions.AppendOwnerOnlyTextAsync(filePath, json + "\n", cancellationToken).ConfigureAwait(false);

                if (!fileExists)
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
            logger.LogWarning(ex, "Failed to write {LogName} audit log entry.", logName);
        }
    }

    private void PrepareForNewDate(string directory, string dateStamp)
    {
        try
        {
            SecureFilePermissions.CreateMissingOwnerOnlyDirectories(directory);
        }
        catch (Exception ex)
        {
            // Leave the date unmarked so the next turn retries preparation once the transient cause
            // clears, rather than silently dropping the rest of the UTC day's audit trail.
            logger.LogError(ex, "Failed to create or secure {LogName} audit log directory {Directory}; audit entries for {DateStamp} will be dropped.", logName, directory, dateStamp);

            return;
        }

        _lastPreparedDateStamp = dateStamp;

        _sizeCapWarnedForCurrentDate = false;
    }

    public void Dispose() => _writeLock.Dispose();
}
