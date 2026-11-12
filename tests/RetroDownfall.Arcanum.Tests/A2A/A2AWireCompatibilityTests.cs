using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.A2A;

/// <summary>Independent peer JSON catches SDK wire changes that an SDK-generated peer would conceal.</summary>
public sealed class A2AWireCompatibilityTests
{
    private const string DiscoveryUrl = "https://literal-agent.example.test/";

    private const string KnownMetadata = """
        {"arcanum.conclave.usage":{"totalTokens":9007199254740993,"costUsd":0.1234567890123456789012345678}}
        """;

    // Removing the settled-task metadata read must lose both exact figures on every observation path.
    [Theory]
    [InlineData(ObservationPath.Poll)]
    [InlineData(ObservationPath.DroppedSubscription)]
    [InlineData(ObservationPath.CompletedSubscription)]
    public async Task DispatchSendingAsync_LiteralSettledTask_PreservesExactPeerUsage(ObservationPath path)
    {
        using LiteralPeerHandler handler = new(KnownMetadata, path);

        Result<A2ADispatchResult> result = await CreateClient(handler).DispatchSendingAsync("answer", null, DiscoveryUrl)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        Assert.Equal("literal peer answer", result.Value.ResponseText);

        Assert.Equal("literal-task", result.Value.TaskId);

        Assert.True(result.Value.RemoteCost.IsKnown);

        Assert.Equal(9007199254740993L, result.Value.RemoteCost.TotalTokens);

        Assert.Equal(0.1234567890123456789012345678m, result.Value.RemoteCost.CostUsd);

        Assert.Equal(path == ObservationPath.Poll ? 0 : 1, handler.SubscriptionRequests);

        Assert.Equal(1, handler.TaskReads);
    }

    // Coercing malformed metadata or defaulting missing usage to zero would turn these into known costs.
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"unrecognised.usage\":{\"totalTokens\":42,\"costUsd\":0.5}}")]
    [InlineData("{\"arcanum.conclave.usage\":\"unknown\"}")]
    [InlineData("{\"arcanum.conclave.usage\":{\"totalTokens\":-1,\"costUsd\":-0.5}}")]
    [InlineData("{\"arcanum.conclave.usage\":{\"totalTokens\":\"42\",\"costUsd\":\"0.5\"}}")]
    public async Task DispatchSendingAsync_LiteralUnreportedOrMalformedUsage_RemainsUnknown(string metadata)
    {
        using LiteralPeerHandler handler = new(metadata, ObservationPath.Poll);

        Result<A2ADispatchResult> result = await CreateClient(handler).DispatchSendingAsync("answer", null, DiscoveryUrl)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        Assert.Equal("literal peer answer", result.Value.ResponseText);

        Assert.False(result.Value.RemoteCost.IsKnown);

        Assert.Null(result.Value.RemoteCost.TotalTokens);

        Assert.Null(result.Value.RemoteCost.CostUsd);
    }

    // Omitting the asynchronous configuration or changing metadata nesting breaks a non-.NET peer.
    [Fact]
    public async Task DispatchSendingAsync_LiteralPeer_ReceivesReturnImmediatelyModesAndDelegationChain()
    {
        using LiteralPeerHandler handler = new(KnownMetadata, ObservationPath.Poll);

        Result<A2ADispatchResult> result = await CreateClient(handler).DispatchSendingAsync(
            "answer", null, DiscoveryUrl, delegationChain: ["upstream-node"])
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        using JsonDocument sent = JsonDocument.Parse(Assert.IsType<string>(handler.SendBody));

        JsonElement parameters = sent.RootElement.GetProperty("params");

        JsonElement configuration = parameters.GetProperty("configuration");

        Assert.True(configuration.GetProperty("returnImmediately").GetBoolean());

        Assert.Equal(["text/plain"], configuration.GetProperty("acceptedOutputModes").EnumerateArray().Select(mode => mode.GetString()));

        JsonElement message = parameters.GetProperty("message");

        Assert.Equal("ROLE_USER", message.GetProperty("role").GetString());

        Assert.Equal("answer", message.GetProperty("parts")[0].GetProperty("text").GetString());

        JsonElement chain = message.GetProperty("metadata").GetProperty("arcanum.conclave.delegationChain");

        Assert.Equal(2, chain.GetArrayLength());

        Assert.Equal("upstream-node", chain[0].GetString());

        Assert.Equal(ConclaveDelegationChain.NodeId, chain[1].GetString());
    }

    private static A2AClientService CreateClient(HttpMessageHandler handler)
    {
        FakeDnsResolver dns = new();

        dns.Add("literal-agent.example.test", IPAddress.Parse("93.184.216.34"));

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Conclave = true, A2AClient = true },
            Integrations = new IntegrationSettings { A2A = new A2AIntegrationSettings() },
        };

        return new A2AClientService(new PeerHttpClientFactory(handler), new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<A2AClientService>.Instance, dns);
    }

    public enum ObservationPath
    {
        Poll,
        DroppedSubscription,
        CompletedSubscription,
    }

    private sealed class PeerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class LiteralPeerHandler(string metadata, ObservationPath path) : HttpMessageHandler
    {
        public string? SendBody { get; private set; }

        public int SubscriptionRequests { get; private set; }

        public int TaskReads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return Json("""
                    {"name":"Literal peer","description":"Independent protocol fixture","version":"1.0.0",
                     "supportedInterfaces":[{"url":"https://literal-agent.example.test/agent","protocolBinding":"JSONRPC","protocolVersion":"1.0"}],
                     "capabilities":{"streaming":STREAMING,"pushNotifications":false},
                     "defaultInputModes":["text/plain"],"defaultOutputModes":["text/plain"],"skills":[]}
                    """.Replace("STREAMING", path == ObservationPath.Poll ? "false" : "true", StringComparison.Ordinal));
            }

            string body = await request.Content!.ReadAsStringAsync(cancellationToken);

            using JsonDocument document = JsonDocument.Parse(body);

            string id = document.RootElement.GetProperty("id").GetRawText();

            string? method = document.RootElement.GetProperty("method").GetString();

            if (method == "SendMessage")
            {
                SendBody = body;

                return Json("""
                    {"jsonrpc":"2.0","id":REQUEST_ID,"result":{"task":{"id":"literal-task","contextId":"literal-context","status":{"state":"TASK_STATE_WORKING"}}}}
                    """.Replace("REQUEST_ID", id, StringComparison.Ordinal).Replace("TASK_METADATA", metadata, StringComparison.Ordinal));
            }

            if (method == "SubscribeToTask")
            {
                SubscriptionRequests++;

                string stream = path == ObservationPath.DroppedSubscription ? string.Empty : """
                    data: {"jsonrpc":"2.0","id":REQUEST_ID,"result":{"statusUpdate":{"taskId":"literal-task","contextId":"literal-context","status":{"state":"TASK_STATE_COMPLETED"}}}}


                    """.Replace("REQUEST_ID", id, StringComparison.Ordinal);

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
            }

            if (method == "GetTask")
            {
                TaskReads++;

                return Json("""
                    {"jsonrpc":"2.0","id":REQUEST_ID,"result":{"id":"literal-task","contextId":"literal-context",
                     "status":{"state":"TASK_STATE_COMPLETED"},
                     "artifacts":[{"artifactId":"literal-answer","parts":[{"text":"literal peer answer"}]}],"metadata":TASK_METADATA}}
                    """.Replace("REQUEST_ID", id, StringComparison.Ordinal).Replace("TASK_METADATA", metadata, StringComparison.Ordinal));
            }

            throw new InvalidOperationException($"Unexpected literal peer request: {method}");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
