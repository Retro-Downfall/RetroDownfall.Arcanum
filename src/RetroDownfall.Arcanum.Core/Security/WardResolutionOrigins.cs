namespace RetroDownfall.Arcanum.Core.Security;

/// <summary>
/// Canonical projections of <see cref="WardResolutionOrigin"/>. The JSON wire name lives on the enum
/// members themselves (camelCase, matching every other native NDJSON discriminator); metric labels
/// use snake_case, matching the closed label domains already published by <c>ArcanumMetrics</c>.
/// </summary>
public static class WardResolutionOrigins
{

    /// <summary>
    /// Prometheus label value for <paramref name="origin"/>. The domain is closed by the enum, so
    /// this can never contribute unbounded label cardinality.
    /// </summary>
    public static string ToMetricLabel(WardResolutionOrigin origin) =>
        origin switch
        {
            WardResolutionOrigin.Human => "human",
            WardResolutionOrigin.AutoApproved => "auto_approved",
            WardResolutionOrigin.AutoDenied => "auto_denied",
            WardResolutionOrigin.TimedOut => "timed_out",
            WardResolutionOrigin.Cancelled => "cancelled",
            WardResolutionOrigin.HostRestarted => "host_restarted",
            WardResolutionOrigin.Ungated => "ungated",
            _ => "human",
        };

}
