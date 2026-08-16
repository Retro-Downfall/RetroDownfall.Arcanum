using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

public interface IArcanumIntelligenceProvider
{
    /// <summary>
    /// <paramref name="invocationContext"/> is the caller's explicit authority classification and has
    /// no default value. Covenant eligibility used to be inferred from whatever happened to be present
    /// — a Session, a working directory, an injected service — and every one of those inferences was a
    /// way for an unattended caller to acquire operator reach by accident. Unattended, subagent, A2A,
    /// batch, recovery, and background callers pass <see cref="ArcanumInvocationContext.None"/>
    /// (§10.12).
    ///
    /// <para><paramref name="auditContext"/> is optional, additive instrumentation for the persisted
    /// inference audit log (§8.26) — <see langword="null"/> (the default) means this turn is simply
    /// not audit-logged, with no other effect on behavior. It stays last so the required context
    /// cannot be omitted positionally.</para>
    /// </summary>
    Task<Result<PromptTurnResult>> ExecutePromptAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        CancellationToken cancellationToken,
        InferenceAuditContext? auditContext = null);

    /// <inheritdoc cref="ExecutePromptAsync"/>
    IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        CancellationToken cancellationToken,
        InferenceAuditContext? auditContext = null);
}
