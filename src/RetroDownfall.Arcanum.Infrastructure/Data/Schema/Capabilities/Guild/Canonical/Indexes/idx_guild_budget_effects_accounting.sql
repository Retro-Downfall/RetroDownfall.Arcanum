CREATE INDEX IF NOT EXISTS idx_guild_budget_effects_accounting ON guild_budget_effects(lower(replace(AccountingAssociationId,'-','')),KindCode);
