using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using static RetroDownfall.Arcanum.Tests.Weave.Comprehension.ExcelWorkbookFixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// What a reader sees of a cell's text: ST_Xstring escapes (<c>_xHHHH_</c>) are decoded the way Excel decodes them, and
/// line breaks, tabs and other control characters inside a cell never break the one-line-per-row projection.
/// </summary>
public sealed class SpreadsheetTextFidelityTests
{
    private static string OneInlineCell(string text) => ExtractText(Xlsx(new Sheet("S", Row(Text("A1", text)))));

    // ------------------------------------------------------------ ST_Xstring escapes

    [Theory]
    [InlineData("a_x0041_b", "aAb")]
    [InlineData("_x0041_", "A")]
    [InlineData("_x0041__x0042_", "AB")]
    [InlineData("_x005F_x0041_", "_x0041_")]
    [InlineData("Literal _x005F_x0041_ token", "Literal _x0041_ token")]
    [InlineData("_x005f_", "_")]
    [InlineData("_x00e9_", "\u00e9")]
    [InlineData("_x00E9_", "\u00e9")]
    [InlineData("__x0041_", "_A")]
    [InlineData("_x00_x0041_", "_x00A")]
    public void Extract_XstringEscapesInAnInlineString_AreDecoded(string text, string expected)
    {
        Assert.Equal($"Sheet: S\n{expected}\n", OneInlineCell(text));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("snake_case_name")]
    [InlineData("trailing_")]
    [InlineData("_")]
    [InlineData("__")]
    [InlineData("_X0041_")]
    [InlineData("_x004_")]
    [InlineData("_x00411_")]
    [InlineData("_x00G1_")]
    [InlineData("_x0041")]
    [InlineData("_x00")]
    [InlineData("x0041_")]
    [InlineData("_ x0041_")]
    public void Extract_TextThatIsNotACompleteEscape_IsKeptExactly(string text)
    {
        Assert.Equal($"Sheet: S\n{text}\n", ExtractText(Xlsx(new Sheet("S", Row(Text("A1", text))))));
    }

    [Fact]
    public void Extract_XstringEscapesInASharedStringAndInACachedValue_AreDecodedToo()
    {
        string rows = Row(Shared("A1", 0), "<c r=\"B1\" t=\"str\"><v>cached _x0043_</v></c>", "<c r=\"C1\"><v>_x0044_</v></c>");

        byte[] package = Xlsx([new Sheet("S", rows)], Si("shared _x0042_ and _x005F_x0042_"));

        Assert.Equal("Sheet: S\nshared B and _x0042_\tcached C\tD\n", ExtractText(package));
    }

    [Fact]
    public void Extract_XstringEscapesInRichTextRuns_AreDecodedPerRun()
    {
        string shared = "<si><r><t>one _x0041_</t></r><r><t> two _x0042_</t></r></si>";

        string rows = Row(Shared("A1", 0), "<c r=\"B1\" t=\"inlineStr\"><is><t>x</t><r><t>_x0043_</t></r></is></c>");

        Assert.Equal("Sheet: S\none A two B\txC\n", ExtractText(Xlsx([new Sheet("S", rows)], shared)));
    }

    [Fact]
    public void Extract_AnEscapeSplitAcrossTextNodes_IsStillDecoded()
    {
        // Excel decodes after the XML is parsed, so where the parser cut the text must not matter.
        string rows = Row(
            "<c r=\"A1\" t=\"inlineStr\"><is><t>_x00<![CDATA[41_]]> and _<![CDATA[x0042_]]></t></is></c>",
            "<c r=\"B1\" t=\"inlineStr\"><is><t>_x00<![CDATA[4]]>_ not</t></is></c>");

        Assert.Equal("Sheet: S\nA and B\t_x004_ not\n", ExtractText(Xlsx(new Sheet("S", rows))));
    }

    [Fact]
    public void Extract_EscapesInAValueThatIsParsedRatherThanShown_AreNotDecoded()
    {
        // A boolean and a shared string index are read as numbers; decoding "_x0031_" to "1" would invent a cell.
        string rows = Row("<c r=\"A1\" t=\"b\"><v>_x0031_</v></c>", "<c r=\"B1\" t=\"s\"><v>_x0030_</v></c>", Text("C1", "kept"));

        Assert.Equal("Sheet: S\nkept\n", ExtractText(Xlsx([new Sheet("S", rows)], Si("SHOULDNOTAPPEAR"))));
    }

