# Dormant Guild Core implementation

This plan implements issue #310 from the resolved DESIGN §23 and proposed API appendix. It delivers reusable Core values, pure policies, ports and generated serialization. Guild runtime, storage, endpoints, commands and settings remain proposed. The existing LRO, SQLCipher and effect pipelines retain their selected ownership.

## Constraints and decisions

- Keep all contracts in Core/Guild; use Result/Result<T> and cancellation for asynchronous ports.
- Treat serialized identifiers, Cards, grants and producer labels as untrusted data. Host-only caller, verified-authority and execution handles have no public construction or serialization path.
- Represent grant arrays immutably, with exact tool identities, explicit finite/unlimited budget dimensions, and distinguish omitted child axes from explicit null expiry. Pure attenuation requires host-verified physical/ownership evidence where data alone cannot establish containment.
- Preserve distinct Agent/Card revision/Run/Commission/root/parent/Operation/member identities, tagged Circle producers, durable receipt identities and separate execution/delivery/accounting facts.
- Keep all seven Commission strings literal and string-only. Unknown remote states are working observations requiring reconciliation.
- Use the current integration base and preserve unrelated primary-checkout work. No runtime registrations, schema migrations, executable tool aliases or arbitrary execution ceilings are added.

## Implementation sequence

- [x] Add failing contract tests for named Core types, ports, errors, host-only boundaries and generated data graphs.
- [x] Implement immutable grants and pure attenuation/verification policy inputs; prove empty, equal, narrower, wider, omitted and invalid values for every axis, plus closed-axis completeness.
- [x] Implement Card, Commission, Circle, content, spend, Cohort, offer/bid/award, note and workspace-lease data contracts. Prove tagged producer invariants, independent stream/dedupe identity and exact enum wires.
- [x] Declare the nine shared asynchronous ports and their named admission/result/dispatch/request contracts. Require trusted context separately from caller intent; keep transaction/transport/runtime implementation outside Core.
- [x] Register the complete serializable graph and concrete collections on GuildJsonContext; round-trip every named data contract and reject trusted handles/raw capabilities.
- [x] Update DESIGN, API, Command Reference and Compendium status, reconcile #144/#310 ownership, and retain the proposed status of future behavior.
Delivery requires independent review, regression tests for findings, solution/tests/coverage/AOT and CI qualification, followed by merging and pushing the qualified candidate to main. Exact candidate results are recorded with the pull request; this document is not runtime activation evidence.

## Review focus

Tests must catch explicit-null versus omitted grants, finite/unlimited and negative/fractional budget input, lexical path claims without verified physical containment, operator/receipt identities disguised as runs, and unknown/contradictory lifecycle observations presented as completion. Grant member inventory supplements semantic tests and never substitutes for authentication or filesystem proof.

The implementation uses the existing architecture directly. A parallel orchestration engine, public capability factories and transport-shaped Core types conflict with the resolved contract and are excluded.
