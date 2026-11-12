CREATE TRIGGER IF NOT EXISTS guild_budget_effects_operator_insert
AFTER INSERT ON guild_budget_effects
BEGIN
    INSERT INTO guild_operator_sequences(KindCode,ObjectId,ScopeId) VALUES(4,NEW.EffectId,NEW.QuestId);
END;
