-- saga_memories.CampaignId is the Campaign a memory belongs to, taken from
-- session_campaign_bindings.CampaignId by SagaMemoryScopeClassifier - by the live write path and by the
-- version-two classification sweep alike. It is what Campaign-scoped recall binds against on every turn,
-- and what a Campaign memory reset deletes by, and both compare it exactly.
--
-- This table's SessionId is not of this family. It is written by the Saga memory store with a bare
-- ToString(), read back the same way by every one of its own readers, and is guarded by nothing here.
--
-- Nullable, and legitimately so: a global-only or unclassified memory carries no Campaign, which is the
-- distinction ScopeKindCode exists to keep separate from a Campaign-scoped one. The guard therefore says
-- nothing about a NULL and everything about a value.
--
-- Both an insert guard and an update guard, because saga_memories refuses no update - which is the only
-- reason this family ever omits the update half.
--
-- Version 5 installs this trigger and drains its own sweep afterwards - a step's statements and the
-- journal row naming its backfill commit together, and the backfill runs in later coordinator passes -
-- so IdentitySpellingBackfill issues UPDATE saga_memories SET CampaignId = upper(CampaignId) against
-- this table with this trigger already enforcing. That write is admitted rather than refused because
-- the repair selects on shape as well as case: it never moves a row that is not already 36 characters
-- with dashes in the four places checked below, so upper() of what it moves is canonical by
-- construction. Relax that shape clause - IdentitySpellingBackfill.CanonicalShapeClause - and this
-- trigger aborts the migration, and every retry of it.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_update for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS saga_memories_CampaignId_guard_identity_update
BEFORE UPDATE OF CampaignId ON saga_memories
WHEN NEW.CampaignId IS NOT NULL
    AND (NEW.CampaignId <> upper(NEW.CampaignId)
        OR length(NEW.CampaignId) <> 36
        OR substr(NEW.CampaignId, 9, 1) <> '-'
        OR substr(NEW.CampaignId, 14, 1) <> '-'
        OR substr(NEW.CampaignId, 19, 1) <> '-'
        OR substr(NEW.CampaignId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'saga_memories.CampaignId must be stored as an uppercase dashed 36-character identity.');
END;
