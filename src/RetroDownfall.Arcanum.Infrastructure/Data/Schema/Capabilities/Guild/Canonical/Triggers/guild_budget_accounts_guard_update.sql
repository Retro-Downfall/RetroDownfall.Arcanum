CREATE TRIGGER IF NOT EXISTS guild_budget_accounts_guard_update
BEFORE UPDATE ON guild_budget_accounts
WHEN NEW.AccountId IS NOT OLD.AccountId
  OR NEW.OwnerKindCode IS NOT OLD.OwnerKindCode
  OR NEW.OwnerId IS NOT OLD.OwnerId
  OR NEW.RootAccountId IS NOT OLD.RootAccountId
  OR NEW.ParentAccountId IS NOT OLD.ParentAccountId
  OR NEW.UsdLimit IS NOT OLD.UsdLimit
  OR NEW.TokenLimit IS NOT OLD.TokenLimit
  OR NEW.Revision<>OLD.Revision+1
  OR NEW.Closed<OLD.Closed
  OR (OLD.SettlementReceiptId IS NOT NULL AND NEW.SettlementReceiptId IS NOT OLD.SettlementReceiptId)
BEGIN
    SELECT RAISE(ABORT, 'Guild budget account identity, funding and closure evidence are immutable.');
END;
