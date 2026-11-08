CREATE INDEX IF NOT EXISTS idx_guild_workspace_leases_owner ON guild_workspace_leases(CampaignId,SessionId,WorkspaceId,StateCode);
