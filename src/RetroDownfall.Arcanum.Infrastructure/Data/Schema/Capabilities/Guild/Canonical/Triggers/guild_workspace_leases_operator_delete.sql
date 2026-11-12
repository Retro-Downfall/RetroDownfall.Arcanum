CREATE TRIGGER IF NOT EXISTS guild_workspace_leases_operator_delete
AFTER DELETE ON guild_workspace_leases
BEGIN
    DELETE FROM guild_operator_sequences WHERE KindCode=2 AND ObjectId=OLD.Id;
END;
