-- One keyed subject digest per row an erasure removed: each Saga memory (twins included), the Lexicon
-- entry, or the Covenant entry. The digest binds the store and the row id, so it can answer "was this
-- row erased" without naming the row, and a re-created identity gets a new row id and a fresh digest.
-- Subjects live and die with their receipt, and like it they carry no timestamp.
CREATE TABLE IF NOT EXISTS memory_erasure_receipt_subjects (
    MutationId TEXT NOT NULL REFERENCES memory_erasure_receipts(MutationId) ON DELETE CASCADE,
    SubjectDigest BLOB NOT NULL CHECK (length(SubjectDigest) = 32),
    PRIMARY KEY (MutationId, SubjectDigest)
);

-- A request naming an erased row is answered by its digest, without knowing which receipt holds it.
CREATE INDEX IF NOT EXISTS idx_memory_erasure_receipt_subjects_digest
    ON memory_erasure_receipt_subjects(SubjectDigest);
