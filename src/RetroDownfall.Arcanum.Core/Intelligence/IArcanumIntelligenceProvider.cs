using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

public interface IArcanumIntelligenceProvider
{
    /// <summary>
    /// <paramref name="auditContext"/> is optional, additive instrumentation for the persisted
    /// inference audit log (§8.26) — <see langword="null"/> (the default) means this turn is simply
    /// not audit-logged, with no other effect on behavior. Appended after
    /// <paramref name="cancellationToken"/> (rather than positioned more naturally next to
    /// <paramref name="request"/>) so every existing positional call site remains source-compatible.
    /// </summary>
    Task<Result<PromptTurnResult>> ExecutePromptAsync(
        PingRequest request,
        CancellationToken cancellationToken = default,
        InferenceAuditContext? auditContext = null);

    /// <inheritdoc cref="ExecutePromptAsync"/>
    IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
        PingRequest request,
        CancellationToken cancellationToken = default,
        InferenceAuditContext? auditContext = null);
}
