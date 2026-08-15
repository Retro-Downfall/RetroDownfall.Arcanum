-- covenant_state is the one Covenant singleton rewritten during normal operation, so its monotonic
-- counters cannot be guarded by CHECK constraints: a CHECK sees only the row being written and
-- cannot tell an advance from a rewind. Readers capture these counters early in a turn and validate
-- against them later, so a counter that moved backward, or that changed after saturating at the
-- signed maximum, would let a stale reader pass a validation it should have failed.
CREATE TRIGGER IF NOT EXISTS covenant_state_validate_update
BEFORE UPDATE ON covenant_state
BEGIN
    SELECT RAISE(ABORT, 'covenant_state is a singleton and its state key cannot change.')
    WHERE NEW.StateKey <> 1;

    -- A reset or restore stamps a new dataset generation and discards every projection built under
    -- the old one, so the two search counters are allowed to restart exactly then. Within a single
    -- generation they only ever move forward.
    SELECT RAISE(ABORT, 'covenant_state canonical search sequence cannot move backward within a generation.')
    WHERE NEW.DatasetGeneration = OLD.DatasetGeneration
        AND NEW.CanonicalSearchSequence < OLD.CanonicalSearchSequence;

    SELECT RAISE(ABORT, 'covenant_state canonical search sequence is saturated and cannot change.')
    WHERE NEW.DatasetGeneration = OLD.DatasetGeneration
        AND OLD.CanonicalSearchSequence = 9223372036854775807
        AND NEW.CanonicalSearchSequence <> OLD.CanonicalSearchSequence;

    SELECT RAISE(ABORT, 'covenant_state next search row id cannot move backward within a generation.')
    WHERE NEW.DatasetGeneration = OLD.DatasetGeneration
        AND NEW.NextSearchRowId < OLD.NextSearchRowId;

    -- The epochs and the master key version stay outside that exception. A reset that let them
    -- restart would hand a turn holding an epoch captured before the reset a value that still
    -- validates against the dataset that replaced it.
    SELECT RAISE(ABORT, 'covenant_state accelerator epoch cannot move backward.')
    WHERE NEW.AcceleratorEpoch < OLD.AcceleratorEpoch;

    SELECT RAISE(ABORT, 'covenant_state accelerator epoch is saturated and cannot change.')
    WHERE OLD.AcceleratorEpoch = 9223372036854775807
        AND NEW.AcceleratorEpoch <> OLD.AcceleratorEpoch;

    SELECT RAISE(ABORT, 'covenant_state key reclamation epoch cannot move backward.')
    WHERE NEW.KeyReclamationEpoch < OLD.KeyReclamationEpoch;

    SELECT RAISE(ABORT, 'covenant_state key reclamation epoch is saturated and cannot change.')
    WHERE OLD.KeyReclamationEpoch = 9223372036854775807
        AND NEW.KeyReclamationEpoch <> OLD.KeyReclamationEpoch;

    SELECT RAISE(ABORT, 'covenant_state envelope key epoch cannot move backward.')
    WHERE NEW.EnvelopeKeyEpoch < OLD.EnvelopeKeyEpoch;

    SELECT RAISE(ABORT, 'covenant_state envelope key epoch is saturated and cannot change.')
    WHERE OLD.EnvelopeKeyEpoch = 9223372036854775807
        AND NEW.EnvelopeKeyEpoch <> OLD.EnvelopeKeyEpoch;

    SELECT RAISE(ABORT, 'covenant_state envelope master key version cannot move backward.')
    WHERE NEW.EnvelopeMasterKeyVersion < OLD.EnvelopeMasterKeyVersion;

    SELECT RAISE(ABORT, 'covenant_state envelope master key version is saturated and cannot change.')
    WHERE OLD.EnvelopeMasterKeyVersion = 9223372036854775807
        AND NEW.EnvelopeMasterKeyVersion <> OLD.EnvelopeMasterKeyVersion;

    -- The applied deletion sequences are this tier's watermark into the core owner-deletion journal.
    -- Rewinding one would replay deletions the journal has already retired.
    SELECT RAISE(ABORT, 'covenant_state applied campaign deletion sequence cannot move backward.')
    WHERE NEW.AppliedCampaignDeletionSequence < OLD.AppliedCampaignDeletionSequence;

    SELECT RAISE(ABORT, 'covenant_state applied campaign deletion sequence is saturated and cannot change.')
    WHERE OLD.AppliedCampaignDeletionSequence = 9223372036854775807
        AND NEW.AppliedCampaignDeletionSequence <> OLD.AppliedCampaignDeletionSequence;

    SELECT RAISE(ABORT, 'covenant_state applied session deletion sequence cannot move backward.')
    WHERE NEW.AppliedSessionDeletionSequence < OLD.AppliedSessionDeletionSequence;

    SELECT RAISE(ABORT, 'covenant_state applied session deletion sequence is saturated and cannot change.')
    WHERE OLD.AppliedSessionDeletionSequence = 9223372036854775807
        AND NEW.AppliedSessionDeletionSequence <> OLD.AppliedSessionDeletionSequence;
END;
