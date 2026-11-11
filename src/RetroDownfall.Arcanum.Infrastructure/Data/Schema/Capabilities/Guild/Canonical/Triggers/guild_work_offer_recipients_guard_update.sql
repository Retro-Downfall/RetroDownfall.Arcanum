CREATE TRIGGER IF NOT EXISTS guild_work_offer_recipients_guard_update BEFORE UPDATE ON guild_work_offer_recipients
BEGIN SELECT RAISE(ABORT,'immutable offer recipient'); END;
