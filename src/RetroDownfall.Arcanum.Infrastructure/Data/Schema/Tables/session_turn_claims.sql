-- Request-level idempotency for every public session-backed turn, without caching a response body.
-- The client turn ID is the caller's Idempotency-Key, so a buffered, streaming, disconnected, or
-- transport-level retry lands on the same claim instead of starting a second turn. The row stores
-- what a retry has to be checked against: the canonical request digest, the execution-dependency
-- digest that freezes everything the turn was planned under, and the durable state it reached.
--
-- The two history fields are deliberately separate. The pre-request watermark and input sensitivity
-- revision are the frozen evidence the turn was planned against and never change; the expected
-- current sensitivity revision is the moving target the guarded maintenance transaction advances by
-- compare-and-swap. Collapsing them into one field would let a maintenance step overwrite the very
-- evidence assistant begin has to compare against.
CREATE TABLE IF NOT EXISTS session_turn_claims (
    ClaimId TEXT NOT NULL PRIMARY KEY,
    OriginInstallationId TEXT NOT NULL,
    OriginRestoreEpoch INTEGER NOT NULL CHECK (OriginRestoreEpoch >= 0),
    ClientTurnId TEXT NOT NULL,
    SessionId TEXT NOT NULL REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    SurfaceCode INTEGER NOT NULL CHECK (SurfaceCode IN (1, 2, 3)),
    RequestDigest BLOB NOT NULL CHECK (length(RequestDigest) = 32),
    DependencyDigest BLOB NOT NULL CHECK (length(DependencyDigest) = 32),
    StateCode INTEGER NOT NULL CHECK (StateCode IN (1, 2, 3, 4, 5, 6)),
    PreRequestHistoryWatermarkUtc TEXT NULL,
    PreRequestHistoryRevision INTEGER NOT NULL CHECK (PreRequestHistoryRevision >= 0),
    InputSensitivityRevision INTEGER NOT NULL CHECK (InputSensitivityRevision >= 0),
    ExpectedCurrentSensitivityRevision INTEGER NOT NULL CHECK (ExpectedCurrentSensitivityRevision >= 0),
    FinalizationReservationId TEXT NOT NULL
        REFERENCES assistant_finalization_capacity_reservations(ReservationId) ON DELETE CASCADE,
    UserEntryId TEXT NULL,
    AssistantEntryId TEXT NULL,
    OwnerBootId TEXT NULL,
    ExecutorId TEXT NULL,
    LeaseDeadlineUtc TEXT NULL,
    HeartbeatAtUtc TEXT NULL,
    CheckpointRevision INTEGER NOT NULL CHECK (CheckpointRevision >= 0),
    -- One bit per maintenance step code, so the four closed steps fit exactly.
    CompletedStepMask INTEGER NOT NULL CHECK (CompletedStepMask BETWEEN 0 AND 15),
    TerminalErrorCode TEXT NULL CHECK (TerminalErrorCode IS NULL OR length(TerminalErrorCode) BETWEEN 1 AND 128),
    TerminalHttpStatus INTEGER NULL CHECK (TerminalHttpStatus IS NULL OR TerminalHttpStatus BETWEEN 100 AND 599),
    TerminalParameterBytes BLOB NULL CHECK (TerminalParameterBytes IS NULL OR length(TerminalParameterBytes) <= 4096),
    TerminalParameterDigest BLOB NULL CHECK (TerminalParameterDigest IS NULL OR length(TerminalParameterDigest) = 32),
    TerminalAtUtc TEXT NULL,
    CreatedAtUtc TEXT NOT NULL,
    -- Exactly one terminal representation is valid per terminal state. Discarded and
    -- RestoredInterrupted reconstruct their typed error envelope from these immutable public fields;
    -- Committed replays through its finalization guard and Erased through its erasure receipt, so
    -- neither may also carry a stored error a replay could return instead.
    CHECK (
        (StateCode IN (4, 6)
            AND TerminalErrorCode IS NOT NULL
            AND TerminalHttpStatus IS NOT NULL
            AND TerminalParameterBytes IS NOT NULL
            AND TerminalParameterDigest IS NOT NULL)
        OR (StateCode IN (1, 2, 3, 5)
            AND TerminalErrorCode IS NULL
            AND TerminalHttpStatus IS NULL
            AND TerminalParameterBytes IS NULL
            AND TerminalParameterDigest IS NULL)
    ),
    -- A terminal claim is timestamped and a live one is not, so a replay can tell a durable answer
    -- from a turn still in flight without consulting a lease.
    CHECK (
        (StateCode IN (3, 4, 5, 6) AND TerminalAtUtc IS NOT NULL)
        OR (StateCode IN (1, 2) AND TerminalAtUtc IS NULL)
    ),
    -- A terminal claim retains no executor authority. Leaving a lease or executor on a finished
    -- claim would let an expired owner renew it and resume a turn that already has an answer, and
    -- RestoredInterrupted explicitly has no executable lease or checkpoint authority at all.
    CHECK (StateCode IN (1, 2) OR (ExecutorId IS NULL AND LeaseDeadlineUtc IS NULL)),
    -- PendingMaintenance has written no Entries yet. Begun, Committed, and Erased all describe a
    -- placeholder that exists, so they name both Entry identities. Discarded and RestoredInterrupted
    -- may be either never-begun or begun-then-terminal.
    CHECK (
        (StateCode = 1 AND UserEntryId IS NULL AND AssistantEntryId IS NULL)
        OR (StateCode IN (2, 3, 5) AND UserEntryId IS NOT NULL AND AssistantEntryId IS NOT NULL)
        OR StateCode IN (4, 6)
    )
);

-- The client turn ID is the sole idempotency identity, scoped by the installation that accepted it.
-- Two installations may legitimately mint the same UUID.
CREATE UNIQUE INDEX IF NOT EXISTS ux_session_turn_claims_origin_client
    ON session_turn_claims(OriginInstallationId, ClientTurnId);

-- At most one live claim per Session, including a Pending claim whose disclosure subject is
-- Orphaned. This is what makes a competing client receive Hub.SessionTurnBusy instead of overtaking
-- maintenance or Entry order.
CREATE UNIQUE INDEX IF NOT EXISTS ux_session_turn_claims_active
    ON session_turn_claims(SessionId)
    WHERE StateCode IN (1, 2);

CREATE UNIQUE INDEX IF NOT EXISTS ux_session_turn_claims_reservation
    ON session_turn_claims(FinalizationReservationId);

CREATE UNIQUE INDEX IF NOT EXISTS ux_session_turn_claims_assistant_entry
    ON session_turn_claims(AssistantEntryId)
    WHERE AssistantEntryId IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_session_turn_claims_session_state
    ON session_turn_claims(SessionId, StateCode);

-- Startup recovery and lease adoption scan live claims by deadline, never the terminal history.
CREATE INDEX IF NOT EXISTS idx_session_turn_claims_lease
    ON session_turn_claims(LeaseDeadlineUtc)
    WHERE StateCode IN (1, 2);
