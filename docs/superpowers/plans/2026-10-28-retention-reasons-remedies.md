# Retention reason codes and truthful reset remedies implementation plan

> **For agentic workers:** Use superpowers:executing-plans inline with TDD. No child agents. Root owns independent review and integration.

**Goal:** Resolve RV253/RV256 with stable closed reason constants and recovery-context-aware operator guidance, preserving every mutation/replay/CAS boundary.

**Architecture:** Retention authors complete guidance where native operation metadata is available; stopped-host conversion preserves it. Code-only reasons remain diagnostics, never authority. No new DTO, service port, schema, recovery/cancel/retry mechanism or PlanId cache.

**Tech stack:** Existing .NET10/native SQLCipher, source-generated JSON, xUnit/native/HTTP/CLI fixtures.

**Spec:** Approved scope and read-only design/acceptance at /private/tmp/arcanum-retention-reasons-remedies-preflight.md and /private/tmp/arcanum-retention-reasons-remedies-acceptance.md. Qualified base22ef764335af59cd946347d05857373762e37b91 includes independently approved a717 runtime/tests and canonical UTC bindings. Preserve a717 branch/packet. Root approved this base after wave97:2109passes/67families68actual/0skip/0warnings/all8gates.

## Global constraints and review focus

Keep every existing code spelling/status, eligibility predicate, current-operation exclusion, transaction/read-capability lifetime, plan binding, owner/revision condition and protected recovery gate. Current owner-bound mutationV4/factoryV2 cannot be generically cancelled/retried/reconciled. Pending LongRest cannot retry; protected and unknown-usage parks retain their actual authority restrictions. Unknown/unsupported metadata receives safe inspection guidance, never raw payload/kind/exception text. No unbounded aggregation236/243 or performance237/238 work. No live/provider/embedding/install/main/push or other worktree edits. No global Hosted rerun/zero-delta claim. Exact SQL/acquisition/catalog changes must be measured, not exempted.

## Task 1: RV253 closed reason contract

Files: ErrorCodes.cs; existing retention main/Pruning/FactoryReset/FactoryActivation sites; InstallationResetService comparisons; owning DESIGN/API/Command Reference; Build/DataRetentionReasonCodeContractTests.cs and native/API/CLI consumers.

- [x] Executable RED: reflect exact public const fields/values for14 missing reasons plus existing controls; require exact API reason table and no duplicate authored raw reason strings, including SQL labels. No missing-symbol compile RED.
- [x] Add exact constants and use at actual sites; parameter-bind SQL result labels to those constants without altering row selection. Add nested-diagnostic code/meaning/action table, distinguish enclosing HTTP errors and typed Covenant passthrough.
- [x] Native/HTTP/CLI controls preserve real pins/holds/batch/accounting/lease values, resource identities and data. Existing a717 expired-cleanup controls remain. Const rename/missing-doc/raw-SQL label causals must fail external literal expectations.

## Task 2: RV256 context-aware global/stopped-host guidance

Files: narrow DataRetentionService global conflict/projection helpers and FactoryReset borrowed-transaction reads; InstallationResetService canonical conversion; needed private pure formatter only; complete native/HTTP/stopped-host/CLI tests.

- [x] Native RED for actual owner-bound active/attention rows, ordinary recovered work, queued/parked LongRest, unknown/unsupported shape. Select minimum metadata with existing native reads, no full-row hydration/per-conflict lookup or predicate change.
- [x] Actual endpoint controls demonstrate owner-bound cancel/retry409 and exact unchanged row/checkpoint/revision; ordinary legacy controls remain and Pending retry stays refused.
- [x] Actual stopped-host planner/command RED proves contextual guidance survives without an appended generic cancel recipe, with no daemon removal, claim/journal/files/credential change. Supersede only unsafe old suffix assertions after real RED.
- [x] Implement closed source-authored guidance using existing recovery distinctions, same-operation resolve/restart/show/stop/re-plan for owners, state-appropriate ordinary guidance. Preserve standalone typed transition cause. No prose parsing or new authority.

## Task 3: Qualification and seal

