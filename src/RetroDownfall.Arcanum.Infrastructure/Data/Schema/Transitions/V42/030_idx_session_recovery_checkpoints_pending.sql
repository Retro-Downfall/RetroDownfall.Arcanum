CREATE INDEX IF NOT EXISTS idx_session_recovery_checkpoints_pending
    ON session_recovery_checkpoints(SessionId,CreatedAtUtcTicks DESC,Id)
    WHERE ConsumingClaimId IS NULL;
