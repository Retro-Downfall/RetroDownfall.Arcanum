namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The shared string table of a workbook, retained under a character and a count budget. A string is stored whole or not at
/// all; once either budget would be exceeded the table stops growing and is marked <see cref="Overflowed"/>, which makes a
/// reference past the retained prefix a truncation rather than a silent gap.
/// </summary>
internal sealed class SpreadsheetSharedStrings
{
    private readonly List<string> _strings;

    private SpreadsheetSharedStrings(List<string> strings, bool overflowed)
    {
        _strings = strings;

        Overflowed = overflowed;
    }

    /// <summary>The table of a workbook that has no shared strings part.</summary>
    public static SpreadsheetSharedStrings Empty { get; } = new([], overflowed: false);

    /// <summary>True when the part held more strings than the budgets allowed to be retained.</summary>
    public bool Overflowed { get; }

    public static SpreadsheetSharedStrings Read(ReadOnlyMemory<byte> part, SpreadsheetLimits limits, CancellationToken cancellationToken)
    {
        using BoundedXmlReader xml = BoundedXmlReader.Create(part, cancellationToken);

        SpreadsheetXml.EnterRoot(xml, "sst");

        List<string> strings = [];

        int remainingCharacters = limits.SharedStringCharacters;

        if (!SpreadsheetChildren.Enter(xml, out SpreadsheetChildren children))
        {
            return new SpreadsheetSharedStrings(strings, overflowed: false);
        }

        while (children.MoveNext())
        {
            if (!SpreadsheetXml.IsMain(xml.Reader, "si"))
            {
                xml.SkipElement();

                continue;
            }

            if (strings.Count >= limits.SharedStrings)
            {
                return new SpreadsheetSharedStrings(strings, overflowed: true);
            }

            SpreadsheetTextBuffer text = new(remainingCharacters, stopWhenFull: true);

            if (!SpreadsheetXml.ReadRichText(xml, text))
            {
                return new SpreadsheetSharedStrings(strings, overflowed: true);
            }

            string item = text.ToString();

            remainingCharacters -= item.Length;

            strings.Add(item);
        }

        return new SpreadsheetSharedStrings(strings, overflowed: false);
    }

    /// <summary>The retained string at <paramref name="index"/>, or false when the index is beyond the retained prefix.</summary>
    public bool TryGet(int index, out string text)
    {
        if (index >= 0 && index < _strings.Count)
        {
            text = _strings[index];

            return true;
        }

        text = string.Empty;

        return false;
    }
}
