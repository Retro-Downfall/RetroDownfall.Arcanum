-- The binding epoch names which incarnation of a key a pin or mask curates, so it is fixed when the
-- epoch row is created. Moving it would re-bind curation recorded against one incarnation to another,
-- or disarm curation that still applies. The dependency epoch keeps advancing beside it.
CREATE TRIGGER IF NOT EXISTS covenant_key_epochs_guard_incarnation
BEFORE UPDATE OF IncarnationEpoch ON covenant_key_epochs
WHEN NEW.IncarnationEpoch <> OLD.IncarnationEpoch
BEGIN
    SELECT RAISE(ABORT, 'A covenant key binding epoch is fixed when its epoch row is created.');
END;
