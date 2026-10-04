-- The receipts recorded against a leftover subject, by the same predicate.
DELETE FROM covenant_curation_receipts
WHERE KeyEpoch <> 0
  AND NOT EXISTS (SELECT 1 FROM covenant_key_epochs k WHERE k.NormalizedKey = covenant_curation_receipts.NormalizedKey);
