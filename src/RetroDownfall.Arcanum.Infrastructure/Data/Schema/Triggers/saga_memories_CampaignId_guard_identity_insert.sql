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
-- The insert half judges a live write on every turn, and judged it too early once. A schema step's DDL
-- commits with its journal row and the step's sweep runs in later coordinator passes, so this trigger is
-- enforcing while session_campaign_bindings.CampaignId may still hold the minority spelling on rows the
-- sweep has not reached - and the classifier used to copy that value into this column verbatim, which
-- aborted the insert for every Session still waiting. SagaMemoryScopeClassifier now canonicalizes the
-- identity it hands on, so what reaches this column is canonical whatever the binding beside it holds
-- and whatever the sweep has drained.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS saga_memories_CampaignId_guard_identity_insert
BEFORE INSERT ON saga_memories
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
