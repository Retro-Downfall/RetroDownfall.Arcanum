-- The caller-requested identity and frozen request evidence for a durable operation. It is a
-- separate one-to-one table rather than new columns on LongRunningOperations because the Grimoire
-- installs its schema declaratively and has no ALTER TABLE or numbered migration path; adding a
-- normalized child is the only way to extend an existing table without rewriting it.
CREATE TABLE IF NOT EXISTS long_running_operation_request_identities (
    OperationId TEXT NOT NULL PRIMARY KEY CHECK (length(OperationId) > 0),
    -- What the caller asked the operation to be called. Unique, so a replayed apply request resolves
    -- to the one operation it already created instead of starting a second one.
    RequestedOperationId TEXT NOT NULL CHECK (length(RequestedOperationId) > 0),
    ApplyRequestDigest BLOB NOT NULL CHECK (length(ApplyRequestDigest) = 32),
    EffectDigest BLOB NOT NULL CHECK (length(EffectDigest) = 32),
    CreatedAtUtc TEXT NOT NULL,
    -- The row has no independent lifetime. It is written in the same transaction as its operation and
    -- goes away with ordinary operation retention, so no sweep has to know it exists.
    CONSTRAINT FK_long_running_operation_request_identities_operation
        FOREIGN KEY (OperationId) REFERENCES "LongRunningOperations" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_long_running_operation_request_identities_requested
    ON long_running_operation_request_identities(RequestedOperationId);
