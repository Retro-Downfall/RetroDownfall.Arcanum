CREATE TRIGGER IF NOT EXISTS guild_quests_operator_insert
AFTER INSERT ON guild_quests
BEGIN
    INSERT INTO guild_operator_sequences(Sequence,KindCode,ObjectId) VALUES(NEW.IssuedSequence,3,NEW.Id);
END;
