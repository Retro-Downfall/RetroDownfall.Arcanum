using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.CommLink;

public interface ICommLinkDispatcher
{

    Task<Result> DispatchAsync(CommLinkMessage message, CancellationToken cancellationToken = default);

}
