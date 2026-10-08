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
    private const string DefaultStem = "guardrails";

    private readonly DailyJsonlAuditWriter _writer;

    private readonly IOptionsMonitor<ArcanumSettings> _optionsMonitor;

    private readonly ILogger<GuardrailAuditLogger> _logger;

    private readonly string? _filePathOverride;

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

        _writer = new DailyJsonlAuditWriter(
            "guardrails",
            DefaultStem,
            logger,
            managedLogMutationGate);
    }

    public async Task LogAsync(GuardrailAuditRecord record, CancellationToken cancellationToken)
    {
        GuardrailsAuditLogSettings config =
            _optionsMonitor.CurrentValue.ResolveGuardrails().AuditLog;

        if (!config.Enabled)
        {
            return;
        }

        await _writer.AppendAsync(
            record,
            AuditJsonContext.Default.GuardrailAuditRecord,
            _filePathOverride ?? config.FilePath,
            config.MaxSizeMb,
            cancellationToken).ConfigureAwait(false);
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
            _writer.ResolvePathParts(_filePathOverride ?? config.FilePath);

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

    public void Dispose() => _writer.Dispose();
}
