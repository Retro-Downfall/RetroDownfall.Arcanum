using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RetroDownfall.Arcanum.Core.Covenant;

public static class ArcanumCanonicalJsonV1
{
    public const int MaxInputUtf8Bytes = 65_536;

    public const int MaxOutputUtf8Bytes = 65_536;

    public const int MaxDepth = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Canonicalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        int byteCount = StrictUtf8.GetByteCount(json);

        if (byteCount > MaxInputUtf8Bytes)
        {
            throw new ArgumentException($"Canonical JSON input cannot exceed {MaxInputUtf8Bytes} UTF-8 bytes.", nameof(json));
        }

        byte[] utf8Json = new byte[byteCount];

        StrictUtf8.GetBytes(json.AsSpan(), utf8Json);

        return Canonicalize(utf8Json);
    }

    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxInputUtf8Bytes)
        {
            throw new ArgumentException($"Canonical JSON input cannot exceed {MaxInputUtf8Bytes} UTF-8 bytes.", nameof(utf8Json));
        }

        if (utf8Json.IsEmpty)
        {
            throw new ArgumentException("Canonical JSON input cannot be empty.", nameof(utf8Json));
        }

        using JsonDocument document = Parse(utf8Json);

        int outputBytes = MeasureElement(document.RootElement);

        if (outputBytes > MaxOutputUtf8Bytes)
        {
            throw new ArgumentException($"Canonical JSON output cannot exceed {MaxOutputUtf8Bytes} UTF-8 bytes.", nameof(utf8Json));
        }

        byte[] output = new byte[outputBytes];
        CanonicalJsonWriter writer = new(output);

        WriteElement(document.RootElement, writer);

        if (writer.WrittenCount != output.Length)
        {
            throw new InvalidOperationException("Canonical JSON output measurement did not match serialization.");
        }

        return output;
    }

    private static JsonDocument Parse(ReadOnlySpan<byte> utf8Json)
    {
        JsonReaderOptions options = new()
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaxDepth
        };

        Utf8JsonReader reader = new(utf8Json, options);

        try
        {
            JsonDocument document = JsonDocument.ParseValue(ref reader);

            try
            {
                if (reader.Read())
                {
                    throw new ArgumentException("Canonical JSON input must contain exactly one JSON value.", nameof(utf8Json));
                }

                return document;
            }
            catch
            {
                document.Dispose();

                throw;
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Canonical JSON input must be strict UTF-8 JSON within the policy depth limit.", nameof(utf8Json), exception);
        }
    }

    private static int MeasureElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => MeasureObject(element),
            JsonValueKind.Array => MeasureArray(element),
            JsonValueKind.String => MeasureString(RequireString(element)),
            JsonValueKind.Number => GetCanonicalNumber(element).Length,
            JsonValueKind.True => 4,
            JsonValueKind.False => 5,
            JsonValueKind.Null => 4,
            _ => throw new ArgumentException("Canonical JSON input contains an unsupported value kind.")
        };

    private static int MeasureObject(JsonElement element)
    {
        List<CanonicalProperty> properties = GetSortedProperties(element);
        int length = 2;

        for (int index = 0; index < properties.Count; index++)
        {
            CanonicalProperty property = properties[index];

            length = AddLength(length, MeasureString(property.Name));
            length = AddLength(length, 1);
            length = AddLength(length, MeasureElement(property.Value));

            if (index > 0)
            {
                length = AddLength(length, 1);
            }
        }

        return length;
    }

    private static int MeasureArray(JsonElement element)
    {
        int length = 2;
        int index = 0;

        foreach (JsonElement item in element.EnumerateArray())
        {
            length = AddLength(length, MeasureElement(item));

            if (index > 0)
            {
                length = AddLength(length, 1);
            }

            index++;
        }

        return length;
    }

    private static int MeasureString(string value)
    {
        int length = 2;
        ReadOnlySpan<char> remaining = value.AsSpan();

        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(remaining, out Rune rune, out int charsConsumed);

            if (status != OperationStatus.Done || IsNoncharacter(rune.Value))
            {
                throw new ArgumentException("Canonical JSON strings must contain valid Unicode scalar values and no noncharacters.", nameof(value));
            }

            length = AddLength(length, GetEscapedRuneLength(rune));
            remaining = remaining[charsConsumed..];
        }

        return length;
    }

    private static int GetEscapedRuneLength(Rune rune)
    {
        if (rune.Value is (int)'"' or (int)'\\')
        {
            return 2;
        }

        if (rune.Value <= 0x1f)
        {
            return rune.Value is 0x08 or 0x09 or 0x0a or 0x0c or 0x0d ? 2 : 6;
        }

        return rune.Utf8SequenceLength;
    }

    private static List<CanonicalProperty> GetSortedProperties(JsonElement element)
    {
        List<CanonicalProperty> properties = [];
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            string name;

            try
            {
                name = property.Name;
            }
            catch (InvalidOperationException exception)
            {
                throw new ArgumentException("Canonical JSON property names must contain valid Unicode scalar values.", nameof(element), exception);
            }

            MeasureString(name);

            if (!names.Add(name))
            {
                throw new ArgumentException("Canonical JSON objects cannot contain duplicate decoded property names.");
            }

            properties.Add(new CanonicalProperty(name, property.Value));
        }

        properties.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));

        return properties;
    }

    private static string RequireString(JsonElement element)
    {
        try
        {
            return element.GetString()
                ?? throw new ArgumentException("Canonical JSON string value was unavailable.");
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException("Canonical JSON strings must contain valid Unicode scalar values.", nameof(element), exception);
        }
    }

    private static string GetCanonicalNumber(JsonElement element)
    {
        string rawNumber = element.GetRawText();

        if (!double.TryParse(rawNumber, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
        {
            throw new ArgumentException("Canonical JSON numbers must be finite IEEE-754 binary64 values.", nameof(element));
        }

        if (value == 0d && HasNonzeroCoefficient(rawNumber))
        {
            throw new ArgumentException("Canonical JSON numbers cannot underflow a nonzero coefficient to binary64 zero.", nameof(element));
        }

        return FormatEcmaScriptNumber(value);
    }

    private static bool HasNonzeroCoefficient(ReadOnlySpan<char> rawNumber)
    {
        foreach (char character in rawNumber)
        {
            if (character is 'e' or 'E')
            {
                return false;
            }

            if (character is >= '1' and <= '9')
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatEcmaScriptNumber(double value)
    {
        if (value == 0d)
        {
            return "0";
        }

        bool negative = value < 0d;
        string roundTrip = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        int exponentMarker = roundTrip.IndexOfAny('E', 'e');
        int explicitExponent = exponentMarker < 0
            ? 0
            : int.Parse(roundTrip.AsSpan(exponentMarker + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        ReadOnlySpan<char> significand = exponentMarker < 0
            ? roundTrip.AsSpan()
            : roundTrip.AsSpan(0, exponentMarker);
        int decimalPoint = significand.IndexOf('.');
        int decimalPosition = (decimalPoint < 0 ? significand.Length : decimalPoint) + explicitExponent;
        Span<char> digitBuffer = stackalloc char[32];
        int digitCount = 0;

        foreach (char character in significand)
        {
            if (character != '.')
            {
                digitBuffer[digitCount++] = character;
            }
        }

        int firstDigit = 0;

        while (firstDigit < digitCount - 1 && digitBuffer[firstDigit] == '0')
        {
            firstDigit++;
            decimalPosition--;
        }

        while (digitCount - firstDigit > 1 && digitBuffer[digitCount - 1] == '0')
        {
            digitCount--;
        }

        ReadOnlySpan<char> digits = digitBuffer[firstDigit..digitCount];
        string magnitude = FormatMagnitude(digits, decimalPosition);

        return negative ? "-" + magnitude : magnitude;
    }

    private static string FormatMagnitude(ReadOnlySpan<char> digits, int decimalPosition)
    {
        int digitCount = digits.Length;

        if (digitCount <= decimalPosition && decimalPosition <= 21)
        {
            return string.Concat(digits, new string('0', decimalPosition - digitCount));
        }

        if (decimalPosition > 0 && decimalPosition <= 21)
        {
            return string.Concat(digits[..decimalPosition], ".", digits[decimalPosition..]);
        }

        if (decimalPosition > -6 && decimalPosition <= 0)
        {
            return string.Concat("0.", new string('0', -decimalPosition), digits);
        }

        int exponent = decimalPosition - 1;
        string exponentText = exponent >= 0
            ? "+" + exponent.ToString(CultureInfo.InvariantCulture)
            : exponent.ToString(CultureInfo.InvariantCulture);

        return digitCount == 1
            ? string.Concat(digits, "e", exponentText)
            : digits[..1].ToString() + "." + digits[1..].ToString() + "e" + exponentText;
    }

    private static void WriteElement(JsonElement element, CanonicalJsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, writer);
                break;
            case JsonValueKind.Array:
                WriteArray(element, writer);
                break;
            case JsonValueKind.String:
                writer.WriteString(RequireString(element));
                break;
            case JsonValueKind.Number:
                writer.WriteAscii(GetCanonicalNumber(element));
                break;
            case JsonValueKind.True:
                writer.WriteAscii("true");
                break;
            case JsonValueKind.False:
                writer.WriteAscii("false");
                break;
            case JsonValueKind.Null:
                writer.WriteAscii("null");
                break;
            default:
                throw new ArgumentException("Canonical JSON input contains an unsupported value kind.");
        }
    }

    private static void WriteObject(JsonElement element, CanonicalJsonWriter writer)
    {
        List<CanonicalProperty> properties = GetSortedProperties(element);

        writer.WriteByte((byte)'{');

        for (int index = 0; index < properties.Count; index++)
        {
            if (index > 0)
            {
                writer.WriteByte((byte)',');
            }

            CanonicalProperty property = properties[index];

            writer.WriteString(property.Name);
            writer.WriteByte((byte)':');
            WriteElement(property.Value, writer);
        }

        writer.WriteByte((byte)'}');
    }

    private static void WriteArray(JsonElement element, CanonicalJsonWriter writer)
    {
        int index = 0;

        writer.WriteByte((byte)'[');

        foreach (JsonElement item in element.EnumerateArray())
        {
            if (index > 0)
            {
                writer.WriteByte((byte)',');
            }

            WriteElement(item, writer);
            index++;
        }

        writer.WriteByte((byte)']');
    }

    private static bool IsNoncharacter(int scalarValue) =>
        scalarValue is >= 0xfdd0 and <= 0xfdef
        || (scalarValue & 0xffff) is 0xfffe or 0xffff;

    private static int AddLength(int current, int addition)
    {
        int value;

        try
        {
            value = checked(current + addition);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("Canonical JSON output length overflowed.", exception);
        }

        if (value > MaxOutputUtf8Bytes)
        {
            throw new ArgumentException($"Canonical JSON output cannot exceed {MaxOutputUtf8Bytes} UTF-8 bytes.");
        }

        return value;
    }

    private sealed class CanonicalJsonWriter(byte[] output)
    {
        private int _writtenCount;

        public int WrittenCount => _writtenCount;

        public void WriteByte(byte value)
        {
            output[_writtenCount] = value;
            _writtenCount++;
        }

        public void WriteAscii(string value)
        {
            foreach (char character in value)
            {
                if (character > 0x7f)
                {
                    throw new InvalidOperationException("Canonical JSON ASCII serialization received a non-ASCII character.");
                }

                WriteByte((byte)character);
            }
        }

        public void WriteString(string value)
        {
            WriteByte((byte)'"');

            ReadOnlySpan<char> remaining = value.AsSpan();

            while (!remaining.IsEmpty)
            {
                OperationStatus status = Rune.DecodeFromUtf16(remaining, out Rune rune, out int charsConsumed);

                if (status != OperationStatus.Done || IsNoncharacter(rune.Value))
                {
                    throw new InvalidOperationException("A validated canonical JSON string changed before serialization.");
                }

                WriteRune(rune);
                remaining = remaining[charsConsumed..];
            }

            WriteByte((byte)'"');
        }

        private void WriteRune(Rune rune)
        {
            switch (rune.Value)
            {
                case '"':
                    WriteAscii("\\\"");
                    return;
                case '\\':
                    WriteAscii("\\\\");
                    return;
                case 0x08:
                    WriteAscii("\\b");
                    return;
                case 0x09:
                    WriteAscii("\\t");
                    return;
                case 0x0a:
                    WriteAscii("\\n");
                    return;
                case 0x0c:
                    WriteAscii("\\f");
                    return;
                case 0x0d:
                    WriteAscii("\\r");
                    return;
            }

            if (rune.Value <= 0x1f)
            {
                WriteAscii("\\u00");
                WriteByte(ToLowerHex((byte)(rune.Value >> 4)));
                WriteByte(ToLowerHex((byte)(rune.Value & 0x0f)));
                return;
            }

            Span<byte> destination = output.AsSpan(_writtenCount, rune.Utf8SequenceLength);
            int bytesWritten = rune.EncodeToUtf8(destination);

            _writtenCount += bytesWritten;
        }

        private static byte ToLowerHex(byte value) =>
            value < 10 ? (byte)('0' + value) : (byte)('a' + value - 10);
    }

    private readonly record struct CanonicalProperty(string Name, JsonElement Value);
}
