using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>Two-pass iterative RTF grammar with bounded group state and output buffering.</summary>
internal ref struct RtfTextScanner(ReadOnlySpan<byte> bytes, ISessionAttachmentDocumentTextSink? output, CancellationToken cancellationToken)
{
    private readonly ReadOnlySpan<byte> _bytes = bytes;

    private readonly ISessionAttachmentDocumentTextSink? _output = output;

    private readonly CancellationToken _cancellationToken = cancellationToken;

    private readonly RtfGroupState[] _groups = new RtfGroupState[RtfDocumentExtractor.MaximumGroupDepth];

    private readonly char[] _buffer = new char[2048];

    private readonly Dictionary<int, int> _fonts = [];

    private int _ansiCodePage = 1252;

    private int _defaultFont;

    private int _bufferLength;

    private int _position;

    private int _depth;

    private int _controls;

    private int _fallback;

    private char _highSurrogate;

    private RtfGroupState _state = new() { CodePage = 1252, UnicodeFallback = 1, FontIndex = -1 };

    public void Run()
    {
        while (_position < _bytes.Length)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            byte current = _bytes[_position++];

            if (_depth == 0 && _position > 1)
            {
                if (current is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                {
                    Reject();
                }

                continue;
            }

            switch (current)
            {
                case (byte)'{':
                    _fallback = 0;

                    if (_depth == _groups.Length)
                    {
                        Reject();
                    }

                    if (_state.UnicodeDestinationPrefix != 0)
                    {
                        Reject();
                    }

                    int alternative = 0;

                    if (_state.UnicodeAlternatives)
                    {
                        if (_state.AlternativeChildren == 2)
                        {
                            Reject();
                        }

                        alternative = ++_state.AlternativeChildren;
                    }

                    _state.GroupStart = false;

                    _groups[_depth++] = _state;

                    _state.GroupStart = true;

                    _state.UnicodeAlternatives = false;

                    _state.AlternativeChildren = 0;

                    _state.UnicodeDestinationPrefix = alternative == 2 ? 1 : 0;

                    if (alternative == 1)
                    {
                        // The backward-compatible ANSI representation is an opaque destination.
                        _state.Skip = true;

                        _state.FontTable = false;
                    }

                    break;

                case (byte)'}':
                    _fallback = 0;

                    if (_depth == 0 || _state.UnicodeDestinationPrefix != 0 ||
                        (_state.UnicodeAlternatives && _state.AlternativeChildren != 2))
                    {
                        Reject();
                    }

                    _state = _groups[--_depth];

                    break;

                case (byte)'\\':
                    if (!Control())
                    {
                        return;
                    }

                    break;

                case (byte)'\r':
                case (byte)'\n':
                    break;

                default:
                    if (_depth == 0 || current == 0 || (current < 32 && current != '\t'))
                    {
                        Reject();
                    }

                    if (_state.UnicodeAlternatives || _state.UnicodeDestinationPrefix != 0)
                    {
                        Reject();
                    }

                    _state.GroupStart = false;

                    if (_fallback > 0)
                    {
                        _fallback--;
                    }
                    else if (!_state.Skip && !Emit(SingleByteTextDecoder.Decode(current, CurrentCodePage())))
                    {
                        return;
                    }

                    break;
            }
        }

        if (_depth != 0 || _highSurrogate != 0)
        {
            Reject();
        }

        Flush();
    }

    private bool Control()
    {
        if (++_controls > RtfDocumentExtractor.MaximumControls || _position == _bytes.Length)
        {
            Reject();
        }

        byte symbol = _bytes[_position++];

        if (_state.UnicodeAlternatives || (_state.UnicodeDestinationPrefix == 1 && symbol != '*'))
        {
            Reject();
        }

        if (_state.UnicodeDestinationPrefix == 1)
        {
            // Only the selected second child of upr may recognize the starred ud destination.
            _state.UnicodeDestinationPrefix = 2;

            return true;
        }

        if (_state.UnicodeDestinationPrefix == 2 && !IsLetter(symbol))
        {
            Reject();
        }

        bool groupStart = _state.GroupStart;

        _state.GroupStart = false;

        if (!IsLetter(symbol))
        {
            char character = '\0';

            switch (symbol)
            {
                case (byte)'\'':
                    if (_bytes.Length - _position < 2)
                    {
                        Reject();
                    }

                    int high = Hex(_bytes[_position++]);

                    int low = Hex(_bytes[_position++]);

                    if (!_state.Skip)
                    {
                        character = SingleByteTextDecoder.Decode((byte)((high << 4) | low), CurrentCodePage());

                        if (character == 0)
                        {
                            Reject();
                        }
                    }

                    break;

                case (byte)'*':
                    break;

                case (byte)'\\':
                case (byte)'{':
                case (byte)'}':
                    character = (char)symbol;

                    break;

                case (byte)'~':
                    character = '\u00a0';

                    break;

                case (byte)'_':
                    character = '-';

                    break;

                case (byte)'\r':
                    if (_position < _bytes.Length && _bytes[_position] == '\n')
                    {
                        _position++;
                    }

                    character = '\n';

                    break;

                case (byte)'\n':
                    character = '\n';

                    break;
            }

            if (_fallback > 0)
            {
                _fallback--;

                return true;
            }

            if (symbol == '*')
            {
                _state.Skip = true;

                _state.FontTable = false;
            }

            return character == 0 || Emit(character);
        }

        int start = _position - 1;

        while (_position < _bytes.Length && IsLetter(_bytes[_position]))
        {
            if (_position - start == 32)
            {
                Reject();
            }

            _position++;
        }

        if (_position - start > 32)
        {
            Reject();
        }

        ReadOnlySpan<byte> word = _bytes[start.._position];

        bool negative = _position < _bytes.Length && _bytes[_position] == '-';

        if (negative)
        {
            _position++;
        }

        int numberStart = _position;

        long number = 0;

        while (_position < _bytes.Length && _bytes[_position] is >= (byte)'0' and <= (byte)'9')
        {
            if (_position - numberStart == 10)
            {
                Reject();
            }

            number = (number * 10) + (_bytes[_position++] - '0');

            if (number > (negative ? 2_147_483_648L : int.MaxValue))
            {
                Reject();
            }
        }

        bool hasNumber = _position > numberStart;

        if (negative && !hasNumber)
        {
            Reject();
        }

        int argument = (int)(negative ? -number : number);

        if (_position < _bytes.Length && _bytes[_position] == ' ')
        {
            _position++;
        }

        if (_state.UnicodeDestinationPrefix == 2)
        {
            if (!word.SequenceEqual("ud"u8) || hasNumber)
            {
                Reject();
            }

            _state.UnicodeDestinationPrefix = 0;

            return true;
        }

        if (word.SequenceEqual("bin"u8))
        {
            if (!hasNumber || argument < 0 || argument > _bytes.Length - _position)
            {
                Reject();
            }

            _position += argument;

            if (_fallback > 0)
            {
                _fallback--;
            }

            return true;
        }

        if (_fallback > 0)
        {
            _fallback--;

            return true;
        }

        // Opaque destinations have syntax but no character semantics. Font tables are the sole exception:
        // read their bounded encoding declarations, never their names, alternative names, or embedded data.
        if (_state.Skip && !_state.FontTable)
        {
            return true;
        }

        if (word.SequenceEqual("upr"u8) && !_state.Skip)
        {
            if (!groupStart || hasNumber)
            {
                Reject();
            }

            _state.UnicodeAlternatives = true;

            _state.AlternativeChildren = 0;
        }
        else if (word.SequenceEqual("fonttbl"u8))
        {
            _state.FontTable = true;

            _state.Skip = true;
        }
        else if (word.SequenceEqual("f"u8))
        {
            if (!hasNumber || argument is < 0 or > 65_535)
            {
                Reject();
            }

            _state.FontIndex = argument;

            if (_state.FontTable && !_fonts.ContainsKey(argument))
            {
                if (_fonts.Count == 4096)
                {
                    Reject();
                }

                _fonts.Add(argument, _ansiCodePage);
            }
        }
        else if (word.SequenceEqual("deff"u8))
        {
            if (!hasNumber || argument is < 0 or > 65_535)
            {
                Reject();
            }

            _defaultFont = argument;
        }
        else if (word.SequenceEqual("fcharset"u8) && _state.FontTable)
        {
            if (!hasNumber)
            {
                Reject();
            }

            int codePage = argument switch
            {
                0 or 1 => _ansiCodePage,
                204 => 1251,
                _ => 0,
            };

            SetFontCodePage(codePage);
        }
        else if (word.SequenceEqual("rtf"u8))
        {
            if (!hasNumber || argument != 1 || start != 2)
            {
                Reject();
            }
        }
        else if (word.SequenceEqual("ansicpg"u8) || word.SequenceEqual("cpg"u8))
        {
            if (!hasNumber || !SingleByteTextDecoder.IsSupported(argument))
            {
                Reject();
            }

            if (_state.FontTable)
            {
                SetFontCodePage(argument);
            }
            else
            {
                _state.CodePage = argument;

                _ansiCodePage = argument;
            }
        }
        else if (word.SequenceEqual("mac"u8) || word.SequenceEqual("pc"u8) || word.SequenceEqual("pca"u8))
        {
            Reject();
        }
        else if (word.SequenceEqual("uc"u8))
        {
            if (!hasNumber || argument is < 0 or > 16)
            {
                Reject();
            }

            _state.UnicodeFallback = argument;
        }
        else if (word.SequenceEqual("u"u8))
        {
            if (!hasNumber || argument is < short.MinValue or > ushort.MaxValue || argument == 0)
            {
                Reject();
            }

            _fallback = _state.UnicodeFallback;

            return Emit((char)(ushort)argument);
        }
        else if (word.SequenceEqual("v"u8))
        {
            _state.Hidden = !hasNumber || argument != 0;
        }
        else if (word.SequenceEqual("webhidden"u8))
        {
            _state.WebHidden = !hasNumber || argument != 0;
        }
        else if (word.SequenceEqual("deleted"u8))
        {
            _state.Deleted = !hasNumber || argument != 0;
        }
        else if (word.SequenceEqual("plain"u8))
        {
            _state.Hidden = false;

            _state.WebHidden = false;

            _state.Deleted = false;

            _state.FontIndex = -1;
        }
        else if (ExcludedDestination(word))
        {
            _state.Skip = true;

            _state.FontTable = false;
        }
        else
        {
            char character = '\0';

            if (word.SequenceEqual("par"u8) || word.SequenceEqual("line"u8) || word.SequenceEqual("page"u8) || word.SequenceEqual("sect"u8) || word.SequenceEqual("row"u8))
            {
                character = '\n';
            }
            else if (word.SequenceEqual("tab"u8) || word.SequenceEqual("cell"u8))
            {
                character = '\t';
            }
            else if (word.SequenceEqual("emdash"u8))
            {
                character = '\u2014';
            }
            else if (word.SequenceEqual("endash"u8))
            {
                character = '\u2013';
            }
            else if (word.SequenceEqual("bullet"u8))
            {
                character = '\u2022';
            }
            else if (word.SequenceEqual("lquote"u8))
            {
                character = '\u2018';
            }
            else if (word.SequenceEqual("rquote"u8))
            {
                character = '\u2019';
            }
            else if (word.SequenceEqual("ldblquote"u8))
            {
                character = '\u201c';
            }
            else if (word.SequenceEqual("rdblquote"u8))
            {
                character = '\u201d';
            }
            else if (word.SequenceEqual("emspace"u8) || word.SequenceEqual("enspace"u8) || word.SequenceEqual("qmspace"u8))
            {
                character = ' ';
            }

            return character == 0 || Emit(character);
        }

        return true;
    }

    private int CurrentCodePage()
    {
        int font = _state.FontIndex < 0 ? _defaultFont : _state.FontIndex;

        return _fonts.TryGetValue(font, out int codePage) ? codePage : _state.CodePage;
    }

    private void SetFontCodePage(int codePage)
    {
        if (!SingleByteTextDecoder.IsSupported(codePage) || _state.FontIndex < 0)
        {
            Reject();
        }

        _fonts[_state.FontIndex] = codePage;
    }

    private bool Emit(char character)
    {
        if (_state.Skip || _state.Hidden || _state.WebHidden || _state.Deleted)
        {
            return true;
        }

        if (character == 0 || character == '\ufffd')
        {
            Reject();
        }

        if (_highSurrogate != 0)
        {
            if (!char.IsLowSurrogate(character))
            {
                Reject();
            }

            char high = _highSurrogate;

            _highSurrogate = '\0';

            return Append(high) && Append(character);
        }

        if (char.IsHighSurrogate(character))
        {
            _highSurrogate = character;

            return true;
        }

        if (char.IsLowSurrogate(character))
        {
            Reject();
        }

        return Append(character);
    }

    private bool Append(char character)
    {
        if (_output is null)
        {
            return true;
        }

        _buffer[_bufferLength++] = character;

        return _bufferLength != _buffer.Length || Flush();
    }

    private bool Flush()
    {
        _cancellationToken.ThrowIfCancellationRequested();

        if (_output is null || _bufferLength == 0)
        {
            return true;
        }

        bool accepted = _output.TryAppend(_buffer.AsSpan(0, _bufferLength));

        _bufferLength = 0;

        return accepted;
    }

    private static bool IsLetter(byte value) => value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

    private static int Hex(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        _ => throw new InvalidDataException("Invalid RTF hexadecimal escape."),
    };

    private static bool ExcludedDestination(ReadOnlySpan<byte> word) =>
        word.SequenceEqual("fonttbl"u8) ||
        word.SequenceEqual("colortbl"u8) ||
        word.SequenceEqual("stylesheet"u8) ||
        word.SequenceEqual("info"u8) ||
        word.SequenceEqual("filetbl"u8) ||
        word.SequenceEqual("revtbl"u8) ||
        word.SequenceEqual("listtable"u8) ||
        word.SequenceEqual("listoverridetable"u8) ||
        word.SequenceEqual("rsidtbl"u8) ||
        word.SequenceEqual("generator"u8) ||
        word.SequenceEqual("xmlnstbl"u8) ||
        word.SequenceEqual("annotation"u8) ||
        word.SequenceEqual("atnid"u8) ||
        word.SequenceEqual("atnauthor"u8) ||
        word.SequenceEqual("atndate"u8) ||
        word.SequenceEqual("atnref"u8) ||
        word.SequenceEqual("object"u8) ||
        word.SequenceEqual("objdata"u8) ||
        word.SequenceEqual("objclass"u8) ||
        word.SequenceEqual("objname"u8) ||
        word.SequenceEqual("objalias"u8) ||
        word.SequenceEqual("objsect"u8) ||
        word.SequenceEqual("result"u8) ||
        word.SequenceEqual("pict"u8) ||
        word.SequenceEqual("shppict"u8) ||
        word.SequenceEqual("nonshppict"u8) ||
        word.SequenceEqual("shp"u8) ||
        word.SequenceEqual("shpinst"u8) ||
        word.SequenceEqual("fldinst"u8) ||
        word.SequenceEqual("datafield"u8) ||
        word.SequenceEqual("formfield"u8) ||
        word.SequenceEqual("bkmkstart"u8) ||
        word.SequenceEqual("bkmkend"u8) ||
        word.SequenceEqual("xe"u8) ||
        word.SequenceEqual("tc"u8) ||
        word.SequenceEqual("do"u8) ||
        word.SequenceEqual("htmltag"u8) ||
        word.SequenceEqual("mhtmltag"u8) ||
        word.SequenceEqual("datastore"u8) ||
        word.SequenceEqual("themedata"u8) ||
        word.SequenceEqual("colorschememapping"u8);

    private static void Reject() => throw new InvalidDataException("Invalid RTF document.");
}

internal struct RtfGroupState
{
    public int CodePage;

    public int UnicodeFallback;

    public int FontIndex;

    public bool FontTable;

    public bool Skip;

    public bool Hidden;

    public bool WebHidden;

    public bool Deleted;

    public bool GroupStart;

    public bool UnicodeAlternatives;

    public int AlternativeChildren;

    public int UnicodeDestinationPrefix;
}
