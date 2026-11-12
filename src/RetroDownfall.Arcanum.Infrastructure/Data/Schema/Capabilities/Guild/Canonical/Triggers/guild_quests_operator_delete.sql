CREATE TRIGGER IF NOT EXISTS guild_quests_operator_delete
AFTER DELETE ON guild_quests
BEGIN
    DELETE FROM guild_operator_sequences WHERE KindCode=3 AND ObjectId=OLD.Id;
END;
