-- One erasure fingerprint per erased memory identity: a keyed digest the write chokepoints check so an
-- erased memory cannot be written back. It holds no memory identity, no scope and no timestamp, so a
-- row says neither what was erased nor when, and the table stays out of the UTC-instant inventory.
-- KeyId names the erasure key that computed the digest, so a row written under another key is found
-- as unverifiable rather than silently never matching.
CREATE TABLE IF NOT EXISTS memory_erasure_fingerprints (
    Fingerprint BLOB NOT NULL PRIMARY KEY CHECK (length(Fingerprint) = 32),
    StoreCode INTEGER NOT NULL CHECK (StoreCode IN (1, 2, 3)),
    KeyId BLOB NOT NULL CHECK (length(KeyId) = 16)
);

-- Status and key reset count fingerprints by store and by the key that wrote them.
CREATE INDEX IF NOT EXISTS idx_memory_erasure_fingerprints_store_key
    ON memory_erasure_fingerprints(StoreCode, KeyId);
