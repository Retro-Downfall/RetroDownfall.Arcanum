CREATE INDEX IF NOT EXISTS idx_guild_quests_cancellation ON guild_quests(CancellationRequested,CancellationConverged,Retired,Id);
