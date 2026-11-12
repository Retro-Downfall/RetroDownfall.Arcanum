CREATE TRIGGER IF NOT EXISTS guild_spend_sources_guard_update
BEFORE UPDATE ON guild_spend_sources
WHEN NEW.EffectId IS NOT OLD.EffectId OR NEW.SourceId IS NOT OLD.SourceId OR NEW.SourceKindCode IS NOT OLD.SourceKindCode
    OR NEW.SourceVersion IS NOT OLD.SourceVersion OR NEW.InstallationGeneration IS NOT OLD.InstallationGeneration OR NEW.AuthorityEpoch IS NOT OLD.AuthorityEpoch OR NEW.AuthorityKeyId IS NOT OLD.AuthorityKeyId
    OR NEW.Endpoint IS NOT OLD.Endpoint OR NEW.Provider IS NOT OLD.Provider OR NEW.Model IS NOT OLD.Model
    OR NEW.PricingSnapshotJson IS NOT OLD.PricingSnapshotJson OR NEW.CredentialFingerprint IS NOT OLD.CredentialFingerprint
    OR (NEW.GenerationId IS NOT OLD.GenerationId AND
        (OLD.GenerationId IS NOT NULL OR OLD.InitialSnapshot IS NOT NULL OR NEW.InitialSnapshot IS NULL))
    OR (OLD.InitialSnapshot IS NOT NULL AND (NEW.InitialSnapshot IS NOT OLD.InitialSnapshot OR NEW.InitialDigest IS NOT OLD.InitialDigest
        OR NEW.InitialOutcome IS NOT OLD.InitialOutcome OR NEW.InitialEventSequence IS NOT OLD.InitialEventSequence))
BEGIN
    SELECT RAISE(ABORT,'Guild spend source evidence is immutable.');
END;
