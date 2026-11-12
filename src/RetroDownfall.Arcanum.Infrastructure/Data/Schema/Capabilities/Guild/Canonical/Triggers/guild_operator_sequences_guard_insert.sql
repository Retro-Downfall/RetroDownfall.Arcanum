CREATE TRIGGER IF NOT EXISTS guild_operator_sequences_guard_insert
BEFORE INSERT ON guild_operator_sequences
WHEN (NEW.KindCode=1 AND NOT EXISTS(SELECT 1 FROM guild_cohorts WHERE Id=NEW.ObjectId))
    OR (NEW.KindCode=2 AND NOT EXISTS(SELECT 1 FROM guild_workspace_leases WHERE Id=NEW.ObjectId))
    OR (NEW.KindCode=3 AND NOT EXISTS(SELECT 1 FROM guild_quests WHERE Id=NEW.ObjectId AND IssuedSequence=NEW.Sequence))
BEGIN
    SELECT RAISE(ABORT,'Guild operator membership requires its canonical object.');
END;
