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
