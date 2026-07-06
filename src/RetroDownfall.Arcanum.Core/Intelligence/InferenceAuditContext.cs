namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Optional per-request metadata for the persisted inference audit log (§8.26), built by the calling
/// HTTP endpoint — which knows transport-layer details (client IP, which route was hit) that
/// <see cref="PingRequest"/> deliberately does not carry, keeping Core free of HTTP/hosting
/// concerns — and threaded through to <see cref="IArcanumIntelligenceProvider"/>. When
/// <see langword="null"/> (the default for every existing caller that does not construct one), the
/// turn is simply not audit-logged; this is purely additive instrumentation, never required for a
/// turn to succeed.
///
/// <see cref="ToolNames"/> and <see cref="ToolArgumentsJson"/> are mutated in place by
/// <c>WizardIntelligenceProvider</c> as tool calls execute during the turn — the caller only sets
/// <see cref="RequestType"/> / <see cref="ClientIp"/> up front.
/// </summary>
public sealed class InferenceAuditContext
{

    public required string RequestType { get; init; }

    public string? ClientIp { get; init; }

    public List<string> ToolNames { get; } = [];

    public List<string> ToolArgumentsJson { get; } = [];

}
