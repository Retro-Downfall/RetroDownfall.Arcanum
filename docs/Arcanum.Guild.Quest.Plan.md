# Quest implementation plan

The specification is DESIGN §§23.5–23.6, 23.9–23.10 and 23.15 and issue #147. Delivery targets `guild-implementation`; no PR is required. The existing Guild, Signet, protected-content and LRO implementations remain the owners of their respective invariants.

## Naming and upgrade contract

The domain name is **Quest** throughout current types, generated JSON, operation/checkpoint identifiers, canonical intent, storage and proposed public surfaces. Existing Guild v2 Signets migrate their two legacy identity column names in the unpublished v3 transition; their row values and canonical grants remain unchanged. Frozen v1/v2 definitions and fingerprints retain their historical spelling. The new Quest execution/checkpoint format has not been published or activated and needs no runtime alias for its former name.

## Implementation and evidence

- [x] Project the seven Quest states from LRO state and committed evidence; test terminal contradictions, cancellation without stop proof, input waits and uncertainty before implementing each behavior. Register the new named data graph for generated JSON and exercise it under Native AOT.
- [x] Extend the existing LRO transaction primitives for initial checkpoint v1 and terminal transitions. Test real SQLite transaction commit/rollback, revision and lease-owner fencing; preserve existing callers' behavior.
- [x] Install Guild schema version 3 with immutable Quest linkage, issuance identity, dispatch evidence, result/outbox/application receipts and parent input. Test fresh/v1/v2 convergence, prior fingerprints and immutable guards before adding canonical SQL and transitions.
- [x] Admit caller intent after current authentication and visibility, looking up replay before new CAS/resolution. Commit generated owner/Card identities, protected goal, LRO, Signet, allocation and start intent together. Test concurrency, restart, changed intent, every participant rollback and erased/deleted-owner replay. Allocation has a required transaction participant; no production substitute is registered before #153.
- [x] Journal dispatch before effects and commit result/outbox and parent application/input/ack in their respective transactions. Test stale lease/revision, all crash boundaries, exact redelivery and conflicting replacement. No uncertain dispatch is retried to repair acknowledgement loss.
- [x] Register one content-free checkpoint-v1 recovery handler and admission classification. Test missing/unknown evidence, maintenance, retained waits, known effect reconciliation and no redispatch. Keep public execution activation dormant.
- [x] Integrate content erasure, owner cleanup, retention, restore integrity and reset conflicts. Test that unresolved effects/accounting and replay denial survive payload erasure and generic-claim expiry.
- [x] Update architecture, API, CLI and configuration owners to distinguish delivered dormant foundations from proposed activation. Review independently, correct findings with regressions and qualify the frozen source locally. GitHub Actions CI is explicitly waived for this issue by the operator.

## Constraints and review focus

Use one admitted immediate transaction and the existing ledger rules. Authority, ledger lease, content revision and delivery revision are separate facts. No raw Signet or protected body appears in checkpoints or replay-denial metadata. No arbitrary depth, retry, duration or total-work ceiling is added. Guild schema changes preserve unrelated families. Ward remains informational.

Review especially replay after owner deletion/reset; exact caller omission semantics; generated identity rollback; expired leases and revocation races; and content-free retention of uncertain external effects. Each is covered by the corresponding task's regression tests.

## Qualification

The final local regression selection passed 2,894 cases with one opt-in invariant-globalization probe skipped. The focused recovery and ownership selection passed 126 cases, including the actual handler beneath an existing recovery work/effect pair, maintenance barriers and cancellation after a typed observer failure. The renamed generated Guild graph passed its Native AOT round trips; the final macOS arm64 shipping publish was warning-free and passed the smoke test against that exact apphost.

Source discovery reported zero diagnostics across 71 primary and 31 additional roots. The reviewed inventory contains 7,708 capsules and matches fresh discovery with two and three root workers. The recorded per-root ceilings use the maximum measured with one, two and three workers under the existing margin rule; checker rules and hard limits are unchanged. Hosted CI was not run. Direct integration and push target `guild-implementation`, with no PR.
