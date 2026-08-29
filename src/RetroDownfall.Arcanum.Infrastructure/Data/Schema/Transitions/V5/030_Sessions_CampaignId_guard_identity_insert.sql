-- Sessions."CampaignId" is the Campaign a Session belongs to, written by the object-relational writer
-- and by the protected artifact transfer store's Campaign mapping. It has no foreign key and two
-- production comparisons that bind the canonical form - the Campaign deletion that clears it and the
-- Campaign-filtered session listing - so a Session spelled the other way keeps pointing at a deleted
-- Campaign and is omitted from that listing.
--
-- Nullable, and clearing it to NULL is how a Campaign deletion unbinds a Session, so the guard says
-- nothing at all about a NULL and everything about a value.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS Sessions_CampaignId_guard_identity_insert
BEFORE INSERT ON "Sessions"
WHEN NEW."CampaignId" IS NOT NULL
    AND (NEW."CampaignId" <> upper(NEW."CampaignId")
        OR length(NEW."CampaignId") <> 36
        OR substr(NEW."CampaignId", 9, 1) <> '-'
        OR substr(NEW."CampaignId", 14, 1) <> '-'
        OR substr(NEW."CampaignId", 19, 1) <> '-'
        OR substr(NEW."CampaignId", 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'Sessions.CampaignId must be stored as an uppercase dashed 36-character identity.');
END;
