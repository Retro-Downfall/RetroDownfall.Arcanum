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
2. **Reservation** — estimate worst-case USD with
   `Σ(MaxModelCalls × maxTokensPerCall × priceSnapshot)` via `BudgetReservationService.EstimateWorstCaseTurnUsd`. A supplied reasoning budget is a completion subset and therefore expands per-call headroom to `max(maxOutputTokens, reasoningBudgetTokens)` rather than being added on top; when reasoning is the more expensive rate, reserve that subset at `ReasoningPer1M`.
   Acquire with a tiny `BEGIN IMMEDIATE` transaction:  
   `committed BillableOperations + outstanding BudgetReservations + estimate ≤ DailyLimitUsd`.  
   Never hold the transaction across provider I/O. Batches reserve **per batch**, not per line.
3. **Per-call context budget** — enforce remaining capacity call-by-call before each model invocation, reserving `max(request MaxOutputTokens or configured ReservedOutputTokens default, Reasoning.BudgetTokens)` just as the dollar reservation uses the larger output headroom (`Hub.ContextBudgetExceeded` / `Hub.TurnBudgetExceeded`).
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

Price by `(provider, model, operation type)`. Snapshot `InputPer1M` / `OutputPer1M` / `CachedPer1M` / nullable `ReasoningPer1M` onto each `BillableOperations` row at record time. Reasoning tokens are an output-token subset: price non-reasoning completion at `OutputPer1M`, price reasoning at `ReasoningPer1M`, and use `OutputPer1M` only when the reasoning rate is null. Preserve null in the snapshot so fallback remains distinguishable from an explicit zero rate; never add reasoning tokens to output cost a second time.

Cached input tokens are likewise a prompt-token subset. For every provider call, preserve prompt, completion, cached, and reasoning counts independently. A present provider total is authoritative even if it is inconsistent with those counts (including zero); derive prompt + completion only when the total is missing. Missing usage contributes no operation cost. Tool rounds and structured-output retries are ledgered individually, then accumulated without a duplicate final aggregate.

### Raw-SQL accounting boundary

`BillableOperations` is intentionally outside `ArcanumDbContext` and its compiled EF model. `TurnRunWriter` uses parameterized raw SQL, including `ReasoningTokens INTEGER NOT NULL DEFAULT 0`. Reasoning counts may feed reconciliation, the low-cardinality reasoning-token metric, and count-only audit metadata; reasoning bodies never enter accounting or audit storage.

## Consequences

- `TurnEngine` composes `ITurnRunWriter`, `IBudgetReservationService`, `IModelCallExecutor`, and `ITurnBudget` as the enforcement seams for cost/capacity.
- The existing `20260721010000_AddInferenceAccountingAndIdempotencyClaims.sql` install script changed in place. There is no new migration and no compiled-model regeneration. Developers must stop every Arcanum host/daemon, back up anything needed, delete the database plus matching `-wal`/`-shm` files, then restart to install a fresh Grimoire. Copy-pastable Bash and PowerShell commands are in [Arcanum.README](../Arcanum.README.md#mandatory-local-grimoire-reinstall).
- Disconnect policy must release or reconcile reservations without dropping partial billed cost (ADR 0003).

See also: `docs/PRIVATE-BETA-NOTES.md`.
