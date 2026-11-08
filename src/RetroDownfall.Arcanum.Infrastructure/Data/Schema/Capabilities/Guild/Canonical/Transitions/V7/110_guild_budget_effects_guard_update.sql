CREATE TRIGGER IF NOT EXISTS guild_budget_effects_guard_update
BEFORE UPDATE ON guild_budget_effects
WHEN NEW.EffectId IS NOT OLD.EffectId
  OR NEW.AccountId IS NOT OLD.AccountId
  OR NEW.QuestId IS NOT OLD.QuestId
  OR NEW.DispatchId IS NOT OLD.DispatchId
  OR NEW.AccountingAssociationId IS NOT OLD.AccountingAssociationId
  OR NEW.KindCode IS NOT OLD.KindCode
  OR NEW.SourceId IS NOT OLD.SourceId
  OR NEW.ReservationId IS NOT OLD.ReservationId
  OR NEW.UsdBound IS NOT OLD.UsdBound
  OR NEW.TokenBound IS NOT OLD.TokenBound
  OR NEW.Revision<>OLD.Revision+1
  OR (OLD.StateCode<>0 AND NEW.StateCode=0)
  OR (OLD.StateCode IN(2,3) AND NEW.StateCode<>OLD.StateCode)
BEGIN
    SELECT RAISE(ABORT, 'Guild physical budget effect identity, bounds and terminal evidence cannot be replaced or regressed.');
END;