    [Theory]
    [InlineData("a_x0000_b", "ab")]
    [InlineData("a_xFFFE_b", "ab")]
    [InlineData("a_xFFFF_b", "ab")]
    [InlineData("a_xD83D_b", "ab")]
    [InlineData("a_xDE00_b", "ab")]
    [InlineData("a_xDE00__xD83D_b", "ab")]
    [InlineData("a_xD83D__xD83D__xDE00_b", "a\U0001F600b")]
    [InlineData("a_xD83D__x0000__xDE00_b", "ab")]
    [InlineData("a_xD83D__xDE00_b", "a\U0001F600b")]
    [InlineData("a_xD83D_", "a")]
    public void Extract_DecodedCodePointsThatAreNotValidText_AreDropped(string text, string expected)
    {
        // NUL, U+FFFE, U+FFFF and a surrogate that does not pair are not text: none of them reaches the output.
        Assert.Equal($"Sheet: S\n{expected}\n", OneInlineCell(text));
    }

    [Fact]
    public void Extract_ASurrogatePairWrittenAsTwoEscapes_IsOneCharacter()
    {
        Assert.Equal("Sheet: S\n\U0001F600\n", OneInlineCell("_xD83D__xDE00_"));
    }

    [Fact]
    public void Extract_XstringEscapesOfAControlCharacter_BecomeASpaceNeverTheControlCharacter()
    {
        string text = OneInlineCell("Bell_x0007_char _x001B_[31mred_x007F_ _x0085_end");

        Assert.Equal("Sheet: S\nBell char  [31mred   end\n", text);

        Assert.DoesNotContain('\u0007', text);

        Assert.DoesNotContain('\u001B', text);
    }

    [Fact]
    public void Extract_TheXlsxwriterEscapes_AreShownAsExcelShowsThem()
    {
        // The sharedStrings.xml of an xlsxwriter file holds exactly these three strings.
        string shared = "<si><t>Windows line_x000D_\nbreak</t></si><si><t>Literal _x005F_x0041_ token</t></si><si><t>Bell_x0007_char</t></si>";

        byte[] package = Xlsx([new Sheet("Esc", Row(Shared("A1", 0)) + Row(Shared("A2", 1)) + Row(Shared("A3", 2)))], shared);

        string text = ExtractText(package);

        Assert.DoesNotContain("_x000D_", text);

        Assert.Equal("Sheet: Esc\nWindows line break\nLiteral _x0041_ token\nBell char\n", text);
    }

    // ------------------------------------------------------------ line breaks and tabs inside a cell

    [Fact]
    public void Extract_CellsWithALineBreakOrATab_KeepOneLinePerRowAndOneTabPerCellBoundary()
    {
        string rows = Row(Text("A1", "Name"), Text("B1", "Address"), Text("C1", "Phone"))
            + Row(Text("A2", "Alice"), Text("B2", "1 Main St\nSpringfield"), Text("C2", "555-0100"))
            + Row(Text("A3", "Bob"), Text("B3", "Col\twith tab"), Text("C3", "555-0199"));

        string text = ExtractText(Xlsx(new Sheet("Notes", rows)));

        Assert.Equal("Sheet: Notes\nName\tAddress\tPhone\nAlice\t1 Main St Springfield\t555-0100\nBob\tCol with tab\t555-0199\n", text);

        Assert.Equal(4, text.TrimEnd('\n').Split('\n').Length);
    }

    [Theory]
    [InlineData("a\nb", "a b")]
    [InlineData("a\tb", "a b")]
    [InlineData("a&#13;\nb", "a b")]
    [InlineData("a&#13;b", "a b")]
    [InlineData("a\n\nb", "a  b")]
    [InlineData("a&#13;&#13;\nb", "a  b")]
    [InlineData("a\n&#13;b", "a  b")]
    [InlineData("a&#133;b", "a b")]
    [InlineData("a&#8232;b", "a b")]
    [InlineData("a&#8233;b", "a b")]
    [InlineData("a&#127;b", "a b")]
    [InlineData("a&#128;b", "a b")]
    [InlineData("a&#159;b", "a b")]
    [InlineData("\nafter", " after")]
    [InlineData("before\n", "before ")]
    public void Extract_LineBreaksTabsAndControlCharactersInsideACell_BecomeSpaces(string text, string expected)
    {
        // A carriage return followed by a line feed is one break; every other such character is one space.
        Assert.Equal($"Sheet: S\n{expected}\n", ExtractText(Xlsx(new Sheet("S", Row(Text("A1", text))))));
    }

