ALTER TABLE guild_events ADD COLUMN SpendEvidenceRevision INTEGER NULL CHECK(SpendEvidenceRevision IS NULL OR (typeof(SpendEvidenceRevision)='integer' AND SpendEvidenceRevision>=1));
