# ADR 0003: Client disconnect policy (claims + accounting)

- Status: Accepted
- Date: 2026-07-21

## Context

Streaming inference can lose the client mid-turn. Idempotency claims and budget reservations must not leak or drop partial billed cost. Claim behavior and accounting behavior must be decided together.

## Decision

`Arcanum:Intelligence:DisconnectPolicy` (`DisconnectPolicy` enum):

| Value | Behavior |
|-------|----------|
| `CancelAbandoned` (0) | Cancel inference on disconnect. Claim → Abandoned (never Completed). Release unused reservation; **still ledger** any provider-billed partial usage via reconcile when `AccumulatedCostUsd > 0`. |
| `ContinueThenReplay` (1) | Keep inference running after disconnect. Claim may Complete for later replay. Run keeps consuming reservation and is fully ledgered. |
| `Auto` (2, **default**) | `ContinueThenReplay` when the request carries `Idempotency-Key`; otherwise `CancelAbandoned`. |

### Implementation notes

- Stream writers (`InferenceExecuteWriter`, OpenAI SSE) unlink `RequestAborted` from the inference CTS under continue-then-replay and drain the hub enumerator without writing to a dead socket.
- `IdempotencyBufferingStream` keeps buffering after the inner response stream dies so a completed turn can still be cached.
- Claims Complete only when the writer sets `Arcanum.IdempotencyTerminal` (clean producer finish), or when a non-aborted response finished within the byte cap — not merely because bytes were buffered while the client was gone under cancel policy.

## Consequences

Operators who need replayable streams under flaky clients should send `Idempotency-Key` (Auto) or force `ContinueThenReplay`. Cancelled non-idempotent streams remain Abandoned and non-replayable.

See also: ADR 0002 (cost composition).
