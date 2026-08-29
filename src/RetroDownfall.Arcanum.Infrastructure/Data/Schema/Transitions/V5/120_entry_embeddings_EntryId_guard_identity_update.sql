-- entry_embeddings.EntryId is copied from Entries."Id" by the weaving service, so on a healthy
-- installation the two always agree. When they do not, the weave's left join reports that Entry as
-- unembedded and the corpus is silently re-embedded at provider cost - the expensive silent failure this
-- work names. The column is this table's TEXT PRIMARY KEY, which SQLite still allows to be NULL, so the
-- guard qualifies on NULL rather than relying on the declaration.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_update for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS entry_embeddings_EntryId_guard_identity_update
BEFORE UPDATE OF EntryId ON entry_embeddings
WHEN NEW.EntryId IS NOT NULL
    AND (NEW.EntryId <> upper(NEW.EntryId)
        OR length(NEW.EntryId) <> 36
        OR substr(NEW.EntryId, 9, 1) <> '-'
        OR substr(NEW.EntryId, 14, 1) <> '-'
        OR substr(NEW.EntryId, 19, 1) <> '-'
        OR substr(NEW.EntryId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'entry_embeddings.EntryId must be stored as an uppercase dashed 36-character identity.');
END;
