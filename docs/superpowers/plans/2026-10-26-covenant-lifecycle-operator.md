# Covenant Lifecycle and Operator Reliability Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans and test-driven-development inline. No child agents. Root independently reviews and integrates the frozen candidate.

**Goal:** Repair RV203/204/205/210/212/213/216 with truthful mutation/recovery and actionable private-data-safe refusals.

**Architecture:** Bounded fresh operator transaction attempts preserve receipt-first replay and stop at COMMIT/uncertainty. Repair distinguishes positive no-mutation evidence from changed/unknown effects using existing journal edges. A closed compiler diagnosis feeds operator/review/MCP messages, and gate refusals describe the actual blocking operation/state without changing authority.

**Tech Stack:** .NET10, existing native SQLCipher fixtures and progress/authorizer/SQL traces, xUnit, existing generated JSON and real MCP capability harness.

**Spec:** Root-approved dated `/private/tmp/arcanum-covenant-lifecycle-operator-preflight.md`; exact U24 register clauses; DESIGN10.17/10.22, API8.28/8.34, Command Reference memory Covenant contract.

## Global Constraints

- Base approved `ee7994d5cf6b26044a00250338fabc2a7d853846`, preserving its branch and immutable package. No mutable controller import.
- Core29 is root-owned for RV145. Core28 and all predecessor fingerprints stay frozen. No schema/version change assumed; coordinate any proven durable-schema requirement before editing it.
- Only positively proven no-mutation permits clean Abandoned; repeated equal catalog digest alone is not proof. Unknown repair/rollback/disposal/COMMIT effects remain closed/manual, never a benign retry.
- Preserve exact authority/scope, target/epoch/token binding, replay-before-expiry, revocation/drain, idempotence, fingerprint release, rollback and one-shot irreversible dispositions.
- Compiler Unicode/key/default acceptance, policy versions, rendering/hashes and corpus remain unchanged. Diagnostics carry only closed reasons, declared policy and safe scalar position/code point, never raw exception text or submitted content.
- Maintenance port remains absent; current feature/health/capability-controlled Covenant tool advertisement remains unchanged. No install/live provider/embedding, operator config, main or push.
- Known-open global Hosted proof is not rerun unchanged or claimed green. Send exact source/member/path/SQL/lifetime delta to its owner; reconcile only actual candidate catalog additions.

## Review Focus

- Busy wait must not commit after lease revocation or retry after a durable COMMIT/publication/uncertain cleanup.
- A failed executor can have changed schema; no Result failure or matching catalog digest proves otherwise.
- No-op pending revision1 and committed pending revision3 must preserve distinct terminal/disposition semantics across restarts.
- Invalid invisible characters after supplementary scalars need Unicode-scalar positions without echoing content.
- Multiple overlapping closures must name an actual blocker, preserve unaffected scopes and never imply automatic restart can fix deterministic manual recovery.

### Task 1: Bounded operator mutation and curation attempts (RV203)

Files: CovenantMutationService.cs/.Curation.cs; narrow retry/diagnostic partial only if necessary; CovenantMutationServiceTests partials; real mutation/curation route fixtures and docs.

- [ ] Add native held-writer RED for set/retire/correct/curate: a prepared operation returns Covenant.Unavailable instead of throwing; exact head/version/receipt/fingerprint state unchanged.
- [ ] Retain actual RED on unchanged production. Lower native test timeout only; no retry seam or product change before this evidence.
- [ ] Implement bounded existing SqliteBusyRetry/cursor policy with fresh transaction and per-attempt resources, current lease revalidation, safe closed storage diagnosis and same-request503 guidance. Required logging dependency must not add a nullable-default service exemption.
- [ ] Add RED/GREEN for exhaustion, release-during-delay success exactly once, cancellation/revocation, busy receipt reads, cleanup failure and post-COMMIT publication boundary as actually reached. Do not retry uncertain rollback/disposal or COMMIT.
- [ ] Complete mutation/curation/concurrency/receipt/replay/authority/fingerprint and actual HTTP status classes; update API503/design guidance.

### Task 2: Repair outcome and journal/restart truth (RV204/205/213)

Files: CovenantSchemaRepairExecutor.cs, CovenantSchemaRepairJournal.cs, CovenantSchemaRepairStartupRecovery.cs, CovenantMaintenanceService repair/finalizer methods, narrowly necessary bootstrap diagnostics, CovenantSchemaRepairTests partials and native executor fixtures.

