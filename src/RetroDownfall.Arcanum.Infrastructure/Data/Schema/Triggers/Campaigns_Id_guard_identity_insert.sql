-- Campaigns."Id" has one writer, the object-relational writer, which the SQLite value binder renders
-- uppercase unconditionally. It is guarded although version 5 repairs nothing here, because it is the
-- identity Sessions."CampaignId" is repaired against: a non-canonical Campaign makes that repair decline
-- every row it would otherwise restore.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS Campaigns_Id_guard_identity_insert
BEFORE INSERT ON "Campaigns"
WHEN NEW."Id" IS NOT NULL
    AND (NEW."Id" <> upper(NEW."Id")
        OR length(NEW."Id") <> 36
        OR substr(NEW."Id", 9, 1) <> '-'
        OR substr(NEW."Id", 14, 1) <> '-'
        OR substr(NEW."Id", 19, 1) <> '-'
        OR substr(NEW."Id", 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'Campaigns.Id must be stored as an uppercase dashed 36-character identity.');
END;
