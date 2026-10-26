# RV118 embedding provider diagnostics implementation evidence

Exact baseline `4adafb838673fcaf0135590ec79ad08bae570f96`; branch `codex/review-embedding-provider-diagnostics`; assigned maintenance-outcomes worktree. This source-frozen report records causal proof before final qualification. The external immutable terminal report supplies the final commit/tree, executed commands, complete class census and artifact checksums without changing qualified bytes. Independent review is required; no integrated credit is claimed.

## RV118 behavior and diagnostic privacy

Weave classifies only actual direct SDK `ClientResultException.Status` or `HttpRequestException.StatusCode` in the valid HTTP range. Status401,403,404 and413 get fixed credential/access/model-or-endpoint/request-size guidance; unknown failures retain bounded availability guidance. Resolution, generation and local accounting stages remain distinct. Existing `Embeddings.ProviderUnavailable` and HTTP503 contracts are preserved. An accounting-write exception shaped like HTTP401 gets local-storage guidance, without blaming provider authentication. Exception messages, bodies, headers, inner exceptions, inputs and endpoint values are never copied into returned errors or retained log exceptions.

Warnings carry closed reason/stage, separately labeled actual lease and initial configured provider/model, safe status/type/counts, RunId and ReservationId. Missing lease provenance is explicitly unknown and resets each batch. Provider-supplied vector metadata cannot assert first-party identity. Identifier labels accept only a bounded conservative ASCII grammar (128 characters, alphanumeric first, remaining alphanumeric or `._-:`, no `..`); unsafe/overlong values are wholly redacted. This intentionally redacts slash-shaped model identifiers. The public message never contains identifier values. No parsing of exception text, arbitrary error code or inner exceptions supplies status/provenance.

## Separately confirmed adjacent identity and pricing correction

Real `EmbeddingGeneratorFactory` plus the real OpenAI SDK over a scripted in-memory HTTP handler proved mid-operation model/provider changes could send differently stamped batches while reservation, pricing and billable rows retained the original identity. Observed model case: SDK requests `model-alpha,model-beta`, returned provenance `provider-alpha/model-alpha,provider-alpha/model-beta`, but rows remained original model at32tokens/$32 and1token/$1 and original reserve$33, despite replacement pricing10x. First-lease reload also dispatched paid work under a replacement name before refusal was possible. These are actual provider-generation paths without a network/live-provider dependency.

The controller approved one initial settings snapshot for expected identity, rates and reservation. After the existing Tapestry validator and caller-token observation, every known canonical lease identity must match the pinned operation provider/model before Generate. A mismatch returns fixed safe guidance and sends no further batch. Earlier paid rows and their original prices remain recorded and reconciled; no automatic replay or repricing occurs. First-batch and later-batch no-send controls prove the distinction. A separate changing-monitor control proves configuration is read once for identity/pricing, avoiding a second mixed pricing snapshot.

This assurance covers the existing canonical provider/model names only. Same-name endpoint and credential rotations still use the existing factory policy and both batches succeed with equal declared provenance. It does not prove endpoint, credential, physical vector-space or legacy lease identity. Ordinary null-provenance leases remain supported and explicitly unknown; protected Tapestry validation retains its existing fail-closed requirement and precedes the new guard. No new authority, schema, capsule or retry/cache policy is introduced.

## Causal TDD evidence

Terminal receipts are in the external evidence archive, together with exact argv/cwd/exit records and retained test-only source snapshot/hash proof.

| Receipt | Passed | Failed | Meaning |
|---|---:|---:|---|
| provider-behavioral-red.trx |10|23|Real SDK/raw HTTP shape, privacy, actual/unknown lease, stage attribution before behavior edits|
| actual-identity-red.trx |2|3|Real SDK/factory first and later identity drift; cancellation controls|
| provider-and-identity-green.trx |38|0|Initial minimal diagnostics/identity behavior|
| actual-cancellation-boundary-red.trx |4|2|New guard initially displaced canceled-token priority|
| complete-direct-first.trx |91|1|Existing provenance fixture used mismatched initial configured model|
| complete-direct-green.trx |92|0|Aligned fixture initial identity, preserved replacement-during-send proof|
| formatted-direct-green.trx |92|0|Owned formatting before mutation baseline|

Three compiler-only attempts have no TRX and receive no behavioral RED/green credit: missing test using, xUnit collection-count analyzer, and incorrect guessed key-resolver return type. Each was corrected before the actual corresponding terminal receipt. The existing provenance fixture correction aligns only initial configuration with its deliberate original lease, preserving hot reload and provider-metadata overwrite assertions.

## Mutation proof and exact restoration

Nine mutants each compiled and produced an actual failing terminal TRX. The entire nine-file then-owned baseline was verified after each restoration. The production/test/API/DESIGN bytes remain exactly those mutated; only plan progress and this implementation report followed. There is no formatting-equivalence inference or repeated unaffected mutant run.

| Mutation | Failed | Passed controls |
|---|---:|---:|
| Ignore structured HTTP statuses |12|2|
| Retain raw provider exception |14|2|
| Substitute current settings for actual lease labels |5|0|
| Emit unsafe identifier values |5|12|
| Send known mismatched identity |3|3|
| Reprice from reloaded settings |1|2|
| Let identity refusal preempt caller cancellation |2|2|
| Blame provider for accounting fault |1|12|
| Reuse previous lease diagnostics after a new resolution failure |1|4|

## Qualification scope and preserved contracts

Final qualification is a normal zero-warning solution build,19 complete named affected classes with terminal TRX/class census, both owned house-spacing modes, Roslyn whitespace verification and committed diff checks. Classes cover Weave/factory/accounting, batch consumer guard, cache/router, OpenAI embedding endpoint, Tapestry/entry/workspace consumers, Wizard, infrastructure DI, nullable constructor/Ef/direct-read boundaries and documentation contracts. Exact commands and final counts belong to the immutable terminal report.

The already integrated RV113 completion and cleanup path remains unchanged: independent accounting disposition semantics, frozen snapshot/ambient cutoff, actual run/reservation/type Warning and scope disposal. Paid cardinality mismatch is still recorded before refusal. Existing supplied and unrequested OperationCanceledException propagation, sequential batching, ordinary legacy lease behavior and protected validation ordering remain intact.

No RV111/RV127 credit, new schema/Core persistence, capsule, factory cache/retry policy, install, live provider/model, persistent configuration, operator command, main/push or other-worktree mutation is claimed. No global hosted discovery, whole-repository suite, coverage or shipping Native AOT/live-model qualification is included. Prior sealed candidates and branches remain untouched.
