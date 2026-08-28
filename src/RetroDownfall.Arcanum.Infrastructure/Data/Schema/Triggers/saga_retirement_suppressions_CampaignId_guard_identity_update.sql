-- saga_retirement_suppressions.CampaignId is the Campaign one retirement applied to. It is a governed
-- stored identity, and IdentitySpellingBackfill.VerifiedColumns is the register that decides which
-- columns those are.
--
-- It was not governed while it was a projection with no reader, and it was recorded as deliberately
-- unrepaired on that ground. The ground moved when a caller began comparing it. A decision justified by
-- the absence of something is only as durable as that absence, and its expiry is not a thing a build can
-- report.
--
-- Nullable, and legitimately so: a suppression over a scope that carries no Campaign holds NULL here,
-- which this table's own CHECK pairs with ScopeKindCode. The guard therefore says nothing about a NULL
-- and everything about a value.
--
-- The digest beside it is left alone. It binds whichever spelling its preimage carried when the
-- retirement was recorded, and a retirement leaves no preimage to recompute it from.
--
-- Version 5 installs this trigger and drains its own sweep afterwards - a step's statements and the
-- journal row naming its backfill commit together, and the backfill runs in later coordinator passes -
-- so IdentitySpellingBackfill issues UPDATE saga_retirement_suppressions SET CampaignId =
-- upper(CampaignId) against this table with this trigger already enforcing. That write is admitted
-- rather than refused because the repair selects on shape as well as case: it never moves a row that is
-- not already 36 characters with dashes in the four places checked below, so upper() of what it moves is
-- canonical by construction. Relax that shape clause - IdentitySpellingBackfill.CanonicalShapeClause -
-- and this trigger aborts the migration, and every retry of it.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_update for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS saga_retirement_suppressions_CampaignId_guard_identity_update
BEFORE UPDATE OF CampaignId ON saga_retirement_suppressions
WHEN NEW.CampaignId IS NOT NULL
    AND (NEW.CampaignId <> upper(NEW.CampaignId)
        OR length(NEW.CampaignId) <> 36
        OR substr(NEW.CampaignId, 9, 1) <> '-'
        OR substr(NEW.CampaignId, 14, 1) <> '-'
        OR substr(NEW.CampaignId, 19, 1) <> '-'
        OR substr(NEW.CampaignId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'saga_retirement_suppressions.CampaignId must be stored as an uppercase dashed 36-character identity.');
END;
