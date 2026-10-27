# Recursive consumer resource sampler repair plan

**Goal:** Preserve bounded qualification when process-resource observation overlaps or fails, without an escaping timer exception or a false accepted result.

**Base:** efb644e5547e5213c4fc78e4b351264759178eb7. Its accepted 10k and aborted 50k packets remain immutable. The aborted run has zero executed results and no harness terminal event; it is not a numerical assertion failure or a passing consumer run.

**Architecture:** Keep the default process reader and 10 ms timer, serialize reader entry with a nonblocking gate, keep clock deadlines active on overlapping ticks, and cancel on unavailable observations. Keep first-cause cancellation, last-known peak, joined timer disposal and reader ownership. Controlled owned-reader/manual-tick and budget-factory seams exercise the same private harness code; default budgets and numerical settings do not change.

## Constraints

No product, schema, configuration, authority or numerical changes. No scale restart without independent new-candidate review and root authorization. Existing 10k limits remain120/1800/180/2100/2220 seconds and4GiB; 50k remains300/7200/300/7800/7920 seconds and6GiB. No live providers, embeddings, installation, other-worktree writes or children.

## Execution and evidence

- Preserve the exact failed 50k trace, raw aborted TRX, partial sidecar and source/binary maps before editing.
- Add behavior-neutral owned-reader/manual-tick instrumentation. The initial Process namespace-collision compile attempt is excluded. Instrumented RED proves overlapping access, escaping sampler faults and missing startup reader cleanup. Joined timer disposal and actual-process controls pass; disposal is not blamed for the original stack.
- Expanded RED proves10 assertion failures and4 controls: sampling/first-cause containment, cancellation-callback escape, startup ownership and actual recursive failure-receipt privacy. The precise OS cause of the original getter failure remains unproved.
- GREEN serializes observation without queuing overlapping reads, checks elapsed time on skipped ticks, retains actual memory-overage refusal and first cause, fails closed as resource-unavailable, contains cancellation-callback faults and disposes owned resources after joined callbacks. Complete harness firstGREEN31passes/2explicit scale opt-in skips; no scale method runs.
- Qualification requires complete harness/numerical owning classes and applicable static guards, a normal zero-warning build, all owned C# styles, meaningful causal reversions and exact restoration. The sealed terminal receipt owns final status rather than a prospective plan checkbox.
- Freeze a clean commit/tree and source/binary/evidence package for independent review. No automatic retry, budget increase, fabricated zero resource sample, numerical failure attribution or Task12/fullCI completion claim.