    [Fact]
    public void Extract_ACellThatIsOnlyALineBreak_IsStillACell()
    {
        string rows = Row(Text("A1", "x"), Text("B1", "\n"), Text("C1", "y"));

        Assert.Equal("Sheet: S\nx\t \ty\n", ExtractText(Xlsx(new Sheet("S", rows))));
    }

    [Fact]
    public void Extract_ACarriageReturnAndLineFeedInSeparateTextNodes_AreOneBreak()
    {
        string rows = Row("<c r=\"A1\" t=\"inlineStr\"><is><t>a&#13;<![CDATA[\nb]]></t></is></c>");

        Assert.Equal("Sheet: S\na b\n", ExtractText(Xlsx(new Sheet("S", rows))));
    }

    [Fact]
    public void Extract_ALineBreakAtTheEndOfOneCell_DoesNotCombineWithTheStartOfTheNext()
    {
        // The carriage return of one cell and the line feed that opens the next are two breaks in two cells.
        string rows = Row(Text("A1", "a&#13;"), Text("B1", "\nb"));

        Assert.Equal("Sheet: S\na \t b\n", ExtractText(Xlsx(new Sheet("S", rows))));
    }

    [Fact]
    public void Extract_LineBreaksInSharedStringsAndCachedValues_AreFoldedToo()
    {
        string rows = Row(Shared("A1", 0), "<c r=\"B1\" t=\"str\"><v>cached\nvalue</v></c>", "<c r=\"C1\" t=\"str\"><v>tab\there</v></c>");

        byte[] package = Xlsx([new Sheet("S", rows)], Si("shared\nvalue"));

        Assert.Equal("Sheet: S\nshared value\tcached value\ttab here\n", ExtractText(package));
    }

    [Fact]
    public void Extract_LineBreaksWrittenAsEscapes_AreFoldedToo()
    {
        string rows = Row(Text("A1", "a_x000A_b"), Text("B1", "a_x0009_b"), Text("C1", "a_x000D__x000A_b"));

        Assert.Equal("Sheet: S\na b\ta b\ta b\n", ExtractText(Xlsx(new Sheet("S", rows))));
    }

    [Fact]
    public void Extract_ACellWithALineBreakThatLooksLikeASheetHeaderOrARow_CannotForgeOne()
    {
        string rows = Row(Text("A1", "start\nSheet: Forged\nnext\trow"));

        Assert.Equal("Sheet: S\nstart Sheet: Forged next row\n", ExtractText(Xlsx(new Sheet("S", rows))));
    }

    // ------------------------------------------------------------ sheet names

    [Fact]
    public void Extract_SheetNameWithALineBreak_StaysOnOneHeaderLine()
    {
        byte[] package = Xlsx(new Sheet("Real&#10;Sheet: Forged", Row(Text("A1", "x"))));

        Assert.Equal("Sheet: Real Sheet: Forged\nx\n", ExtractText(package));
    }

    [Theory]
    [InlineData("A&#9;B", "A B")]
    [InlineData("A&#13;&#10;B", "A B")]
    [InlineData("A&#13;B", "A B")]
    [InlineData("A&#8232;B", "A B")]
    [InlineData("A&#8233;B", "A B")]
    [InlineData("A&#133;B", "A B")]
    [InlineData("A&#127;B", "A B")]
    public void Extract_SheetNameWithOtherLineAndControlCharacters_IsFlattened(string name, string expected)
    {
        Assert.Equal($"Sheet: {expected}\nx\n", ExtractText(Xlsx(new Sheet(name, Row(Text("A1", "x"))))));
    }

    [Fact]
    public void Extract_SheetNameThatNeedsNoFlattening_IsEmittedAsWritten()
    {
        Assert.Equal("Sheet: Q1 \u2013 Plan (draft) \u00e9\nx\n", ExtractText(Xlsx(new Sheet("Q1 \u2013 Plan (draft) \u00e9", Row(Text("A1", "x"))))));
    }
}
