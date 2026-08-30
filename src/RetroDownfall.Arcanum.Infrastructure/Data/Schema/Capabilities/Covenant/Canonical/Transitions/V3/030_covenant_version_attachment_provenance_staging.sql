CREATE TEMP TABLE covenant_version_attachment_provenance_v3_staging AS
SELECT
    VersionId, Ordinal, AttachmentId, AttachmentVersionIdentity, LogicalKey, ContentHash,
    SourceRangeKindCode, SourceStart, SourceEnd, SourceTurnId, MaterializationReference
FROM covenant_version_attachment_provenance;
