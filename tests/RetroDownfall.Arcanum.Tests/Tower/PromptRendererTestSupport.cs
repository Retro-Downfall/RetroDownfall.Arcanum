using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Tower;

namespace RetroDownfall.Arcanum.Tests.Tower;

internal static class PromptRendererTestSupport
{
    internal static PromptRenderer CreateRenderer(IManaMeter meter) => new(meter);
}

