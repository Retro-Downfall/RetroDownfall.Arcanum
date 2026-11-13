CREATE INDEX IF NOT EXISTS idx_guild_spend_refreshes_effect_norm ON guild_spend_refreshes(lower(replace(EffectId,'-','')),OperationId);