- [ ] Expanded positive/negative/privacy/cancellation/native query-plan and sourcegen controls; complete actual owning classes including retention, installation/stopped-host, operations endpoint/store/recovery, API/CLI/docs/native/source/acquisition/AOT guards.
- [ ] Meaningful executable causal reversals with raw assertion TRX and exact full tracked+untracked source/tree restoration; rebuilt post-restore gate.
- [ ] Normal zero-warning solution build; owned house/compact/Roslyn whitespace/style/analyzers/diff. Freeze exact source throughout commands; strict capsule updates0.
- [ ] Explicit owned-path commit and immutable full source/Git/RED/causal/qualification package. Report exact source/member/query/lifetime map to Hosted closure owner; root independently reviews/integrates.

Ruling: base reconciliation initially assumed the old helper parameter spelling cmd. Actual approved base uses command; the corrected proof verified all9 a717 non-public-doc inputs plus four explicit UTC bindings before any repository file edit. No behavioral RED or runtime defect is claimed from that tooling assertion.

## Execution evidence so far

- Closed constant/API/source contract RED:16 genuine failures/2 existing controls, production unchanged22ef. Native/stopped-host contextual advice RED:14 genuine failures. Transition-inspection restart-remedy RED:2 genuine failures/22 owner HTTP controls. Initial fixture/compiler runs are retained but excluded: missing test LINQ cast, missing fixture import/null binding, and three guessed codec/enum symbol names corrected to their existing APIs.
- First GREEN32; expanded installation/HTTP/native190; complete seven-family owning gate945/0skip. Native SQL projection now independently asserts all3 fixed wire labels/resources. Codec-valid launch-shape controls are distinct from malformed stored-payload controls and do not claim authenticated owner authority. Strict native/source/acquisition/SQL/doc controls161PASS after the final test-only symbol correction.
- Existing suffix assertions were deliberately superseded after the genuine native/CLI RED: code-only cancellation/retry advice was unsafe for current owner versions and Pending Long Rest. No mutation, recovery, CAS, lease, checkpoint, table eligibility, current-operation exclusion, confirmation, cancellation or public DTO/status semantics changed. The borrowed factory writer recheck executes the same two operation predicates and is independently tested against preview guidance and owner exclusion.
- All6 production paths already existed. The exact14 new constant values are the existing wire values, and3 SQL-projected reason labels are now constant parameters. Four added safe operation columns in the existing preview and writer-recheck reads select closed guidance; checkpoint payload/reference/public-summary are never selected for it. No new native acquisition, source catalog path, schema object/version or package.
- Failed inspection remains its original typed cause and retains evidence; only a positively inspectable unfinished transition receives an attempted startup-recovery suggestion, and neither ending promises restart repairs missing or corrupt evidence. Ordinary/queued/unknown-usage/protected Long Rest advice retains its actual authority distinctions.

## Independent review correction

Root identified a concrete remaining RV256 gap before approval: preserved guidance began with HTTP-backed show/cancel while the reset had stopped the host. Actual native/stopped-host ordering RED16fail/3real-client network controls now proves it for owner-bound, Pending, parked, unsupported and ordinary forms. The existing Show/Cancel/Retry handlers call ArcanumApiClient HTTP operations; an unavailable fake HTTP transport produces exit3 with actual operation requests and no local fallback. Conditional start-before-inspect/action wording now preserves evidence if startup cannot serve and never promises startup repairs the record. This is the same RV256 finding, no extra original credit.

The first final driver stopped at whitespace verification (normal build0warnings, house/compact passed), before any whole-union test ran; two formatting diagnostics in already owned existing code are retained. They are style failures, not behavioral RED or a passing final gate. The corrected candidate receives fresh causal/full owning/static receipts.

Corrected host prerequisite GREEN55PASS/0skip includes the complete OperationCommandTests class and native/stopped-host/HTTP controls. Conditional startup precedes every suggested inspection/action, while inability to serve preserves evidence. Prior partial-final whitespace failure remains explicitly retained, and all causal/final source bindings are refreshed for this correction.
