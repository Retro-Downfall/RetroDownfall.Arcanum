# Input guardrail audit context — RV-107 (2026-10-26)

Baseb0d987c0d07a0a7e643542ef6d511762647df988; the RV100 parent remains separately sealed for review. Original finding RV107 names missing input SessionId and default model metadata. The input gate intentionally runs before model acquisition and must remain there.

Pass the requested SessionId into input violation audit context. Resolve the prospective canonical model using the existing pure ProviderResolver only: no provider acquisition, network work, session lookup or new session creation before guardrails. If configuration cannot resolve a model, preserve the requested model value and the guardrail refusal. The model field describes preflight selection, not a completed provider call or eventual fallback. A sessionless/stateless rejection keeps SessionId null. This context never grants session/campaign authority.

TDD: buffered/streaming x supplied/absent SessionId x explicit/default model; unresolved model retains guardrail precedence and zero chat calls. Then minimal helper change, complete Wizard/guardrail/audit route and owning doc checks, causal original-context mutation, exact qualification and independent review. No schema, config key, public DTO shape or production path additions. User pause before installation/live Ollama remains in force.
