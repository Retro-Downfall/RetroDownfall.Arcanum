-- The candidate key covenant_curation_heads carries a composite foreign key to. It proves a head's
-- current version belongs to the same subject and carries the same revision, which no single-column
-- reference to CurationVersionId could.
CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_versions_head_candidate
    ON covenant_curation_versions(CurationVersionId, NormalizedKey, LaneCode, KeyEpoch, Revision);
