-- Content-free, keyed evidence that one memory was retired, kept so the next extraction pass cannot
-- re-add what the operator just removed. It deliberately names no memory: the row has to outlive the
-- row it describes, because an operator who retires a memory and then deletes it must not thereby
-- re-enable the extraction they rejected.
--
-- The digest is an HMAC rather than a bare hash for two reasons, both narrow and both stated in full.
-- annal_versions.ContentHash is already a bare SHA-256 of the same bytes, so an unkeyed digest here
-- would be that identical value and the two tables would join into one confirmation oracle rather
-- than none. And deleting the single saga_suppression_key row makes every digest here permanently
-- useless for confirming a guess about content that has since been erased, which one row cannot do
-- for an unkeyed hash.
--
-- The scope columns say which scope the retirement applied to, and are stored rather than derived
-- because the Campaign-scoped memory reset selects on them: that is the operation an operator runs to
-- take one Campaign's memories and the evidence about them together. Deleting the Campaign itself
-- reaches neither, and reaches that Campaign's memories no more than its suppressions - a project
-- deletion removes the project and clears the Session references, and leaves what was extracted inside
-- it exactly where it is.
--
-- CampaignId is settled the way every other stored Campaign identity is, because that reset compares it
-- exactly. The digest is not, and cannot be: it binds whatever spelling the memory row held when the
-- retirement was recorded and has no preimage left to recompute from, so both paths that ask about it
-- ask for the canonical rendering and its lowercase image together.
CREATE TABLE IF NOT EXISTS saga_retirement_suppressions (
    SuppressionDigest BLOB NOT NULL PRIMARY KEY CHECK (length(SuppressionDigest) = 32),
    ScopeKindCode INTEGER NOT NULL CHECK (ScopeKindCode IN (0, 1, 2, 3)),
    CampaignId TEXT NULL,
    RetiredAtUtc TEXT NOT NULL,
    CHECK ((ScopeKindCode = 2 AND CampaignId IS NOT NULL) OR (ScopeKindCode <> 2 AND CampaignId IS NULL))
);
