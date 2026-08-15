-- At most four checkpoint rows per claim, one for each closed maintenance step. A Committed row is
-- written in the same transaction as the summary, title, Saga, or Lexicon output and its
-- sensitivity label, so recovery can verify the referenced artifact and reuse it instead of calling
-- the provider again. A provider result lost before that transaction is simply not a checkpoint,
-- which is why Prepared and Failed rows carry no reusable output identity.
--
-- The four-row bound is structural: the primary key pairs the claim with a step code drawn from a
-- closed set of four, so no caller can accumulate checkpoints for a step that does not exist.
CREATE TABLE IF NOT EXISTS session_turn_maintenance_steps (
    ClaimId TEXT NOT NULL REFERENCES session_turn_claims(ClaimId) ON DELETE CASCADE,
    StepCode INTEGER NOT NULL CHECK (StepCode IN (1, 2, 3, 4)),
    CheckpointStateCode INTEGER NOT NULL CHECK (CheckpointStateCode IN (1, 2, 3)),
    InputHistoryRevision INTEGER NOT NULL CHECK (InputHistoryRevision >= 0),
    InputSensitivityRevision INTEGER NOT NULL CHECK (InputSensitivityRevision >= 0),
    ProviderCallDigest BLOB NULL CHECK (ProviderCallDigest IS NULL OR length(ProviderCallDigest) = 32),
    DisclosureReceiptDigest BLOB NULL CHECK (DisclosureReceiptDigest IS NULL OR length(DisclosureReceiptDigest) = 32),
    OutputArtifactKindCode INTEGER NULL CHECK (OutputArtifactKindCode IS NULL OR OutputArtifactKindCode IN (3, 5, 6, 7)),
    OutputArtifactId TEXT NULL,
    OutputManifestDigest BLOB NULL CHECK (OutputManifestDigest IS NULL OR length(OutputManifestDigest) = 32),
    OutputSensitivityDigest BLOB NULL CHECK (OutputSensitivityDigest IS NULL OR length(OutputSensitivityDigest) = 32),
    AppliedTargetRevision INTEGER NULL CHECK (AppliedTargetRevision IS NULL OR AppliedTargetRevision >= 0),
    CheckpointRevision INTEGER NOT NULL CHECK (CheckpointRevision >= 0),
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (ClaimId, StepCode),
    -- Only a Committed checkpoint has an output to reuse, and it needs the complete identity plus
    -- both digests to prove the artifact recovery finds is the one this step produced. A partial
    -- output would let recovery adopt an artifact it cannot verify.
    CHECK (
        (CheckpointStateCode = 2
            AND OutputArtifactKindCode IS NOT NULL
            AND OutputArtifactId IS NOT NULL
            AND OutputManifestDigest IS NOT NULL
            AND OutputSensitivityDigest IS NOT NULL
            AND AppliedTargetRevision IS NOT NULL)
        OR (CheckpointStateCode IN (1, 3)
            AND OutputArtifactKindCode IS NULL
            AND OutputArtifactId IS NULL
            AND OutputManifestDigest IS NULL
            AND OutputSensitivityDigest IS NULL
            AND AppliedTargetRevision IS NULL)
    ),
    -- Each step produces exactly one kind of artifact: Summary produces a Summary, Title produces a
    -- SessionTitle, Saga a Saga, and Lexicon a Lexicon. A mismatched pair would let a recovered
    -- checkpoint apply one step's artifact to another step's target.
    CHECK (
        OutputArtifactKindCode IS NULL
        OR (StepCode = 1 AND OutputArtifactKindCode = 3)
        OR (StepCode = 2 AND OutputArtifactKindCode = 5)
        OR (StepCode = 3 AND OutputArtifactKindCode = 6)
        OR (StepCode = 4 AND OutputArtifactKindCode = 7)
    )
);

CREATE INDEX IF NOT EXISTS idx_session_turn_maintenance_steps_state
    ON session_turn_maintenance_steps(ClaimId, CheckpointStateCode);
