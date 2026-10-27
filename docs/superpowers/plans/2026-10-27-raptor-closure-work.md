# RAPTOR native closure work implementation plan

**Goal:** Remove proved generation-wide scans from actual closure reads and checkpoint recounts while preserving exact DAG semantics.

**Architecture:** Drive the existing recursive frontier through existing generation/edge/node indexes with explicit SQLite CROSS JOIN order. Keep every recursive UNION, DISTINCT SourceId, origin, generation predicate and transaction unchanged.

**Spec:** Arcanum.DESIGN.md §21.11; approved investigation and query proposal under /private/tmp/arcanum-raptor-closure-*.md.

## Constraints

Base 2044493330d5e36d21f9540d4d9843ecf1c4b3e4 is preserved on its sealed branch. No schema, numerical recipe, provider, authority, configuration or publication changes. No live calls or installs. Full Task12, 50k consumer completion and root-owned full CI remain open. No large rerun or timeout increase without independent repaired-candidate acceptance and root authorization.

## Steps

- [x] Diagnose using actual SQLCipher service calls at 256/2048 leaves and unrelated native edges, exact VM counts, captured SQL and query plans. Preserve diagnostic-only SQL replay and its exact result equality as evidence.
- [x] Prove three closure-work REDs plus an independent checkpoint-recount RED against unchanged production; seven cancellation/transaction/generation/diamond controls pass on the base.
- [x] Apply only the approved frontier-first joins in ReadClosureAsync and the checkpoint recount.
- Qualification requires focused GREEN and complete affected classes/guards with terminal TRX, a normal zero-warning build and all owned C# styles. The sealed qualification receipt is authoritative for terminal status.
- [x] Independent closure/recount query-order reversions fail three/one causal cases respectively, with eight/ten controls, restoring all 5,030 source hashes after each.
- [ ] Freeze immutable commit/tree, full-context diff, source/binary binding, commands and terminal evidence for independent review. Do not infer 50k completion from these small probes.
