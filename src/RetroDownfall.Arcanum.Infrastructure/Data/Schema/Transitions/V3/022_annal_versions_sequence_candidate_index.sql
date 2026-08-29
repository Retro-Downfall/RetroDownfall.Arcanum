-- The candidate key annal_dependencies carries both of its composite foreign keys to. Binding an edge's
-- recorded sequence to the version it names is what stops the ordering check from being told a lie.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_sequence_candidate
ON annal_versions(VersionId, Sequence);
