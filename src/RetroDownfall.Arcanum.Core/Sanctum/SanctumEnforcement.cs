namespace RetroDownfall.Arcanum.Core.Sanctum;

/// <summary>
/// The one rule that turns a Sanctum verdict into whether the guarded operation goes on. <see cref="ISanctumGuard"/> records every
/// breach in both modes and reports it as not allowed; what differs is what the caller does next.
/// </summary>
public static class SanctumEnforcement
{
    /// <summary>
    /// True when the operation may continue after <paramref name="result"/> under <paramref name="mode"/>:
    /// the guard allowed it, or the Sanctum is <see cref="SanctumMode.AuditOnly"/>, which keeps the breach it recorded and lets the
    /// operation proceed. <see cref="SanctumMode.Strict"/> stops at the first verdict that is not allowed. Every network operation the
    /// Sanctum governs (the initial target of <c>read_url</c> and its legacy alias, every redirect hop, and every research citation)
    /// asks this one question.
    /// </summary>
    public static bool AllowsContinuation(SanctumResult result, SanctumMode mode)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Allowed || mode == SanctumMode.AuditOnly;
    }
}
