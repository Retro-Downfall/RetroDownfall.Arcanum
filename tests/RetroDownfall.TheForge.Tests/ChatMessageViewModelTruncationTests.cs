using System.ComponentModel;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

/// <summary>
/// The Tome caps a transcript bubble by UTF-16 code-unit count. Every cut has to land on a whole
/// character: slicing between the two halves of a surrogate pair leaves a lone surrogate that
/// renders as a replacement glyph and is invalid once re-encoded to UTF-8.
/// </summary>
public class ChatMessageViewModelTruncationTests
{

    private const string Emoji = "\U0001F525";

    [Fact]
    public void Constructor_DoesNotSplitASurrogatePairWhenCappingOversizedContent()
    {

        // Pad so the cap lands exactly between the high and low surrogate of the final emoji.
        int contentLimit = ChatMessageViewModel.DefaultMaxContentChars
            - ChatMessageViewModel.ContentTruncationMarker.Length;

        string content = new string('a', contentLimit - 1) + string.Concat(Enumerable.Repeat(Emoji, 64));

        ChatMessageViewModel message = new("assistant", content);

        AssertNoLoneSurrogate(message.Content);

    }

    [Fact]
    public void AppendContent_DoesNotSplitASurrogatePairWhenTheBufferCutLandsMidPair()
    {

        int contentLimit = ChatMessageViewModel.DefaultMaxReasoningChars
            - ChatMessageViewModel.ReasoningTruncationMarker.Length;

        // The seed already exceeds the limit by one char, so the StringBuilder.Length cut is the one
        // that lands mid-pair — the appended chunk contributes nothing.
        string seed = new string('a', contentLimit - 1) + Emoji;

        ChatMessageViewModel message = new("reasoning", string.Empty);

        message.AppendContent(seed);

        message.AppendContent(new string('b', ChatMessageViewModel.DefaultMaxReasoningChars));

        message.CompleteStreamingContent();

        AssertNoLoneSurrogate(message.Content);

    }

    [Fact]
    public void AppendContent_DoesNotSplitASurrogatePairInsideTheAppendedChunk()
    {

        int contentLimit = ChatMessageViewModel.DefaultMaxReasoningChars
            - ChatMessageViewModel.ReasoningTruncationMarker.Length;

        ChatMessageViewModel message = new("reasoning", string.Empty);

        message.AppendContent(new string('a', contentLimit - 1));

        // One char of headroom remains and the chunk opens with a two-char emoji, so the chunk slice
        // is the cut that lands mid-pair.
        message.AppendContent(string.Concat(Enumerable.Repeat(Emoji, 64)));

        message.CompleteStreamingContent();

        AssertNoLoneSurrogate(message.Content);

    }

    [Fact]
    public void AppendContent_ManyNewlineTerminatedChunks_PublishesFarFewerTimesThanChunks()
    {

        ChatMessageViewModel message = new("assistant", string.Empty);

        int publishCount = 0;

        message.PropertyChanged += (object? _, PropertyChangedEventArgs e) =>
        {

            if (e.PropertyName == nameof(ChatMessageViewModel.Content))
            {

                publishCount++;

            }

        };

        // A code-heavy streamed response: every chunk is short and newline-terminated, the shape the
        // old per-newline publish trigger turned into roughly one publish per chunk.
        const int chunkCount = 6_250;

        for (int i = 0; i < chunkCount; i++)
        {

            message.AppendContent("x\n");

        }

        message.CompleteStreamingContent();

        Assert.True(
            publishCount <= chunkCount / 16,
            $"expected far fewer than {chunkCount} Content publishes, got {publishCount}.");

    }

    private static void AssertNoLoneSurrogate(string text)
    {

        Assert.NotEmpty(text);

        for (int i = 0; i < text.Length; i++)
        {

            if (char.IsHighSurrogate(text[i]))
            {

                Assert.True(
                    i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]),
                    $"Lone high surrogate at index {i}.");

                i++;

                continue;

            }

            Assert.False(char.IsLowSurrogate(text[i]), $"Orphaned low surrogate at index {i}.");

        }

    }

}
