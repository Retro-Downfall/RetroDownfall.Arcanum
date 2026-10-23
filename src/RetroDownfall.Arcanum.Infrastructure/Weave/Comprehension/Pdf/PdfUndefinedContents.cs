using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

/// <summary>
/// ISO 32000-1 7.3.10 says an indirect reference to an object the file does not define is the null object, so a page whose
/// <c>/Contents</c> (or one element of its <c>/Contents</c> array) refers to nothing is a blank page, and the rest of the
/// file is still good. PdfPig refuses such a page with an <see cref="InvalidOperationException"/> even with lenient parsing,
/// and the refusal would lose every other page of the document.
/// <para>
/// This is the one repair made, and only after PdfPig has refused a page: it walks the page tree once and replaces each
/// <c>/Contents</c> reference that resolves to nothing (or to the null object) with an empty content stream, which is what
/// the specification says the reader sees. A reference to an object that exists but is not a stream is the wrong type, not
/// null, and is left to be refused.
/// </para>
/// </summary>
internal static class PdfUndefinedContents
{
    /// <summary>
    /// Replaces every undefined <c>/Contents</c> reference of every page with an empty stream. Returns whether it replaced
    /// any, which is when reading the refused page again can succeed. Anything the walk itself cannot make sense of means no
    /// repair, so the original refusal stands.
    /// </summary>
    public static bool TryReadAsNull(PdfDocument document, CancellationToken cancellationToken)
    {
        try
        {
            return ReplaceUndefinedReferences(document, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ReplaceUndefinedReferences(PdfDocument document, CancellationToken cancellationToken)
    {
        IPdfTokenScanner scanner = document.Structure.TokenScanner;

        if (!document.Structure.Catalog.CatalogDictionary.Data.TryGetValue("Pages", out IToken? root))
        {
            return false;
        }

        HashSet<IndirectReference> visited = [];

        Stack<IToken> pending = [];

        pending.Push(root);

        bool replaced = false;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IToken token = pending.Pop();

            if (token is IndirectReferenceToken reference && !visited.Add(reference.Data))
            {
                continue;
            }

            if (Resolve(token, scanner) is not DictionaryToken node)
            {
                continue;
            }

            if (node.Data.TryGetValue("Kids", out IToken? kids))
            {
                if (Resolve(kids, scanner) is ArrayToken children)
                {
                    foreach (IToken child in children.Data)
                    {
                        pending.Push(child);
                    }
                }

                continue;
            }

            if (node.Data.TryGetValue("Contents", out IToken? contents))
            {
                replaced |= ReplaceUndefined(contents, document, scanner);
            }
        }

        return replaced;
    }

    private static bool ReplaceUndefined(IToken contents, PdfDocument document, IPdfTokenScanner scanner)
    {
        if (contents is IndirectReferenceToken single)
        {
            return ReplaceIfUndefined(single.Data, document, scanner);
        }

        bool replaced = false;

        if (contents is ArrayToken array)
        {
            foreach (IToken element in array.Data)
            {
                if (element is IndirectReferenceToken elementReference)
                {
                    replaced |= ReplaceIfUndefined(elementReference.Data, document, scanner);
                }
            }
        }

        return replaced;
    }

    private static bool ReplaceIfUndefined(IndirectReference reference, PdfDocument document, IPdfTokenScanner scanner)
    {
        ObjectToken? target = scanner.Get(reference);

        if (target is not null && target.Data is not NullToken)
        {
            return false;
        }

        document.Advanced.ReplaceIndirectObject(reference, new StreamToken(DictionaryToken.Empty, Array.Empty<byte>()));

        return true;
    }

    /// <summary>The token itself, or the object a reference names (null when it names nothing).</summary>
    private static IToken? Resolve(IToken token, IPdfTokenScanner scanner) =>
        token is IndirectReferenceToken reference ? scanner.Get(reference.Data)?.Data : token;
}
