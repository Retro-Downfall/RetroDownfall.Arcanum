using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Persisted guardrails audit log (Tier 3 Phase 4, §8.x) — a durable, append-only JSONL trail of
/// guardrail violations that blocked an inference turn, independent of the inference audit log
/// (which records completed turns). Implementations must never throw: a logging failure must not
/// fail the guardrail check that recorded it (the turn is already being rejected — the audit trail
/// is best-effort). A complete no-op when
/// <c>Arcanum:Security:Guardrails:AuditLog:Enabled</c> is <see langword="false"/> (the default) —
/// no file I/O at all.
/// </summary>
public interface IGuardrailAuditLogger
{

    /// <summary>
    /// Appends one violation record. Never throws — internal failures (disk full, permission denied)
    /// are logged and swallowed, since a guardrail that already blocked a turn must not be reported as
    /// failed just because its audit trail could not be written.
    /// </summary>
    Task LogAsync(GuardrailAuditRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Reads matching records across the retained dated log files for <c>GET /api/guardrails/audit</c>,
    /// newest first. All filters are optional (<see langword="null"/> = no filter on that field).
    /// <paramref name="limit"/> bounds the result count (already clamped by the caller). Malformed/
    /// partial lines (a concurrent write mid-flush) are skipped, not thrown.
    /// </summary>
    Task<IReadOnlyList<GuardrailAuditRecord>> QueryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? stage,
        string? violationType,
        string? sessionId,
        int limit,
        CancellationToken cancellationToken);

}
