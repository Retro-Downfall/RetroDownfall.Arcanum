CREATE TABLE covenant_versions_replacement (
    VersionId TEXT NOT NULL PRIMARY KEY,
    EntryId TEXT NOT NULL REFERENCES covenant_entries(EntryId),
    LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
    LaneRevision INTEGER NOT NULL CHECK (LaneRevision > 0),
    OperationCode INTEGER NOT NULL CHECK (OperationCode IN (1, 2)),
    AuthoredContent TEXT NULL,
    CompiledContent TEXT NULL,
    AuthoredHash BLOB NULL CHECK (AuthoredHash IS NULL OR length(AuthoredHash) = 32),
    RenderedHash BLOB NULL CHECK (RenderedHash IS NULL OR length(RenderedHash) = 32),
    CompiledByteCost INTEGER NOT NULL CHECK (CompiledByteCost >= 0),
    RequiredFenceLength INTEGER NOT NULL CHECK (RequiredFenceLength >= 0),
    CompilerPolicyVersion INTEGER NOT NULL CHECK (CompilerPolicyVersion > 0),
    RendererPolicyVersion INTEGER NOT NULL CHECK (RendererPolicyVersion > 0),
    OriginCode INTEGER NOT NULL CHECK (OriginCode IN (1, 2, 3)),
    SourceTurnId TEXT NULL,
    SourceToolCallId TEXT NULL,
    BasePlanDigest BLOB NULL CHECK (BasePlanDigest IS NULL OR length(BasePlanDigest) = 32),
    AdmissionReceiptDigest BLOB NULL CHECK (AdmissionReceiptDigest IS NULL OR length(AdmissionReceiptDigest) = 32),
    WardReceiptDigest BLOB NULL CHECK (WardReceiptDigest IS NULL OR length(WardReceiptDigest) = 32),
    AuthorizationModeCode INTEGER NULL CHECK (AuthorizationModeCode IS NULL OR AuthorizationModeCode IN (1, 2, 3)),
    MutationId TEXT NOT NULL,
    RequestIdempotencyDigest BLOB NOT NULL CHECK (length(RequestIdempotencyDigest) = 32),
    AuthorizationDigest BLOB NOT NULL CHECK (length(AuthorizationDigest) = 32),
    FinalMutationDigest BLOB NOT NULL CHECK (length(FinalMutationDigest) = 32),
    PredecessorVersionId TEXT NULL REFERENCES covenant_versions_replacement(VersionId),
    AttachmentProvenanceCount INTEGER NOT NULL CHECK (AttachmentProvenanceCount >= 0),
    AttachmentProvenanceDigest BLOB NOT NULL CHECK (length(AttachmentProvenanceDigest) = 32),
    CreatedAtUtc TEXT NOT NULL,
    CHECK (
        (OperationCode = 1
            AND AuthoredContent IS NOT NULL
            AND CompiledContent IS NOT NULL
            AND AuthoredHash IS NOT NULL
            AND RenderedHash IS NOT NULL)
        OR (OperationCode = 2
            AND AuthoredContent IS NULL
            AND CompiledContent IS NULL
            AND AuthoredHash IS NULL
            AND RenderedHash IS NULL
            AND CompiledByteCost = 0)
    ),
    CHECK (OriginCode <> 2 OR LaneCode = 2),
    CHECK (OriginCode = 1 OR SourceTurnId IS NOT NULL),
    CHECK (
        (OriginCode = 3 AND (
            (WardReceiptDigest IS NULL AND AuthorizationModeCode IS NULL)
            OR (WardReceiptDigest IS NOT NULL AND AuthorizationModeCode IS NOT NULL AND AuthorizationModeCode IN (2, 3))
        ))
        OR (OriginCode <> 3 AND WardReceiptDigest IS NULL AND AuthorizationModeCode IS NULL)
    ),
    CHECK (
        (OriginCode IN (2, 3) AND SourceToolCallId IS NOT NULL AND BasePlanDigest IS NOT NULL)
        OR (OriginCode = 1 AND SourceToolCallId IS NULL AND BasePlanDigest IS NULL)
    ),
    CHECK ((LaneRevision = 1 AND PredecessorVersionId IS NULL) OR (LaneRevision > 1 AND PredecessorVersionId IS NOT NULL))
);
