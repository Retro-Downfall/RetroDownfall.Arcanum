CREATE UNIQUE INDEX IF NOT EXISTS idx_guild_events_spend_evidence ON guild_events(SpendEffectId,SpendEvidenceRevision) WHERE SpendEffectId IS NOT NULL;
