using System.Text;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

/// <summary>Reads the PDF fixtures documented in <c>TestData/Comprehension/Pdf/README.md</c>.</summary>
internal static class PdfFixtures
{
    public static byte[] Read(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", "Pdf", fileName));

    public static string ReadText(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", "Pdf", fileName), Encoding.UTF8);

    /// <summary>The text with every whitespace character removed, for comparing layouts that wrap or pad differently.</summary>
    public static string WithoutWhitespace(string text) => string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
}
