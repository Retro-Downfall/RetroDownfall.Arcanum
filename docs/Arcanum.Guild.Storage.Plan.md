# Guild Storage Implementation Plan

> For agentic workers: use `superpowers:subagent-driven-development` and TDD for each implementation task.

**Goal:** Deliver issue #145's protected storage and Roster foundation from main, preserving the dormant execution boundary.

**Architecture:** Guild is an independently admitted optional SQLCipher family depending on healthy Core. One immutable protected content representation backs normalized Card revisions. One owner cleanup coordinator processes all participants before advancing either deletion cursor. Existing maintenance, sensitivity purge and outbound policies remain the enforcement boundaries.

**Tech stack:** .NET 10, generated JSON, admitted parameterized SQLite/SQLCipher, existing no-follow authored-file reader.

**Spec:** `Arcanum.DESIGN.md` §§23.3, 23.15 and GitHub issue #145; prerequisite #310 is delivered at integration base `f5740424`.

## Constraints and decisions

- Preserve historical schema fingerprints and numeric values; append GuildContent artifact 17 and retention 32/33/34.
- No reflection JSON, EF query reads, raw authority/credentials/reasoning bodies, or arbitrary total-work ceilings.
- Guild storage availability depends on Core, independently of an unrelated Covenant fault. Protected operations still require their relevant Covenant services.
- Both Guild cleanup cursors start at zero. Missing owners refuse admission immediately. Purged means bytes are already deleted; Unlabeled permits revalidated ordinary deletion; Blocked/failure forbids deletion and cursor advance.
- Keep public Guild execution/configuration/HTTP/CLI activation dormant. No Ward approval gate.
- Declared explicit ids are installation lookup aliases to host-generated identities. Selectors compare ordinally; only `global:` and `workspace:` prefixes narrow source kind. Workspace comes from explicit host context.
- Reuse the existing effective Spell authored-file byte bound for direct agent Markdown; normalized stored content remains at most 65,536 UTF-8 bytes, with oversized encoded payloads refused before acceptance. Remote input uses an exact host-configured peer/interface, never an endpoint selected by prose.
- Preserve the unrelated dirty primary checkout; work only in the isolated issue branch.

## Review focus

- Historical upgrades preserve real labelled data and published pins, including offline/restore admission.
- Same-name and explicit-id conflicts are deterministic under concurrent refresh; old pinned bytes cannot change.
- Deleted owners, protected content and mid-read file replacement fail closed before materialization or new admission.
- A blocked cleanup participant or crash cannot let another participant skip an event.
- Healthy Guild storage remains usable during unrelated Covenant damage; cancellation never becomes schema damage.

## Tasks

- [x] **Schema and storage:** Add Guild family/tier, manifests, initializer, ownership, availability and declared Core 37→38 registry transition. Add canonical content, Card heads/revisions/skills and evidence tables. Store generated Card/guidance/tool-request payload once. Write real SQLCipher fresh/upgrade/data preservation/admission/immutable-revision tests, observe failures, implement, rerun.
- [x] **Sensitivity routing:** Extend enum, typed validation and policy manifest without changing prior literals. Add Guild purge policy and kernel plan, matching exact owner/revision/digest and retaining required evidence. Write failing registry/label and real kernel purge tests; implement and verify.
- [x] **Roster discovery:** Implement strict parser, direct no-follow global/workspace discovery, exact-byte source identity, immutable refresh, duplicate/rebind policy, selectors, derived ownership, fixed-peer remote refresh and admitted pagination. Write failing malformed/file-race/conflict/pinning/invalid-refresh/cursor tests, then implement.
- [x] **Retention and owner cleanup:** Append classes and disabled timer defaults; inventory/measure/prune/reset. Build one coordinator with zero cursors and all-table final transaction, purge outside its write transaction, final label/owner checks, bounded orphan sweep and resumable replay. Test labelled/unlabelled/blocked outcomes, both owner types, multi-table failure and crash recovery on SQLCipher.
- [x] **Integration and documentation:** Register storage/cleanup without enabling executable Guild; update DESIGN and relevant API/CLI/configuration/engineering/human references to distinguish delivered from proposed behavior. Update generated JSON, AOT/direct SQL, ownership and bounded-materialization inventories.
- **Review and delivery gate:** Run focused tests, solution/client suites, coverage, Native AOT and native asset gates; independent code review and fix findings. Qualify the exact candidate through CI, merge to main only when all required checks pass, push, verify remote refs and issue state.

## Evidence

- Baseline Guild contract suite: 334 passed, 0 failed, 0 skipped on integration base.

- Focused implementation and regression suite: 465 passed, 0 failed, 0 skipped.
- Broad schema, documentation, generated API and architecture contracts: 1,241 passed, 0 failed, 0 skipped.
- Native Guild graph/storage-payload/parser smoke, strict native SQLCipher verification, Release solution build and first-party AOT IL gate passed. Complete runtime coverage, client suites, shipping smoke and CI remain delivery gates.
