using System.Globalization;
using System.Xml;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>Extracts the text a reader sees in an OpenDocument presentation package (<c>.odp</c>).</summary>
/// <remarks>
/// <para>
/// Each visible <c>draw:page</c> is introduced by <c>Slide &lt;n&gt;</c>, where <c>n</c> is the 1-based position of the
/// page in the presentation: a hidden page is skipped but still counts. Speaker notes, presentation-level
/// declarations, master pages and layouts are never projected (plan decision D8). Only
/// <c>META-INF/manifest.xml</c>, <c>content.xml</c> and (when declared) <c>styles.xml</c> are ever read.
/// </para>
/// </remarks>
internal sealed class OpenDocumentPresentationExtractor : ISessionAttachmentDocumentExtractor
{
    public ValueTask ExtractAsync(
        ReadOnlyMemory<byte> bytes,
        ISessionAttachmentDocumentTextSink output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        OpenDocumentPackageContent content = OpenDocumentPackageContent.Load(
            bytes,
            SessionAttachmentComprehensionPolicy.OpenDocumentPresentationMimeType,
            "presentation",
            cancellationToken);

        using BoundedXmlReader xml = content.OpenBody("presentation", cancellationToken);

        OpenDocumentContentWalker walker = new(xml, content.Styles, output);

        WalkPages(xml, content.Styles, walker);

        walker.EmitNotes();

        return ValueTask.CompletedTask;
    }

    /// <summary>Projects the pages under the <c>office:presentation</c> element the reader is on.</summary>
    private static void WalkPages(BoundedXmlReader xml, OpenDocumentStyleTable styles, OpenDocumentContentWalker walker)
    {
        XmlReader reader = xml.Reader;

        if (reader.IsEmptyElement)
        {
            return;
        }

        xml.Read();

        int slide = 0;

        while (!walker.Stopped && !reader.EOF && reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                xml.Read();

                continue;
            }

            if (reader.NamespaceURI != OpenDocumentNames.Draw || reader.LocalName != "page")
            {
                // Header, footer and date declarations, show settings and the like are not slide content.
                xml.SkipElement();

                continue;
            }

            slide++;

            string? styleName = reader.GetAttribute("style-name", OpenDocumentNames.Draw);

            if (styles.IsHidden(OpenDocumentStyleTable.Switch.Page, OpenDocumentStyleTable.DrawingPageFamily, styleName))
            {
                xml.SkipElement();

                continue;
            }

            if (!walker.Emit("Slide " + slide.ToString(CultureInfo.InvariantCulture) + "\n"))
            {
                return;
            }

            walker.WalkElement();
        }
    }
}
