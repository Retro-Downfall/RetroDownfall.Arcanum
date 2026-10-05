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

            Result<CommLinkDeliveryResult> r;

            try
            {
                r = await inner
                    .DispatchAsync(message, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Nothing delivered yet: the cancellation is the caller's to receive. Something delivered
                // already: an earlier sink carried the alert and a cancellation cannot recall it, so the
                // true verdict is Delivered, and reporting a cancellation instead would invite a retry
                // that sends the alert to that sink twice. Either way no later sink is tried.
                if (anyDelivered)
                {
                    break;
                }

                throw;
            }
            catch (Exception ex)
            {
                // One sink throwing must not skip the rest: an alert that one sink cannot carry may
                // still reach another. Log the type only; a sink's message can name its endpoint.
                logger?.LogWarning(
                    "Comm Link inner dispatcher {Dispatcher} threw {FailureType}.",
                    inner.GetType().Name,
                    ex.GetType().Name);

                lastFailure = new Error(
                    ErrorCodes.CommLink.DispatcherException,
                    "A Comm Link dispatcher failed unexpectedly. See server logs for details.");

                continue;
            }

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
