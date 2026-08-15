-- The binding is written once and read as authority forever after. Exactly one update exists: the
-- authenticated one-time resolution that turns an unresolved legacy row into a final one. Everything
-- else is rejected outright, because an editable binding would let a Session be moved into another
-- Campaign's context, or laundered into Global context, without leaving the receipt that makes such
-- a move reviewable.
CREATE TRIGGER IF NOT EXISTS session_campaign_bindings_guard_update
BEFORE UPDATE ON session_campaign_bindings
BEGIN
    SELECT RAISE(ABORT, 'A Session Campaign binding resolution requires the Session binding write scope.')
    WHERE arcanum_session_binding_write_authorized() = 0;

    SELECT RAISE(ABORT, 'A Session Campaign binding cannot change the Session it belongs to.')
    WHERE NEW.SessionId <> OLD.SessionId;

    SELECT RAISE(ABORT, 'Only an unresolved legacy Session Campaign binding can be resolved.')
    WHERE OLD.BindingKindCode <> 3;

    SELECT RAISE(ABORT, 'A resolved Session Campaign binding must be final.')
    WHERE NEW.BindingKindCode NOT IN (1, 2);
END;
