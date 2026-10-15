namespace RetroDownfall.Arcanum.Core.Sanctum;

/// <summary>
/// How tightly a Campaign's Sanctum binds, ordered so that a larger value is stricter. It is how a caller chooses between two Campaigns
/// that both claim a directory: the stricter Sanctum governs, because applying too much containment is the safe error and applying too
/// little is not.
/// </summary>
public enum SanctumStrictness
{
    /// <summary>The Sanctum is switched off for the Campaign.</summary>
    Disabled = 0,

    /// <summary>The Sanctum records every breach and lets the operation go on.</summary>
    AuditOnly = 1,

    /// <summary>The Sanctum records every breach and stops the operation.</summary>
    Strict = 2,

    /// <summary>
    /// The Campaign's Sanctum could not be read, so whether it was on is unknowable. Ranked above everything, so the Campaign whose policy
    /// cannot be read is the one that governs, and the caller then refuses what that Sanctum would have judged.
    /// </summary>
    Unreadable = 3,
}

/// <summary>
/// The one rule that ranks a Sanctum configuration.
/// </summary>
public static class SanctumStrictnessPolicy
{
    /// <summary>
    /// The strictness of <paramref name="config"/>. A mode that is not <see cref="SanctumMode.AuditOnly"/> counts as strict, the way
    /// <see cref="SanctumEnforcement.AllowsContinuation"/> treats an undefined mode.
    /// </summary>
    public static SanctumStrictness Of(SanctumConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!config.Enabled)
        {
            return SanctumStrictness.Disabled;
        }

        return config.Mode == SanctumMode.AuditOnly
            ? SanctumStrictness.AuditOnly
            : SanctumStrictness.Strict;
    }
}
