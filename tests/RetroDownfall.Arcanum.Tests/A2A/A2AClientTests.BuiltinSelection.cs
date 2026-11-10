using A2A;

using Microsoft.AspNetCore.TestHost;

namespace RetroDownfall.Arcanum.Tests.A2A;

public sealed partial class A2AClientTests
{
    [Theory]
    [InlineData("JSONRPC", "HTTP+JSON", "/agent")]
    [InlineData("HTTP+JSON", "JSONRPC", "/agent-http")]
    [InlineData("http+json", "jsonrpc", "/agent-http")]
    public async Task GuildA2ABuiltinSelectionPreservesAdvertisedOrder(string first, string second, string selectedPath)
    {
        AgentCard card = BuildFakeCard();

        static string Path(string binding) => string.Equals(binding, "JSONRPC", StringComparison.OrdinalIgnoreCase) ? "/agent" : "/agent-http";

        card.SupportedInterfaces =
        [
            new AgentInterface { Url = $"http://{FakeAgentHost}/unsupported", ProtocolBinding = "unsupported", ProtocolVersion = "1.0" },
            new AgentInterface { Url = $"http://{FakeAgentHost}{Path(first)}", ProtocolBinding = first, ProtocolVersion = "1.0" },
            new AgentInterface { Url = $"http://{FakeAgentHost}{Path(second)}", ProtocolBinding = second, ProtocolVersion = "1.0" },
        ];

        using TestServer server = await CreateFakeRemoteAgentServerAsync(new EchoingAgentHandler("selected builtin"), card);

        using HttpMessageHandler remote = server.CreateHandler();

        List<string> paths = [];

        using InterceptingHttpHandler handler = new(remote, (request, _) =>
        {
            if (request.Method == HttpMethod.Post) paths.Add(request.RequestUri!.AbsolutePath);

            return Task.CompletedTask;
        });

        var result = await CreateClient(handler, EnabledSettings()).DispatchSendingAsync("Compute", null, DiscoveryUrl);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("selected builtin", result.Value.ResponseText);

        Assert.NotEmpty(paths);

        Assert.All(paths, path => Assert.StartsWith(selectedPath, path, StringComparison.Ordinal));
    }
}
