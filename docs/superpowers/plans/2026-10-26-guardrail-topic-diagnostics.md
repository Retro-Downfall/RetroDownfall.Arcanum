# Guardrail topic diagnostics — RV100 (2026-10-26)

Basef0575e6f above runtime24dd4a704139d695c93408fd7d37ac65f6b89499. Reuse isolated review-maintenance-outcomes worktree on codex/review-guardrail-topic-diagnostics; approved compaction branch7e7ab5d0 is preserved. OriginalRV100 is in the October24 review register.

## Behavior

An input that matches no allowed topic must say that rather than claiming a match. Validate every nonblank authored allowed/blocked regular expression in ConfigurationValidator using the runtime IgnoreCase/CultureInvariant syntax and500ms match budget; name exact configuration list/index and fixed repair guidance without echoing pattern or regex exception. Keep null/blank runtime skip semantics and existing feature-disabled pass-through. Invalid runtime patterns remain skipped for compatibility but emit one safe field/index warning per configuration snapshot, shared across input/output and concurrent requests; a new snapshot can warn again. Use weak snapshot ownership and bounded per-config index keys rather than unbounded global history or subscriptions.

Confirmed adjacent privacy issue: current invalid-pattern logs include the pattern and exception; timeout logs also retain RegexMatchTimeoutException containing input/pattern. Remove raw pattern labels and exception objects from these diagnostics, using closed PII labels or topic field/index. Preserve timeout fail-closed behavior/audit and all current-turn PII guards. No audit/authority policy change.

## TDD and delivery

Add behavioral config/pipeline RED first: missing allowed topic, indexed malformed patterns with feature on/off, concurrent warning deduplication and reload, private pattern/input/exception absence, timeout still blocked and audited. Valid regex/null/blank, feature-off and existing malformed-runtime-skip controls remain. Apply smallest source repair; verify causal regression mutations and exact restoration. Complete Guardrails/config/route/serialization/doc/boundary classes, normal0warning build, owned style and immutable package; independent review before integration. Owning docs API/Compendium/DESIGN; no schema/new config keys/liveproviders. FullCI and user pause before localinstall/Ollama/embedding remain terminal requirements.
