CREATE INDEX IF NOT EXISTS idx_guild_owner_cleanup_event ON guild_owner_cleanup(EventSequence,OwnerKindCode,OwnerId);
