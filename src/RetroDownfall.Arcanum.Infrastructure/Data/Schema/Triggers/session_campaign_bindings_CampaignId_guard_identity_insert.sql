-- session_campaign_bindings.CampaignId is the Campaign a Session is bound to, and it carries no foreign
-- key by design: it is the historical authority identity, so a Campaign deletion clears its own row
-- without rewriting the durable fact that this Session was bound to that Campaign. Nothing therefore
-- forced its two writers to agree, and they did not. The core data initializer canonicalized what it
-- backfilled while the turn-begin repository bound a bare ToString(), so one table held two spellings of
-- one Campaign - and SagaMemoryScopeClassifier copied whichever it found straight into
-- saga_memories.CampaignId, so Campaign-scoped recall returned only the half whose binding came from the
-- repository and the assistant simply did not remember the rest. That is the history this guard exists
-- for rather than a description of the code now: the classifier canonicalizes the identity it hands on,
-- so a memory no longer inherits this column's spelling. What still reads this column exactly is a
-- Campaign memory reset, which selects the Sessions whose watermarks it must clear by comparing it.
--
-- Nullable, and legitimately so: a global-only or unresolved binding carries no Campaign at all, which
-- the table's own CHECK states. The guard therefore says nothing about a NULL and everything about a
-- value.
--
-- Both an insert guard and an update guard, and the update half is reachable where
-- session_campaign_bindings_SessionId_guard_identity_update would not have been.
-- session_campaign_bindings_guard_update permits exactly one rewrite of this column - the
-- spelling-only canonicalization the version-five sweep performs, under the Session binding write
-- scope - and the one-time resolution of an unresolved legacy row writes it for the first time. Both
-- are writes an identity guard must judge.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS session_campaign_bindings_CampaignId_guard_identity_insert
BEFORE INSERT ON session_campaign_bindings
WHEN NEW.CampaignId IS NOT NULL
    AND (NEW.CampaignId <> upper(NEW.CampaignId)
        OR length(NEW.CampaignId) <> 36
        OR substr(NEW.CampaignId, 9, 1) <> '-'
        OR substr(NEW.CampaignId, 14, 1) <> '-'
        OR substr(NEW.CampaignId, 19, 1) <> '-'
        OR substr(NEW.CampaignId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'session_campaign_bindings.CampaignId must be stored as an uppercase dashed 36-character identity.');
END;
