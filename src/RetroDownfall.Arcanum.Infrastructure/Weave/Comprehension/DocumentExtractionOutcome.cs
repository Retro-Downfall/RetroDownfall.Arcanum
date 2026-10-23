namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The closed set of results of one text extraction by an isolated worker, whatever the format. A kind carries no message:
/// nothing the parser or the document says can reach a status field or a log through it. The worker kind's
/// <see cref="DocumentWorkerProfile"/> words the reason each verdict without text ends with.
/// </summary>
internal enum DocumentExtractionKind
{
    /// <summary>At least one page showed text.</summary>
    Extracted,

    /// <summary>The document is well formed but shows no text on any page (image-only, blank, or only spaces).</summary>
    NoText,

    /// <summary>The document needs a password other than the empty one, or is protected by a security handler that has no password to give (public-key, certificate encryption).</summary>
    PasswordRequired,

    /// <summary>The bytes are not a document of the kind the parser can read (truncated, damaged, garbage, empty).</summary>
    Malformed,

    /// <summary>The document declares or shows more pages, objects or glyphs than the extractor's bounds allow.</summary>
    Bounds,
}

/// <summary>The result of one extraction by an isolated worker: what the worker's frame carries.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Pages">
/// One entry per page in order (for a PDF, a PDF page), an empty string for a page without text, so an entry's index plus
/// one is the page number. Empty unless <paramref name="Kind"/> is <see cref="DocumentExtractionKind.Extracted"/>.
/// </param>
/// <param name="Truncated">
/// The output ceiling stopped extraction before every page was fully represented. Only an extracted result can be truncated.
/// </param>
internal sealed record DocumentExtractionOutcome(DocumentExtractionKind Kind, IReadOnlyList<string> Pages, bool Truncated)
{
    /// <summary>An outcome that carries no text.</summary>
    public static DocumentExtractionOutcome Without(DocumentExtractionKind kind) => new(kind, [], false);
}
