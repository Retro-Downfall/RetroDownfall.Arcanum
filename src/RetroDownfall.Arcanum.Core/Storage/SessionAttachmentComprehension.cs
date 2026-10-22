using System.Collections.ObjectModel;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>Closed document eligibility for Comprehension; durable attachment kinds remain unchanged.</summary>
public static class SessionAttachmentComprehensionPolicy
{
    public const string MimeType = "application/vnd.arcanum.comprehension-text";

    public const string FormatName = "Comprehension text container";

    public static IReadOnlyList<string> SupportedMimeTypes { get; } =
        new ReadOnlyCollection<string>([MimeType]);

    public static bool IsEnabled(ArcanumSettings settings) =>
        settings.Features?.AttachmentRetrieval == true && settings.Features?.Comprehension == true;

    public static bool IsEligibleDocument(SessionAttachmentRecord record, ArcanumSettings settings) =>
        IsEnabled(settings)
        && record.Kind == SessionAttachmentKind.Binary
        && string.Equals(NormalizeMimeType(record.MimeType), MimeType, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeMimeType(string mimeType)
    {
        ArgumentNullException.ThrowIfNull(mimeType);

        int separator = mimeType.IndexOf(';', StringComparison.Ordinal);

        return (separator < 0 ? mimeType : mimeType[..separator]).Trim();
    }
}

/// <summary>
/// Pure bytes-to-text implementation for one reviewed document format. Implementations must observe
/// cancellation between bounded operations, avoid allocations proportional to untrusted expanded
/// output, and stop immediately when the sink returns false. They may not access files, services,
/// providers, persistence or ambient authority. Effectful extractors are not supported. The host bounds
/// parser waiting and retained output; it cannot preempt arbitrary in-process code or bound an
/// implementation's private allocations.
/// </summary>
public interface ISessionAttachmentDocumentExtractor
{
    ValueTask ExtractAsync(
        ReadOnlyMemory<byte> bytes,
        ISessionAttachmentDocumentTextSink output,
        CancellationToken cancellationToken);
}

/// <summary>Bounded text output. False means the ceiling was reached and extraction must stop.</summary>
public interface ISessionAttachmentDocumentTextSink
{
    bool TryAppend(ReadOnlySpan<char> text);
}
