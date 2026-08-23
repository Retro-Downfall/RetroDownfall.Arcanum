using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.TheForge;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.TheForge;

internal static class PromptRendererTestSupport
{

    internal static PromptRenderer CreateRenderer(IManaMeter meter, ArcanumSettings? settings = null) =>
        new(meter, new TestOptionsMonitor<ArcanumSettings>(settings ?? new ArcanumSettings()));

}

