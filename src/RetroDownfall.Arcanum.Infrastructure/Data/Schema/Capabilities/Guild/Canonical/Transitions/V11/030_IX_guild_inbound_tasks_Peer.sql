CREATE INDEX IF NOT EXISTS IX_guild_inbound_tasks_Peer ON guild_inbound_tasks(PeerId,GrantRevision,AuthorityEpoch,QuestId);
