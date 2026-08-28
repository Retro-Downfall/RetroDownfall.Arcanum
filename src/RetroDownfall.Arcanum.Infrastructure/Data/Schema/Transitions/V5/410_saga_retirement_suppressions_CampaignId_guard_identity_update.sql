-- saga_retirement_suppressions.CampaignId is the Campaign one retirement applied to, copied from the
-- memory row's own Campaign by RetireAsync and canonicalized on the way in. A Campaign-scoped memory
-- reset deletes by it and compares it exactly.
--
-- That comparison is why the column is settled, and the reason is worth stating rather than implying.
-- It was a projection nothing anywhere compared, and was recorded as deliberately unrepaired on exactly
-- that ground. A divergence documented as safe because nothing compares it stops being safe the moment
-- something does, and nothing in a build notices the moment arriving.
--
-- Nullable, and legitimately so: a suppression over a Global or unresolved scope carries no Campaign,
-- which this table's own CHECK pairs with ScopeKindCode. The guard therefore says nothing about a NULL
-- and everything about a value.
--
-- The digest beside it is not of this family and is left alone. It binds whatever spelling the memory
-- row held when the retirement was recorded, it cannot be recomputed once the retired content is gone,
-- and both paths that ask about it ask for the canonical rendering and its lowercase image together.
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
