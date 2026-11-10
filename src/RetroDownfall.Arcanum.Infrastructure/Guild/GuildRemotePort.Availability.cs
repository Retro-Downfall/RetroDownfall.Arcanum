using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildRemotePort
{
    internal static bool IsTransportEnabled(ArcanumSettings settings)
    {
        ConclaveA2ASettings a2a = settings.ResolveA2A();

        return settings.Features.Guild && settings.ResolveConclave().Enabled && a2a.Enabled && a2a.ClientEnabled;
    }

    internal static bool IsAvailable(ArcanumSettings settings)
    {
        BudgetSettings budget = settings.ResolveBudget();

        return IsTransportEnabled(settings) && settings.ResolveA2A().AllowedRemoteAgents is { Length: > 0 }
            && !(budget.Enabled && budget.DailyLimitUsd > 0);
    }
}
