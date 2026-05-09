using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Process-local overrides for Unseen Servant job polling intervals (Phase 2: Adaptive Initiative).
/// </summary>
public interface IUnseenServantPacer
{

    void SetDynamicInterval(string jobName, int intervalMinutes);

    int GetEffectiveInterval(UnseenServantJob job);

}
