-- Every existing curation head and version recorded the key's KeyEpoch as it stood when the curation
-- was written, and a live pin's epoch still equals it. Binding each existing key row to its current
-- KeyEpoch keeps every live pin and mask live without rewriting an append-only curation row, and
-- leaves a pin recorded against an earlier epoch inert, as it already was.
UPDATE covenant_key_epochs SET IncarnationEpoch = KeyEpoch;
