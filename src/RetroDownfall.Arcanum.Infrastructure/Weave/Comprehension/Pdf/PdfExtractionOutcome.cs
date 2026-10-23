namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

/// <summary>
/// The closed set of results of one PDF text extraction. A kind carries no message: nothing the parser or the document
/// says can reach a status field or a log through it.
/// </summary>
internal enum PdfExtractionKind
{
    /// <summary>At least one page showed text.</summary>
    Extracted,

    /// <summary>The document is well formed but shows no text on any page (image-only, blank, or only spaces).</summary>
    NoText,

    /// <summary>The document needs a password other than the empty one, or is protected by a security handler that has no password to give (public-key, certificate encryption).</summary>
    PasswordRequired,

    /// <summary>The bytes are not a PDF the parser can read (truncated, damaged, garbage, empty).</summary>
    Malformed,

    /// <summary>The document declares or shows more pages or glyphs than the bounds allow.</summary>
    Bounds,
}

/// <summary>The result of one PDF extraction.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Pages">
/// One entry per PDF page in order, an empty string for a page without text, so an entry's index plus one is the page
/// number. Empty unless <paramref name="Kind"/> is <see cref="PdfExtractionKind.Extracted"/>.
/// </param>
/// <param name="Truncated">
/// The output ceiling stopped extraction before every page was fully represented. Only an extracted result can be truncated.
/// </param>
internal sealed record PdfExtractionOutcome(PdfExtractionKind Kind, IReadOnlyList<string> Pages, bool Truncated)
{
    /// <summary>An outcome that carries no text.</summary>
    public static PdfExtractionOutcome Without(PdfExtractionKind kind) => new(kind, [], false);
}