- [ ] Add real strict journal RED for ReopenPending revision1 with valid and invalid canonical tier (Abandoned/no repair), committed revision3 health controls and exact single owner/terminal revision.
- [ ] Add real request/restart RED for positively non-mutating refusal, actual durable phase after advance/finalizer failure, partial index DDL failure and unreadable/malformed/duplicate journal safe diagnosis. Generic unknown-effect failure remains a keep-closed positive control.
- [ ] Introduce a narrow internal typed execution outcome for positively no change, known change and unknown effects plus closed failure; no message parsing or catalog-only no-change inference. Existing strict graph/CAS remains unchanged.
- [ ] Route positive no-change through Prepared→ReopenPending→Abandoned. Record changed truth before permitted health progression; unknown effects remain active/closed with actionable manual recovery. Preserve exact original owner and digest checks; do not manufacture automatic retries.
- [ ] Report actual last proven durable phase. Add one safe contextual warning per failure with typed operation/action/phase/stage/cause, distinguish restartable finalizer/publication failures from deterministic manual recovery. Remove arbitrary driver/refused-invariant messages from public errors/logs without losing closed cause.
- [ ] Complete journal/executor/maintenance/startup/finalizer/bootstrap classes and docs. Confirm no schema diff or version/fingerprint change; coordinate before any contrary requirement.

### Task 3: Actionable shared compiler refusals (RV210/216)

Files: narrow Core compiler/default/key metadata seam, MutationService/review maps, MCP CovenantTools/CovenantSchemas, compiler/corpus/default/service/review/MCP tests, owning API/design/CLI docs.

- [ ] Add wire/service RED for U+200B, prohibited control, supplementary-scalar prefix, whitespace-only, invalid UTF16, byte cap and typed-default newline. Assert declared field, code point and1-based scalar offset as meaningful; no BCL parameter or submitted content echo.
- [ ] Add genuine MCP capability RED for invalid key/oversize/format input: InvalidKey versus InvalidContent, actionable fixed policy, no staged intent, closed structured log; preserve current advertisement/admission conditions and forbidden authority fields.
- [ ] Add closed typed compilation refusal compatible with ArgumentException callers; map contextual messages using its reason/position only. Do not add a second scalar/normalization policy or parse exception.Message. Unknown compiler failures retain private generic guidance.
- [ ] Preserve review's current Validation.InvalidBody and decision-ordinal wrapper; its already-fixed raw-exception behavior is a control, not new credit. Document exact grammar, UTF8 cap, Format/control/whitespace and typed-default no-whitespace rules.
- [ ] Complete Core corpus/default/compiler, operator/review, real MCP and sourcegen/contract classes. Assert accepted artifacts and hash corpus unchanged.

### Task 4: Truthful operation-gate refusal context (RV212)

Files: CovenantOperationGate.cs; existing gate/concurrency tests and narrow partials; owning design/API guidance.

- [ ] Add behavioral RED for live acquisition/drain, live exclusive, completed KeepClosed, disposed-without-disposition and conflicting recovery owner. Assert actual operation and wait-versus-recovery context, no private identity.
- [ ] Derive a deterministic actual intersecting closure under existing lock; use closed operation/state labels only. Do not change closure selection, owner matching, coverage or logging behavior.
- [ ] Complete unaffected scope, same-owner resume, multiple closure, revocation/drain/cancellation/one-shot finalizer controls and whole gate classes.

### Task 5: Exact candidate qualification and handoff

- [ ] Record actual new source/SQL/acquisition/member/lifetime delta; refresh only real catalog entries and send to Hosted closure owner. Keep global proof/fullCI explicitly open.
- [ ] Run meaningful causal mutations for each new boundary; retain failure TRX and exact owned-source restoration after each.
- [ ] Freeze source before complete affected classes/guards/docs, normal zero-warning solution build, house/compact/Roslyn/diff checks. Distinguish fixture/compile/tool failures from behavioral RED and any inherited open gate.
- [ ] Commit explicit owned paths, verify clean branch and parent/tree/blob identities; seal immutable parent/candidate snapshots/full diff/TRX/log/hash report for independent root review/integration. No push or installation/live qualification.

## 2026-10-27 Recovery ruling and verification notes

The native autocommit data-change probe proved that an unchanged catalog digest cannot make an interrupted Prepared repair safe to replay. Root approved the conservative no-schema rule: recovered Prepared never reruns; only exact-catalog plus the pure original invalid-action dispatch refusal can establish no mutation. Existing automatic-resume tests intentionally now exercise committed recovery or fresh live-owned execution, preserving health/owner/finalizer assertions. Before-source fixtures and genuine crash/restart RED are retained. Core29 remains root-owned; no schema file or version changes here.

Expanded evidence includes partial native index DDL, impossible phase/revision pairs, failed Prepared publication, truthful KeepClosed response phase, native inspection interruption/cancellation, COMMIT contention without retry, closed compiler-log values, and actual BEGIN-stage exhaustion. Fixture/compile-only attempts are retained separately and receive no behavioral RED credit.
