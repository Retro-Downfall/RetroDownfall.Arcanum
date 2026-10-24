using System.Globalization;
using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The closed set of reasons a worker frame is refused. A defect names which rule failed and never carries any of the
/// frame's bytes, so nothing a document says can reach a status field or a log through it.
/// </summary>
internal enum ComprehensionWorkerFrameDefect
{
    /// <summary>No defect: the frame decoded.</summary>
    None,

    /// <summary>More bytes than any frame within the output ceiling can occupy.</summary>
    Oversize,

    /// <summary>The bytes are not valid UTF-8.</summary>
    InvalidUtf8,

    /// <summary>The first line is not the exact header of a known status.</summary>
    Header,

    /// <summary>The trailer is missing, malformed, or not the last line.</summary>
    Trailer,

    /// <summary>The trailer's character count differs from the body's.</summary>
    Length,

    /// <summary>The body is longer than the output ceiling.</summary>
    BodyCeiling,

    /// <summary>The trailer's page count differs from the body's.</summary>
    PageCount,

    /// <summary>The body does not fit the header's status.</summary>
    Payload,
}

/// <summary>The result of decoding one worker frame.</summary>
/// <param name="Outcome">The decoded outcome, or <see langword="null"/> when the frame was refused.</param>
/// <param name="Defect">Which rule refused the frame, or <see cref="ComprehensionWorkerFrameDefect.None"/>.</param>
internal sealed record ComprehensionWorkerFrameResult(DocumentExtractionOutcome? Outcome, ComprehensionWorkerFrameDefect Defect)
{
    /// <summary>True when the frame decoded. A refused frame is a protocol failure, not a document verdict.</summary>
    public bool IsValid => Defect == ComprehensionWorkerFrameDefect.None;
}

/// <summary>
/// The versioned wire frame the comprehension worker writes to stdout and the host validates before it believes any of it:
/// one grammar for every worker kind, both ends bounded by the kind's output ceiling.
/// </summary>
/// <remarks>
/// <code>
/// ARCANUM-COMPREHENSION/1 &lt;status&gt;\n
/// &lt;page&gt;\f&lt;page&gt;\f...\n
/// END &lt;utf16-length-of-body&gt; &lt;page-count&gt;\n
/// </code>
/// UTF-8 without a byte-order mark. The status is one of <c>extracted</c>, <c>extracted-truncated</c>, <c>no-text</c>,
/// <c>password-required</c>, <c>malformed</c> and <c>bounds</c>. The body is the page texts joined by U+000C (an empty
/// string for a page without text) and is empty, with a page count of zero, for every status except the two extracted
/// ones. The trailer is found from the end, so page text of any shape cannot end the frame early, and a crashed or
/// truncated worker leaves no trailer. Decoding fails closed: any deviation is a <see cref="ComprehensionWorkerFrameDefect"/>.
/// </remarks>
internal static class ComprehensionWorkerFrame
{
    /// <summary>Separates page texts in the body; page text never contains it.</summary>
    internal const char PageSeparator = '\f';

    private const string HeaderPrefix = "ARCANUM-COMPREHENSION/1 ";

    private const string TrailerPrefix = "END ";

    private const string Extracted = "extracted";

    private const string ExtractedTruncated = "extracted-truncated";

    private const string NoText = "no-text";

    private const string PasswordRequired = "password-required";

    private const string Malformed = "malformed";

    private const string Bounds = "bounds";

    /// <summary>Room for the header line and the trailer line around a body of the worst case encoding.</summary>
    private const int FrameSlackBytes = 256;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The most bytes any valid frame for <paramref name="maxOutputCharacters"/> can occupy: three bytes per UTF-16 unit plus the two lines.</summary>
    internal static long MaximumFrameBytes(int maxOutputCharacters) => 3L * maxOutputCharacters + FrameSlackBytes;

    /// <summary>
    /// Writes the frame for an outcome, refusing an outcome no frame within <paramref name="maxOutputCharacters"/> could
    /// carry, so the worker never writes a frame its host's <see cref="Decode"/> refuses for its length.
    /// </summary>
    /// <param name="maxOutputCharacters">The output ceiling the body may not exceed: the worker kind's, as for decoding.</param>
    /// <exception cref="ArgumentException">
    /// The outcome has a page the body cannot hold, text its kind cannot carry, or a body longer than the ceiling.
    /// </exception>
    internal static byte[] Encode(DocumentExtractionOutcome outcome, int maxOutputCharacters)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        string status = StatusOf(outcome);

        string body = string.Join(PageSeparator, outcome.Pages);

        if (body.Length > maxOutputCharacters)
        {
            throw new ArgumentException("The outcome's text is longer than the output ceiling.", nameof(outcome));
        }

        if (outcome.Kind == DocumentExtractionKind.Extracted)
        {
            if (outcome.Pages.Any(page => page.Contains('\0') || page.Contains(PageSeparator)) || !HasVisibleCharacter(body))
            {
                throw new ArgumentException("An extracted outcome needs pages without NUL or form feed and with a visible character.", nameof(outcome));
            }
        }
        else if (outcome.Pages.Count != 0 || outcome.Truncated)
        {
            throw new ArgumentException("An outcome without text cannot carry pages or a truncated flag.", nameof(outcome));
        }

        string frame = string.Create(
            CultureInfo.InvariantCulture,
            $"{HeaderPrefix}{status}\n{body}\n{TrailerPrefix}{body.Length} {outcome.Pages.Count}\n");

