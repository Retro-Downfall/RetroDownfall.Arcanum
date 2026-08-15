-- Everything a retry is authenticated against has to survive the turn that is executing. The client
-- identity, request digest, and dependency digest decide whether a second request is the same
-- logical turn or an idempotency conflict, so an executing turn that could edit them would be able
-- to make any later request match itself. The frozen pre-request history and input sensitivity
-- revisions are the evidence assistant begin compares against, which is exactly why the separately
-- mutable expected-current revision exists: maintenance advances the expectation without touching
-- the evidence.
--
-- The state graph is closed and forward-only. A terminal claim already returned its answer, so
-- reopening one would let a request that has a durable result run again; the single exception is a
-- committed claim becoming an erasure tombstone, which changes the answer from content to
-- Covenant.ArtifactErased and never back.
CREATE TRIGGER IF NOT EXISTS session_turn_claims_validate_update
BEFORE UPDATE ON session_turn_claims
BEGIN
    SELECT RAISE(ABORT, 'A discarded, erased, or restore-interrupted claim is terminal and cannot be rewritten.')
    WHERE OLD.StateCode IN (4, 5, 6);

    SELECT RAISE(ABORT, 'session_turn_claims accepts only its closed forward state edges.')
    WHERE NOT (
        (OLD.StateCode = 1 AND NEW.StateCode IN (1, 2, 4, 6))
        OR (OLD.StateCode = 2 AND NEW.StateCode IN (2, 3, 4, 6))
        OR (OLD.StateCode = 3 AND NEW.StateCode IN (3, 5))
    );

    SELECT RAISE(ABORT, 'A session turn claim cannot change its identity.')
    WHERE NEW.ClaimId <> OLD.ClaimId
        OR NEW.OriginInstallationId <> OLD.OriginInstallationId
        OR NEW.OriginRestoreEpoch <> OLD.OriginRestoreEpoch
        OR NEW.ClientTurnId <> OLD.ClientTurnId
        OR NEW.CreatedAtUtc <> OLD.CreatedAtUtc;

    SELECT RAISE(ABORT, 'A session turn claim cannot change the Session it is bound to.')
    WHERE NEW.SessionId <> OLD.SessionId;

    SELECT RAISE(ABORT, 'A session turn claim cannot change the request it was accepted for.')
    WHERE NEW.SurfaceCode <> OLD.SurfaceCode
        OR NEW.RequestDigest <> OLD.RequestDigest
        OR NEW.DependencyDigest <> OLD.DependencyDigest;

    SELECT RAISE(ABORT, 'A session turn claim cannot change the pre-request evidence it was planned against.')
    WHERE NEW.PreRequestHistoryWatermarkUtc IS NOT OLD.PreRequestHistoryWatermarkUtc
        OR NEW.PreRequestHistoryRevision <> OLD.PreRequestHistoryRevision
        OR NEW.InputSensitivityRevision <> OLD.InputSensitivityRevision;

    SELECT RAISE(ABORT, 'A session turn claim cannot change the finalization capacity it reserved.')
    WHERE NEW.FinalizationReservationId <> OLD.FinalizationReservationId;

    -- The expectation tracks a projection that only moves forward, so a lowered expectation could
    -- make a stale plan pass the compare-and-swap that is supposed to fail it.
    SELECT RAISE(ABORT, 'The expected current sensitivity revision of a claim cannot move backward.')
    WHERE NEW.ExpectedCurrentSensitivityRevision < OLD.ExpectedCurrentSensitivityRevision;

    SELECT RAISE(ABORT, 'The checkpoint revision of a claim cannot move backward.')
    WHERE NEW.CheckpointRevision < OLD.CheckpointRevision;

    -- Completed steps accumulate. Clearing a bit would offer recovery a step to redo whose output
    -- artifact and watermark were already committed.
    SELECT RAISE(ABORT, 'The completed step mask of a claim can only gain steps.')
    WHERE (NEW.CompletedStepMask | OLD.CompletedStepMask) <> NEW.CompletedStepMask;

    -- Entry identities are recorded once, by the assistant-begin transaction that created them.
    SELECT RAISE(ABORT, 'A session turn claim cannot rebind an Entry identity it already recorded.')
    WHERE (OLD.UserEntryId IS NOT NULL AND NEW.UserEntryId IS NOT OLD.UserEntryId)
        OR (OLD.AssistantEntryId IS NOT NULL AND NEW.AssistantEntryId IS NOT OLD.AssistantEntryId);

    SELECT RAISE(ABORT, 'A committed claim may only advance to its erasure tombstone state.')
    WHERE OLD.StateCode = 3
        AND (NEW.ExpectedCurrentSensitivityRevision <> OLD.ExpectedCurrentSensitivityRevision
            OR NEW.CheckpointRevision <> OLD.CheckpointRevision
            OR NEW.CompletedStepMask <> OLD.CompletedStepMask
            OR NEW.OwnerBootId IS NOT OLD.OwnerBootId
            OR NEW.HeartbeatAtUtc IS NOT OLD.HeartbeatAtUtc);
END;
