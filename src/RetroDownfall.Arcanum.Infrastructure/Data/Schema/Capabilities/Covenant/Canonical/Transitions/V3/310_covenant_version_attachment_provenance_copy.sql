INSERT INTO covenant_version_attachment_provenance (
    VersionId, Ordinal, AttachmentId, AttachmentVersionIdentity, LogicalKey, ContentHash,
    SourceRangeKindCode, SourceStart, SourceEnd, SourceTurnId, MaterializationReference
)
SELECT
    VersionId, Ordinal, AttachmentId, AttachmentVersionIdentity, LogicalKey, ContentHash,
    SourceRangeKindCode, SourceStart, SourceEnd, SourceTurnId, MaterializationReference
FROM temp.covenant_version_attachment_provenance_v3_staging;
