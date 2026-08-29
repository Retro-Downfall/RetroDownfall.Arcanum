-- One row, by CHECK rather than by convention. The key is generated lazily inside the first
-- retirement's own transaction, so an installation that never retires anything never holds one.
CREATE TABLE IF NOT EXISTS saga_suppression_key (
    KeyId INTEGER NOT NULL PRIMARY KEY CHECK (KeyId = 1),
    KeyMaterial BLOB NOT NULL CHECK (length(KeyMaterial) = 32),
    CreatedAtUtc TEXT NOT NULL
);
