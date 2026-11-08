CREATE UNIQUE INDEX IF NOT EXISTS ux_guild_workspace_lease_replay ON guild_workspace_leases(PrincipalId,COALESCE(QuestId,''),IdempotencyKey);
