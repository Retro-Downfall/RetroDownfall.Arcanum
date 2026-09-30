-- The versions the purged heads pointed at, and any other leftover version, by the same predicate.
DELETE FROM covenant_curation_versions
WHERE KeyEpoch <> 0
  AND NOT EXISTS (SELECT 1 FROM covenant_key_epochs k WHERE k.NormalizedKey = covenant_curation_versions.NormalizedKey);
