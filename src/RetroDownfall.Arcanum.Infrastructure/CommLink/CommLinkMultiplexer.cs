using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.CommLink;

internal sealed class CommLinkMultiplexer(IReadOnlyList<ICommLinkDispatcher> dispatchers) : ICommLinkDispatcher
{

    public async Task<Result> DispatchAsync(CommLinkMessage message, CancellationToken cancellationToken = default)
    {

        foreach (ICommLinkDispatcher inner in dispatchers)
        {

            Result r = await inner
                .DispatchAsync(message, cancellationToken)
                .ConfigureAwait(false);

            if (r.IsFailure)
            {

                return r;

            }

        }

        return Result.Success();

    }

}
