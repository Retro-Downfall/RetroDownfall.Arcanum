namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

/// <summary>
/// One JSONL line in the persisted guardrails audit log (<c>Arcanum:Guardrails:AuditLog</c>,
/// §8.x). Written by <c>GuardrailAuditLogger</c> only when a guardrail violation blocks a turn —
/// never for allowed turns — so the trail is a focused record of policy hits, not a per-turn log.
/// Multiple violations detected in a single turn may each produce a separate audit record (capped at
/// a small per-turn limit), so callers should not assume one record per rejected turn.
/// <see cref="MatchedTextRedacted"/> is always the redacted form (never raw PII); the pipeline
/// redacts before emitting. Field names are deliberately camelCase (via <c>AuditJsonContext</c>'s
/// naming policy) to match the inference audit log's on-disk contract.
/// </summary>
public sealed record GuardrailAuditRecord(
    string Timestamp,
    string? SessionId,
    string Stage,
    string ViolationType,
    string? MatchedTextRedacted,
    string? Model);
