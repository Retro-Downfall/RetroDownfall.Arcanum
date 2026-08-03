using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Persisted inference audit log (§8.26) — a durable, append-only JSONL trail of completed
/// inference turns, independent of the Grimoire (which stores conversation content, not
/// operational metadata). Implementations must never throw: a logging failure must not fail the
/// inference turn it is recording. A complete no-op when <c>Arcanum:Host:AuditLog:Enabled</c> is
/// <c>false</c> (the default) — no file I/O at all.
/// </summary>
public interface IInferenceAuditLogger
{

    /// <summary>
    /// Appends one record. Never throws — internal failures (disk full, permission denied) are
    /// logged and swallowed, since a turn that already completed successfully must not be reported
    /// as failed just because its audit trail could not be written.
    /// </summary>
    Task LogAsync(InferenceAuditRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Reads matching records across the retained dated log files for <c>GET /api/audit</c>, newest
    /// first. All filters are optional (<see langword="null"/> = no filter on that field).
    /// <paramref name="limit"/> bounds the first result page (already clamped by the caller).
    /// Malformed/partial lines (a concurrent write mid-flush) are skipped, not thrown.
    /// </summary>
    Task<IReadOnlyList<InferenceAuditRecord>> QueryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? model,
        string? sessionId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one stable newest-first page. <paramref name="cursor"/> is an opaque continuation from
    /// the immediately preceding page and is rejected if it is malformed, references replaced or
    /// retained-away data, or is reused with different filters.
    /// </summary>
    Task<Result<AuditQueryPage<InferenceAuditRecord>>> QueryPageAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? model,
        string? sessionId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken);

}
