namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Closed set of deterministic reasons a reviewed document package is refused. Each value maps to
/// one code-owned status and reason, so no entry name, part name, XML text, or framework message
/// can reach a status field or log through a rejection.
/// </summary>
internal enum DocumentPackageRejection
{
    Encrypted,

    UnsupportedContainer,

    NotZip,

    EntryBounds,

    ExpansionBounds,

    UnsafeEntryName,

    MissingPart,

    FormatMismatch,

    MalformedXml,

    StructureBounds,
}

/// <summary>Thrown by a pure document extractor to refuse its input with a closed rejection.</summary>
internal sealed class DocumentPackageRejectedException(DocumentPackageRejection rejection)
    : Exception(DocumentPackageRejections.Reason(rejection))
{
    public DocumentPackageRejection Rejection { get; } = rejection;

    public SessionAttachmentExtractionResult ToResult() => new(
        DocumentPackageRejections.Status(Rejection),
        string.Empty,
        WasTruncated: false,
        DocumentPackageRejections.Reason(Rejection));
}

internal static class DocumentPackageRejections
{
    public static SessionAttachmentExtractionStatus Status(DocumentPackageRejection rejection) => rejection switch
    {
        DocumentPackageRejection.Encrypted or DocumentPackageRejection.UnsupportedContainer =>
            SessionAttachmentExtractionStatus.NotEligible,
        _ => SessionAttachmentExtractionStatus.Failed,
    };

    public static string Reason(DocumentPackageRejection rejection) => rejection switch
    {
        DocumentPackageRejection.Encrypted => "Encrypted document packages are not supported.",
        DocumentPackageRejection.UnsupportedContainer => "Document package uses an unsupported ZIP feature.",
        DocumentPackageRejection.NotZip => "Document package is not a valid ZIP container.",
        DocumentPackageRejection.EntryBounds => "Document package exceeds its entry bounds.",
        DocumentPackageRejection.ExpansionBounds => "Document package exceeds its expansion bounds.",
        DocumentPackageRejection.UnsafeEntryName => "Document package contains an unsafe or duplicate entry name.",
        DocumentPackageRejection.MissingPart => "Document package is missing a required part.",
        DocumentPackageRejection.FormatMismatch => "Document package does not match its declared format.",
        DocumentPackageRejection.MalformedXml => "Document part is not well-formed XML or declares a DTD.",
        DocumentPackageRejection.StructureBounds => "Document structure exceeds its bounds.",
        _ => "Document extraction failed.",
    };
}
