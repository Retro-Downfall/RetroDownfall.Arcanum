using System.Buffers.Text;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Primitives;

/// <summary>
/// The one canonical unpadded base64url decoder: exactly the spellings an encoder writes, every other
/// spelling a plain <c>false</c>, and never an exception.
/// </summary>
public sealed class CanonicalBase64UrlTests
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    private const byte Sentinel = 0xEE;

    private static readonly byte[] Key = [.. Enumerable.Range(0, 32).Select(static value => (byte)((value * 7) + 3))];

    private static readonly string KeyText = Base64Url.EncodeToString(Key);

    [Fact]
    public void Exact_returns_the_bytes_of_the_canonical_spelling()
    {
        Assert.Equal(43, KeyText.Length);

        Assert.True(CanonicalBase64Url.TryDecodeExact(KeyText, 32, out byte[] decoded));

        Assert.Equal(Key, decoded);
    }

    [Fact]
    public void Exact_hands_back_a_fresh_array_each_time()
    {
        Assert.True(CanonicalBase64Url.TryDecodeExact(KeyText, 32, out byte[] first));

        Assert.True(CanonicalBase64Url.TryDecodeExact(KeyText, 32, out byte[] second));

        Assert.NotSame(first, second);

        first[0] ^= 0xFF;

        Assert.Equal(Key, second);
    }

    private static readonly (string? Value, string Reason)[] NotCanonicalAtAnyLength =
    [
        (null, "null"),
        (string.Empty, "empty"),
        (KeyText + "=", "padded"),
        (KeyText[..41], "a length no decoder can read"),
        ("+" + KeyText[1..], "standard alphabet plus"),
        ("/" + KeyText[1..], "standard alphabet slash"),
        (Convert.ToBase64String(Key), "standard alphabet with padding"),
        (NonCanonicalBase64Url.WithUnusedBitSet(KeyText), "unused trailing bit set"),
        (" " + KeyText[1..], "a space"),
        (KeyText[..42] + "\n", "a newline"),
        (KeyText[..42] + "\0", "a NUL"),
        (KeyText[..42] + "\uFF21", "a full-width letter"),
        (KeyText[..42] + "\u00E9", "a non-ASCII letter"),
    ];

    public static TheoryData<string?, string> NotCanonicalAnywhere()
    {
        TheoryData<string?, string> rows = [];

        foreach ((string? value, string reason) in NotCanonicalAtAnyLength)
        {
            rows.Add(value, reason);
        }

        return rows;
    }

    public static TheoryData<string?, string> NotThirtyTwoBytes()
    {
        TheoryData<string?, string> rows = NotCanonicalAnywhere();

        // Canonical spellings of other lengths: refusals for exactly thirty-two bytes and nothing else.
        rows.Add(KeyText + "A", "one character long");
        rows.Add(KeyText[..42], "one character short");
        rows.Add(KeyText[..40], "two groups short");

        return rows;
    }

    [Theory]
    [MemberData(nameof(NotThirtyTwoBytes))]
    public void Exact_refuses_every_other_spelling_without_throwing_and_hands_back_nothing(
        string? encoded,
        string reason)
    {
        bool accepted = CanonicalBase64Url.TryDecodeExact(encoded, 32, out byte[] decoded);

        Assert.False(accepted, reason);

        Assert.Empty(decoded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Exact_treats_a_nonpositive_length_as_the_programming_error_it_is(int expectedBytes)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => CanonicalBase64Url.TryDecodeExact(KeyText, expectedBytes, out _));
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(-1, 8)]
    [InlineData(5, 4)]
    public void Bounded_treats_impossible_bounds_as_the_programming_error_they_are(int minimumBytes, int maximumBytes)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => CanonicalBase64Url.TryDecodeBounded(KeyText, minimumBytes, maximumBytes, out _));
    }

    [Theory]
    [InlineData("AA", 1, 1, true)]
    [InlineData("AAA", 2, 2, true)]
    [InlineData("AAAA", 3, 3, true)]
    [InlineData("AAAAAA", 4, 4, true)]
    [InlineData("AAAAAAA", 5, 5, true)]
    [InlineData("AAAA", 1, 3, true)]
    [InlineData("AA", 1, 3, true)]
    [InlineData("AAAAAA", 1, 4, true)]
    [InlineData("AB", 1, 1, false)]
    [InlineData("AAB", 1, 2, false)]
    [InlineData("AAAAAB", 1, 4, false)]
    [InlineData("AAAAAAB", 1, 5, false)]
    [InlineData("AAAAA", 1, 9, false)]
    [InlineData("A", 1, 9, false)]
    [InlineData("AAAAAA", 1, 3, false)]
    [InlineData("AAAAAA", 5, 9, false)]
    [InlineData("AAAA", 4, 9, false)]
    [InlineData("AAAA=", 1, 9, false)]
    [InlineData("AAA=", 1, 9, false)]
    [InlineData("AA==", 1, 9, false)]
    [InlineData("", 1, 9, false)]
    public void Bounded_accepts_exactly_the_canonical_spellings_inside_its_bounds(
        string encoded,
        int minimumBytes,
        int maximumBytes,
        bool expected)
    {
        bool accepted = CanonicalBase64Url.TryDecodeBounded(encoded, minimumBytes, maximumBytes, out byte[] decoded);

        Assert.Equal(expected, accepted);

        if (accepted)
        {
            Assert.Equal(Base64Url.DecodeFromChars(encoded), decoded);
        }
        else
        {
            Assert.Empty(decoded);
        }
    }

    [Fact]
    public void Bounded_refuses_null()
    {
        Assert.False(CanonicalBase64Url.TryDecodeBounded(null, 1, 9, out byte[] decoded));

        Assert.Empty(decoded);
    }

    [Fact]
    public void Bounded_refuses_a_token_longer_than_the_largest_legal_encoding_before_decoding_it()
    {
        // Eight bytes encode to eleven characters, so twelve cannot be legal however they decode.
        Assert.True(CanonicalBase64Url.TryDecodeBounded(new string('A', 11), 1, 8, out _));

        Assert.False(CanonicalBase64Url.TryDecodeBounded(new string('A', 12), 1, 8, out byte[] decoded));

        Assert.Empty(decoded);

        Assert.False(CanonicalBase64Url.TryDecodeBounded(new string('A', 1 << 20), 1, 8, out _));
    }

    [Fact]
    public void Bounded_returns_an_array_of_exactly_the_decoded_length()
    {
        byte[] payload = [.. Enumerable.Range(0, 100).Select(static value => (byte)value)];

        Assert.True(CanonicalBase64Url.TryDecodeBounded(Base64Url.EncodeToString(payload), 1, 4096, out byte[] decoded));

        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void Decoding_into_a_destination_writes_exactly_the_decoded_bytes_and_leaves_the_rest_alone()
    {
        Span<byte> destination = new byte[40];

        destination.Fill(Sentinel);

        Assert.True(CanonicalBase64Url.TryDecode(KeyText, destination, out int written));

        Assert.Equal(32, written);

        Assert.True(destination[..32].SequenceEqual(Key));

        Assert.True(destination[32..].SequenceEqual(Enumerable.Repeat(Sentinel, 8).ToArray()));
    }

    [Theory]
    [MemberData(nameof(NotCanonicalAnywhere))]
    public void Decoding_into_a_destination_zeroes_all_of_it_on_every_refusal(string? encoded, string reason)
    {
        // Every one of these is refused after the decoder has either written some of the destination or
        // none of it, and the caller must not be left holding either.
        Span<byte> destination = new byte[40];

        destination.Fill(Sentinel);

        Assert.False(CanonicalBase64Url.TryDecode(encoded, destination, out int written), reason);

        Assert.Equal(0, written);

        Assert.True(destination.IndexOfAnyExcept((byte)0) < 0, reason + ": the destination was not zeroed");
    }

    [Fact]
    public void Decoding_into_a_destination_that_is_too_small_is_a_refusal_that_zeroes_it()
    {
        Span<byte> destination = new byte[31];

        destination.Fill(Sentinel);

        Assert.False(CanonicalBase64Url.TryDecode(KeyText, destination, out int written));

        Assert.Equal(0, written);

        Assert.True(destination.IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void A_noncanonical_final_character_is_zeroed_even_though_the_bytes_before_it_decoded()
    {
        // The refusal this pins is the one the framework's throwing decoder turns into FormatException:
        // thirty-one of the thirty-two bytes are real, so a decoder that wrote them and then gave up
        // would leave key material behind in a buffer the caller believes is empty.
        Span<byte> destination = new byte[32];

        destination.Fill(Sentinel);

        Assert.False(CanonicalBase64Url.TryDecode(NonCanonicalBase64Url.WithUnusedBitSet(KeyText), destination, out _));

        Assert.True(destination.IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void Every_short_string_is_accepted_exactly_when_it_is_the_canonical_spelling_of_something()
    {
        // An exhaustive sweep, against an oracle that shares nothing with the implementation: the
        // alphabet, the length arithmetic, and the rule that a short final group's unused low bits are
        // zero. Characters outside the alphabet and the padding character are in the sweep on purpose.
        string[] symbols = ["A", "B", "Q", "w", "-", "=", "+", "\u00E9"];

        int checkedStrings = 0;

        for (int length = 0; length <= 6; length++)
        {
            foreach (string candidate in Strings(symbols, length))
            {
                bool canonical = IsCanonicalSpelling(candidate, out int decodedLength);

                bool accepted = CanonicalBase64Url.TryDecodeBounded(candidate, 1, 16, out byte[] decoded);

                Assert.Equal(canonical && decodedLength is >= 1 and <= 16, accepted);

                if (accepted)
                {
                    Assert.Equal(decodedLength, decoded.Length);

                    Assert.Equal(candidate, Base64Url.EncodeToString(decoded));
                }
                else
                {
                    Assert.Empty(decoded);
                }

                Span<byte> destination = new byte[16];

                destination.Fill(Sentinel);

                bool intoDestination = CanonicalBase64Url.TryDecode(candidate, destination, out int written);

                Assert.Equal(canonical && decodedLength > 0, intoDestination);

                if (!intoDestination)
                {
                    Assert.Equal(0, written);

                    Assert.True(destination.IndexOfAnyExcept((byte)0) < 0);
                }

                checkedStrings++;
            }
        }

        Assert.True(checkedStrings > 100_000);
    }

    private static IEnumerable<string> Strings(string[] symbols, int length)
    {
        if (length == 0)
        {
            yield return string.Empty;

            yield break;
        }

        foreach (string prefix in Strings(symbols, length - 1))
        {
            foreach (string symbol in symbols)
            {
                yield return prefix + symbol;
            }
        }
    }

    private static bool IsCanonicalSpelling(string value, out int decodedLength)
    {
        decodedLength = (value.Length / 4 * 3) + ((value.Length % 4) switch
        {
            2 => 1,
            3 => 2,
            _ => 0,
        });

        if (value.Length == 0 || value.Length % 4 == 1)
        {
            return false;
        }

        if (value.Any(static character => Alphabet.IndexOf(character, StringComparison.Ordinal) < 0))
        {
            return false;
        }

        int last = Alphabet.IndexOf(value[^1], StringComparison.Ordinal);

        return (value.Length % 4) switch
        {
            2 => (last & 0b1111) == 0,
            3 => (last & 0b11) == 0,
            _ => true,
        };
    }
}
