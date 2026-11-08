CREATE UNIQUE INDEX IF NOT EXISTS ux_guild_workspace_lease_mutations_replay ON guild_workspace_lease_mutations(PrincipalId,COALESCE(QuestId,''),IdempotencyKey);
