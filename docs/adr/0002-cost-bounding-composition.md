# ADR 0002: Cost-bounding composition

- Status: Accepted
- Date: 2026-07-21

## Context

Arcanum has three related cost/capacity controls that must not invent divergent notions of “too much”:

1. TurnLimits (per-turn ceilings: rounds, model calls, tool calls)
2. Daily budget reservation (USD)
3. Per-call context budget (tokens / tool-result groups)

Without an explicit composition order, reservation sizing, call-by-call enforcement, and reconcile drift apart.

## Decision

Compose in this order for every billable inference turn:

1. **TurnLimits** — seed `ITurnBudget` from `TurnLimitsDefaults` / `MaxToolInferenceRounds`.
2. **Reservation** — estimate worst-case USD as  
   `Σ(MaxModelCalls × maxTokensPerCall × priceSnapshot)` via `BudgetReservationService.EstimateWorstCaseTurnUsd`.  
   Acquire with a tiny `BEGIN IMMEDIATE` transaction:  
   `committed BillableOperations + outstanding BudgetReservations + estimate ≤ DailyLimitUsd`.  
   Never hold the transaction across provider I/O. Batches reserve **per batch**, not per line.
3. **Per-call context budget** — enforce remaining capacity call-by-call before each model invocation (`Hub.ContextBudgetExceeded` / `Hub.TurnBudgetExceeded`).
4. **Reconcile** — after the turn (or on abandon), true up the reservation to actual ledgered cost; release unused capacity; still ledger any provider-billed partial usage.

Session `TotalCostUsd` remains a **projection/cache** only. Authoritative daily spend is `BillableOperations.CompletedAt` (UTC day) plus outstanding reservations.

### Disconnect

See **ADR 0003**: default `Auto` (continue-then-replay with `Idempotency-Key`, else cancel→Abandoned). Both policies ledger partial billed cost and never leak reservations.

### Non-billable (closed list)

- `GET /models` (and health probes that do not call a provider)
- `POST /api/providers/test`
- `POST /api/intelligence/mana`

Everything that hits a provider for tokens is billable (chat, embeddings, routing, Lexicon/Saga extraction, structured-output retries).

### Pricing

Price by `(provider, model, operation type)`. Snapshot `InputPer1M` / `OutputPer1M` / `CachedPer1M` onto each `BillableOperations` row at record time.

## Consequences

- `ITurnRunWriter`, `IBudgetReservationService`, `IModelCallExecutor`, and `ITurnBudget` are the enforcement seams for cost/capacity until a unified turn engine owns them.
- Operators must reinstall/restart Grimoire when the inference-accounting / idempotency-claims SQL migration lands (no silent wipe; no user migration path).
- Disconnect policy must release or reconcile reservations without dropping partial billed cost (ADR 0003).

See also: `docs/PRIVATE-BETA-NOTES.md`.
