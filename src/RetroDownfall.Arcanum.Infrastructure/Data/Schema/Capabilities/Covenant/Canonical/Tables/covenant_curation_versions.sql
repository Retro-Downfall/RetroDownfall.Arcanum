-- The append-only record of what an operator curated, and when. Curation is a separate history from
-- covenant_versions rather than an extension of it: a pin is not a version of the operator's text, so
-- it carries no content, no tombstone, and no lane revision of the entry's own, and folding it into a
-- table whose every constraint is about content would need a third arm that stores nothing.
--
-- NOTE: this file is the head definition and its statements are copied character for character into
-- the version-2 transition files. GrimoireSchemaManifestInspector compares normalized sqlite_master
-- text against normalized head text, so a reindent here that the transition does not match reports
-- DefinitionDrift on every evolved installation and on none of the fresh ones.
CREATE TABLE IF NOT EXISTS covenant_curation_versions (
    CurationVersionId TEXT NOT NULL PRIMARY KEY,
    ScopeCode INTEGER NOT NULL CHECK (ScopeCode IN (1, 2)),
    CampaignId TEXT NULL,
    NormalizedKey TEXT NOT NULL CHECK (length(NormalizedKey) BETWEEN 1 AND 128),
    LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
    KeyEpoch INTEGER NOT NULL CHECK (KeyEpoch >= 0),
    CurationKindCode INTEGER NOT NULL CHECK (CurationKindCode IN (1, 2, 3, 4)),
    Revision INTEGER NOT NULL CHECK (Revision > 0),
    PredecessorVersionId TEXT NULL REFERENCES covenant_curation_versions(CurationVersionId),
    MutationId TEXT NOT NULL,
    RequestIdempotencyDigest BLOB NOT NULL CHECK (length(RequestIdempotencyDigest) = 32),
    AuthorizationDigest BLOB NOT NULL CHECK (length(AuthorizationDigest) = 32),
    FinalMutationDigest BLOB NOT NULL CHECK (length(FinalMutationDigest) = 32),
    CreatedAtUtc TEXT NOT NULL,
    CHECK ((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL)),
    -- A mask names a Campaign and the Confirmed lane and nothing else. A Global mask has no broader
    -- scope to fall back from, and the Proposed lane is review-only beside effective Confirmed
    -- content, so masking it would change nothing an operator could observe.
    CHECK (CurationKindCode NOT IN (3, 4) OR (ScopeCode = 2 AND LaneCode = 1)),
    -- Revision one opens a subject's chain and has no predecessor; every later revision links to one.
    CHECK ((Revision = 1 AND PredecessorVersionId IS NULL) OR (Revision > 1 AND PredecessorVersionId IS NOT NULL))
);

-- The candidate key covenant_curation_heads carries a composite foreign key to. It proves a head's
-- current version belongs to the same subject and carries the same revision, which no single-column
-- reference to CurationVersionId could.
CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_versions_head_candidate
    ON covenant_curation_versions(CurationVersionId, NormalizedKey, LaneCode, KeyEpoch, Revision);

CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_versions_global_revision
    ON covenant_curation_versions(NormalizedKey, LaneCode, KeyEpoch, Revision) WHERE CampaignId IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_versions_campaign_revision
    ON covenant_curation_versions(CampaignId, NormalizedKey, LaneCode, KeyEpoch, Revision) WHERE CampaignId IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_versions_mutation
    ON covenant_curation_versions(MutationId);

CREATE INDEX IF NOT EXISTS idx_covenant_curation_versions_campaign_cleanup
    ON covenant_curation_versions(CampaignId);
