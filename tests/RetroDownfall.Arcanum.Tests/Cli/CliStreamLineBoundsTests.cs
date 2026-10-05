using System.Net;

using System.Net.Http.Headers;

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
/// Pins the line-framed streams (<c>ask</c>, <c>web research</c>, the Apprentice Chronicle and the
/// watch SSE stream) against the hazards a real socket produces: delivery in one-byte reads that
/// split a code point or a CRLF pair, a line that never ends, and a server that sends one event and
/// then waits. The NDJSON loops used to go through an unbounded <c>StreamReader.ReadLineAsync</c>
/// with no test of the first two.
/// </summary>
public sealed class CliStreamLineBoundsTests
{
    private const string Emoji = "\U0001F600";

    private const string MessagePlaceholder = "@@message@@";

    /// <summary>
    /// Hang guard for the tests that leave the stream quiet, not a behavioural assertion: an event the
    /// reader already holds is handed over without another read, so this only turns a held-back event
    /// into a failure instead of a hung suite.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);

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

    /// <summary>
    /// A server that sends one event and then waits for the client (an <c>ask_human</c> prompt, a
    /// tool-start event ahead of a long tool run) must see the event delivered at once. The decoders
    /// the loops read through used to go back to the stream whenever the bytes of one read filled their
    /// buffer exactly, so an event of exactly that many bytes was held until the server sent more,
    /// which for a prompt it was waiting on the client to answer was never. The sizes are the decoder
    /// buffers the loops used (1,024 and 4,096 bytes) and a multiple of both. Plain ASCII alone does not
    /// reach every loop, because a read that fills the byte buffer also fills a 4,096-character caller
    /// buffer when the text is one byte per character, so the larger sizes are multibyte text.
    /// </summary>
    [Theory]
    [InlineData(1024, false)]
    [InlineData(4096, false)]
    [InlineData(4096, true)]
    [InlineData(8192, true)]
    public async Task Ask_stream_delivers_an_event_of_exactly_a_read_buffer_while_the_server_waits(int wireBytes, bool multibyte)
    {
        string padding = PaddingForExactBytes(wireBytes, multibyte, AskLine);

        IntelligenceEvent first = await FirstItemWhileTheServerWaitsAsync(
            AskLine(padding),
            static (client, token) => client.AskStreamAsync(new PingRequest("hello"), token));

        Assert.Equal(IntelligenceEventType.Token, first.Type);

        Assert.Equal(padding, first.Message);
    }

    [Theory]
    [InlineData(1024, false)]
    [InlineData(4096, false)]
    [InlineData(4096, true)]
    [InlineData(8192, true)]
    public async Task Research_stream_delivers_a_frame_of_exactly_a_read_buffer_while_the_server_waits(int wireBytes, bool multibyte)
    {
        string padding = PaddingForExactBytes(wireBytes, multibyte, ResearchLine);

        WebResearchStreamFrame first = await FirstItemWhileTheServerWaitsAsync(
            ResearchLine(padding),
            static (client, token) => client.ResearchWebAsync(
                new WebResearchWorkflowRequest { Question = "why" },
                token));

        Assert.Equal(WebResearchStreamFrameType.Progress, first.Type);

        Assert.Equal(padding, first.Message);
    }

    [Theory]
    [InlineData(1024, false)]
    [InlineData(4096, false)]
    [InlineData(4096, true)]
    [InlineData(8192, true)]
    public async Task Chronicle_stream_delivers_a_frame_of_exactly_a_read_buffer_while_the_server_waits(int wireBytes, bool multibyte)
    {
        string padding = PaddingForExactBytes(wireBytes, multibyte, ChronicleLine);

        ChronicleFrame first = await FirstItemWhileTheServerWaitsAsync(
            ChronicleLine(padding),
            static (client, token) => client.StreamApprenticeChronicleAsync(Guid.NewGuid(), token));

        Assert.Equal("status", first.Type);

        Assert.Equal(padding, first.Message);
    }

