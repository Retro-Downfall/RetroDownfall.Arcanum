CREATE TRIGGER IF NOT EXISTS guild_budget_effects_operator_delete
AFTER DELETE ON guild_budget_effects
BEGIN
    DELETE FROM guild_operator_sequences WHERE KindCode=4 AND ObjectId=OLD.EffectId;
END;
