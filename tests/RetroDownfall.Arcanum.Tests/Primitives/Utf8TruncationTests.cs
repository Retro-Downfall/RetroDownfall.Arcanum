using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Primitives;

public sealed class Utf8TruncationTests
{

    // U+1F600 GRINNING FACE, encoded as the surrogate pair \uD83D\uDE00 (4 UTF-8 bytes).
    private const string Emoji = "\uD83D\uDE00";

    [Fact]
    public void SafeCharSliceLength_TextShorterThanMax_ReturnsFullLength()
    {

        Assert.Equal(5, Utf8Truncation.SafeCharSliceLength("hello", 80));

    }

    [Fact]
    public void SafeCharSliceLength_TextExactlyAtMax_ReturnsFullLength()
    {

        Assert.Equal(5, Utf8Truncation.SafeCharSliceLength("hello", 5));

    }

    [Fact]
    public void SafeCharSliceLength_BoundaryMidSurrogatePair_NudgesBackOneChar()
    {

        // "ab" + emoji (2 chars) + "cd" — a max of 3 chars would land right between the emoji's
        // high and low surrogate halves; the safe length must exclude the whole pair rather than
        // split it.
        string text = "ab" + Emoji + "cd";

        int safeLength = Utf8Truncation.SafeCharSliceLength(text, 3);

        Assert.Equal(2, safeLength);

        string sliced = text[..safeLength];

        Assert.Equal("ab", sliced);

        Assert.False(char.IsSurrogate(sliced[^1]));

    }

    [Fact]
    public void SafeCharSliceLength_BoundaryAfterCompleteSurrogatePair_KeepsPairIntact()
    {

        string text = "ab" + Emoji + "cd";

        int safeLength = Utf8Truncation.SafeCharSliceLength(text, 4);

        Assert.Equal(4, safeLength);

        Assert.Equal("ab" + Emoji, text[..safeLength]);

    }

    [Fact]
    public void SafeCharSliceLength_ZeroOrNegativeMax_ReturnsZero()
    {

        Assert.Equal(0, Utf8Truncation.SafeCharSliceLength("hello", 0));

        Assert.Equal(0, Utf8Truncation.SafeCharSliceLength("hello", -1));

    }

    [Fact]
    public void ChooseSafeCharCount_BoundaryMidSurrogatePair_NudgesBackOneChar()
    {

        // Each ASCII char is 1 UTF-8 byte; the emoji is 4. A budget of 6 bytes ("ab" + 4) lands
        // exactly at the emoji's byte boundary, so nothing should be nudged. A budget of 5 lands one
        // byte into the emoji's high-surrogate half and must be nudged back to exclude the pair.
        string text = "ab" + Emoji + "cd";

        Assert.Equal(2, Utf8Truncation.ChooseSafeCharCount(text, 5));

        Assert.Equal(4, Utf8Truncation.ChooseSafeCharCount(text, 6));

    }

    [Fact]
    public void TruncateToUtf8ByteBudget_NeverSplitsASurrogatePair()
    {

        string text = "ab" + Emoji + "cd";

        string truncated = Utf8Truncation.TruncateToUtf8ByteBudget(text, 5);

        Assert.Equal("ab", truncated);

        Assert.All(truncated, c => Assert.False(char.IsSurrogate(c)));

    }

    [Fact]
    public void TruncateToUtf8ByteBudget_WithinBudget_ReturnsTextUnchanged()
    {

        Assert.Equal("hello", Utf8Truncation.TruncateToUtf8ByteBudget("hello", 80));

    }

    [Fact]
    public void TruncateToUtf8ByteBudget_NonPositiveBudget_ReturnsEmpty()
    {

        Assert.Equal(string.Empty, Utf8Truncation.TruncateToUtf8ByteBudget("hello", 0));

    }

}
