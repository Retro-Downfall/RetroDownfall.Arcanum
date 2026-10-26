using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>Fresh facts exist in the PDF alone; the later answer cannot be copied from the first reply.</summary>
internal sealed record PdfQualificationChallenge(string FirstSeal, string SecondSeal,
    int FirstQuantity, int FirstRejects, int SecondQuantity, int SecondRejects)
{
    public const string FileName = "evidence.pdf";

    public const string LogicalName = "evidence";

    public const string Metadata = "evidence.pdf evidence PDF qualification";

    public const string FirstPrompt = "Read the attached PDF. Compute quantity minus rejects for each manifest. "
        + "Return the seal of the manifest with the larger net quantity and that computed net. "
        + "Reply exactly SEAL=<winning seal>; NET=<net integer> and nothing else. Do not call tools.";

    public const string SecondPrompt = "Read the pinned PDF again. Extract the secondary inspection seal, "
        + "which was not requested in the previous answer. Reply exactly INSPECTION=<inspection seal> and nothing else. Do not call tools.";

    private int FirstNet => checked(FirstQuantity - FirstRejects);

    private int SecondNet => checked(SecondQuantity - SecondRejects);

    public string InspectionSeal => FirstNet > SecondNet ? SecondSeal : FirstSeal;

    public string FirstExpected => FormattableString.Invariant($"SEAL={(FirstNet > SecondNet ? FirstSeal : SecondSeal)}; NET={Math.Max(FirstNet, SecondNet)}");

    public string SecondExpected => "INSPECTION=" + InspectionSeal;

    public string[] DocumentLines =>
    [
        "Shipping manifests",
        FormattableString.Invariant($"Manifest A: quantity {FirstQuantity}; rejects {FirstRejects}; seal {FirstSeal}"),
        FormattableString.Invariant($"Manifest B: quantity {SecondQuantity}; rejects {SecondRejects}; seal {SecondSeal}"),
        "Secondary inspection seal: " + InspectionSeal,
    ];

    public static PdfQualificationChallenge Create()
    {
        while (true)
        {
            PdfQualificationChallenge challenge = new(
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                RandomNumberGenerator.GetInt32(40, 100), RandomNumberGenerator.GetInt32(1, 15),
                RandomNumberGenerator.GetInt32(40, 100), RandomNumberGenerator.GetInt32(1, 15));

            if (challenge.FirstNet != challenge.SecondNet && challenge.FirstSeal != challenge.SecondSeal) return challenge;
        }
    }

    public bool MatchesFirstAnswer(string answer) => string.Equals(answer.Trim(), FirstExpected, StringComparison.Ordinal);

    public bool MatchesSecondAnswer(string answer) => string.Equals(answer.Trim(), SecondExpected, StringComparison.Ordinal);

    public byte[] BuildPdf()
    {
        if (FirstNet == SecondNet || FirstNet < 0 || SecondNet < 0 || FirstSeal == SecondSeal
            || !ValidSeal(FirstSeal) || !ValidSeal(SecondSeal))
        {
            throw new ArgumentException("PDF qualification needs distinct opaque seals and positive unequal manifest totals.");
        }

        return BuildPdf(DocumentLines);
    }

    private static bool ValidSeal(string value) => value.Length == 32 && value.All(static c => char.IsAsciiHexDigit(c));

    internal static byte[] BuildPdf(IReadOnlyList<string> lines)
    {
        if (lines.Count is < 1 or > 20 || lines.Any(static line => line.Length > 200 || line.Any(static c => c is < ' ' or > '~')))
        {
            throw new ArgumentException("The tiny PDF writer accepts bounded printable ASCII lines only.");
        }

        string content = "BT\n/F1 10 Tf\n40 750 Td\n16 TL\n"
            + string.Join("\nT*\n", lines.Select(static line => "(" + Escape(line) + ") Tj")) + "\nET";

        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            FormattableString.Invariant($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];

        StringBuilder output = new("%PDF-1.4\n");

        List<int> offsets = [];

        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(output.ToString()));

            output.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        int xref = Encoding.ASCII.GetByteCount(output.ToString());

        output.Append("xref\n0 6\n0000000000 65535 f \n");

        foreach (int offset in offsets) output.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");

        output.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(output.ToString());
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
}