    [Theory]
    [InlineData(1024, false)]
    [InlineData(4096, false)]
    [InlineData(4096, true)]
    [InlineData(8192, true)]
    public async Task Watch_stream_delivers_an_event_of_exactly_a_read_buffer_while_the_server_waits(int wireBytes, bool multibyte)
    {
        string padding = PaddingForExactBytes(wireBytes, multibyte, WatchEvent);

        WatchSseFrame first = await FirstItemWhileTheServerWaitsAsync(
            WatchEvent(padding),
            static (client, token) => client.WatchSseAsync("api/events/mcp", token),
            "text/event-stream");

        Assert.Equal(WatchSseFrameType.Data, first.Type);

        Assert.Equal(padding, first.Data!.Value.GetProperty("message").GetString());
    }

    // The serializer escapes non-ASCII text, which would keep the wire at one byte per character, so
    // the message is spliced in raw (a JSON string may carry UTF-8 text directly).
    private static string AskLine(string message) =>
        JsonSerializer.Serialize(
            new IntelligenceEvent(IntelligenceEventType.Token, MessagePlaceholder),
            ArcanumJsonContext.Default.IntelligenceEvent).Replace(MessagePlaceholder, message, StringComparison.Ordinal) + "\n";

    private static string ResearchLine(string message) =>
        JsonSerializer.Serialize(
            new WebResearchStreamFrame
            {
                Type = WebResearchStreamFrameType.Progress,
                Message = MessagePlaceholder,
            },
            ArcanumJsonContext.Default.WebResearchStreamFrame).Replace(MessagePlaceholder, message, StringComparison.Ordinal) + "\n";

    private static string ChronicleLine(string message) =>
        "data: {\"type\":\"status\",\"message\":\"" + message + "\"}\n";

    private static string WatchEvent(string message) =>
        "data: {\"message\":\"" + message + "\"}\n\n";

    /// <summary>
    /// The padding that makes <paramref name="wire"/> exactly <paramref name="totalBytes"/> long, so
    /// the test controls how the bytes line up with a decoder's read buffer. Multibyte padding is
    /// two-byte characters, which makes the decoder produce fewer characters than it was given bytes:
    /// a read that fills the byte buffer then leaves the caller's character buffer short as well.
    /// </summary>
    private static string PaddingForExactBytes(int totalBytes, bool multibyte, Func<string, string> wire)
    {
        int unpadded = Encoding.UTF8.GetByteCount(wire(string.Empty));

        Assert.True(totalBytes >= unpadded);

        int remaining = totalBytes - unpadded;

        string padding = multibyte
            ? new string('\u00E9', remaining / 2) + new string('p', remaining % 2)
            : new string('p', remaining);

        Assert.Equal(totalBytes, Encoding.UTF8.GetByteCount(wire(padding)));

        return padding;
    }

    /// <summary>
    /// Serves <paramref name="wire"/> and then leaves the connection open and silent, and returns the
    /// first item the stream yields. A reader that waits for more bytes before it hands the item over
    /// never returns here, and the failure says so.
    /// </summary>
    private static async Task<T> FirstItemWhileTheServerWaitsAsync<T>(
        string wire,
        Func<ArcanumApiClient, CancellationToken, IAsyncEnumerable<T>> open,
        string? mediaType = null)
    {
        using QuietAfterBytesStream stream = new(Encoding.UTF8.GetBytes(wire));

        StreamContent content = new(stream);

        if (mediaType is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        }

        ArcanumApiClient client = CreateClient(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });

        using CancellationTokenSource shutdown = new();

        await using IAsyncEnumerator<T> items = open(client, shutdown.Token)
            .GetAsyncEnumerator(shutdown.Token);

        Task<bool> move = items.MoveNextAsync().AsTask();

        try
        {
            bool hasItem;

            try
            {
                hasItem = await move.WaitAsync(HangGuard);
            }
            catch (TimeoutException)
            {
                Assert.Fail(
                    "The stream held the event back until the server sent more bytes: "
                    + $"{stream.ReadsServed} read(s) served, no item delivered.");

                throw;
            }

            Assert.True(hasItem);

            return items.Current;
        }
        finally
        {
            // A failed wait leaves the read outstanding; cancelling it lets the iterator finish
            // before it is disposed, so the assertion above is the failure that surfaces.
            await shutdown.CancelAsync();

            _ = await Task.WhenAny(move);
        }
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
