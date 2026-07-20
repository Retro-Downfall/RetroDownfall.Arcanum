using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.CommLink;

public interface ICommLinkDispatcher
{

    /// <summary>
    /// Dispatches a Comm Link message.
    /// Success with <see cref="CommLinkDeliveryStatus.Delivered"/> or
    /// <see cref="CommLinkDeliveryStatus.Suppressed"/>; failed <see cref="Result{T}"/> for transport errors.
    /// </summary>
    Task<Result<CommLinkDeliveryResult>> DispatchAsync(
        CommLinkMessage message,
        CancellationToken cancellationToken = default);

}
