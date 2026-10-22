using System.Globalization;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// The bound-parameter spelling of the Comprehension MIME registry, built once from the code-owned list.
/// A statement compares <c>lower(trim(...))</c> of a stored MIME against <see cref="ParameterList"/> and adds
/// <see cref="Parameters"/> to its command, so set membership follows the registry without literals.
/// </summary>
internal static class SessionAttachmentDocumentMimeSql
{
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
