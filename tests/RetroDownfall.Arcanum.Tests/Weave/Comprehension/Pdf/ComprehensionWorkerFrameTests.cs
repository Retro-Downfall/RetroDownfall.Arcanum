using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

public sealed class ComprehensionWorkerFrameTests
{
    private const int Ceiling = 262_144;

    private static PdfExtractionOutcome Extracted(bool truncated, params string[] pages) =>
        new(PdfExtractionKind.Extracted, pages, truncated);

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private static string Frame(string status, string body, long length, int pages) =>
        $"ARCANUM-COMPREHENSION/1 {status}\n{body}\nEND {length} {pages}\n";

    private static ComprehensionWorkerFrameResult Decode(string frame, int ceiling = Ceiling) =>
        ComprehensionWorkerFrame.Decode(Utf8(frame), ceiling);

    private static void AssertRefused(ComprehensionWorkerFrameDefect defect, ComprehensionWorkerFrameResult result)
    {
        Assert.Equal(defect, result.Defect);

        Assert.False(result.IsValid);

        Assert.Null(result.Outcome);
    }

    private static void AssertRoundTrips(PdfExtractionOutcome outcome, int ceiling = Ceiling)
    {
        ComprehensionWorkerFrameResult result = ComprehensionWorkerFrame.Decode(ComprehensionWorkerFrame.Encode(outcome), ceiling);

        Assert.Equal(ComprehensionWorkerFrameDefect.None, result.Defect);

        Assert.True(result.IsValid);

        PdfExtractionOutcome decoded = Assert.IsType<PdfExtractionOutcome>(result.Outcome);

        Assert.Equal(outcome.Kind, decoded.Kind);

        Assert.Equal(outcome.Truncated, decoded.Truncated);

        Assert.Equal(outcome.Pages, decoded.Pages);
    }

    // ---------------------------------------------------------------- encoding

    [Fact]
    public void Encode_ExtractedPages_WritesHeaderBodyAndTrailer()
    {
        byte[] frame = ComprehensionWorkerFrame.Encode(Extracted(false, "ab", string.Empty, "c"));

        Assert.Equal("ARCANUM-COMPREHENSION/1 extracted\nab\f\fc\nEND 5 3\n", Encoding.UTF8.GetString(frame));
    }

    [Fact]
    public void Encode_TruncatedOutcome_UsesTheTruncatedStatus()
    {
        byte[] frame = ComprehensionWorkerFrame.Encode(Extracted(true, "ab"));

        Assert.Equal("ARCANUM-COMPREHENSION/1 extracted-truncated\nab\nEND 2 1\n", Encoding.UTF8.GetString(frame));
    }

    [Theory]
    [InlineData("NoText", "no-text")]
    [InlineData("PasswordRequired", "password-required")]
    [InlineData("Malformed", "malformed")]
    [InlineData("Bounds", "bounds")]
    public void Encode_OutcomeWithoutText_WritesAnEmptyBodyAndNoPages(string kind, string status)
    {
        byte[] frame = ComprehensionWorkerFrame.Encode(PdfExtractionOutcome.Without(Enum.Parse<PdfExtractionKind>(kind)));

        Assert.Equal($"ARCANUM-COMPREHENSION/1 {status}\n\nEND 0 0\n", Encoding.UTF8.GetString(frame));
    }

    [Fact]
    public void Encode_WritesUtf8WithoutAByteOrderMark()
    {
        byte[] frame = ComprehensionWorkerFrame.Encode(Extracted(false, "\U0001F600日"));

        byte[] expected = [.. Utf8("ARCANUM-COMPREHENSION/1 extracted\n"), 0xF0, 0x9F, 0x98, 0x80, 0xE6, 0x97, 0xA5, .. Utf8("\nEND 3 1\n")];

        Assert.Equal(expected, frame);
    }

