using System.Globalization;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// The Grimoire's single persisted representation for an instant in time.
/// </summary>
/// <remarks>
/// Fixed-width UTC text preserves all <see cref="DateTime"/> ticks and makes SQLite ordinal TEXT
/// ordering identical to chronological ordering. Historical values may carry an explicit offset or
/// no suffix; the latter were always intended as UTC and are never interpreted in the host's local
/// time zone.
/// </remarks>
internal static class UtcInstantText
{
    internal const string FormatPattern = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private const DateTimeStyles ParseStyles =
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    private static readonly string[] AcceptedFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss'Z'",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd HH:mm:sszzz",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
    ];

    internal static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString(FormatPattern, CultureInfo.InvariantCulture);

    internal static string? Format(DateTimeOffset? value) =>
        value is DateTimeOffset instant ? Format(instant) : null;

    internal static string Format(DateTime value)
    {
        if (value.Kind == DateTimeKind.Local)
        {
            throw new ArgumentException(
                "A Local DateTime cannot cross the Grimoire persistence boundary; provide UTC or the "
                + "historical unspecified-as-UTC form.",
                nameof(value));
        }

        DateTime utc = value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return utc.ToString(FormatPattern, CultureInfo.InvariantCulture);
    }

    internal static string? Format(DateTime? value) =>
        value is DateTime instant ? Format(instant) : null;

    internal static string Normalize(string stored) => Format(Parse(stored));

    internal static DateTimeOffset Parse(string stored)
    {
        if (!TryParse(stored, out DateTimeOffset parsed))
        {
            throw new FormatException("Stored Grimoire instant is not a supported timestamp.");
        }

        return parsed.ToUniversalTime();
    }

    internal static bool TryParse(string? stored, out DateTimeOffset parsed)
    {
        if (!string.IsNullOrWhiteSpace(stored)
            && DateTimeOffset.TryParseExact(
                stored,
                AcceptedFormats,
                CultureInfo.InvariantCulture,
                ParseStyles,
                out parsed))
        {
            parsed = parsed.ToUniversalTime();

            return true;
        }

        parsed = default;

        return false;
    }

    internal static DateTime ParseDateTime(string stored) => Parse(stored).UtcDateTime;
}
