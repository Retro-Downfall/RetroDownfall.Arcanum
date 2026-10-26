# RV164/RV165 attachment drift reliability

Base: controller `c2de747d9ec5760d120382687fc915d955cf83f6`; preserve all earlier grapheme/harness branches. Inline executing-plans/TDD; no children. Scope is CommandCenterAttachmentDriftMonitor, owning tests, DESIGN and this plan. No Host change is needed unless new evidence requires coordination with root.

Binding claims: review register RV164/RV165; DESIGN §10.2.5 and telemetry indexing contract; API GET session attachments remains the authenticated revalidation/authority boundary. Filesystem notifications only invalidate hints; no client hashing, badge promotion or source authority. No schema/API changes, nullable service exemption, terminal initialization, models, installs, config or push.

## Approved design

- Preserve the production DI constructor. Add explicit internal watcher-factory/enable/TimeProvider injection for deterministic real event callbacks and fake-clock/HTTP tests, with no OS watcher enable in those fixtures. Keep event subscriptions in Start to preserve observer classifications.
- Start the refresh pump independently of optional watcher construction/enable. Contain expected failures only at those operations, dispose partial resources, warn with closed reason and actionable explicit-refresh guidance, never arbitrary exceptions/messages/paths. Unexpected programmer/config corruption must not be swallowed. Capture the lifetime token before scheduling; cancellation/disposal are idempotent and still stop the pump after watcher startup failed.
- Startup, RequestRefresh, post-turn/slash requests and pending-index polling remain effective without workspace sources or a watcher. Keep pending polling after transient GET failure, without a tight retry loop. Error/overflow conservatively requests backend revalidation.
- Ordinary events require current WorkspaceFile sources and path relevance. SourceWorkspaceIdentity is opaque and must not be treated as a path; do not join SourceRelativePath to WorkingDirectory. Use conservative segment-aware full-path suffix/source-ancestor matching, old/new rename paths, safe casing and lexical normalization. Unknown mappings and directory/root moves that cannot be ruled unrelated retain bounded conservative hints. Document that overmatching grants no authority. Cover a working directory below the registered root.
- Use atomic pending reason bits with one bounded wake slot, retaining explicit/poll reasons arriving during debounce, spacing waits or a fetch. Fixed 150ms coalescing plus monotonic minimum one-second interval after the preceding logical revalidation completes prevents sustained hints from producing a fetch storm or indefinitely postponing refresh.

## TDD and delivery

1. Add only behavior-preserving operation/clock injection; observe genuine RED for irrelevant/no-source storms, cadence, relevant suffix/rename/ancestor handling and optional watcher creation/enable failures. Keep explicit/error/pending controls. Record source hashes and fixture/tool failures separately.
2. Implement the smallest filtered scheduler/startup fallback. Tests exercise actual monitor pump + ArcanumApiClient against fake authenticated HTTP, real FileSystemWatcher event dispatch, deterministic TimeProvider and safe structured log values. Extend owner/session-switch, in-flight reason retention, pending failure retry and cancellation/disposal coverage.
3. Prove causal mutants for removed filtering, cadence, pump-before-watcher-failure and reason retention as appropriate. Restore every owned path exactly. Run complete owning/adjacent classes, normal zero-warning solution build and owned style/source observer checks; no broad exemption changes.
4. Commit explicit owned paths; qualify exact candidate/source hashes, seal immutable full-context diff/snapshots/receipts and report for root's independent review. Retain conservative path-mapping limitations and any genuine separate finding. No integration/push or live qualification.

## Rulings and evidence

- Source-relative metadata and workspace identity cannot establish an absolute client root. Root approved conservative suffix/ancestor comparisons and uncertain directory/root hints; ordinary unmatched create/change churn is filtered, and uncertain rename/deletion can overmatch without granting authority. NFC/NFD spelling equivalence is advisory only.
- Root approved pacing from operation completion after a real delayed-credential RED showed entry-point pacing could compress successive operations. API-internal authentication/retry sends remain the API client's existing contract.
- Root clarified that newly queued reasons survive failed/cancelled attempts; ordinary failures do not create permanent automatic retries. A proposed contrary test was removed before any retry implementation and retained as superseded evidence, not a product defect. Known Pending projections alone keep self-polling after transient failure, at bounded cadence.
- First fixture failures (event-emitter member hiding; fake API authority mismatch) are retained separately from genuine RED. Valid initial RED: 6 failures/7 controls; pending retry RED: 1 failure; final pacing/blank-metadata RED: 2 failures/2 controls; NFC/NFD hint RED: 1 failure. Current whole monitor classes: 36 passing cases before final style/qualification.
