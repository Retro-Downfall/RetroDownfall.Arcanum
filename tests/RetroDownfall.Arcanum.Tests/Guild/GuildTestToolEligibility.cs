using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

internal static class GuildTestToolEligibility
{
    internal static GuildToolEligibility Enabled { get; } =
        For(new ArcanumSettings { Features = new() { Guild = true } });

    internal static GuildToolEligibility For(ArcanumSettings settings, IOptionsMonitor<ArcanumSettings>? monitor = null) =>
        new(false, Microsoft.Extensions.Options.Options.Create(settings), monitor);
}
