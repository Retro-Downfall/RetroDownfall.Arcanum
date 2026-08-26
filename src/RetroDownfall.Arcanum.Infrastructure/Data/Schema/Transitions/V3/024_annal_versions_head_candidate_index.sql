-- The candidate key annal_heads carries a composite foreign key to. A plain reference to VersionId would
-- let a head adopt a version belonging to another claim, or one whose revision and operation disagree
-- with the head's own columns.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_head_candidate
ON annal_versions(VersionId, ClaimId, Revision, OperationCode);
