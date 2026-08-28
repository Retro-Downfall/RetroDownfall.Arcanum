-- The write-time half of the identity canonicalisation, and the half that makes it hold. Once every
-- stored identity has one spelling a comparison can be an exact indexed equality again, and the only
-- thing that keeps it that way is a refusal at the write. A guard fires on whatever produced the row -
-- the object-relational writer, a raw Guid handed to the provider, an interpolation, or SQL nobody has
-- written yet - which is exactly what a source scan of the writers can never cover.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong. A dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence; the two columns that legitimately hold that form - lexicon_entries.Id
-- and IdempotencyClaims.Id - are single-writer and are deliberately guarded nowhere. The predicate here
-- is the one IdentitySpellingBackfill.CountNonCanonicalAsync asks of the stored data, so the question a
-- guard refuses on and the question the sweep reports on are the same question.
--
-- ONE TRIGGER PER COLUMN, not one per table, and the reasons are in this order of weight. RAISE(ABORT)
-- takes a string literal, so a trigger covering several columns structurally cannot name the one that
-- failed - and the message is the whole of what a developer sees. The update half has to be
-- BEFORE UPDATE OF <column>, which is per-column by construction. And a guarded table can carry
-- identity-shaped columns that are deliberately outside this family - the provenance SessionIds,
-- lexicon_fact_attachment_provenance.EntryId, attachment_memory_consultations.SourceEntryId - so a name
-- of the form <table>_guard_identity would claim a coverage the trigger does not have. The cost is an
-- object per governed column where one per table would do, paid once, in a tree that is one object per
-- file.
--
-- The authoritative list of what this family governs is IdentitySpellingBackfill.VerifiedColumns plus
-- artifact_sensitivity.SessionId, and a test pins that every entry has its guards.
--
-- Sessions."Id" is the identity most of this schema is keyed to. Every writer hands the provider a Guid
-- or an already-uppercased rendering of one - the object-relational writer, the protected artifact
-- transfer store and the backup session importer - so every row an installation holds is already
-- canonical, and this guard is here for the writer nobody has written yet.
CREATE TRIGGER IF NOT EXISTS Sessions_Id_guard_identity_insert
BEFORE INSERT ON "Sessions"
WHEN NEW."Id" IS NOT NULL
    AND (NEW."Id" <> upper(NEW."Id")
        OR length(NEW."Id") <> 36
        OR substr(NEW."Id", 9, 1) <> '-'
        OR substr(NEW."Id", 14, 1) <> '-'
        OR substr(NEW."Id", 19, 1) <> '-'
        OR substr(NEW."Id", 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'Sessions.Id must be stored as an uppercase dashed 36-character identity.');
END;
