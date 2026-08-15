CREATE TABLE IF NOT EXISTS covenant_versions (
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
    PredecessorVersionId TEXT NULL REFERENCES covenant_versions(VersionId),
    AttachmentProvenanceCount INTEGER NOT NULL CHECK (AttachmentProvenanceCount >= 0),
    AttachmentProvenanceDigest BLOB NOT NULL CHECK (length(AttachmentProvenanceDigest) = 32),
    CreatedAtUtc TEXT NOT NULL,
    -- A Set carries content and both hashes; a Retire is a tombstone and carries neither. Storing
    -- one without the other would let a retirement resurrect text the operator asked to remove.
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
            AND RenderedHash IS NULL)
    ),
    -- An agent-authored version names the turn it came from. The operator origin has no turn, so
    -- only the two agent origins require one. (The no-Global-Proposed rule spans entries and
    -- versions, so it cannot be a single-table CHECK; covenant_heads_validate_* enforces it.)
    CHECK (OriginCode = 1 OR SourceTurnId IS NOT NULL),
    -- AgentApproved retirement is the one origin that requires Ward evidence and a Ward mode.
    CHECK (
        (OriginCode = 3 AND WardReceiptDigest IS NOT NULL AND AuthorizationModeCode IN (2, 3))
        OR (OriginCode <> 3 AND WardReceiptDigest IS NULL)
    ),
    -- An agent-authored version names the turn and tool call that produced it; an operator one does
    -- not have either, and must not borrow them.
    CHECK (
        (OriginCode IN (2, 3) AND SourceToolCallId IS NOT NULL AND BasePlanDigest IS NOT NULL)
        OR (OriginCode = 1 AND SourceToolCallId IS NULL AND BasePlanDigest IS NULL)
    ),
    -- Revision one is the first version in its lane and has no predecessor; every later revision
    -- links to exactly one.
    CHECK ((LaneRevision = 1 AND PredecessorVersionId IS NULL) OR (LaneRevision > 1 AND PredecessorVersionId IS NOT NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_versions_entry_lane_revision
    ON covenant_versions(EntryId, LaneCode, LaneRevision);

-- The candidate key covenant_heads carries a composite foreign key to. It proves a head's current
-- version belongs to the same entry and lane and carries the same revision and operation, which no
-- single-column reference to VersionId could.
CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_versions_head_candidate
    ON covenant_versions(VersionId, EntryId, LaneCode, LaneRevision, OperationCode);

CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_versions_mutation
    ON covenant_versions(MutationId);

CREATE INDEX IF NOT EXISTS idx_covenant_versions_entry_created
    ON covenant_versions(EntryId, CreatedAtUtc);

CREATE INDEX IF NOT EXISTS idx_covenant_versions_source_turn
    ON covenant_versions(SourceTurnId);
