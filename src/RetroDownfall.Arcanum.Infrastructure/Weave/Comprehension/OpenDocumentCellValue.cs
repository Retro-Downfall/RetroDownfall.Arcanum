using System.Globalization;
using System.Xml;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The value a spreadsheet cell shows, read from its value attributes. Calc formats <c>office:value</c>,
/// <c>office:date-value</c>, <c>office:time-value</c> and <c>office:boolean-value</c> with the cell's style and discards
/// the <c>text:p</c> the producer cached beside them, so for those cells the attribute is what a reader sees and the
/// paragraph can say anything. The attribute is projected raw, as the spreadsheet extractor projects the cached
/// <c>&lt;v&gt;</c> of a worksheet cell: no number format is applied, a date stays a date and a boolean is
/// <c>TRUE</c> or <c>FALSE</c>.
/// </summary>
/// <remarks>
/// A value a consumer could not show as a value (not a number, not a date or duration, not a boolean, or longer than
/// <see cref="MaximumLength"/> characters) is not projected: the attribute is not a place to put words for the model.
/// </remarks>
internal static class OpenDocumentCellValue
{
    /// <summary>The longest value projected; every well-formed number, date or duration is far shorter.</summary>
    internal const int MaximumLength = 128;

    private const string DateCharacters = "0123456789-:.TZ+";

    private const string DurationCharacters = "0123456789-:.PTYMDHS";

    /// <summary>True for the value types whose cell shows a value formatted from an attribute rather than its paragraphs.</summary>
    public static bool IsValueType(string? valueType) =>
        valueType is "float" or "percentage" or "currency" or "date" or "time" or "boolean";

    /// <summary>
    /// The text projected for the cell the reader is on, whose <c>office:value-type</c> is <paramref name="valueType"/>
    /// (one of those <see cref="IsValueType"/> accepts), or null when the cell lacks the attribute that type reads or
    /// its value is not of that type's form.
    /// </summary>
    public static string? Read(XmlReader reader, string valueType)
    {
        string? raw = valueType switch
        {
            "float" or "percentage" or "currency" => reader.GetAttribute("value", OpenDocumentNames.Office),
            "date" => reader.GetAttribute("date-value", OpenDocumentNames.Office),
            "time" => reader.GetAttribute("time-value", OpenDocumentNames.Office),
            _ => reader.GetAttribute("boolean-value", OpenDocumentNames.Office),
        };

        string? value = raw?.Trim();

        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength)
        {
            return null;
        }

        return valueType switch
        {
            "float" or "percentage" or "currency" => IsNumber(value) ? value : null,
            "date" => IsTemporal(value, DateCharacters) ? value : null,
            "time" => IsTemporal(value, DurationCharacters) ? value : null,
            _ => value switch
            {
                "true" => "TRUE",
                "false" => "FALSE",
                _ => null,
            },
        };
    }

    private static bool IsNumber(string value) =>
        double.TryParse(
            value,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture,
            out double number)
        && double.IsFinite(number);

    private static bool IsTemporal(string value, string allowed)
    {
        bool digit = false;

        foreach (char current in value)
        {
            if (!allowed.Contains(current, StringComparison.Ordinal))
            {
                return false;
            }

            digit |= char.IsAsciiDigit(current);
        }

        return digit;
    }
}
