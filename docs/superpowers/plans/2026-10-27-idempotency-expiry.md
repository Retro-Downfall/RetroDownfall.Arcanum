# RV258 stale idempotency claim expiry

The existing Unseen Servant sweep reaches DeleteExpiredAsync, which deletes only old terminal claims. A crashed process leaves Running/Claimed rows and retention blockers indefinitely unless the same request key is replayed.

Use native SQLCipher tests first for both nonterminal states, tick-exact TTL boundaries, recent creation, active/recently expired leases, independent-connection heartbeat/reclaim, unknown states and unchanged terminal expiry. In the existing atomic parameterized DELETE, retain CreatedAt < cutoff and extend its state predicate only to Running/Claimed with LeaseExpiresAt < cutoff. This is expiry after the complete replay TTL, never execution or state promotion, and current lease values are checked in the deleting statement. Keep unknown states and current leases. No schema, scheduling, authority, provider or wire change.

Document the precise TTL policy in DESIGN. Qualify the complete claim-store, idempotency/Unseen Servant, SQL/source boundaries and owning documentation families, then independently review before integration. Full CI remains a final campaign gate. Installation/live Ollama/embedding checks remain paused.
