using System.Net;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Api.Models;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Pins the line-framed NDJSON streams (<c>ask</c>, <c>web research</c> and the Apprentice Chronicle)
/// against the same two hazards the watch SSE parser is pinned against: delivery in one-byte reads
/// that split a code point or a CRLF pair, and a line that never ends. Both used to go through an
/// unbounded <c>StreamReader.ReadLineAsync</c> with no test of either.
/// </summary>
public sealed class CliStreamLineBoundsTests
{
    private const string Emoji = "\U0001F600";

    /// <summary>
    /// A four-byte emoji arrives one byte per read and the line terminators are CRLF. The event must
    /// come out whole, which fails if a decoder or line splitter assumes a whole code point or a whole
    /// terminator per read.
    /// </summary>
    [Fact]
    public async Task Ask_stream_survives_one_byte_reads_including_a_split_emoji_and_crlf()
    {
        byte[] token = JsonSerializer.SerializeToUtf8Bytes(
            new IntelligenceEvent(IntelligenceEventType.Token, Emoji),
            ArcanumJsonContext.Default.IntelligenceEvent);

        byte[] wire = [.. token, .. "\r\n"u8, .. token, .. "\r\n"u8];

        using OneByteReadStream stream = new(wire);

        ArcanumApiClient client = CreateClient(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            });

        List<IntelligenceEvent> events = await CollectAsync(
            client.AskStreamAsync(new PingRequest("hello"), CancellationToken.None));

        Assert.Collection(
            events,
            first =>
            {
                Assert.Equal(IntelligenceEventType.Token, first.Type);

                Assert.Equal(Emoji, first.Message);
            },
            second =>
            {
                Assert.Equal(IntelligenceEventType.Token, second.Type);

                Assert.Equal(Emoji, second.Message);
            });

