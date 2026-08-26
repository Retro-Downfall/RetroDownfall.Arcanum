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
-- The scope columns restate what the digest was computed over. That is not a second measurement of
-- the digest: it is the only way a Campaign deletion can find its own suppressions, and a suppression
-- that outlived the Campaign identity it applied to would suppress extraction for an owner that no
-- longer exists with nothing left to remove it.
CREATE TABLE IF NOT EXISTS saga_retirement_suppressions (
    SuppressionDigest BLOB NOT NULL PRIMARY KEY CHECK (length(SuppressionDigest) = 32),
    ScopeKindCode INTEGER NOT NULL CHECK (ScopeKindCode IN (0, 1, 2, 3)),
    CampaignId TEXT NULL,
    RetiredAtUtc TEXT NOT NULL,
    CHECK ((ScopeKindCode = 2 AND CampaignId IS NOT NULL) OR (ScopeKindCode <> 2 AND CampaignId IS NULL))
);
