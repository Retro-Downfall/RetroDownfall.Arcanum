CREATE TRIGGER IF NOT EXISTS guild_content_cohort_clock_delete AFTER DELETE ON guild_content
BEGIN
    INSERT INTO guild_cohort_content_clock(Id,Revision) VALUES(1,1)
    ON CONFLICT(Id) DO UPDATE SET Revision=Revision+1 WHERE Revision<9223372036854775807;
    SELECT CASE WHEN changes()<>1 THEN RAISE(ABORT,'Cohort content clock exhausted') END;
END;