    private static PdfExtractionOutcome Unframeable(int index) => index switch
    {
        0 => Extracted(false, "a\fb"),
        1 => Extracted(false, "a\0b"),
        2 => Extracted(false, "lone \uD83D surrogate"),
        3 => Extracted(false),
        4 => Extracted(false, string.Empty),
        5 => Extracted(false, "  ", "\n"),
        6 => new PdfExtractionOutcome(PdfExtractionKind.NoText, ["text"], false),
        7 => new PdfExtractionOutcome(PdfExtractionKind.Malformed, [], true),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Encode_OutcomeThatNoFrameCouldCarry_IsRefusedBeforeItIsWritten(int index)
    {
        // 0 a form feed in a page, 1 NUL, 2 a lone surrogate, 3 no pages, 4 an empty page only, 5 whitespace only,
        // 6 pages on a textless kind, 7 a truncated flag on a textless kind. Each would decode as a protocol failure,
        // so the writer refuses them instead of emitting a frame the host rejects.
        PdfExtractionOutcome outcome = Unframeable(index);

        Assert.ThrowsAny<ArgumentException>(() => ComprehensionWorkerFrame.Encode(outcome));
    }

    // ---------------------------------------------------------------- round trips

    [Fact]
    public void RoundTrip_OrdinaryPages()
    {
        AssertRoundTrips(Extracted(false, "first page\nsecond line", "page two"));
    }

    [Fact]
    public void RoundTrip_EmptyPagesAtTheStartInTheMiddleAndAtTheEnd()
    {
        AssertRoundTrips(Extracted(false, string.Empty, "a", string.Empty, string.Empty, "b", string.Empty));
    }

    [Fact]
    public void RoundTrip_CjkAndSurrogatePairs()
    {
        AssertRoundTrips(Extracted(false, "日本語\n漢字", "\U00020BB7野家 \U0001F600 end"));
    }

    [Fact]
    public void RoundTrip_TruncatedFlagAndEveryTextlessKind()
    {
        AssertRoundTrips(Extracted(true, "cut"));

        AssertRoundTrips(PdfExtractionOutcome.Without(PdfExtractionKind.NoText));

        AssertRoundTrips(PdfExtractionOutcome.Without(PdfExtractionKind.PasswordRequired));

        AssertRoundTrips(PdfExtractionOutcome.Without(PdfExtractionKind.Malformed));

        AssertRoundTrips(PdfExtractionOutcome.Without(PdfExtractionKind.Bounds));
    }

    [Fact]
    public void RoundTrip_TextThatLooksLikeAHeaderAndATrailer()
    {
        // The trailer is found from the end, so a page that ends in a line shaped like one cannot end the frame early.
        AssertRoundTrips(Extracted(false, "ARCANUM-COMPREHENSION/1 malformed\nEND 0 0", "x\nEND 9 9\n"));
    }

    [Fact]
    public void RoundTrip_PagesThatEndInNewlinesAndBlankLines()
    {
        AssertRoundTrips(Extracted(false, "line\n", "\n\n", "last\n\n"));
    }

    [Fact]
    public void RoundTrip_BodyOfThreeByteCharactersThatFillsTheCeilingExactly()
    {
        // The worst case for the byte bound: 262,144 characters of three UTF-8 bytes each.
        string page = new('日', Ceiling);

        byte[] frame = ComprehensionWorkerFrame.Encode(Extracted(false, page));

        Assert.True(frame.Length <= ComprehensionWorkerFrame.MaximumFrameBytes(Ceiling));

        AssertRoundTrips(Extracted(false, page));
    }

    [Fact]
    public void RoundTrip_BodyOfSurrogatePairsThatFillsTheCeilingExactly()
    {
        string page = string.Concat(Enumerable.Repeat("\U0001F600", Ceiling / 2));

        Assert.Equal(Ceiling, page.Length);

        AssertRoundTrips(Extracted(true, page));
    }

    [Fact]
    public void RoundTrip_BodyThatEndsWithAPairWhoseHalvesStraddleTheSmallCeiling()
    {
        // The extractor never leaves half a pair at its ceiling; the frame then carries the whole pair.
        AssertRoundTrips(Extracted(true, "AB\U0001F600"), ceiling: 4);
    }

    [Fact]
    public void RoundTrip_FullyFilledSeparatorsCountTowardTheCeiling()
    {
        // 3 separators + 2 characters = 5.
        AssertRoundTrips(Extracted(false, "a", string.Empty, string.Empty, "b"), ceiling: 5);
    }

    [Theory]
    [InlineData("astral-plane.pdf")]
    [InlineData("cjk-tounicode.pdf")]
    [InlineData("multi-page.pdf")]
    [InlineData("hidden.pdf")]
    [InlineData("image-only.pdf")]
    [InlineData("encrypted-aes-password.pdf")]
    [InlineData("truncated.pdf")]
    public void RoundTrip_WhatTheExtractorProduces(string fileName)
    {
        PdfExtractionOutcome outcome = new PdfTextExtractor().Extract(PdfFixtures.Read(fileName), PdfExtractionLimits.Default, CancellationToken.None);

        AssertRoundTrips(outcome);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void RoundTrip_WhatTheExtractorProducesWhenItsCeilingCutsASurrogatePair(int ceiling)
    {
        PdfExtractionOutcome outcome = new PdfTextExtractor().Extract(
            PdfFixtures.Read("astral-plane.pdf"),
            new PdfExtractionLimits(2000, 250_000, 2_000_000, ceiling),
            CancellationToken.None);

        Assert.True(outcome.Truncated);

        AssertRoundTrips(outcome, ceiling);
    }

    // ---------------------------------------------------------------- the header

    [Fact]
    public void Decode_EmptyInput_HasNoHeader()
    {
        AssertRefused(ComprehensionWorkerFrameDefect.Header, ComprehensionWorkerFrame.Decode([], Ceiling));
    }

    [Theory]
    [InlineData("ARCANUM-COMPREHENSION/2 extracted\nab\nEND 2 1\n")]
    [InlineData("ARCANUM-COMPREHENSION/1  extracted\nab\nEND 2 1\n")]
    [InlineData("ARCANUM-COMPREHENSION/1 Extracted\nab\nEND 2 1\n")]
    [InlineData("ARCANUM-COMPREHENSION/1 extracted \nab\nEND 2 1\n")]
    [InlineData("ARCANUM-COMPREHENSION/1 truncated\nab\nEND 2 1\n")]
    [InlineData("ARCANUM-COMPREHENSION/1\nab\nEND 2 1\n")]
    [InlineData("arcanum-comprehension/1 extracted\nab\nEND 2 1\n")]
    [InlineData("\nARCANUM-COMPREHENSION/1 extracted\nab\nEND 2 1\n")]
    [InlineData("﻿ARCANUM-COMPREHENSION/1 extracted\nab\nEND 2 1\n")]
    [InlineData("ARCANUM-COMPREHENSION/1 extracted\r\nab\nEND 2 1\n")]
    public void Decode_UnknownOrMisspelledHeader_IsRefused(string frame)
    {
        AssertRefused(ComprehensionWorkerFrameDefect.Header, Decode(frame));
    }

    [Fact]
    public void Decode_HeaderWithoutALineEnd_IsRefusedAsAHeader()
    {
        AssertRefused(ComprehensionWorkerFrameDefect.Header, Decode("ARCANUM-COMPREHENSION/1 extracted"));
    }

    [Theory]
    [InlineData("extracted", false)]
    [InlineData("extracted-truncated", true)]
    public void Decode_ExtractedStatuses_CarryTheTruncatedFlag(string status, bool truncated)
    {
        ComprehensionWorkerFrameResult result = Decode(Frame(status, "ab", 2, 1));

        Assert.True(result.IsValid);

        Assert.Equal(PdfExtractionKind.Extracted, result.Outcome!.Kind);

        Assert.Equal(truncated, result.Outcome.Truncated);
    }

    // ---------------------------------------------------------------- the trailer

    [Fact]
    public void Decode_FrameWithoutATrailer_IsRefused()
    {
        AssertRefused(ComprehensionWorkerFrameDefect.Trailer, Decode("ARCANUM-COMPREHENSION/1 extracted\nab\n"));
    }

    [Fact]
    public void Decode_FrameCutBeforeTheFinalLineEnd_IsRefused()
    {
        AssertRefused(ComprehensionWorkerFrameDefect.Trailer, Decode("ARCANUM-COMPREHENSION/1 extracted\nab\nEND 2 1"));
    }

    [Fact]
    public void Decode_FrameWhoseLastCharacterIsNotALineEnd_IsRefusedEvenIfDroppingItWouldLeaveAValidTrailer()
    {
        // "END 2 12" read without its last character would be "END 2 1", which matches the body.
        AssertRefused(ComprehensionWorkerFrameDefect.Trailer, Decode("ARCANUM-COMPREHENSION/1 extracted\nab\nEND 2 12"));
    }

    [Fact]
    public void Decode_TrailerWithoutTheBodyTerminatingLineEnd_IsRefused()
    {
        // "header\nEND 0 0\n": the body's own newline is missing, so the trailer shares the header's line end.
        AssertRefused(ComprehensionWorkerFrameDefect.Trailer, Decode("ARCANUM-COMPREHENSION/1 no-text\nEND 0 0\n"));
    }

    [Theory]
    [InlineData("END 2")]
    [InlineData("END 2 1 0")]
    [InlineData("END  2 1")]
    [InlineData("END 2  1")]
    [InlineData("END 2 1 ")]
    [InlineData("END -2 1")]
    [InlineData("END 2 -1")]
    [InlineData("END +2 1")]
    [InlineData("END 02 1")]
    [InlineData("END 2 01")]
    [InlineData("END two 1")]
    [InlineData("END 2 one")]
    [InlineData("end 2 1")]
    [InlineData("STOP 2 1")]
    [InlineData("END 99999999999 1")]
    [InlineData("END 2 99999999999")]
    [InlineData("END٢ 1")]
    [InlineData("")]
    public void Decode_TrailerThatIsNotExactlyEndLengthPages_IsRefused(string trailer)
    {
        AssertRefused(ComprehensionWorkerFrameDefect.Trailer, Decode($"ARCANUM-COMPREHENSION/1 extracted\nab\n{trailer}\n"));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("\n")]
    [InlineData("\nEND 2 1")]
    [InlineData(" ")]
    public void Decode_BytesAfterTheTrailer_AreRefused(string extra)
    {
        // The trailer must be the last line, so anything after it leaves a different last line.
        AssertRefused(ComprehensionWorkerFrameDefect.Trailer, Decode(Frame("extracted", "ab", 2, 1) + extra));
    }

    [Fact]
    public void Decode_TwoFramesBackToBack_AreRefused()
    {
        string frame = Frame("extracted", "ab", 2, 1);

        Assert.False(Decode(frame + frame).IsValid);
    }

    // ---------------------------------------------------------------- declared counts

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(0)]
    public void Decode_LengthThatDiffersFromTheBody_IsRefused(long declared)
    {
        AssertRefused(ComprehensionWorkerFrameDefect.Length, Decode(Frame("extracted", "ab", declared, 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Decode_PageCountThatDiffersFromTheBody_IsRefused(int declared)
    {
        // "a\fb" is two pages.
        AssertRefused(ComprehensionWorkerFrameDefect.PageCount, Decode(Frame("extracted", "a\fb", 3, declared)));
    }

    [Fact]
    public void Decode_PageCountThatMatchesTheSeparators_IsAccepted()
    {
        Assert.True(Decode(Frame("extracted", "a\fb", 3, 2)).IsValid);
    }

    [Fact]
    public void Decode_PageCountCountsSeparatorsAndNotLines()
    {
        // Two lines, one page.
        Assert.True(Decode(Frame("extracted", "a\nb", 3, 1)).IsValid);
    }

    [Fact]
    public void Decode_AnOutcomeWithoutTextButWithAPageCount_IsRefused()
    {
        AssertRefused(ComprehensionWorkerFrameDefect.PageCount, Decode(Frame("malformed", string.Empty, 0, 1)));
    }

    // ---------------------------------------------------------------- the bytes

    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xE6, 0x97 })]
    [InlineData(new byte[] { 0xC0, 0x80 })]
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 })]
    public void Decode_BodyThatIsNotValidUtf8_IsRefused(byte[] badBytes)
    {
        byte[] frame = [.. Utf8("ARCANUM-COMPREHENSION/1 extracted\nab"), .. badBytes, .. Utf8("\nEND 3 1\n")];

        AssertRefused(ComprehensionWorkerFrameDefect.InvalidUtf8, ComprehensionWorkerFrame.Decode(frame, Ceiling));
    }

    [Fact]
    public void Decode_FrameLongerThanAnyFrameWithinTheCeilingCouldBe_IsRefusedBeforeItIsDecoded()
    {
        // An ASCII body of Ceiling characters would be well-formed, but these bytes would not even fit the worst case.
        byte[] oversize = new byte[checked((int)ComprehensionWorkerFrame.MaximumFrameBytes(Ceiling)) + 1];

        AssertRefused(ComprehensionWorkerFrameDefect.Oversize, ComprehensionWorkerFrame.Decode(oversize, Ceiling));
    }

    [Fact]
    public void MaximumFrameBytes_CoversThreeBytesPerCharacterPlusTheHeaderAndTrailer()
    {
        long bound = ComprehensionWorkerFrame.MaximumFrameBytes(Ceiling);

        Assert.True(bound >= 3L * Ceiling + Utf8("ARCANUM-COMPREHENSION/1 extracted-truncated\n").Length + Utf8("\nEND 262144 262145\n").Length);

        Assert.True(bound < 3L * Ceiling + 1024);
    }

    // ---------------------------------------------------------------- the ceiling

    [Fact]
    public void Decode_BodyOneCharacterOverTheCeiling_IsRefused()
    {
        AssertRefused(ComprehensionWorkerFrameDefect.BodyCeiling, Decode(Frame("extracted", "abcd", 4, 1), ceiling: 3));
    }

    [Fact]
    public void Decode_BodyExactlyTheCeiling_IsAccepted()
    {
        Assert.True(Decode(Frame("extracted", "abc", 3, 1), ceiling: 3).IsValid);
    }

    [Fact]
    public void Decode_SeparatorsCountTowardTheCeiling()
    {
        // "a\fb" is three characters.
        AssertRefused(ComprehensionWorkerFrameDefect.BodyCeiling, Decode(Frame("extracted", "a\fb", 3, 2), ceiling: 2));
    }

    [Fact]
    public void Decode_CeilingCountsUtf16UnitsNotBytesOrCodePoints()
    {
        // Two code points, four UTF-16 units, eight UTF-8 bytes.
        string body = "\U0001F600\U0001F600";

        Assert.True(Decode(Frame("extracted", body, 4, 1), ceiling: 4).IsValid);

        AssertRefused(ComprehensionWorkerFrameDefect.BodyCeiling, Decode(Frame("extracted", body, 4, 1), ceiling: 3));
    }

    // ---------------------------------------------------------------- what the body may hold

    [Fact]
    public void Decode_NulInTheBody_IsRefused()
    {
        AssertRefused(ComprehensionWorkerFrameDefect.Payload, Decode(Frame("extracted", "a\0b", 3, 1)));
    }

    [Theory]
    [InlineData("no-text")]
    [InlineData("password-required")]
    [InlineData("malformed")]
    [InlineData("bounds")]
    public void Decode_TextlessStatusWithABody_IsRefused(string status)
    {
        AssertRefused(ComprehensionWorkerFrameDefect.Payload, Decode(Frame(status, "text", 4, 0)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\f\n")]
    public void Decode_ExtractedStatusWithoutAnyVisibleCharacter_IsRefused(string body)
    {
        int pages = body.Count(character => character == '\f') + 1;

        AssertRefused(ComprehensionWorkerFrameDefect.Payload, Decode(Frame("extracted", body, body.Length, pages)));
    }

    // ---------------------------------------------------------------- damage anywhere

    [Fact]
    public void Decode_DamagedFrames_NeverThrowAndAreAcceptedOnlyInTheirOneCanonicalForm()
    {
        // The decoder reads bytes a possibly confused child wrote. Whatever it is given it must answer, and the only frames
        // it may accept are exactly those the encoder writes, so no second spelling of a frame can slip through.
        PdfExtractionOutcome[] outcomes =
        [
            Extracted(false, "ab", string.Empty, "c\nd"),
            Extracted(true, "日本語\U0001F600", "END 1 1"),
            PdfExtractionOutcome.Without(PdfExtractionKind.NoText),
            PdfExtractionOutcome.Without(PdfExtractionKind.Bounds),
        ];

        Random random = new(163);

        int accepted = 0;

        for (int iteration = 0; iteration < 6000; iteration++)
        {
            List<byte> frame = [.. ComprehensionWorkerFrame.Encode(outcomes[random.Next(outcomes.Length)])];

            for (int edit = random.Next(1, 4); edit > 0 && frame.Count > 0; edit--)
            {
                int at = random.Next(frame.Count);

                switch (random.Next(4))
                {
                    case 0:
                        frame[at] = (byte)random.Next(256);

                        break;
                    case 1:
                        frame.RemoveAt(at);

                        break;
                    case 2:
                        frame.Insert(at, (byte)random.Next(256));

                        break;
                    default:
                        frame[at] = (byte)"0123456789 \n\fEND"[random.Next(16)];

                        break;
                }
            }

            byte[] damaged = [.. frame];

            ComprehensionWorkerFrameResult result = ComprehensionWorkerFrame.Decode(damaged, Ceiling);

            if (result.IsValid)
            {
                accepted++;

                Assert.Equal(damaged, ComprehensionWorkerFrame.Encode(result.Outcome!));
            }
            else
            {
                Assert.Null(result.Outcome);
            }
        }

        // Most edits land in text that stays well-formed, so a fair number of frames are still accepted, and the property above held for each.
        Assert.InRange(accepted, 1, 5999);
    }

    // ---------------------------------------------------------------- truncation anywhere

    [Fact]
    public void Decode_EveryPrefixOfAValidFrame_IsRefused()
    {
        byte[] frame = ComprehensionWorkerFrame.Encode(Extracted(false, "ab", string.Empty, "日\U0001F600"));

        Assert.True(ComprehensionWorkerFrame.Decode(frame, Ceiling).IsValid);

        for (int length = 0; length < frame.Length; length++)
        {
            Assert.False(ComprehensionWorkerFrame.Decode(frame.AsSpan(0, length), Ceiling).IsValid, $"prefix of {length} bytes decoded");
        }
    }
}
