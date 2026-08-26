-- A version is the immutable record of one assertion. Its content binding says which bytes it was
-- written about and its timestamps say when it was believed; editing a row in place would leave that
-- evidence describing something else. A correction is written as the next revision, and a version's
-- transaction time is closed by its successor rather than by an update to itself.
CREATE TRIGGER IF NOT EXISTS annal_versions_guard_update
BEFORE UPDATE ON annal_versions
BEGIN
    SELECT RAISE(ABORT, 'annal_versions is append-only; a correction is the next revision.');
END;
