using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class SseFrameParserTests
{

    [Fact]
    public async Task ParseAsync_SingleDataLine_YieldsOneFrame()
    {

        using StringReader reader = new("data: {\"hello\":\"world\"}\n\n");

        List<SseEvent> frames = await CollectAsync(reader);

        SseEvent frame = Assert.Single(frames);

        Assert.Null(frame.Event);

        Assert.Equal("{\"hello\":\"world\"}", frame.Data);

    }

    [Fact]
    public async Task ParseAsync_MultiLineData_JoinsWithNewline()
    {

        using StringReader reader = new("data: line one\ndata: line two\n\n");

        List<SseEvent> frames = await CollectAsync(reader);

        SseEvent frame = Assert.Single(frames);

        Assert.Equal("line one\nline two", frame.Data);

    }

    [Fact]
    public async Task ParseAsync_EventLine_IsCaptured()
    {

        using StringReader reader = new("event: entry\ndata: {\"id\":1}\n\n");

        List<SseEvent> frames = await CollectAsync(reader);

        SseEvent frame = Assert.Single(frames);

        Assert.Equal("entry", frame.Event);

    }

    [Fact]
    public async Task ParseAsync_KeepAliveComment_IsSkipped()
    {

        using StringReader reader = new(": keep-alive\n\ndata: {\"a\":1}\n\n");

        List<SseEvent> frames = await CollectAsync(reader);

        SseEvent frame = Assert.Single(frames);

        Assert.Equal("{\"a\":1}", frame.Data);

    }

    [Fact]
    public async Task ParseAsync_DoneTerminator_StopsBeforeYielding()
    {

        using StringReader reader = new("data: {\"a\":1}\n\ndata: [DONE]\n\ndata: {\"b\":2}\n\n");

        List<SseEvent> frames = await CollectAsync(reader);

        SseEvent frame = Assert.Single(frames);

        Assert.Equal("{\"a\":1}", frame.Data);

    }

    [Fact]
    public async Task ParseAsync_MultipleFrames_YieldsInOrder()
    {

        using StringReader reader = new("data: {\"seq\":1}\n\ndata: {\"seq\":2}\n\ndata: {\"seq\":3}\n\n");

        List<SseEvent> frames = await CollectAsync(reader);

        Assert.Equal(3, frames.Count);

        Assert.Equal("{\"seq\":1}", frames[0].Data);

        Assert.Equal("{\"seq\":3}", frames[2].Data);

    }

    private static async Task<List<SseEvent>> CollectAsync(TextReader reader)
    {

        List<SseEvent> frames = [];

        await foreach (SseEvent frame in SseFrameParser.ParseAsync(reader, CancellationToken.None))
        {

            frames.Add(frame);

        }

        return frames;

    }

}
