namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

/// <summary>
/// The structural and retention bounds of one PDF extraction. <see cref="Default"/> holds the code-owned production
/// values; tests construct smaller ones so each bound can be exercised without a huge fixture.
/// </summary>
internal sealed class PdfExtractionLimits
{
    internal PdfExtractionLimits(int maxPages, int maxLettersPerPage, int maxLettersPerDocument, int maxOutputCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPages, 1);

        ArgumentOutOfRangeException.ThrowIfLessThan(maxLettersPerPage, 1);

        ArgumentOutOfRangeException.ThrowIfLessThan(maxLettersPerDocument, 1);

        ArgumentOutOfRangeException.ThrowIfLessThan(maxOutputCharacters, 1);

        MaxPages = maxPages;

        MaxLettersPerPage = maxLettersPerPage;

        MaxLettersPerDocument = maxLettersPerDocument;

        MaxOutputCharacters = maxOutputCharacters;
    }

    public static PdfExtractionLimits Default { get; } = new(
        maxPages: 2000,
        maxLettersPerPage: 250_000,
        maxLettersPerDocument: 2_000_000,
        maxOutputCharacters: 262_144);

    /// <summary>Most pages a document may declare; one more is refused before any page is read.</summary>
    public int MaxPages { get; }

    /// <summary>Most glyphs one page may show.</summary>
    public int MaxLettersPerPage { get; }

    /// <summary>Most glyphs the pages read so far may show in total.</summary>
    public int MaxLettersPerDocument { get; }

    /// <summary>Most UTF-16 characters of output, page separators included.</summary>
    public int MaxOutputCharacters { get; }
}
