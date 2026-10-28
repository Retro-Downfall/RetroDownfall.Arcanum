# Default tokenizer startup readiness (RV123)

The API's first context estimate currently constructs the embedded `o200k_base` vocabulary. Initialize this exact code-owned default deterministically after configuration validation and before request-pipeline configuration returns. Keep other internal requested encodings lazy. This moves local initialization cost to startup, with no provider, model-discovery, configuration, installation or network work.

## Accepted scope

- Reuse the existing `ConfigurationStartupValidator` startup filter and singleton `InferenceTokenizerResolver`; introduce no hosted service or authority surface.
- Preserve tokenizer results, fallback warnings, failed-construction behavior, token margins and output/context reserves.
- Serialize cold initialization only after a real four-caller probe reproduces duplicate construction. Keep cached lookups outside that lock and do not cache thrown initialization failures.
- Preserve the sealed operational-notices candidate and build from qualified controller checkpoint `22ef764335af59cd946347d05857373762e37b91` independently of it.

## Contract-first evidence

1. Actual production DI startup-filter registration and real tokenizer: initialization precedes pipeline configuration, first count uses the existing instance, and only the code-owned default initializes at startup.
2. Rejected raw configuration initializes nothing. An unknown encoding requested later retains the cached default fallback. Literal `hello world` remains two tokens.
3. Four concurrent cold callers return the same tokenizer and construct it once. The original implementation constructs four vocabularies in the controlled probe.
4. Preserve the genuine RED, excluded fixture mistakes, exact source hashes and raw TRX; then verify GREEN and causal omission reversals with exact restoration.
5. Measure five fresh offline managed Debug processes before and after, keeping binaries and command receipts. These measurements are descriptive and are not Native AOT timing or elapsed-time acceptance thresholds.
6. Run complete affected classes, native/AOT/source guards, normal zero-warning build and owned formatting checks; seal an immutable source/evidence package for independent review. Full CI and installation/live qualification remain separate campaign gates.
