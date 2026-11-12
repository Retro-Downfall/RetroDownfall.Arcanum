CREATE TRIGGER IF NOT EXISTS guild_operator_sequences_guard_delete
BEFORE DELETE ON guild_operator_sequences
WHEN (OLD.KindCode=1 AND EXISTS(SELECT 1 FROM guild_cohorts WHERE Id=OLD.ObjectId))
    OR (OLD.KindCode=2 AND EXISTS(SELECT 1 FROM guild_workspace_leases WHERE Id=OLD.ObjectId))
    OR (OLD.KindCode=3 AND EXISTS(SELECT 1 FROM guild_quests WHERE Id=OLD.ObjectId))
BEGIN
    SELECT RAISE(ABORT,'Guild operator membership remains while its canonical object exists.');
END;
