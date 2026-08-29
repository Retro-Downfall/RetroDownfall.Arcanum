-- The binding is written once and read as authority forever after. Two updates exist: the
-- authenticated one-time resolution that turns an unresolved legacy row into a final one, and the
-- spelling-only canonicalization of CampaignId the version-five identity sweep performs. Everything
-- else is rejected outright, because an editable binding would let a Session be moved into another
-- Campaign's context, or laundered into Global context, without leaving the receipt that makes such
-- a move reviewable.
--
-- The canonicalization exemption below is spelling and nothing else, and every degree of freedom is
-- closed by name. This table has exactly four columns. SessionId is pinned by the abort above it,
-- BindingKindCode and BoundAtUtc are pinned by the exemption itself, and CampaignId is pinned to the
-- single value upper(OLD.CampaignId) - so the one write it admits cannot name a different Campaign,
-- cannot launder a Campaign binding into a Global one, and cannot alter the receipt. It still demands
-- the Session binding write scope, which begins FALSE on every connection.
--
-- Both IS NOT NULL tests are defence in depth, and an earlier version of this comment claimed more for
-- them than is true. Removing either does open the exemption's three-valued hole - a Global-only row
-- given a Campaign identity, or a Campaign binding whose identity is cleared to NULL, passes this
-- trigger with the matching conjunct gone - but the write still never lands, because the table's own
-- CHECK pairs BindingKindCode with the presence of CampaignId and refuses exactly those two rows. With
-- both conjuncts removed, nothing is admitted that was not admitted before: NULL AND FALSE is FALSE, so
-- any other pinned conjunct that differs collapses the exemption and the abort fires normally, and the
-- hole is left open only for an update that changes nothing at all. What these two buy is that the
-- trigger keeps refusing on its own terms if that CHECK is ever relaxed, and that the refusal names
-- this guard rather than a constraint. Measured against the real table in SQLite, each conjunct removed
-- individually and together, rather than reasoned about - which is how the original claim went wrong.
CREATE TRIGGER IF NOT EXISTS session_campaign_bindings_guard_update
BEFORE UPDATE ON session_campaign_bindings
BEGIN
    SELECT RAISE(ABORT, 'A Session Campaign binding resolution requires the Session binding write scope.')
    WHERE arcanum_session_binding_write_authorized() = 0;

    SELECT RAISE(ABORT, 'A Session Campaign binding cannot change the Session it belongs to.')
    WHERE NEW.SessionId <> OLD.SessionId;

    SELECT RAISE(ABORT, 'Only an unresolved legacy Session Campaign binding can be resolved.')
    WHERE OLD.BindingKindCode <> 3
      AND NOT (OLD.CampaignId IS NOT NULL
          AND NEW.CampaignId IS NOT NULL
          AND NEW.CampaignId = upper(OLD.CampaignId)
          AND NEW.BindingKindCode = OLD.BindingKindCode
          AND NEW.BoundAtUtc = OLD.BoundAtUtc);

    SELECT RAISE(ABORT, 'A resolved Session Campaign binding must be final.')
    WHERE NEW.BindingKindCode NOT IN (1, 2);
END;
