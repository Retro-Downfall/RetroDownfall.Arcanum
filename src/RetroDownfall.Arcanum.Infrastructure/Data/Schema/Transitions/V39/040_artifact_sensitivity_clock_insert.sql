CREATE TRIGGER IF NOT EXISTS artifact_sensitivity_clock_insert
AFTER INSERT ON artifact_sensitivity
BEGIN
    INSERT INTO artifact_sensitivity_clock(Id,Revision) VALUES(1,1)
    ON CONFLICT(Id) DO UPDATE SET Revision=Revision+1;
END;
