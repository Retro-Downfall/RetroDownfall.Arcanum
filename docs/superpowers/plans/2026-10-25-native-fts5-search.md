# Native FTS5 Search Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Keep behavioral RED, smallest GREEN, mutation RED and exact restored GREEN.

**Goal:** Support the complete native FTS5 query grammar in archive and session transcript searches.

**Architecture:** Bind unchanged query text to native MATCH. Use typed Result failures for query validation, preserve actual storage failures and the existing session title branch, and publish accurate tool/API/CLI guidance.

**Tech Stack:** .NET 10, existing native SQLCipher FTS5, source-generated JSON and current repository/admission composition.

**Spec:** `docs/superpowers/specs/2026-10-25-native-fts5-search.md`

## Global constraints

No new runtime dependency, reflection serialization, SQL interpolation, weakened authority or personal installation changes. Existing query-length/result limits stay; do not silently truncate native expressions. Original RV072 and additional FTS5 work share implementation but remain separately described in the register.

## Review focus

Invalid expressions on an empty corpus or a title-only result must still be invalid. Native failures must not be misclassified as user syntax errors. Quoted punctuation and column filters must retain native semantics. Expression length must not alter grammar. Session pagination keeps existing dates and stable IDs.

### Task 1: Native grammar and typed failure propagation

**Files:** GrimoireRepository.cs, SessionRepository.cs, IGrimoireRepository.cs, ISessionRepository.cs; a focused shared Infrastructure/Data FTS5 validation helper if required; SearchArchives MCP handler; SessionEndpoints/CampaignEndpoints; their test fakes and tests.

- [x] Run Fts5SearchIntegrationTests against unchanged repository search (40 native grammar cases: initial RED 28 fail, 12 controls pass).
- [x] Bind the original expression in both repository MATCH paths and run all 40 cases.
- [x] Add real API/MCP invalid, empty-corpus, overlong, cancellation and storage-failure tests before changing their paths. Return Result<string> from archive search and Result<SessionQueryResult> from session query; migrate real consumers and test implementations without default-success fallbacks. Existing Validation.InvalidQuery maps to 400 and MCP tool errors.
- [x] Implement bounded native validation and narrow syntax classification, reject whole overlong expressions, then pass failure-path tests. Prove no query/native exception content in logs and no injection effect.
- [x] Update tools/list, schemas and owning DESIGN/API/CLI docs with examples and actual title behavior.
- [x] Run affected suites and architecture guards, mutate runtime behaviors meaningfully, restore exact bytes and rerun. Verify zero-warning build, both blank-line modes, whitespace and diff checks.
- [ ] Complete independent review and controller-owned terminal qualification after the local candidate commit.

Full final hosted closure/capsule, all suites, CI, AOT and isolated live qualification remain controller-owned.

## Implementation evidence

Native grammar baseline: 28 failing and 12 passing cases before unchanged expressions were bound; 40/40 after the minimal grammar change. Typed invalid-query baseline: 7 failures. Expanded native controls: 67 passed, then 375 affected/guard tests passed. Final restored affected suites: 640 passed, zero failures or skips. Solution build: zero warnings and zero errors. Both scoped blank-line modes, dotnet format whitespace verification and git diff checks passed. Three controlled mutation runs produced 11, 8 and 5 expected failures respectively; exact candidate bytes were restored and SHA-256 checked after each run. Local logs are `/private/tmp/arcanum-fts5-*.log`; the delivery report is `/private/tmp/arcanum-fts5-report.md`.

Ruling: native parser validation uses a bounded one-row MATCH probe on the existing admitted scoped connection. The session list can otherwise short-circuit MATCH on an empty corpus or title hit, so validation cannot rely on enumerating its final result. No additional whole-result scan or handwritten FTS grammar was introduced.

Ruling: storage exceptions retain their existing infrastructure failure path rather than being converted into Validation.InvalidQuery. Only recognized native parser diagnostics in the fixed validation command are mapped; diagnostics and query text are not logged or returned.

Full repository suites, shipping Native AOT, CI and isolated live qualification remain controller-owned; the local affected suites are not a claim that those terminal gates ran.
