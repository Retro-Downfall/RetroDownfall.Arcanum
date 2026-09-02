using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Storage;

public sealed class SessionAttachmentPathSanitizerTests
{

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("///")]
    [InlineData("../..")]
    public void TrySanitize_rejects_empty_or_dot_only(string? input)
    {

        Assert.False(SessionAttachmentPathSanitizer.TrySanitize(input, out string sanitized, out string error));

        Assert.Equal(string.Empty, sanitized);

        Assert.False(string.IsNullOrWhiteSpace(error));

    }

    [Theory]
    [InlineData("../etc/passwd", "etcpasswd")]
    [InlineData("..\\windows\\system32", "windowssystem32")]
    [InlineData("foo/../bar", "foobar")]
    [InlineData("a/b", "ab")]
    [InlineData("a\\b", "ab")]
    [InlineData("/absolute", "absolute")]
    public void TrySanitize_strips_path_traversal_and_separators(string input, string expected)
    {

        Assert.True(SessionAttachmentPathSanitizer.TrySanitize(input, out string sanitized, out string error));

        Assert.Equal(expected, sanitized);

        Assert.Equal(string.Empty, error);

    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("nul.txt")]
    public void TrySanitize_rejects_reserved_names(string input)
    {

        Assert.False(SessionAttachmentPathSanitizer.TrySanitize(input, out _, out string error));

        Assert.False(string.IsNullOrWhiteSpace(error));

    }

    [Theory]
    [InlineData("notes.txt", "notes.txt")]
    [InlineData("shot.png", "shot.png")]
    [InlineData("My Notes.TXT", "My Notes.TXT")]
    [InlineData("file_name-1.md", "file_name-1.md")]
    public void TrySanitize_accepts_safe_names(string input, string expected)
    {

        Assert.True(SessionAttachmentPathSanitizer.TrySanitize(input, out string sanitized, out string error));

        Assert.Equal(expected, sanitized);

        Assert.Equal(string.Empty, error);

    }

    [Theory]
    [InlineData("q3-report?.md", "q3-report.md")]
    [InlineData("why<not>.txt", "whynot.txt")]
    [InlineData("wild*card.log", "wildcard.log")]
    [InlineData("say \"hello\".txt", "say hello.txt")]
    [InlineData("left|right.csv", "leftright.csv")]
    public void TrySanitize_strips_characters_windows_filenames_reject(string input, string expected)
    {

        Assert.True(SessionAttachmentPathSanitizer.TrySanitize(input, out string sanitized, out string error));

        Assert.Equal(expected, sanitized);

        Assert.Equal(string.Empty, error);

    }

    [Fact]
    public void TrySanitize_rejects_names_made_only_of_unsafe_characters()
    {

        Assert.False(SessionAttachmentPathSanitizer.TrySanitize("<*?>", out string sanitized, out string error));

        Assert.Equal(string.Empty, sanitized);

        Assert.False(string.IsNullOrWhiteSpace(error));

    }

    [Fact]
    public void TrySanitize_strips_leading_dots()
    {

        Assert.True(SessionAttachmentPathSanitizer.TrySanitize("...hidden.txt", out string sanitized, out _));

        Assert.Equal("hidden.txt", sanitized);

    }

    [Fact]
    public void TrySanitize_trims_a_trailing_dot_so_windows_normalisation_cannot_collide_two_logical_keys()
    {

        bool dottedOk = SessionAttachmentPathSanitizer.TrySanitize("notes.", out string dotted, out _);

        bool plainOk = SessionAttachmentPathSanitizer.TrySanitize("notes", out string plain, out _);

        if (dottedOk && plainOk)
        {

            Assert.Equal(plain, dotted);

        }
        else
        {

            Assert.False(dottedOk);

            Assert.False(plainOk);

        }

    }

    [Fact]
    public void TrySanitize_trims_a_trailing_dot_exposed_by_stripping_a_trailing_unsafe_character()
    {

        Assert.True(SessionAttachmentPathSanitizer.TrySanitize("report", out string expected, out _));

        Assert.True(SessionAttachmentPathSanitizer.TrySanitize("report.|", out string sanitized, out _));

        Assert.Equal(expected, sanitized);

    }

    [Fact]
    public void TrySanitize_trims_a_trailing_dot_exposed_by_truncation()
    {

        // 121 chars; truncating to the 120-char cap drops the trailing 'b' and would leave a
        // trailing '.' if nothing re-trimmed after the cut.
        string aDotB = new string('a', 119) + ".b";

        string aOnly = new string('a', 119);

        bool longOk = SessionAttachmentPathSanitizer.TrySanitize(aDotB, out string longSanitized, out _);

        bool shortOk = SessionAttachmentPathSanitizer.TrySanitize(aOnly, out string shortSanitized, out _);

        if (longOk && shortOk)
        {

            Assert.Equal(shortSanitized, longSanitized);

        }
        else
        {

            Assert.False(longOk);

            Assert.False(shortOk);

        }

    }

    [Fact]
    public void TrySanitize_strips_control_characters()
    {

        Assert.True(SessionAttachmentPathSanitizer.TrySanitize("a\nb\tc.txt", out string sanitized, out _));

        Assert.Equal("abc.txt", sanitized);

    }

    [Fact]
    public void TrySanitize_caps_length_near_120()
    {

        string input = new('a', 200);

        Assert.True(SessionAttachmentPathSanitizer.TrySanitize(input, out string sanitized, out _));

        Assert.Equal(120, sanitized.Length);

    }

    [Fact]
    public void TrySanitize_does_not_split_a_surrogate_pair_at_the_length_cap()
    {

        string input = new string('a', 119) + "\U0001F600" + new string('b', 40);

        Assert.True(SessionAttachmentPathSanitizer.TrySanitize(input, out string sanitized, out _));

        Assert.Equal(119, sanitized.Length);

        Assert.DoesNotContain(sanitized, char.IsSurrogate);

        Assert.Equal(sanitized, Utf8Truncation.NormalizeInvalidUtf16(sanitized));

    }

    [Fact]
    public void TrySanitize_keeps_a_whole_surrogate_pair_that_fits_under_the_cap()
    {

        string input = new string('a', 118) + "\U0001F600" + new string('b', 40);

        Assert.True(SessionAttachmentPathSanitizer.TrySanitize(input, out string sanitized, out _));

        Assert.Equal(120, sanitized.Length);

        Assert.EndsWith("\U0001F600", sanitized, StringComparison.Ordinal);

        Assert.Equal(sanitized, Utf8Truncation.NormalizeInvalidUtf16(sanitized));

    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("turn/../escape")]
    [InlineData("turn\\evil")]
    [InlineData("bad id")]
    public void TryValidatePendingTurnId_rejects_unsafe(string? input)
    {

        Assert.False(SessionAttachmentPathSanitizer.TryValidatePendingTurnId(input, out string validated, out string error));

        Assert.Equal(string.Empty, validated);

        Assert.False(string.IsNullOrEmpty(error));

    }

    [Theory]
    [InlineData("turn-abc123", "turn-abc123")]
    [InlineData("orphan-deadbeef", "orphan-deadbeef")]
    public void TryValidatePendingTurnId_accepts_safe(string input, string expected)
    {

        Assert.True(SessionAttachmentPathSanitizer.TryValidatePendingTurnId(input, out string validated, out string error));

        Assert.Equal(expected, validated);

        Assert.Equal(string.Empty, error);

    }

}
