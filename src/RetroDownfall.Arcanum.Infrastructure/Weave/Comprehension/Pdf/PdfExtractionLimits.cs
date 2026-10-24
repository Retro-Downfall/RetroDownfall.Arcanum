namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

/// <summary>
/// The structural and retention bounds of one PDF extraction. <see cref="Default"/> holds the code-owned production
/// values; tests construct smaller ones so each bound can be exercised without a huge fixture.
/// </summary>
internal sealed class PdfExtractionLimits
{
    internal PdfExtractionLimits(int maxPages, int maxObjects, int maxLettersPerPage, int maxLettersPerDocument, int maxOutputCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPages, 1);

        ArgumentOutOfRangeException.ThrowIfLessThan(maxObjects, 1);

        ArgumentOutOfRangeException.ThrowIfLessThan(maxLettersPerPage, 1);

        ArgumentOutOfRangeException.ThrowIfLessThan(maxLettersPerDocument, 1);

        ArgumentOutOfRangeException.ThrowIfLessThan(maxOutputCharacters, 1);

        MaxPages = maxPages;

        MaxObjects = maxObjects;

        MaxLettersPerPage = maxLettersPerPage;

        MaxLettersPerDocument = maxLettersPerDocument;

        MaxOutputCharacters = maxOutputCharacters;
    }

    public static PdfExtractionLimits Default { get; } = new(
        maxPages: 2000,
        maxObjects: 100_000,
        maxLettersPerPage: 250_000,
        maxLettersPerDocument: 2_000_000,
        maxOutputCharacters: 262_144);

    /// <summary>Most pages a document may declare; one more is refused before any page is read.</summary>
    public int MaxPages { get; }

    /// <summary>
    /// Most objects the document's cross-reference data may list as in use; one more is refused right after the open, before
    /// the page tree is counted or any page is read. An object stored inside an object stream (a type 2 entry) counts and a free
    /// entry does not; the trailer's <c>/Size</c> is not used, because the file chooses it freely. PdfPig builds no table from a
    /// scan, so an object the table lacks but the file writes out is not counted. A file the intake ceiling admits (1 MiB) holds
    /// at most about 80,000 objects written out (13 bytes each at the least), fewer than this bound, and an object inside an
    /// object stream can only be found through a listed entry. The check comes after the open, which has already read the table,
    /// so it bounds what is traversed afterwards, not the parser's memory and time while opening; the worker's process limits
    /// bound those. The default is 100,000: the densest 1 MiB documents measured list about 9,000 (a page of nothing but links
    /// written by WebKit) and a text document written by Quartz lists about 1,600 (the fixture README records the measurements).
    /// </summary>
    public int MaxObjects { get; }

    /// <summary>
    /// Most glyphs one page may show. PdfPig builds every glyph of a page before this is checked, so it bounds what the
    /// extractor processes and keeps, not the parser's own memory and time; the worker's process limits bound those.
    /// </summary>
    public int MaxLettersPerPage { get; }

    /// <summary>Most glyphs the pages read so far may show in total. Checked after each page is built, like <see cref="MaxLettersPerPage"/>.</summary>
    public int MaxLettersPerDocument { get; }

    /// <summary>Most UTF-16 characters of output, page separators included.</summary>
    public int MaxOutputCharacters { get; }
}