        return StrictUtf8.GetBytes(frame);
    }

    /// <summary>Validates and reads a frame; a frame that deviates anywhere is refused with the rule it broke.</summary>
    /// <param name="bytes">Everything the worker wrote to stdout.</param>
    /// <param name="maxOutputCharacters">The output ceiling the body may not exceed.</param>
    internal static ComprehensionWorkerFrameResult Decode(ReadOnlySpan<byte> bytes, int maxOutputCharacters)
    {
        if (bytes.Length > MaximumFrameBytes(maxOutputCharacters))
        {
            return Refused(ComprehensionWorkerFrameDefect.Oversize);
        }

        string text;

        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Refused(ComprehensionWorkerFrameDefect.InvalidUtf8);
        }

        int headerEnd = text.IndexOf('\n');

        if (headerEnd < 0 || !TryReadStatus(text.AsSpan(0, headerEnd), out DocumentExtractionKind kind, out bool truncated))
        {
            return Refused(ComprehensionWorkerFrameDefect.Header);
        }

        if (text[^1] != '\n')
        {
            return Refused(ComprehensionWorkerFrameDefect.Trailer);
        }

        int bodyEnd = text.LastIndexOf('\n', text.Length - 2);

        if (bodyEnd <= headerEnd
            || !TryReadTrailer(text.AsSpan(bodyEnd + 1, text.Length - 2 - bodyEnd), out int declaredLength, out int declaredPages))
        {
            return Refused(ComprehensionWorkerFrameDefect.Trailer);
        }

        string body = text.Substring(headerEnd + 1, bodyEnd - headerEnd - 1);

        if (body.Length != declaredLength)
        {
            return Refused(ComprehensionWorkerFrameDefect.Length);
        }

        if (body.Length > maxOutputCharacters)
        {
            return Refused(ComprehensionWorkerFrameDefect.BodyCeiling);
        }

        if (kind != DocumentExtractionKind.Extracted)
        {
            if (declaredPages != 0)
            {
                return Refused(ComprehensionWorkerFrameDefect.PageCount);
            }

            return body.Length == 0
                ? new ComprehensionWorkerFrameResult(DocumentExtractionOutcome.Without(kind), ComprehensionWorkerFrameDefect.None)
                : Refused(ComprehensionWorkerFrameDefect.Payload);
        }

        string[] pages = body.Split(PageSeparator);

        if (pages.Length != declaredPages)
        {
            return Refused(ComprehensionWorkerFrameDefect.PageCount);
        }

        if (body.Contains('\0') || !HasVisibleCharacter(body))
        {
            return Refused(ComprehensionWorkerFrameDefect.Payload);
        }

        return new ComprehensionWorkerFrameResult(
            new DocumentExtractionOutcome(DocumentExtractionKind.Extracted, pages, truncated),
            ComprehensionWorkerFrameDefect.None);
    }

    private static ComprehensionWorkerFrameResult Refused(ComprehensionWorkerFrameDefect defect) => new(null, defect);

    private static string StatusOf(DocumentExtractionOutcome outcome) => outcome.Kind switch
    {
        DocumentExtractionKind.Extracted => outcome.Truncated ? ExtractedTruncated : Extracted,
        DocumentExtractionKind.NoText => NoText,
        DocumentExtractionKind.PasswordRequired => PasswordRequired,
        DocumentExtractionKind.Malformed => Malformed,
        DocumentExtractionKind.Bounds => Bounds,
        _ => throw new ArgumentException("Unknown extraction kind.", nameof(outcome)),
    };

    private static bool TryReadStatus(ReadOnlySpan<char> line, out DocumentExtractionKind kind, out bool truncated)
    {
        kind = default;

        truncated = false;

        if (!line.StartsWith(HeaderPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> status = line[HeaderPrefix.Length..];

        if (status.SequenceEqual(Extracted))
        {
            kind = DocumentExtractionKind.Extracted;
        }
        else if (status.SequenceEqual(ExtractedTruncated))
        {
            kind = DocumentExtractionKind.Extracted;

            truncated = true;
        }
        else if (status.SequenceEqual(NoText))
        {
            kind = DocumentExtractionKind.NoText;
        }
        else if (status.SequenceEqual(PasswordRequired))
        {
            kind = DocumentExtractionKind.PasswordRequired;
        }
        else if (status.SequenceEqual(Malformed))
        {
            kind = DocumentExtractionKind.Malformed;
        }
        else if (status.SequenceEqual(Bounds))
        {
            kind = DocumentExtractionKind.Bounds;
        }
        else
        {
            return false;
        }

        return true;
    }

    private static bool TryReadTrailer(ReadOnlySpan<char> line, out int length, out int pages)
    {
        length = 0;

        pages = 0;

        if (!line.StartsWith(TrailerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> counts = line[TrailerPrefix.Length..];

        int space = counts.IndexOf(' ');

        return space >= 0
            && TryReadCount(counts[..space], out length)
            && TryReadCount(counts[(space + 1)..], out pages);
    }

    /// <summary>Reads a canonical decimal count: ASCII digits only, no sign, no leading zero, within <see cref="int"/>.</summary>
    private static bool TryReadCount(ReadOnlySpan<char> text, out int count) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out count)
        && text.SequenceEqual(count.ToString(CultureInfo.InvariantCulture));

    private static bool HasVisibleCharacter(string text)
    {
        foreach (char character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }
}
