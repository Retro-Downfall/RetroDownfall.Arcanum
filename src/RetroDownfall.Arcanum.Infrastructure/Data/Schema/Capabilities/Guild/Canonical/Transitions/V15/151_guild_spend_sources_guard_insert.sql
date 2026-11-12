CREATE TRIGGER IF NOT EXISTS guild_spend_sources_guard_insert
BEFORE INSERT ON guild_spend_sources
WHEN NEW.GenerationId IS NOT NULL OR NEW.InitialSnapshot IS NOT NULL OR NOT EXISTS(SELECT 1 FROM guild_budget_effects e JOIN guild_quests q ON q.Id=e.QuestId
    WHERE e.EffectId=NEW.EffectId AND e.KindCode=0 AND e.SourceId=NEW.SourceId
      AND (NEW.SourceKindCode=0 OR NEW.InstallationGeneration=q.InstallationGeneration))
BEGIN
    SELECT RAISE(ABORT,'Guild spend source requires its exact local physical effect.');
END;
