using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

public interface IArcanumIntelligenceProvider
{
    Task<Result<string>> ExecutePromptAsync(
        PingRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
        PingRequest request,
        CancellationToken cancellationToken = default);
}