        Assert.Equal(1, stream.LargestRead);
    }

    /// <summary>
    /// A line over the limit is discarded without being buffered, the diagnostic names the limit and
    /// the way to recover the record, and the next line on the same stream is delivered normally.
    /// </summary>
    [Fact]
    public async Task Ask_stream_discards_a_line_over_the_limit_and_continues()
    {
        string oversized = JsonSerializer.Serialize(
            new IntelligenceEvent(IntelligenceEventType.Token, new string('x', 400)),
            ArcanumJsonContext.Default.IntelligenceEvent);

        string accepted = JsonSerializer.Serialize(
            new IntelligenceEvent(IntelligenceEventType.Token, "ok"),
            ArcanumJsonContext.Default.IntelligenceEvent);

        ArcanumApiClient client = CreateClient(
            Ndjson(oversized + "\n" + accepted + "\n"),
            maxStreamLineLength: 128);

        List<IntelligenceEvent> events = await CollectAsync(
            client.AskStreamAsync(new PingRequest("hello"), CancellationToken.None));

        Assert.Collection(
            events,
            discarded =>
            {
                Assert.Equal(IntelligenceEventType.Status, discarded.Type);

                Assert.Contains("128 characters", discarded.Message, StringComparison.Ordinal);

                Assert.Contains("show or export", discarded.Message, StringComparison.Ordinal);
            },
            kept =>
            {
                Assert.Equal(IntelligenceEventType.Token, kept.Type);

                Assert.Equal("ok", kept.Message);
            });
    }

    [Fact]
    public async Task Ask_stream_keeps_a_line_exactly_at_the_limit()
    {
        string line = JsonSerializer.Serialize(
            new IntelligenceEvent(IntelligenceEventType.Token, "edge"),
            ArcanumJsonContext.Default.IntelligenceEvent);

        ArcanumApiClient client = CreateClient(
            Ndjson(line + "\n"),
            maxStreamLineLength: line.Length);

        List<IntelligenceEvent> events = await CollectAsync(
            client.AskStreamAsync(new PingRequest("hello"), CancellationToken.None));

        IntelligenceEvent only = Assert.Single(events);

        Assert.Equal("edge", only.Message);
    }

    [Fact]
    public async Task Research_stream_reports_a_line_over_the_limit_as_an_invalid_response_and_continues()
    {
        string oversized = JsonSerializer.Serialize(
            new WebResearchStreamFrame
            {
                Type = WebResearchStreamFrameType.Progress,
                Message = new string('y', 400),
            },
            ArcanumJsonContext.Default.WebResearchStreamFrame);

        string accepted = JsonSerializer.Serialize(
            new WebResearchStreamFrame
            {
                Type = WebResearchStreamFrameType.Progress,
                Message = "after",
            },
            ArcanumJsonContext.Default.WebResearchStreamFrame);

        ArcanumApiClient client = CreateClient(
            Ndjson(oversized + "\n" + accepted + "\n"),
            maxStreamLineLength: 128);

        List<WebResearchStreamFrame> frames = await CollectAsync(
            client.ResearchWebAsync(
                new WebResearchWorkflowRequest { Question = "why" },
                CancellationToken.None));

        Assert.Collection(
            frames,
            discarded =>
            {
                Assert.Equal(WebResearchStreamFrameType.Error, discarded.Type);

                Assert.Equal("Api.InvalidResponse", discarded.Code);

                Assert.Contains("128 characters", discarded.Message, StringComparison.Ordinal);
            },
            kept => Assert.Equal("after", kept.Message));
    }

    [Fact]
    public async Task Chronicle_stream_warns_about_a_line_over_the_limit_and_continues()
    {
        string oversized = "data: {\"type\":\"status\",\"message\":\"" + new string('z', 400) + "\"}";

        ArcanumApiClient client = CreateClient(
            Ndjson(oversized + "\n\ndata: {\"type\":\"status\",\"message\":\"after\"}\n\ndata: [DONE]\n\n"),
            maxStreamLineLength: 128);

        List<ChronicleFrame> frames = await CollectAsync(
            client.StreamApprenticeChronicleAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Collection(
            frames,
            discarded =>
            {
                Assert.Equal("warning", discarded.Type);

                Assert.Contains("128 characters", discarded.Message, StringComparison.Ordinal);
            },
            kept => Assert.Equal("after", kept.Message));
    }

    /// <summary>
    /// The Chronicle stream is SSE-shaped, so it faces the same fragmented delivery.
    /// </summary>
    [Fact]
    public async Task Chronicle_stream_survives_one_byte_reads_including_a_split_emoji()
    {
        string wire = "data: {\"type\":\"status\",\"message\":\"" + Emoji + "\"}\r\n\r\ndata: [DONE]\r\n\r\n";

        using OneByteReadStream stream = new(Encoding.UTF8.GetBytes(wire));

        ArcanumApiClient client = CreateClient(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            });

        List<ChronicleFrame> frames = await CollectAsync(
            client.StreamApprenticeChronicleAsync(Guid.NewGuid(), CancellationToken.None));

        ChronicleFrame only = Assert.Single(frames);

        Assert.Equal(Emoji, only.Message);

        Assert.Equal(1, stream.LargestRead);
    }

    private static HttpResponseMessage Ndjson(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson"),
        };

    private static ArcanumApiClient CreateClient(
        HttpResponseMessage response,
        int maxStreamLineLength = BoundedLineReader.DefaultMaxLineLength) =>
        new(
            new SingleResponseClientFactory(response),
            ArcanumApiCredentialLeaseTestFactory.Create("test-key"))
        {
            MaxStreamLineLength = maxStreamLineLength,
        };

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        List<T> items = [];

        await foreach (T item in source)
        {
            items.Add(item);
        }

        return items;
    }

    private sealed class SingleResponseClientFactory(HttpResponseMessage response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new SingleResponseHandler(response), disposeHandler: true)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
                Timeout = Timeout.InfiniteTimeSpan,
            };
    }

    private sealed class SingleResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
