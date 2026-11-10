using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildLocalHostPolicy
{
    internal ImmutableArray<string> RemoteOutputModes => [.. A2AAgentCardPolicy.ResolveInputModes(settings.CurrentValue.ResolveA2A())];
}
