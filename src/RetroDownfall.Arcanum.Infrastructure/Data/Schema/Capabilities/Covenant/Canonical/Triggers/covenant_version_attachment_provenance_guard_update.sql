-- Provenance rows are the source coordinates a version was compiled from, and the version commits to
-- them through AttachmentProvenanceCount and AttachmentProvenanceDigest. Editing a row here would
-- break that commitment while the version still looks intact, so the set is written once with its
-- version and never revised.
CREATE TRIGGER IF NOT EXISTS covenant_version_attachment_provenance_guard_update
BEFORE UPDATE ON covenant_version_attachment_provenance
BEGIN
    SELECT RAISE(ABORT, 'covenant_version_attachment_provenance is append-only; existing rows cannot be updated.');
END;
