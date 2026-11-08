using System.Net;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Tests.A2A;

public sealed partial class A2AClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_cutover_refuses_legacy_remote_issue_and_continuation_before_network(bool continuation)
    {
        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        ArcanumSettings settings = EnabledSettings();

        settings.Features.Guild = true;

        A2AClientService client = CreateClient(handler, settings);

        Result<A2ADispatchResult> result = continuation
            ? await client.ContinueSendingAsync(DiscoveryUrl, "existing-task", "Continue")
            : await client.DispatchSendingAsync("Compute a result", null, DiscoveryUrl);

        Assert.Equal(ErrorCodes.Guild.LegacyRunUnavailable, result.Error.Code);

        Assert.Empty(handler.Requests);
    }
}
