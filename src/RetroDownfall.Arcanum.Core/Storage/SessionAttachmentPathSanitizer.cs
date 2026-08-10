using System.Text;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Sanitizes logical keys and original filenames for session attachment paths:
/// strips separators, <c>..</c>, control characters, characters Windows filenames reject
/// (<c>* ? " &lt; &gt; |</c>), and leading dots; caps length; rejects empty and reserved names.
/// The stripped set is identical on every platform so a store written on macOS or Linux stays
/// readable on a Windows host.
/// </summary>
public static class SessionAttachmentPathSanitizer
{

    private const int MaxLength = 120;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
    };

    public static bool TrySanitize(string? input, out string sanitized, out string error)
    {

        sanitized = string.Empty;

        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {

            error = "Name is empty.";

            return false;

        }

        StringBuilder builder = new(input.Length);

        foreach (char c in input.Trim())
        {

            if (c is '/' or '\\' or ':')
            {

                continue;

            }

            if (c is '*' or '?' or '"' or '<' or '>' or '|')
            {

                continue;

            }

            if (char.IsControl(c))
            {

                continue;

            }

            builder.Append(c);

        }

        string candidate = builder.ToString();

        while (candidate.Contains("..", StringComparison.Ordinal))
        {

            candidate = candidate.Replace("..", string.Empty, StringComparison.Ordinal);

        }

        candidate = candidate.TrimStart('.');

        if (candidate.Length == 0)
        {

            error = "Name is empty after sanitization.";

            return false;

        }

        if (candidate.Length > MaxLength)
        {

            candidate = candidate[..MaxLength];

        }

        if (IsReserved(candidate))
        {

            error = "Name is reserved.";

            sanitized = string.Empty;

            return false;

        }

        sanitized = candidate;

        return true;

    }

    /// <summary>
    /// Validates a pending-turn id for use under <c>_pending/{turnId}/</c>: ASCII letters/digits,
    /// <c>-</c>, <c>_</c>, <c>.</c> only; no path separators or traversal.
    /// </summary>
    public static bool TryValidatePendingTurnId(string? input, out string validated, out string error)
    {

        validated = string.Empty;

        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {

            error = "Pending turn id is empty.";

            return false;

        }

        string trimmed = input.Trim();

        if (trimmed.Length > MaxLength)
        {

            error = "Pending turn id is too long.";

            return false;

        }

        if (trimmed is "." or "..")
        {

            error = "Pending turn id is reserved.";

            return false;

        }

        foreach (char c in trimmed)
        {

            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            {

                continue;

            }

            error = "Pending turn id contains unsafe characters.";

            return false;

        }

        validated = trimmed;

        return true;

    }

    private static bool IsReserved(string name)
    {

        string stem = name;

        int dot = name.IndexOf('.');

        if (dot >= 0)
        {

            stem = name[..dot];

        }

        return ReservedNames.Contains(stem);

    }

}
