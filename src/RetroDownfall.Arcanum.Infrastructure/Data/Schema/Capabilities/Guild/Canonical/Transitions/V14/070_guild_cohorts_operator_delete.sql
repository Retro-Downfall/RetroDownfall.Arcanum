CREATE TRIGGER IF NOT EXISTS guild_cohorts_operator_delete
AFTER DELETE ON guild_cohorts
BEGIN
    DELETE FROM guild_operator_sequences WHERE KindCode=1 AND ObjectId=OLD.Id;
END;
