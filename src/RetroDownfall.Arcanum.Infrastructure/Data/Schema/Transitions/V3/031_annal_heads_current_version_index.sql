-- One version is current for at most one claim.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_heads_current_version
ON annal_heads(CurrentVersionId);
