CREATE TRIGGER IF NOT EXISTS guild_budget_evidence_guard_update
BEFORE UPDATE ON guild_budget_evidence
BEGIN
    SELECT RAISE(ABORT, 'Guild physical budget evidence receipts are immutable.');
END;
