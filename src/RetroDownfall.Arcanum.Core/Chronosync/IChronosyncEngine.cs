using RetroDownfall.Arcanum.Core.Pattern.Entities;

namespace RetroDownfall.Arcanum.Core.Chronosync;

public interface IChronosyncEngine
{
    Task<ChronosyncReport> AnalyzeAndSyncAsync(PatternSnapshot currentSnapshot, CancellationToken cancellationToken = default);
}
