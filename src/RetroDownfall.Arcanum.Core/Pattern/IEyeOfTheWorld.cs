using RetroDownfall.Arcanum.Core.Pattern.Entities;

namespace RetroDownfall.Arcanum.Core.Pattern;

public interface IEyeOfTheWorld
{
    Task<PatternSnapshot> PerceivePatternAsync(string directoryPath, CancellationToken cancellationToken);
}
