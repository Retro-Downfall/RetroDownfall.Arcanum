-- Replaces the version-four guard rather than adding beside it, because a trigger is a whole object
-- and the drift gate compares its installed text against the head tree. An evolved installation that
-- kept the older text would report DefinitionDrift on the very version this step completes.
--
-- The change is the CampaignId canonicalization exemption; see the head object for why the exemption
-- is spelling and nothing else, and for what the two IS NOT NULL tests inside it do and do not buy.
-- Without it, the version-five sweep below cannot repair a Campaign binding at all: every row that
-- needs repairing carries BindingKindCode 2, and the version-four guard aborts any update to a row
-- whose kind is not 3.
DROP TRIGGER IF EXISTS session_campaign_bindings_guard_update;

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
