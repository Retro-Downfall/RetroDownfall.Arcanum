CREATE TRIGGER IF NOT EXISTS guild_workspace_leases_operator_insert
AFTER INSERT ON guild_workspace_leases
BEGIN
    INSERT INTO guild_operator_sequences(KindCode,ObjectId) VALUES(2,NEW.Id);
END;
