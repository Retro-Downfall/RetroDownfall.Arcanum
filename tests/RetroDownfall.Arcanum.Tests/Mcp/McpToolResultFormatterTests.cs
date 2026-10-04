using System.Collections;

using System.Text;

using ModelContextProtocol.Protocol;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpToolResultFormatterTests
{
    [Fact]
    public void FormatContentText_stops_accumulating_at_cap()
    {
        const long cap = 200;

        CountingContentList blocks = new(count: 100_000, text: new string('x', 50));

        string text = McpToolResultFormatter.FormatContentText(new CallToolResult { Content = blocks }, cap);

        // Each block adds 51 bytes (text plus separator), so the cap is crossed by the fifth block.
        // Reading any further block means the whole result was accumulated before being cut.
        Assert.InRange(blocks.Visited, 1, 6);

        Assert.EndsWith("\n[truncated: exceeded 200 bytes]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatContentText_stops_inside_a_single_oversized_block_without_copying_the_whole_block()
    {
        string huge = new('y', 5_000_000);

        string text = McpToolResultFormatter.FormatContentText(
            new CallToolResult { Content = [new TextContentBlock { Text = huge }] },
            maxUtf8Bytes: 100);

        Assert.Equal(new string('y', 100) + "\n[truncated: exceeded 100 bytes]", text);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(5L)]
    [InlineData(10L)]
    [InlineData(11L)]
    [InlineData(12L)]
    [InlineData(13L)]
    [InlineData(25L)]
    [InlineData(40L)]
    [InlineData(1_000L)]
    [InlineData(long.MaxValue)]
    public void FormatContentText_matches_the_whole_text_truncation_for_every_cap(long cap)
    {
        // Multi-byte text, a surrogate pair and an omitted non-text block, so the byte-based cut lands
        // on every kind of boundary the earlier build-everything-then-cut implementation handled.
        List<ContentBlock> content =
        [
            new TextContentBlock { Text = "héllo wörld" },
            new TextContentBlock { Text = "😀😀😀" },
            new ImageContentBlock { Data = Encoding.UTF8.GetBytes("png"), MimeType = "image/png" },
            new TextContentBlock { Text = "tail text that is rather longer than the others" },
        ];

        string expected = McpSecurityLimits.TruncateUtf8(
            string.Join(
                global::System.Environment.NewLine,
                content.Select(static block => block is TextContentBlock text ? text.Text : $"[{block.Type} content omitted]")),
            cap);

        Assert.Equal(expected, McpToolResultFormatter.FormatContentText(new CallToolResult { Content = content }, cap));
    }

    [Fact]
    public void FormatContentText_without_a_cap_returns_every_block()
    {
        CallToolResult result = new()
        {
            Content = [new TextContentBlock { Text = "one" }, new TextContentBlock { Text = "two" }],
        };

        Assert.Equal("one" + global::System.Environment.NewLine + "two", McpToolResultFormatter.FormatContentText(result));
    }

    private sealed class CountingContentList(int count, string text) : IList<ContentBlock>
    {
        private int _visited;

        public int Visited => Volatile.Read(ref _visited);

        public int Count => count;

        public bool IsReadOnly => true;

        public ContentBlock this[int index]
        {
            get => new TextContentBlock { Text = text };
            set => throw new NotSupportedException();
        }

        public IEnumerator<ContentBlock> GetEnumerator()
        {
            for (int index = 0; index < count; index++)
            {
                _ = Interlocked.Increment(ref _visited);

                yield return new TextContentBlock { Text = text };
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public int IndexOf(ContentBlock item) => throw new NotSupportedException();

        public void Insert(int index, ContentBlock item) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();

        public void Add(ContentBlock item) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(ContentBlock item) => throw new NotSupportedException();

        public void CopyTo(ContentBlock[] array, int arrayIndex) => throw new NotSupportedException();

        public bool Remove(ContentBlock item) => throw new NotSupportedException();
    }
}
