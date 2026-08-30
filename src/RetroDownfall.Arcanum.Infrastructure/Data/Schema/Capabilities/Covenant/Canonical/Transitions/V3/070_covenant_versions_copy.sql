INSERT INTO covenant_versions_replacement (
    VersionId, EntryId, LaneCode, LaneRevision, OperationCode,
    AuthoredContent, CompiledContent, AuthoredHash, RenderedHash,
    CompiledByteCost, RequiredFenceLength, CompilerPolicyVersion, RendererPolicyVersion,
    OriginCode, SourceTurnId, SourceToolCallId, BasePlanDigest, AdmissionReceiptDigest,
    WardReceiptDigest, AuthorizationModeCode, MutationId, RequestIdempotencyDigest,
    AuthorizationDigest, FinalMutationDigest, PredecessorVersionId,
    AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc
)
SELECT
    VersionId, EntryId, LaneCode, LaneRevision, OperationCode,
    AuthoredContent, CompiledContent, AuthoredHash, RenderedHash,
    CompiledByteCost, RequiredFenceLength, CompilerPolicyVersion, RendererPolicyVersion,
    OriginCode, SourceTurnId, SourceToolCallId, BasePlanDigest, AdmissionReceiptDigest,
    WardReceiptDigest, AuthorizationModeCode, MutationId, RequestIdempotencyDigest,
    AuthorizationDigest, FinalMutationDigest, PredecessorVersionId,
    AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc
FROM covenant_versions;
