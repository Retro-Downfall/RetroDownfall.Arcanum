-- A label is the immutable evidence that one exact artifact revision is Covenant derived. Editing it
-- in place is how a downgrade would happen: the sensitivity code, the generation provenance, or the
-- content digest could be rewritten to describe cleaner bytes than the artifact actually holds,
-- and every reader that trusts the label would then trust the artifact. A changed artifact gets a
-- new label written beside its new revision instead.
CREATE TRIGGER IF NOT EXISTS artifact_sensitivity_guard_update
BEFORE UPDATE ON artifact_sensitivity
BEGIN
    SELECT RAISE(ABORT, 'artifact_sensitivity rows are immutable; a new artifact revision needs a new label.');
END;
