CREATE INDEX IF NOT EXISTS idx_disclosure_subject_state_unfolded
    ON disclosure_subject_state(OriginInstallationId, SubjectKind, SubjectId)
    WHERE LastFoldedOrdinal < LastAllocatedOrdinal;
