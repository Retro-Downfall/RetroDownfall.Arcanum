-- Phase one of a two-phase filesystem operation commits its recovery authority here before any
-- marker byte is written. Whoever can insert this row can make startup recovery act on a workspace
-- path with the installation's own authority, so the insert requires the connection-local marker
-- intent mutation scope, which begins FALSE on every connection. The path, restore, and full-reset
-- lifecycles borrow it only on their caller-owned live transaction; direct SQL never has it.
CREATE TRIGGER IF NOT EXISTS campaign_path_marker_intents_guard_insert
BEFORE INSERT ON campaign_path_marker_intents
WHEN arcanum_campaign_path_marker_intent_mutation_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'A Campaign path marker intent insert requires the marker intent mutation scope.');
END;
