-- The guarded current pointer for one curation subject. The subject is a scoped key and lane, and it
-- deliberately does not require a row in covenant_heads: masking a Global key inside a Campaign is
-- exactly the case where that Campaign holds no entry, no head, and no version for the key.
--
-- KeyEpoch is part of the subject rather than a recorded detail. A key that is retired, reclaimed,
-- and later re-created is a different key wearing an old name, and a pin recorded against the earlier
-- epoch must be inert rather than silently applying to content the operator never saw.
--
-- NOTE: this file is the head definition and its statements are copied character for character into
-- the version-2 transition files.
CREATE TABLE IF NOT EXISTS covenant_curation_heads (
    ScopeCode INTEGER NOT NULL CHECK (ScopeCode IN (1, 2)),
    CampaignId TEXT NULL,
    NormalizedKey TEXT NOT NULL CHECK (length(NormalizedKey) BETWEEN 1 AND 128),
    LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
    KeyEpoch INTEGER NOT NULL CHECK (KeyEpoch >= 0),
    IsPinned INTEGER NOT NULL CHECK (IsPinned IN (0, 1)),
    IsMasked INTEGER NOT NULL CHECK (IsMasked IN (0, 1)),
    CurrentVersionId TEXT NOT NULL,
    CurrentRevision INTEGER NOT NULL CHECK (CurrentRevision > 0),
    UpdatedAtUtc TEXT NOT NULL,
    -- The composite reference is the point: a plain reference to CurationVersionId would let a head
    -- adopt a version belonging to another subject, or one whose revision disagrees with its own.
    FOREIGN KEY (CurrentVersionId, NormalizedKey, LaneCode, KeyEpoch, CurrentRevision)
        REFERENCES covenant_curation_versions(CurationVersionId, NormalizedKey, LaneCode, KeyEpoch, Revision),
    CHECK ((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL)),
    -- Only a Campaign Confirmed subject can be masked, on the same terms the version table states.
    CHECK (IsMasked = 0 OR (ScopeCode = 2 AND LaneCode = 1))
);
