using System.Text;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Exceptions;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

internal sealed class PdfTextExtractor
{
    /// <summary>The deepest object nesting PdfPig may follow: the value the pinned nightly ships with and was measured at.</summary>
    private const int MaxNestingDepth = 256;

    public DocumentExtractionOutcome Extract(ReadOnlyMemory<byte> bytes, PdfExtractionLimits limits, CancellationToken cancellationToken)
    {
        PdfDocument document;

        try
        {
            document = PdfDocument.Open(bytes, CreateOptions());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Opening reads the trailer and its encryption dictionary. PdfPig reads every /Encrypt dictionary as a Standard
            // handler, so a public-key (certificate) one fails here with an unrelated exception: a well formed, protected
            // file that is not damaged. A file whose trailer declares no encryption failed for another reason.
            bool protectedFile = exception is PdfDocumentEncryptedException || PdfTrailerEncryption.IsDeclared(bytes.Span);

            return DocumentExtractionOutcome.Without(protectedFile ? DocumentExtractionKind.PasswordRequired : DocumentExtractionKind.Malformed);
        }

        using (document)
        {
            try
            {
                return ExtractPages(document, limits, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PdfDocumentEncryptedException)
            {
                return DocumentExtractionOutcome.Without(DocumentExtractionKind.PasswordRequired);
            }
            catch (Exception)
            {
                return DocumentExtractionOutcome.Without(DocumentExtractionKind.Malformed);
            }
        }
    }

    /// <summary>
    /// Options for one open. A fresh instance each time: the parser is handed it, so it is never shared between extractions.
    /// Parsing is strict. Missing fonts are skipped rather than failing the document. No password is supplied. /ActualText
    /// replacement is off, because it is text no reader sees that the file's author controls. Path clipping is off, because
    /// with it on PdfPig drops text that lies outside the page, and #163 decides every text-showing run is extracted. The
    /// nesting depth is pinned here so a library default that grows cannot bring back the stack overflow the pinned nightly
    /// turned into an exception.
    /// </summary>
    private static ParsingOptions CreateOptions() => new()
    {
        UseLenientParsing = false,
        SkipMissingFonts = true,
        UseActualText = false,
        ClipPaths = false,
        MaxStackDepth = MaxNestingDepth,
    };

    private static DocumentExtractionOutcome ExtractPages(PdfDocument document, PdfExtractionLimits limits, CancellationToken cancellationToken)
    {
        if (document.Structure.CrossReferenceTable.ObjectOffsets.Count > limits.MaxObjects)
        {
            return DocumentExtractionOutcome.Without(DocumentExtractionKind.Bounds);
        }

        int pageCount = document.NumberOfPages;

        if (pageCount > limits.MaxPages)
        {
            return DocumentExtractionOutcome.Without(DocumentExtractionKind.Bounds);
        }

        List<string> pages = [];

        bool undefinedContentsRead = false;

        long letters = 0;

        int used = 0;

        bool hasText = false;

        bool truncated = false;

        for (int number = 1; number <= pageCount; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int separator = pages.Count == 0 ? 0 : 1;

            if (used + separator > limits.MaxOutputCharacters)
            {
                truncated = true;

                break;
            }

            Page page = GetPage(document, number, ref undefinedContentsRead, cancellationToken);

            int pageLetters = page.Letters.Count;

            letters += pageLetters;

            if (pageLetters > limits.MaxLettersPerPage || letters > limits.MaxLettersPerDocument)
            {
                return DocumentExtractionOutcome.Without(DocumentExtractionKind.Bounds);
            }

            string text = RemoveUnsafeCharacters(PdfPageText.Build(page.Letters));

            int available = limits.MaxOutputCharacters - used - separator;

            if (text.Length > available)
            {
                text = CutAtSurrogateBoundary(text, available);

                truncated = true;
            }

            if (truncated && text.Length == 0)
            {
                break;
            }

            pages.Add(text);

            used += separator + text.Length;

            hasText |= HasVisibleCharacter(text);

            if (truncated)
            {
                break;
            }
        }

        if (!hasText)
        {
            return DocumentExtractionOutcome.Without(truncated ? DocumentExtractionKind.Bounds : DocumentExtractionKind.NoText);
        }

        return new DocumentExtractionOutcome(DocumentExtractionKind.Extracted, pages, truncated);
    }

    /// <summary>
    /// PdfPig refuses a page whose <c>/Contents</c> refers to an object the file does not define, which the specification
    /// reads as the null object (a blank page). The first such refusal repairs every page of the document once
    /// (<see cref="PdfUndefinedContents"/>) and reads the page again; any other refusal, and a second one, stands.
    /// </summary>
    private static Page GetPage(PdfDocument document, int number, ref bool undefinedContentsRead, CancellationToken cancellationToken)
    {
        try
        {
            return document.GetPage(number);
        }
        catch (InvalidOperationException)
        {
            if (undefinedContentsRead || !PdfUndefinedContents.TryReadAsNull(document, cancellationToken))
            {
                throw;
            }

            undefinedContentsRead = true;

            return document.GetPage(number);
        }
    }

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

    /// <summary>The first <paramref name="length"/> characters, or one fewer when that would end between the halves of a surrogate pair.</summary>
    private static string CutAtSurrogateBoundary(string text, int length)
    {
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    /// <summary>
    /// Removes NUL and form feed, which the worker frame and the sink cannot carry (form feed separates pages), and replaces
    /// every surrogate that is not half of a valid pair with U+FFFD, which UTF-8 cannot encode.
    /// </summary>
    internal static string RemoveUnsafeCharacters(string text)
    {
        StringBuilder safe = new(text.Length);

        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];

            if (character is '\0' or '\f')
            {
                continue;
            }

            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                safe.Append(character).Append(text[index + 1]);

                index++;
            }
            else
            {
                safe.Append(char.IsSurrogate(character) ? '\uFFFD' : character);
            }
        }

        return safe.ToString();
    }
}
