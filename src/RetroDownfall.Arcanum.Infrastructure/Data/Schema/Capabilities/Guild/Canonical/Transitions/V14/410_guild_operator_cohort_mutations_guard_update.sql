CREATE TRIGGER IF NOT EXISTS guild_operator_cohort_mutations_guard_update
BEFORE UPDATE ON guild_operator_cohort_mutations
BEGIN
    SELECT RAISE(ABORT, 'Guild operator Cohort mutation receipts are immutable.');
END;
