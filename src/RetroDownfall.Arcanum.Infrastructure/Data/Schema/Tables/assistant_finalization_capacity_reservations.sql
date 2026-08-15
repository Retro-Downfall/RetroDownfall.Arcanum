-- The durable ledger that makes finalization-guard capacity real rather than advisory. A public
-- claim reserves one future guard slot in the same transaction that writes PendingMaintenance, and
-- assistant begin consumes that exact reservation in the same transaction that inserts the
-- placeholder. Because the slot is reserved before any provider work starts, a turn can never reach
-- the point of needing a guard and discover the ceiling is already full; and because the identity
-- is fixed up front, a retry consumes the reservation it already owns instead of allocating a
-- second one.
--
-- Internal, imported, and forked guards have no claim and no waiting period, so they insert an
-- already Consumed row beside their placeholder or finalization.
CREATE TABLE IF NOT EXISTS assistant_finalization_capacity_reservations (
    ReservationId TEXT NOT NULL PRIMARY KEY,
    SessionId TEXT NOT NULL REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    AssistantEntryId TEXT NOT NULL,
    OriginCode INTEGER NOT NULL CHECK (OriginCode IN (1, 2, 3, 4)),
    ClaimId TEXT NULL,
    StateCode INTEGER NOT NULL CHECK (StateCode IN (1, 2, 3)),
    CreatedAtUtc TEXT NOT NULL,
    StateChangedAtUtc TEXT NOT NULL,
    -- A claim identity is present exactly for PublicClaim. An internal, imported, or forked
    -- reservation that carried one could be released by a claim that does not own it; a public one
    -- without a claim could never be released when its claim terminates without ever beginning.
    CHECK (
        (OriginCode = 1 AND ClaimId IS NOT NULL)
        OR (OriginCode IN (2, 3, 4) AND ClaimId IS NULL)
    )
);

-- One reservation per future assistant identity. A second reservation for the same identity would
-- consume two guard slots for the one finalization that identity can ever have.
CREATE UNIQUE INDEX IF NOT EXISTS ux_assistant_finalization_capacity_reservations_assistant
    ON assistant_finalization_capacity_reservations(AssistantEntryId);

CREATE UNIQUE INDEX IF NOT EXISTS ux_assistant_finalization_capacity_reservations_claim
    ON assistant_finalization_capacity_reservations(ClaimId)
    WHERE ClaimId IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_assistant_finalization_capacity_reservations_session_state
    ON assistant_finalization_capacity_reservations(SessionId, StateCode);
