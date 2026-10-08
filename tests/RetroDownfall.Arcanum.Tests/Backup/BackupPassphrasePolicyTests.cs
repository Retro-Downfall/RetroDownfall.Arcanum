using RetroDownfall.Arcanum.Core.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// The creation floor counts what a person sees as a character, not the UTF-16 code units a string
/// happens to be stored in: otherwise six emoji, or six accented letters written as a base letter plus
/// a combining mark, meet a rule the documentation states as twelve characters.
/// </summary>
public sealed class BackupPassphrasePolicyTests
{
    private const string Emoji = "\U0001F600";

    private const string FamilyEmoji = "\U0001F468‍\U0001F469‍\U0001F467";

    private const string DecomposedE = "é";

    [Theory]
    [InlineData("pppppppppppp", true)]
    [InlineData("ppppppppppp", false)]
    [InlineData("", false)]
    public void Plain_text_is_counted_character_for_character(string passphrase, bool expected) =>
        Assert.Equal(expected, BackupPassphrasePolicy.MeetsCreateMinimum(passphrase));

    [Fact]
    public void Six_emoji_are_six_characters_not_twelve()
    {
        string sixEmoji = string.Concat(Enumerable.Repeat(Emoji, 6));

        Assert.Equal(12, sixEmoji.Length);

        Assert.False(BackupPassphrasePolicy.MeetsCreateMinimum(sixEmoji));
    }

    [Fact]
    public void Twelve_emoji_meet_the_minimum()
    {
        Assert.True(
            BackupPassphrasePolicy.MeetsCreateMinimum(
                string.Concat(Enumerable.Repeat(Emoji, BackupPassphrasePolicy.MinimumCreateCharacters))));
    }

    [Fact]
    public void A_base_letter_with_a_combining_mark_is_one_character()
    {
        string six = string.Concat(Enumerable.Repeat(DecomposedE, 6));

        string twelve = string.Concat(Enumerable.Repeat(DecomposedE, 12));

        Assert.False(BackupPassphrasePolicy.MeetsCreateMinimum(six));

        Assert.True(BackupPassphrasePolicy.MeetsCreateMinimum(twelve));
    }

    [Fact]
    public void A_joined_emoji_sequence_is_one_character()
    {
        Assert.False(
            BackupPassphrasePolicy.MeetsCreateMinimum(
                string.Concat(Enumerable.Repeat(FamilyEmoji, 11))));

        Assert.True(
            BackupPassphrasePolicy.MeetsCreateMinimum(
                string.Concat(Enumerable.Repeat(FamilyEmoji, 12))));
    }

    [Fact]
    public void An_unpaired_surrogate_counts_as_a_character_rather_than_throwing()
    {
        string eleven = new string('p', 11) + "\uD83D";

        Assert.True(BackupPassphrasePolicy.MeetsCreateMinimum(eleven));

        Assert.False(BackupPassphrasePolicy.MeetsCreateMinimum(new string('p', 10) + "\uD83D"));
    }

    [Fact]
    public void The_documented_minimum_is_twelve_and_the_messages_say_so()
    {
        Assert.Equal(12, BackupPassphrasePolicy.MinimumCreateCharacters);

        Assert.Contains("12", BackupPassphrasePolicy.CreateMinimumMessage, StringComparison.Ordinal);

        Assert.Contains("12", BackupPassphrasePolicy.CreateWarning, StringComparison.Ordinal);
    }
}
