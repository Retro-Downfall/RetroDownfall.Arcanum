CREATE INDEX IF NOT EXISTS idx_guild_spend_sources_provider_binding ON guild_spend_sources(SourceKindCode) WHERE SourceKindCode=1;
