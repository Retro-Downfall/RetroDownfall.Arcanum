namespace RetroDownfall.Arcanum.Core.Sanctum;

/// <summary>
/// The one rule that turns a Sanctum verdict into whether the guarded operation goes on. <see cref="ISanctumGuard"/> records every
/// policy breach in both modes and reports it as not allowed; what differs is what the caller does next.
/// </summary>
public static class SanctumEnforcement
{
    /// <summary>
    /// True when the operation may continue after <paramref name="result"/> under <paramref name="mode"/>:
    /// the guard allowed it, or the Sanctum is <see cref="SanctumMode.AuditOnly"/> and the refusal is a policy breach, which AuditOnly keeps
    /// as its audit row and lets the operation proceed. <see cref="SanctumMode.Strict"/> stops at the first verdict that is not allowed.
    /// AuditOnly overrides a <see cref="SanctumDenialKind.Policy"/> refusal and nothing else: when the guard could not evaluate the policy at
    /// all (<see cref="SanctumDenialKind.PolicyUnavailable"/>) or could not record the breach (<see cref="SanctumDenialKind.AuditRecordFailed"/>)
    /// there is no audit control left to rely on, so every mode refuses. The initial target of <c>read_url</c> and its legacy alias, the
    /// preflight of every other governed tool call and the attachment source read ask this one question; a redirect hop and a research
    /// citation are refused in every mode, and so never ask it.
    /// </summary>
    public static bool AllowsContinuation(SanctumResult result, SanctumMode mode)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Allowed
            || (mode == SanctumMode.AuditOnly && result.DenialKind == SanctumDenialKind.Policy);
    }
}
