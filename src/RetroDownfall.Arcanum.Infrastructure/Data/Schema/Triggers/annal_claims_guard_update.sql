-- A claim is an identity, and an identity that could be edited would let one claim's history be
-- reattributed to another durable row after the fact.
CREATE TRIGGER IF NOT EXISTS annal_claims_guard_update
BEFORE UPDATE ON annal_claims
BEGIN
    SELECT RAISE(ABORT, 'annal_claims is append-only; existing rows cannot be updated.');
END;
