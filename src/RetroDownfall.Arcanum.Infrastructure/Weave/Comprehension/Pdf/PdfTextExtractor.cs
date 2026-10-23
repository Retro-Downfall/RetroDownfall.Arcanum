using System.Text;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

internal sealed class PdfTextExtractor
{
    public PdfExtractionOutcome Extract(ReadOnlyMemory<byte> bytes, PdfExtractionLimits limits, CancellationToken cancellationToken)
    {
        try
        {
            return ExtractPages(bytes, limits, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PdfDocumentEncryptedException)
        {
            return PdfExtractionOutcome.Without(PdfExtractionKind.PasswordRequired);
        }
        catch (Exception)
        {
            return PdfExtractionOutcome.Without(PdfExtractionKind.Malformed);
        }
    }

    /// <summary>Options for one open. A fresh instance each time: the parser is handed it, so it is never shared between extractions.</summary>
    private static ParsingOptions CreateOptions() => new()
    {
        UseLenientParsing = false,
        SkipMissingFonts = true,
    };

    private static PdfExtractionOutcome ExtractPages(ReadOnlyMemory<byte> bytes, PdfExtractionLimits limits, CancellationToken cancellationToken)
    {
        using PdfDocument document = PdfDocument.Open(bytes, CreateOptions());

        int pageCount = document.NumberOfPages;

        if (pageCount > limits.MaxPages)
        {
            return PdfExtractionOutcome.Without(PdfExtractionKind.Bounds);
        }

        List<string> pages = [];

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

            Page page = document.GetPage(number);

            int pageLetters = page.Letters.Count;

            letters += pageLetters;

            if (pageLetters > limits.MaxLettersPerPage || letters > limits.MaxLettersPerDocument)
            {
                return PdfExtractionOutcome.Without(PdfExtractionKind.Bounds);
            }

            string text = RemoveUnsafeCharacters(ContentOrderTextExtractor.GetText(page));

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
            return PdfExtractionOutcome.Without(truncated ? PdfExtractionKind.Bounds : PdfExtractionKind.NoText);
        }

        return new PdfExtractionOutcome(PdfExtractionKind.Extracted, pages, truncated);
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
