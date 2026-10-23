using System.Globalization;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// The SQL spelling of the Comprehension MIME registry and of its normalisation, built once from the
/// code-owned list. A statement compares <see cref="NormalizedExpression"/> of a stored MIME against
/// <see cref="ParameterList"/> and adds <see cref="Parameters"/> to its command, so set membership follows the
/// registry without literals and agrees with <see cref="SessionAttachmentComprehensionPolicy.NormalizeMimeType"/>.
/// </summary>
internal static class SessionAttachmentDocumentMimeSql
{
    /// <summary>
    /// The SQL form of <see cref="SessionAttachmentComprehensionPolicy.NormalizeMimeType"/> for a column or
    /// expression that holds a MIME: cut at the first semicolon, trim only space, tab, LF and CR, and fold
    /// ASCII case (SQLite's <c>lower</c> folds ASCII only). Every statement that classifies a stored MIME uses
    /// this one spelling, so no statement trims what the C# policy does not or the reverse.
    /// </summary>
    internal static string NormalizedExpression(string column) =>
        $"lower(trim(substr({column}, 1, instr({column} || ';', ';') - 1), ' ' || char(9) || char(10) || char(13)))";

    /// <summary>The registry MIME types with the parameter name each one binds to, in registry order.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> Parameters { get; } =
    [
        .. SessionAttachmentComprehensionPolicy.Formats.Select(static (format, index) =>
            new KeyValuePair<string, string>(
                "@documentMime" + index.ToString(CultureInfo.InvariantCulture),
                format.MimeType)),
    ];

    /// <summary>The comma-separated parameter names for an <c>IN (...)</c> or <c>NOT IN (...)</c> list.</summary>
    internal static string ParameterList { get; } = string.Join(", ", Parameters.Select(static pair => pair.Key));
}
