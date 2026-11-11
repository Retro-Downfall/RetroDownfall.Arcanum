CREATE TRIGGER IF NOT EXISTS guild_distribution_notifications_guard_update BEFORE UPDATE ON guild_distribution_notifications
BEGIN SELECT RAISE(ABORT,'distribution notification identity is immutable'); END;
