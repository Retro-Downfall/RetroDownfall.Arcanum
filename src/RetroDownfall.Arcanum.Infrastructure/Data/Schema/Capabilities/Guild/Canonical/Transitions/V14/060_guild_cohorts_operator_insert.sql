CREATE TRIGGER IF NOT EXISTS guild_cohorts_operator_insert
AFTER INSERT ON guild_cohorts
BEGIN
    INSERT INTO guild_operator_sequences(KindCode,ObjectId) VALUES(1,NEW.Id);
END;
