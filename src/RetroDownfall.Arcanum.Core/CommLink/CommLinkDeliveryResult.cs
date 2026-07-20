namespace RetroDownfall.Arcanum.Core.CommLink;

/// <summary>
/// Outcome of a Comm Link dispatch attempt that did not fail transport.
/// </summary>
public enum CommLinkDeliveryStatus
{
    /// <summary>At least one configured destination accepted the message.</summary>
    Delivered,

    /// <summary>No destination was configured, or policy intentionally skipped delivery.</summary>
    Suppressed,
}

/// <summary>
/// Successful dispatch result distinguishing delivered vs intentionally suppressed notifications.
/// Transport / HTTP failures remain <see cref="Primitives.Result{T}"/> failures.
/// </summary>
public readonly record struct CommLinkDeliveryResult(CommLinkDeliveryStatus Status);
