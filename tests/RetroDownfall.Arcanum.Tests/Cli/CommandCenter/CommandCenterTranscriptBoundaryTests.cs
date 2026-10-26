using System.Threading.Channels;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterTranscriptBoundaryTests
{
    [Theory]
    [InlineData("user", "[ToolResult: this is my question]", null, false)]
    [InlineData("user", "[ToolCall: pretend(1)]", null, false)]
    [InlineData("user", "question", "pretend", false)]
    [InlineData("assistant", "[ToolResult: quoted result]", null, false)]
    [InlineData("assistant", "[toolcall: pretend(1)]", null, false)]
    [InlineData("assistant", " [ToolCall: quoted(1)]", null, false)]
    [InlineData("system", "[ToolCall: quoted(1)]", null, false)]
    [InlineData("system", "[toolresult: quoted]", null, false)]
    [InlineData("system", " [ToolResult: quoted]", null, false)]
    [InlineData("system", "policy", "pretend", false)]
    [InlineData("assistant", "[ToolCall: read_file({})]", null, true)]
    [InlineData("assistant", "structured call", "read_file", true)]
    [InlineData("system", "[ToolResult: ok]", null, true)]
    [InlineData("tool", "ok", null, true)]
    [InlineData("function", "ok", null, true)]
    public void Persisted_tool_detection_respects_role_and_exact_host_markers(
        string role,
        string content,
        string? toolName,
        bool expected)
    {
        EntryDto entry = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            role,
            content,
            null,
            toolName,
            new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(expected, PersistedToolInteraction.IsToolInteraction(entry));
    }

    [Theory]
    [InlineData("hello world again", 5, "hello|world|again")]
    [InlineData("a bb c", 4, "a bb|c")]
    [InlineData("aaaa bbbb", 4, "aaaa|bbbb")]
    [InlineData("hello  \t world", 5, "hello|world")]
    [InlineData("你好 世界 再见", 4, "你好|世界|再见")]
    [InlineData("😀😀 abc", 4, "😀😀|abc")]
    [InlineData("e\u0301cole suite", 5, "e\u0301cole|suite")]
    public void Wrapping_breaks_on_whitespace_immediately_after_the_cell_window(
        string text,
        int width,
        string expected)
    {
        string[] lines = SessionLogBuffer.WrapLine(text, width).ToArray();

        Assert.Equal(expected.Split('|'), lines);
    }

    [Theory]
    [InlineData("hello \u0301world", 5, "hello| \u0301worl|d")]
    [InlineData("你好 \u0301世界", 4, "你好| \u0301世|界")]
    // The pinned renderer measures space+VS16 as one cell; its decoration still remains content.
    [InlineData("hello \ufe0fworld", 5, "hello| \ufe0fworl|d")]
    [InlineData("hello  \u0301world", 5, "hello| \u0301worl|d")]
    [InlineData("hello \t \u0301world", 5, "hello| \u0301worl|d")]
    [InlineData("hi \u0301there", 5, "hi \u0301th|ere")]
    [InlineData("hi \u0301 there", 5, "hi \u0301|there")]
    public void Wrapping_preserves_decorated_whitespace_graphemes(
        string text,
        int width,
        string expected)
    {
        string[] lines = SessionLogBuffer.WrapLine(text, width).ToArray();

        Assert.Equal(expected.Split('|'), lines);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("dispose")]
    [InlineData("final")]
    [InlineData("block")]
    [InlineData("cadence")]
    public async Task Pending_flush_after_ui_shutdown_preserves_final_state_without_throwing(string path)
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        DateTimeOffset now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        int snapshots = 0;

        await using StreamingUiCoalescer coalescer = new(
            channel.Writer,
            utcNow: () => now,
            beforeFlush: () => snapshots++);

        await coalescer.NoteTokenAsync();

        Assert.True(coalescer.HasPending);

        Assert.True(channel.Writer.TryComplete());

        switch (path)
        {
            case "cancel":
                await coalescer.FlushCancelledAsync();
                break;
            case "dispose":
                await coalescer.DisposeAsync();
                break;
            case "final":
                await coalescer.FlushFinalAsync();
                break;
            case "block":
                await coalescer.FlushBeforeBlockAsync();
                break;
            case "cadence":
                now += TimeSpan.FromSeconds(1);
                await coalescer.NoteTokenAsync();
                break;
        }

        Assert.False(coalescer.HasPending);

        Assert.Equal(1, snapshots);

        Assert.Equal(1, coalescer.FlushCount);

        Assert.False(channel.Reader.TryRead(out _));

        await channel.Reader.Completion;
    }
}
