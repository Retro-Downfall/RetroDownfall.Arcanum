using System.Threading.Channels;
using A2A;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Tests.A2A;

public sealed partial class A2AServerTests
{
    [Fact]
    public async Task Guild_cutover_rejects_legacy_inbound_work_without_creating_an_apprentice()
    {
        FakeConclaveArchmage archmage = new();

        FakeApprenticeRuntime runtime = new(Channel.CreateUnbounded<ApprenticeEvent>().Reader);

        ArcanumSettings settings = EnabledSettings();

        settings.Features.Guild = true;

        await using ArcanumA2AAgentHandler handler = CreateHandler(settings, archmage, runtime, new FakeApprenticeRepository(), new FakeSessionRepository());

        AgentEventQueue queue = new();

        await handler.ExecuteAsync(RequestWithText("Compute a result"), queue, default);

        List<StreamResponse> responses = await DrainAsync(queue);

        Assert.Equal(TaskState.Rejected, LatestState(responses));

        Assert.Null(archmage.LastRequest);

        Assert.Empty(runtime.StartedApprenticeIds);
    }
}
