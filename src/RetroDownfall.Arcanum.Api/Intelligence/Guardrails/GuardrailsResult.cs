namespace RetroDownfall.Arcanum.Api.Intelligence.Guardrails;

/// <summary>
/// Outcome of a guardrails scan (Tier 3 Phase 4). <see cref="IsAllowed"/> is <see langword="true"/>
/// when no violation triggered a block; <see cref="Violations"/> lists every detected violation
/// (PII, toxicity, topic) with <see cref="GuardrailsViolation.MatchedText"/> already redacted so the
/// record is safe to log or return on the wire.
/// </summary>
public sealed record GuardrailsResult(bool IsAllowed, GuardrailsViolation[] Violations)
{

    public static GuardrailsResult Allowed { get; } = new(true, []);

}

/// <summary>
/// A single guardrail violation. <see cref="Type"/> is one of <c>pii</c>, <c>toxicity</c>,
/// <c>topic-allowed</c>, or <c>topic-blocked</c>. <see cref="MatchedText"/> is the redacted span that
/// tripped the rule — never the raw PII/secret — so this DTO is safe to persist in the audit log or
/// surface in an error envelope.
/// </summary>
public sealed record GuardrailsViolation(string Type, string Message, string? MatchedText);
