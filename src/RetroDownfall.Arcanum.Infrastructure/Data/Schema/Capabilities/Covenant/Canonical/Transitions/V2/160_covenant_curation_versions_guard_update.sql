-- A curation version is the immutable record of one accepted change, and later revisions chain to it
-- by PredecessorVersionId. Editing a row in place would leave the receipt already issued against it
-- describing a state that no longer exists. A reversal is written as the next revision.
CREATE TRIGGER IF NOT EXISTS covenant_curation_versions_guard_update
BEFORE UPDATE ON covenant_curation_versions
BEGIN
    SELECT RAISE(ABORT, 'covenant_curation_versions is append-only; existing rows cannot be updated.');
END;
