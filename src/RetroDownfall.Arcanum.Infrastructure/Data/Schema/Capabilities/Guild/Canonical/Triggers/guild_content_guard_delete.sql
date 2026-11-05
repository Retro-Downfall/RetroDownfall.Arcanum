CREATE TRIGGER IF NOT EXISTS guild_content_guard_delete
BEFORE DELETE ON guild_content
WHEN (OLD.SensitivityCode <> 0 OR EXISTS (
    SELECT 1 FROM artifact_sensitivity
    WHERE ArtifactKindCode = 17 AND lower(replace(ArtifactId,'-','')) = lower(replace(OLD.ContentId,'-',''))))
    AND arcanum_sensitivity_purge_authorized() = 0
    AND arcanum_covenant_family_maintenance_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'Protected Guild content deletion requires its protected erasure authority.');
END;
