using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

public interface ICovenantLinker
{
    Result<CovenantTurnPlan> Link(CovenantTurnSnapshot snapshot);
}
