using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Decodes the ECMA-376 <c>ST_Xstring</c> escape the way Excel does: <c>_xHHHH_</c> (four hexadecimal digits, either case)
/// stands for the UTF-16 code unit <c>HHHH</c>, and <c>_x005F_</c> is how a literal underscore that would otherwise start
/// an escape is written, so <c>_x005F_x0041_</c> shows as <c>_x0041_</c>. Text is decoded once, left to right, and
/// anything that is not a complete escape is kept exactly as written.
/// </summary>
/// <remarks>
/// <para>
/// A decoded code unit that is not valid text is dropped: NUL, U+FFFE, U+FFFF, and a surrogate that does not complete a
/// pair with the one decoded just before it. Two escapes that spell a surrogate pair decode to the one character they name.
/// </para>
/// <para>
/// Text arrives in the chunks the XML reader produced (the pieces around CDATA sections), and an escape can straddle two
/// of them, so up to six characters of a possible escape are held back until the next chunk or <see cref="Finish"/>. The
/// decoded text goes to the target in pieces of a few thousand characters, so a long text is never copied whole.
/// </para>
/// </remarks>
internal sealed class SpreadsheetXstringDecoder
{
    private const int EscapeLength = 7;

    private const int FlushThreshold = 4096;

    private readonly char[] _escape = new char[EscapeLength];

    private readonly StringBuilder _output = new();

    private int _escapeLength;

    private char _heldHighSurrogate;

    /// <summary>Decodes <paramref name="text"/> into <paramref name="target"/>. False means the target is full: stop reading.</summary>
    public bool Write(string text, ISpreadsheetTextTarget target)
    {
        int index = 0;

        while (index < text.Length)
        {
            if (_escapeLength == 0)
            {
                int underscore = text.IndexOf('_', index);

                int plainEnd = underscore < 0 ? text.Length : underscore;

                while (index < plainEnd)
                {
                    int count = Math.Min(plainEnd - index, FlushThreshold);

                    _heldHighSurrogate = '\0';

                    _output.Append(text, index, count);

                    index += count;

                    if (_output.Length >= FlushThreshold && !Drain(target))
                    {
                        return false;
                    }
                }

                if (underscore < 0)
                {
                    break;
                }

                _escape[0] = '_';

                _escapeLength = 1;

                index++;
            }
            else
            {
                Feed(text[index++]);
            }

            if (_output.Length >= FlushThreshold && !Drain(target))
            {
                return false;
            }
        }

        return Drain(target);
    }

    /// <summary>Ends the text element: what is held back (an escape that never completed) is plain text after all.</summary>
    public bool Finish(ISpreadsheetTextTarget target)
    {
        AbandonEscape();

        _heldHighSurrogate = '\0';

        return Drain(target);
    }

    /// <summary>Takes the next character of a possible escape, <c>_x</c> and four digits and <c>_</c>, already begun.</summary>
    private void Feed(char current)
    {
        if (Continues(current))
        {
            _escape[_escapeLength++] = current;

            if (_escapeLength == EscapeLength)
            {
                Complete();
            }

            return;
        }

        // Not an escape: what was held is plain text. This character may itself begin one.
        AbandonEscape();

        if (current == '_')
        {
            _escape[0] = '_';

            _escapeLength = 1;

            return;
        }

        _heldHighSurrogate = '\0';

        _output.Append(current);
    }

    private bool Continues(char current) => _escapeLength switch
    {
        1 => current == 'x',
        EscapeLength - 1 => current == '_',
        _ => char.IsAsciiHexDigit(current),
    };

    private void Complete()
    {
        _escapeLength = 0;

        char unit = (char)((HexValue(_escape[2]) << 12) | (HexValue(_escape[3]) << 8) | (HexValue(_escape[4]) << 4) | HexValue(_escape[5]));

        if (char.IsHighSurrogate(unit))
        {
            // Held until the next decoded unit says whether it is the first half of a pair; a high surrogate before it is lone.
            _heldHighSurrogate = unit;

            return;
        }

        if (char.IsLowSurrogate(unit))
        {
            if (_heldHighSurrogate != '\0')
            {
                _output.Append(_heldHighSurrogate).Append(unit);
            }

            _heldHighSurrogate = '\0';

            return;
        }

        _heldHighSurrogate = '\0';

        if (unit is not ('\0' or '\ufffe' or '\uffff'))
        {
            _output.Append(unit);
        }
    }

    private void AbandonEscape()
    {
        if (_escapeLength == 0)
        {
            return;
        }

        _heldHighSurrogate = '\0';

        _output.Append(_escape, 0, _escapeLength);

        _escapeLength = 0;
    }

    private bool Drain(ISpreadsheetTextTarget target)
    {
        if (_output.Length == 0)
        {
            return true;
        }

        string piece = _output.ToString();

        _output.Clear();

        return target.Append(piece);
    }

    private static int HexValue(char digit) => digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10;
}
