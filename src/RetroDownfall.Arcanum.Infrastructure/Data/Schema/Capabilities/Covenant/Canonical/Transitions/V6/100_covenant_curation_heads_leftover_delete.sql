-- Before this version a family reset deleted key rows and left their curation behind. A nonzero epoch
-- is only ever read from a key row that exists, so curation at a nonzero epoch whose key has no row
-- can only be such a leftover, and it would otherwise bind to the key once it is re-created. Epoch 0
-- is a legitimate pin or mask recorded before the key's first head, and it stays. Heads go first
-- because each references its current curation version.
DELETE FROM covenant_curation_heads
WHERE KeyEpoch <> 0
  AND NOT EXISTS (SELECT 1 FROM covenant_key_epochs k WHERE k.NormalizedKey = covenant_curation_heads.NormalizedKey);
