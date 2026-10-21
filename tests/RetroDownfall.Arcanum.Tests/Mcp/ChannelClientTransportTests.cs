using System.Text;

using System.Text.Json;

using System.Text.Json.Nodes;

using System.Threading.Channels;

using ModelContextProtocol;

using ModelContextProtocol.Protocol;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class ChannelClientTransportTests
{
    private const int MaxLineBytes = 512;

    [Fact]
    public async Task SendMessageAsync_rejects_request_whose_line_with_delimiter_exceeds_cap()
    {
        // The internal server measures the whole line it reads, delimiter included. A request whose JSON
        // is exactly the cap is therefore one byte over it on the wire and would be dropped by the
        // server, stranding the call until its own timeout; the client must refuse it up front.
        (Channel<string> toServer, Channel<string> fromServer) = CreateChannels();

        await using ITransport transport = await ConnectAsync(toServer, fromServer);

        JsonRpcRequest atCap = RequestWithJsonBytes(MaxLineBytes);

        _ = await Assert.ThrowsAsync<McpLineSizeExceededException>(
            () => transport.SendMessageAsync(atCap));

        Assert.False(toServer.Reader.TryRead(out _), "A line over the cap was written to the server channel.");
    }

    [Fact]
    public async Task SendMessageAsync_writes_a_request_whose_line_with_delimiter_fits_the_cap_exactly()
    {
        (Channel<string> toServer, Channel<string> fromServer) = CreateChannels();

        await using ITransport transport = await ConnectAsync(toServer, fromServer);

        JsonRpcRequest justUnder = RequestWithJsonBytes(MaxLineBytes - 1);

        await transport.SendMessageAsync(justUnder);

        Assert.True(toServer.Reader.TryRead(out string? line));

        Assert.Equal(MaxLineBytes, Encoding.UTF8.GetByteCount(line));

        Assert.EndsWith("\n", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("oversized")]
    [InlineData("cancelled")]
    public async Task A_failed_send_releases_its_Covenant_capability_for_the_same_request_id(string failure)
    {
        CovenantToolCapabilityRegistry registry = new();

        using IDisposable staging = CovenantToolStagingAmbient.Push(Staging(registry));

        (Channel<string> toServer, Channel<string> fromServer) = CreateChannels();

        ChannelClientTransport client = new(toServer.Writer, fromServer.Reader, MaxLineBytes, "covenant-send");

        await using ITransport transport = await client.ConnectAsync();

        await FailSendAsync(transport, toServer, failure);

        Assert.Equal(0, registry.CountForTests);

        JsonRpcRequest retry = CovenantRequest();

        SessionAttachmentAmbientSend.BindSdkToolsCall("covenant-send", retry);

        var grant = registry.TryTake("covenant-send", "7");

        Assert.True(grant.IsSuccess, grant.Error.Message);

        await grant.Value.Capability.DisposeAsync();

        Assert.True(registry.Remove("covenant-send", "7", grant.Value.Nonce));
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("oversized")]
    [InlineData("cancelled")]
    public async Task A_failed_duplicate_send_preserves_the_earlier_requests_Covenant_capability(string failure)
    {
        CovenantToolCapabilityRegistry registry = new();

        using IDisposable staging = CovenantToolStagingAmbient.Push(Staging(registry));

        (Channel<string> toServer, Channel<string> fromServer) = CreateChannels();

        ChannelClientTransport client = new(toServer.Writer, fromServer.Reader, MaxLineBytes, "covenant-send");

        await using ITransport transport = await client.ConnectAsync();

        await transport.SendMessageAsync(CovenantRequest());

        await FailSendAsync(transport, toServer, failure);

        var grant = registry.TryTake("covenant-send", "7");

        Assert.True(grant.IsSuccess, grant.Error.Message);

        await grant.Value.Capability.DisposeAsync();

        Assert.True(registry.Remove("covenant-send", "7", grant.Value.Nonce));
    }

    private static async Task FailSendAsync(ITransport transport, Channel<string> toServer, string failure)
    {
        JsonRpcRequest request = CovenantRequest();

        using CancellationTokenSource cancellation = new();

        if (failure == "closed")
        {
            toServer.Writer.Complete();

            await Assert.ThrowsAsync<ChannelClosedException>(() => transport.SendMessageAsync(request));
        }
        else if (failure == "oversized")
        {
            request.Params!["pad"] = new string('p', MaxLineBytes);

            await Assert.ThrowsAsync<McpLineSizeExceededException>(() => transport.SendMessageAsync(request));
        }
        else
        {
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => transport.SendMessageAsync(request, cancellation.Token));
        }
    }

    private static JsonRpcRequest CovenantRequest() => new()
    {
        Method = "tools/call",
        Id = new RequestId(7),
        Params = new JsonObject { ["name"] = CovenantToolNames.ProposeCovenant, ["arguments"] = new JsonObject() },
    };

    private static CovenantToolStagingContext Staging(CovenantToolCapabilityRegistry registry)
    {
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();

        return new CovenantToolStagingContext(
            new CovenantMutationCollector(Guid.NewGuid(), plan.Digest, CovenantTask6Fixture.BranchId),
            CovenantCapabilityFixtures.Campaign(),
            CovenantCapabilityFixtures.Admission(plan),
            CovenantCapabilityFixtures.Materialization(),
            new CovenantCapabilityFixtures.StubHeadProbe(),
            true,
            registry,
            CancellationToken.None);
    }

    private static (Channel<string> ToServer, Channel<string> FromServer) CreateChannels() =>
        (Channel.CreateUnbounded<string>(), Channel.CreateUnbounded<string>());

    private static async Task<ITransport> ConnectAsync(Channel<string> toServer, Channel<string> fromServer)
    {
        ChannelClientTransport clientTransport = new(
            toServer.Writer,
            fromServer.Reader,
            MaxLineBytes);

        return await clientTransport.ConnectAsync();
    }

    // Builds a tools/call request whose serialized JSON is exactly jsonBytes UTF-8 bytes long.
    private static JsonRpcRequest RequestWithJsonBytes(int jsonBytes)
    {
        int baseline = SerializedBytes(PaddedRequest(0));

        JsonRpcRequest request = PaddedRequest(jsonBytes - baseline);

        Assert.Equal(jsonBytes, SerializedBytes(request));

        return request;
    }

    private static JsonRpcRequest PaddedRequest(int padding) =>
        new()
        {
            Method = "tools/call",
            Id = new RequestId(1),
            Params = new JsonObject { ["name"] = "read_file_chunk", ["pad"] = new string('p', padding) },
        };

    private static int SerializedBytes(JsonRpcRequest request) =>
        Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize<JsonRpcMessage>(request, McpJsonUtilities.DefaultOptions));
}
