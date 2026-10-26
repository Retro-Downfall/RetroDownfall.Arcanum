CREATE TRIGGER IF NOT EXISTS campaign_rollup_artifacts_manifest_delete
AFTER DELETE ON campaign_rollup_artifacts
WHEN OLD.ManifestId IS NOT NULL
BEGIN
    DELETE FROM campaign_rollup_manifests
    WHERE LineageId = (SELECT LineageId FROM campaign_rollup_manifests WHERE ManifestId = OLD.ManifestId)
        AND Ordinal > COALESCE((SELECT MAX(root.Ordinal) FROM campaign_rollup_artifacts artifact
            JOIN campaign_rollup_manifests root ON root.ManifestId = artifact.ManifestId
            WHERE root.LineageId = campaign_rollup_manifests.LineageId), 0);
END;
