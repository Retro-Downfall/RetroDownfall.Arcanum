# RV105 semantic routing diagnostics implementation report

Exact source baseline: `1e71847ec5450ad0bddf764619e8c6dea5ee3eda`; branch `codex/review-semantic-routing-diagnostics` in the assigned maintenance-outcomes worktree. The prior guardrail branch remains at `45b6279e64c82240b0e3f53bcc08bef8530fe079`. Controller staged RV030 work was not used. This tracked report describes source and causal proof before freeze; the separate immutable terminal manifest binds qualification to the exact candidate commit/tree. No integrated credit is claimed.

The scoped SemanticSpellRouter now consumes IOptionsMonitor and captures CurrentValue once at ResolveAsync entry, before awaits. That same object supplies the resolved policy and diagnostic epoch. A static ConditionalWeakTable holds a six-bit atomic closed-reason state per weak settings-object key, with no event subscriptions, private payload, logger reference or unbounded reason dictionary. Real AddOptions DI tests prove distinct scoped routers and request snapshots share the retained monitor epoch; two scopes and eight parallel scopes warn once per reason. Replacing the actual monitor cache creates a fresh epoch and permits a new warning even when configuration values are equal. An in-flight failure retains its captured old epoch after replacement.

Warnings use only the six fixed local reasons catalog_unavailable, provider_unavailable, prompt_embedding_failed, dimension_mismatch, catalog_vectors_missing and unexpected_error, plus safe exception type (none for non-exception branches). Fixed guidance explains that full-catalog LLM routing may add billable provider work and names documented integrations.embeddings.provider, integrations.embeddings.model, integrations.embeddings.dimensions and the supported arcanum doctor command. No raw exception object/message, Error.Code, prompt, spell name/path, provider/model value, endpoint or credential is emitted. The cache retains its existing Debug level and uses closed local catalog_embedding_failed/catalog_refresh_failed reasons and safe type, preserving failed-Result versus unexpected-refresh provenance.

Disabled routing, empty catalogs, genuine pure no-match and requested cancellation remain normal controls without failure Warnings. Unrequested cancellation retains the existing unexpected fallback behavior. Current pure/hybrid policy remains code-owned; scoring, threshold/top-K, hybrid threshold no-match Debug, candidates, default fallback, cache keys, locking, re-embedding/invalidation and embedding observations are unchanged. No cache/performance correction or RV127 credit is claimed. Three original constructor-test sites changed only from TestOptionsSnapshot to TestOptionsMonitor. No unrelated Wizard production behavior changed.

Genuine Wizard controls assert physical LLM calls and recorded Routing/Chat billable operations: healthy pure embedding selection uses one LLM call with no Routing operation; disabled and unavailable-provider fallback use two LLM calls with one Routing and one Chat operation. No real provider or operator command was invoked.

| Terminal receipt | Passed | Failed | Meaning |
|---|---:|---:|---|
| actual-red.trx | 8 | 11 | Six real fallback reasons, scoped/parallel/in-flight epoch failures, independent cache privacy and typed-failure provenance; normal/cancellation/Wizard controls passed. |
| focused-green.trx | 19 | 0 | Minimal diagnostic implementation over original RED set. |
| added-controls-green.trx | 21 | 0 | Explicit healthy billable-call and unrequested cancellation controls. |
| formatted-direct-green.trx | 58 | 0 | Complete embedding router/cache/LLM-router classes and genuine Wizard billing controls. |

The initial compiler-only attempt (nullable-flow and xUnit assertion analyzer findings) is retained without behavioral credit; normal analyzers stayed enabled. The valid test-only diff/source hashes precede production changes.

Meaningful mutants, each with a terminal failing TRX and exact restoration of all eight then-owned hashes:

| Mutation | Passed | Failed |
|---|---:|---:|
| fallback-hidden-at-debug | 6 | 9 |
| request-local-warning-state | 0 | 8 |
| inflight-failure-uses-new-epoch | 6 | 1 |
| one-bucket-suppresses-other-reasons | 6 | 1 |
| router-retains-raw-exception | 12 | 3 |
| cache-emits-arbitrary-error-code | 6 | 3 |
| cache-retains-raw-exception | 8 | 1 |

Final gate boundary: normal zero-warning solution build; complete router/cache/LLM-router and both changed Wizard consumer classes, Weave consumer, real service registrations, strict constructor/AOT/acquisition/documentation guards; both owned house-style modes, Roslyn whitespace and committed diff checks. These gates run after commit freeze and are reported with exact argv, logs, terminal TRX, full class census, source/evidence hashes and archive verification in the immutable candidate package. Source plan final-stage boxes remain pending at freeze so qualification does not change tested bytes. No global hosted discovery, whole-repository suite, coverage, shipping Native AOT/provenance, live provider/Ollama, installation, persistent configuration, main, push or other-worktree mutation is claimed.
