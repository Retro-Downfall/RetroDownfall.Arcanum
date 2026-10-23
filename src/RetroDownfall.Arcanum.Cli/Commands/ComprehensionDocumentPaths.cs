using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// The one CLI decision, and its wording, for a local Comprehension document offered as inline text. A document
/// is a package, not text: read as UTF-8 it would send the model raw archive bytes, and an uncompressed XML part
/// would carry the hidden runs, comments and notes Comprehension withholds. So <c>arcanum run --with @path</c> and
/// Command Center's <c>@path</c> and <c>/attach</c> never stage one, and each names the supported route instead:
/// store it as a session attachment and reference that, so the server's Comprehension rules decide what the model
/// reads.
/// </summary>
internal static class ComprehensionDocumentPaths
{
    /// <summary>Why a Comprehension document is never staged: it is a package, not text.</summary>
    internal const string Reason = "a Comprehension document, which is never sent as inline text";

    /// <summary>The supported route for a Comprehension document.</summary>
    internal const string Guidance =
        "store it with \"arcanum attachment add <path>\" and reference it by the attachment's id with "
        + "\"arcanum run --attachment <id>\" (in Command Center, \"/attachments add <name>\")";

    /// <summary>
    /// Whether the file's extension maps to a document the Comprehension registry reads. The decision is the
    /// shared detector's and the shared registry's, so no second extension list exists here, and it is made
    /// from the name alone: the file is not opened, so a document is refused without a byte of it being read.
    /// </summary>
    internal static bool IsComprehensionDocument(string path) =>
        SessionAttachmentComprehensionPolicy.TryGetFormat(
            AttachmentMimeDetector.Detect(ReadOnlySpan<byte>.Empty, Path.GetFileName(path)),
            out _);
}
