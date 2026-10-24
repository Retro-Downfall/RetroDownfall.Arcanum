namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

/// <summary>
/// Whether a file's trailer declares encryption: an <c>/Encrypt</c> key directly inside a dictionary that follows the
/// <c>trailer</c> keyword, or inside a cross-reference stream's dictionary (<c>/Type /XRef</c>), which is where a PDF 1.5
/// file keeps its trailer entries. The bytes are read as PDF tokens from the start, so the name inside a string, a comment,
/// a stream's data, any other dictionary or a value nested in the trailer does not count. It is asked only after a failed
/// open, where it separates a protected file (one whose security handler PdfPig does not implement) from a damaged one.
/// </summary>
internal static class PdfTrailerEncryption
{
    public static bool IsDeclared(ReadOnlySpan<byte> bytes)
    {
        // The nesting of dictionaries and arrays, and what is known about the outermost one while it is open.
        int depth = 0;

        bool outermostIsDictionary = false;

        bool trailer = false;

        bool crossReferenceStream = false;

        bool encrypt = false;

        // The last token at the top level was the trailer keyword; the last name in the outermost dictionary was /Type.
        bool afterTrailerKeyword = false;

        bool afterType = false;

        int position = 0;

        while (position < bytes.Length)
        {
            byte current = bytes[position];

            byte next = position + 1 < bytes.Length ? bytes[position + 1] : (byte)0;

            if (IsWhitespace(current))
            {
                position++;

                continue;
            }

            if (current == '%')
            {
                position = SkipComment(bytes, position);

                continue;
            }

            if ((current == '<' && next == '<') || current == '[')
            {
                if (depth == 0)
                {
                    outermostIsDictionary = current == '<';

                    trailer = afterTrailerKeyword && outermostIsDictionary;

                    crossReferenceStream = false;

                    encrypt = false;
                }

                depth++;

                position += current == '[' ? 1 : 2;
            }
            else if ((current == '>' && next == '>') || current == ']')
            {
                position += current == ']' ? 1 : 2;

                if (depth == 0)
                {
                    continue;
                }

                depth--;

                if (depth == 0 && outermostIsDictionary && encrypt && (trailer || crossReferenceStream))
                {
                    return true;
                }
            }
            else if (current == '(')
            {
                position = SkipLiteralString(bytes, position);
            }
            else if (current == '<')
            {
                int end = bytes[position..].IndexOf((byte)'>');

                position = end < 0 ? bytes.Length : position + end + 1;
            }
            else if (current == '/')
            {
                int end = EndOfRegular(bytes, position + 1);

                ReadOnlySpan<byte> name = bytes[(position + 1)..end];

                position = end;

                if (depth == 1 && outermostIsDictionary)
                {
                    crossReferenceStream |= afterType && name.SequenceEqual("XRef"u8);

                    encrypt |= name.SequenceEqual("Encrypt"u8);

                    afterType = name.SequenceEqual("Type"u8);
                }

                afterTrailerKeyword = false;

                continue;
            }
            else if (IsDelimiter(current))
            {
                position++;
            }
            else
            {
                int end = EndOfRegular(bytes, position);

                ReadOnlySpan<byte> token = bytes[position..end];

                position = end;

                if (depth == 0 && token.SequenceEqual("stream"u8))
                {
                    int endstream = bytes[position..].IndexOf("endstream"u8);

                    if (endstream < 0)
                    {
                        return false;
                    }

                    position += endstream + "endstream"u8.Length;
                }

                afterTrailerKeyword = depth == 0 && token.SequenceEqual("trailer"u8);

                afterType = false;

                continue;
            }

            afterTrailerKeyword = false;

            afterType = false;
        }

        return false;
    }

    private static int SkipComment(ReadOnlySpan<byte> bytes, int position)
    {
        int end = bytes[position..].IndexOfAny((byte)'\r', (byte)'\n');

        return end < 0 ? bytes.Length : position + end;
    }

    /// <summary>The position after a literal string's closing parenthesis: parentheses nest, and a backslash escapes the byte after it.</summary>
    private static int SkipLiteralString(ReadOnlySpan<byte> bytes, int position)
    {
        int open = 0;

        while (position < bytes.Length)
        {
            byte current = bytes[position];

            if (current == '\\')
            {
                position += 2;

                continue;
            }

            position++;

            if (current == '(')
            {
                open++;
            }
            else if (current == ')' && --open == 0)
            {
                return position;
            }
        }

        return bytes.Length;
    }

    private static int EndOfRegular(ReadOnlySpan<byte> bytes, int position)
    {
        while (position < bytes.Length && !IsWhitespace(bytes[position]) && !IsDelimiter(bytes[position]))
        {
            position++;
        }

        return position;
    }

    private static bool IsWhitespace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte value) =>
        value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
}
