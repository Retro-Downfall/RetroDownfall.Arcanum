-- One immutable statement of one claim: who asserted it, whose memory it is, how sensitive it is, when
-- it was true, and when Arcanum came to hold it.
--
-- Sequence is an INTEGER PRIMARY KEY, which is SQLite's rowid alias, so the engine allocates it inside
-- the insert statement. An explicit MAX(Sequence) + 1 would race under the deferred transaction the
-- Saga insert path opens, and the resulting unique-constraint abort is not a SQLITE_BUSY and would
-- therefore not be retried. That allocation order is what annal_dependencies uses to make a cycle
-- unrepresentable.
--
-- Transaction time has only one column. A version's belief ends at the RecordedAtUtc of the version
-- whose PredecessorVersionId names it, and is open when none does. Storing that end would need an
-- update to a row annal_versions_guard_update forbids updating, and would be a second measurement of a
-- quantity the successor's own timestamp already states. Valid time keeps both ends, because a validity
-- end is a fact the version states about the world rather than a consequence of a later write: a
-- version may say "true until March" on the day it is written and never be superseded at all.
CREATE TABLE IF NOT EXISTS annal_versions (
    Sequence INTEGER PRIMARY KEY,
    VersionId TEXT NOT NULL,
    ClaimId TEXT NOT NULL REFERENCES annal_claims(ClaimId),
    Revision INTEGER NOT NULL CHECK (Revision > 0),
    OperationCode INTEGER NOT NULL CHECK (OperationCode IN (1, 2, 3)),
    OriginCode INTEGER NOT NULL CHECK (OriginCode IN (1, 2, 3, 4)),
    ScopeKindCode INTEGER NOT NULL CHECK (ScopeKindCode IN (0, 1, 2, 3)),
    CampaignId TEXT NULL,
    SensitivityCode INTEGER NOT NULL CHECK (SensitivityCode IN (0, 1)),
    ContentHash BLOB NULL CHECK (ContentHash IS NULL OR length(ContentHash) = 32),
    ValidFromUtc TEXT NOT NULL,
    ValidToUtc TEXT NULL,
    RecordedAtUtc TEXT NOT NULL,
    PredecessorVersionId TEXT NULL REFERENCES annal_versions(VersionId) ON DELETE CASCADE,
    SourceSessionId TEXT NULL,
    -- A Campaign-scoped version names its Campaign, and no other kind borrows one. The two unresolved
    -- kinds are deliberately reachable here: a version that copies an unresolved subject's scope has to
    -- be able to say so rather than rounding it up to installation-global authority.
    CHECK ((ScopeKindCode = 2 AND CampaignId IS NOT NULL) OR (ScopeKindCode <> 2 AND CampaignId IS NULL)),
    -- A retirement is a tombstone and binds to no content. Letting one carry a hash would leave a record
    -- of exactly the bytes the retirement was meant to stop standing behind.
    CHECK ((OperationCode = 3 AND ContentHash IS NULL) OR (OperationCode <> 3 AND ContentHash IS NOT NULL)),
    -- Revision one begins a claim and has no predecessor; every later revision links to exactly one.
    CHECK ((Revision = 1 AND PredecessorVersionId IS NULL) OR (Revision > 1 AND PredecessorVersionId IS NOT NULL)),
    -- Both columns are round-trip "o"-format UTC text, which orders lexicographically, so this compares
    -- instants rather than a coincidence of formatting.
    CHECK (ValidToUtc IS NULL OR ValidToUtc >= ValidFromUtc),
    -- A version nobody attested cannot name a Session as its source.
    CHECK (OriginCode <> 4 OR SourceSessionId IS NULL)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_version
ON annal_versions(VersionId);

-- The candidate key annal_dependencies carries both of its composite foreign keys to. Binding an edge's
-- recorded sequence to the version it names is what stops the ordering check from being told a lie.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_sequence_candidate
ON annal_versions(VersionId, Sequence);

CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_claim_revision
ON annal_versions(ClaimId, Revision);

-- The candidate key annal_heads carries a composite foreign key to. A plain reference to VersionId would
-- let a head adopt a version belonging to another claim, or one whose revision and operation disagree
-- with the head's own columns.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_head_candidate
ON annal_versions(VersionId, ClaimId, Revision, OperationCode);

-- Reading one claim's history in order, which is the shape every consumer of this table wants.
CREATE INDEX IF NOT EXISTS idx_annal_versions_claim_recorded
ON annal_versions(ClaimId, RecordedAtUtc);

-- The derived transaction-time end resolves a version's successor through this column, and an erasure
-- walks the same edge.
CREATE INDEX IF NOT EXISTS idx_annal_versions_predecessor
ON annal_versions(PredecessorVersionId);
