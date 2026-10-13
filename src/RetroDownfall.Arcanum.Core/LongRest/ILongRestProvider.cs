using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.LongRest;

public interface ILongRestProvider
{
    Task<Result<LongRestProviderResult>> ExecuteAsync(LongRestProviderPayload payload, ITurnBudget budget, Func<CancellationToken, Task> beforeSend, LongRestProviderAdmission admission, CancellationToken ct);
}
