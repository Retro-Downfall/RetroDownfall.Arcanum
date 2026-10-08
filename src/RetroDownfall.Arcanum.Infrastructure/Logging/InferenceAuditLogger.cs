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
    private const string DefaultStem = "audit";

    private readonly DailyJsonlAuditWriter _writer;

    private readonly IOptionsMonitor<ArcanumSettings> _optionsMonitor;

    private readonly ILogger<InferenceAuditLogger> _logger;

    private readonly string? _filePathOverride;

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

        _writer = new DailyJsonlAuditWriter(
            "inference",
            DefaultStem,
            logger,
            managedLogMutationGate);
    }

    public async Task LogAsync(InferenceAuditRecord record, CancellationToken cancellationToken)
    {
        HostAuditLogSettings config = ResolveConfig();

        if (!config.Enabled)
        {
            return;
        }

        await _writer.AppendAsync(
            record,
            AuditJsonContext.Default.InferenceAuditRecord,
            _filePathOverride ?? config.FilePath,
            config.MaxSizeMb,
            cancellationToken).ConfigureAwait(false);
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
            _writer.ResolvePathParts(_filePathOverride ?? config.FilePath);

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

    private HostAuditLogSettings ResolveConfig() =>
        _optionsMonitor.CurrentValue.ResolveHostAuditLog();

    /// <summary>
    /// Splits the configured <c>FilePath</c> into the directory and filename stem (default
    /// <c>audit</c>); see <see cref="DailyJsonlAuditWriter.ResolvePathParts(string, string)"/>.
    /// </summary>
    internal static (string Directory, string Stem) ResolvePathParts(string configuredPath) =>
        DailyJsonlAuditWriter.ResolvePathParts(configuredPath, DefaultStem);

    public void Dispose() => _writer.Dispose();
}
