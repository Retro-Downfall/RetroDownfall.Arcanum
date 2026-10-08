-- One row per Tapestry leaf: the SHA-256 of that leaf's text, kept so the sweep can tell whether a scope
-- changed without reading the scope's text. SourceKind names the corpus (the TapestryLeafSourceKind
-- spelling) and SourceId the row in it, so the key names no Session, workspace or attachment and the row
-- says nothing about what the text was. IsBlank marks a row the Tapestry skips because its text is empty
-- or whitespace, so a blank row is known to be blank without being read again.
--
-- A row is a cache that the corpus tables keep honest, not a record any writer maintains. A trigger on
-- each corpus table drops the row when its source row is inserted, has its id or text changed, or is
-- deleted, and the Tapestry stores a missing row the first time it needs the hash. The insert trigger is
-- there because an INSERT OR REPLACE over an existing source row removes it without firing the delete
-- trigger. Nothing is backfilled on upgrade: every row starts missing and is stored on first use, and a
-- row with no source row left is harmless, since the fingerprint reads the corpus table first.
CREATE TABLE IF NOT EXISTS tapestry_leaf_hashes (
    SourceKind TEXT NOT NULL CHECK (SourceKind IN ('WorkspaceFileChunk', 'SessionAttachmentChunk', 'Entry')),
    SourceId TEXT NOT NULL,
    ContentSha256 TEXT NOT NULL CHECK (length(ContentSha256) = 64),
    IsBlank INTEGER NOT NULL CHECK (IsBlank IN (0, 1)),
    PRIMARY KEY (SourceKind, SourceId)
);
