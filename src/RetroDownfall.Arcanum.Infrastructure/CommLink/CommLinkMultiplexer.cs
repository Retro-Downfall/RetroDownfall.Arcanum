using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.CommLink;

internal sealed class CommLinkMultiplexer(
    IReadOnlyList<ICommLinkDispatcher> dispatchers,
    ILogger<CommLinkMultiplexer>? logger = null) : ICommLinkDispatcher
{

    public async Task<Result<CommLinkDeliveryResult>> DispatchAsync(
        CommLinkMessage message,
        CancellationToken cancellationToken = default)
    {

        bool anyDelivered = false;

        bool anyAttempted = false;

        Error? lastFailure = null;

        foreach (ICommLinkDispatcher inner in dispatchers)
        {

            anyAttempted = true;

            Result<CommLinkDeliveryResult> r = await inner
                .DispatchAsync(message, cancellationToken)
                .ConfigureAwait(false);

            if (r.IsFailure)
            {

                lastFailure = r.Error;

                logger?.LogWarning(
                    "Comm Link inner dispatcher failed: {Code} {Message}",
                    r.Error.Code,
                    r.Error.Message);

                continue;

            }

            if (r.Value.Status == CommLinkDeliveryStatus.Delivered)
            {

                anyDelivered = true;

            }

        }

        if (anyDelivered)
        {

            return Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Delivered));

        }

        if (!anyAttempted)
        {

            return Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Suppressed));

        }

        // Attempts occurred: either all suppressed, or failures with no delivery.
        if (lastFailure is { } failure)
        {

            return Result<CommLinkDeliveryResult>.Failure(failure);

        }

        return Result<CommLinkDeliveryResult>.Success(
            new CommLinkDeliveryResult(CommLinkDeliveryStatus.Suppressed));

    }

}
