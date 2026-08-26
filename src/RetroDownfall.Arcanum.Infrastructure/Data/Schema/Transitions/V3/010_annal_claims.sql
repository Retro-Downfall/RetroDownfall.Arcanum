-- A claim is the identity a durable assertion keeps across every correction. It binds to the exact row
-- that carries its content, and that binding lives here rather than on each version. A Lexicon
-- correction rewrites one row in place, so every revision of that claim names the same
-- lexicon_entries.Id; a per-version binding with a unique index over it would refuse the second
-- revision, and without the index two claims could quietly own one row.
CREATE TABLE IF NOT EXISTS annal_claims (
    ClaimId TEXT NOT NULL PRIMARY KEY,
    SubjectStoreCode INTEGER NOT NULL CHECK (SubjectStoreCode IN (1, 2)),
    SubjectId TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL
);
