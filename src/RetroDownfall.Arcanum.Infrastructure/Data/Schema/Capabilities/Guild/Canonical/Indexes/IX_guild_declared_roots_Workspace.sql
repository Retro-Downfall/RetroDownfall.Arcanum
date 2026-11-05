CREATE INDEX IF NOT EXISTS IX_guild_declared_roots_Workspace
ON guild_declared_roots(WorkspaceId COLLATE NOCASE,RootKey);
