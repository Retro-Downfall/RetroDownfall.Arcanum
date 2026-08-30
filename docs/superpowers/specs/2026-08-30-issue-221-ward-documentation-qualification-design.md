# Issue #221: Ward Documentation Sweep and AOT Qualification

**Status:** Approved design, pending implementation.

**Branch:** `codex/issue-221-ward-documentation-sweep`, cut from the tracked `remove-wards` aggregation branch at `165e23656b48c618ca67b9cdd76edb3e3256f1a3`. That branch contains current `main` at `decdf011f69ab91c1e48a0d50c2bbf97cd928162` and the completed #216-#220 Ward-removal slices.

**Issues:** Delivery slice #221 under epic #197. Issues #216-#220 are closed prerequisites. Issue #230 is an intentional open follow-on under the same parent and remains separate because it changes Annals behavior. Completing #221 does not close #197; the parent stays open until #230 is delivered.

## 1. Objective

Make every governed product document describe the finished Ward-removal surface coherently, publish the complete 31-name server-tool vocabulary as the standing answer to which tools are Ward-gated, and qualify that finished surface through the repository's Release, Native AOT, and documentation gates.

This is a contract and qualification slice, not a new runtime-behavior slice. The implementation may change tests, canonical documentation, code-owned help descriptions, and generated documentation. It must not change tool admission, execution order, persistence, configuration semantics, Ward event shapes, or any retained safety and capability boundary.

## 2. Approved decisions

### 2.1 Governed-document boundary

The claim and issue-reference sweep applies to the canonical documents and structured inventories governed by `DocumentationIssueReferenceTests`, plus the root `README.md` where issue references are explicitly allowed.

The dated review snapshots and the plan/specification archive under `docs/superpowers` are historical records of completed work. They are deliberately preserved rather than rewritten to make their past-tense design context look current. The new #221 design and plan are part of that archive.

### 2.2 Follow-on issue #230

Issue #230 remains included in the #197 child accounting but is not a prerequisite Ward-removal slice. Its Annals default, claim-write failure policy, and possible backfill require a separate design and TDD cycle after #221.

Closeout for #221 must report #230 as open/Ready and leave #197 open/In Progress. It must not implement, close, relabel, or reprioritize #230.

### 2.3 No product behavior change

The code shipped by #216-#220 is the runtime authority for this slice. If the documentation sweep discovers a real mismatch between that implementation and the approved record-only contract, stop and surface it before broadening scope. Do not quietly change production behavior inside a documentation qualification slice.

## 3. Finished runtime invariants

### 3.1 Record-only Wards

- Every server-executed tool call emits exactly one informational `warded` / `wardResolved` pair with one shared id and `origin: "ungated"`.
- The host records the allowed resolution atomically through `IWard.RecordAutomaticResolution`; it creates no live Ward, waits for no operator, applies no approval decision, and does not auto-deny unattended work.
- `/api/wards`, direct `arcanum ward`, Command Center `/ward`, historical resolution origins, and the retained active-record engine remain compatibility surfaces.
- Command Center turns every Ward frame into an informational Incantation note. It opens no Ward modal, captures no allow/deny key, keeps no per-session allowlist, and posts no turn-stream resolution.
- `ForbiddenArts` is an operator-authored advertisement filter used only when a request selects `ToolPolicy.NoForbiddenArts`. It is not an execution gate.
- `UnattendedMode` controls the default availability of genuine human-input tools. It does not deny ordinary server tools.

### 3.2 Independent retained boundaries

`Ungated` is audit information, not authority. The documentation must preserve and distinguish:

- Sanctum and `WorkspacePathPolicy` containment;
- Artifact Attunement and request-selected tool policies;
- host-process edition and explicit-enablement policy;
- `workspace_check` trust, platform, containment, and runtime eligibility;
- tool-specific validation and capability checks;
- Covenant target preflight, Campaign binding, disclosure-before-effect accounting, mutation capability, and publication rules;
- diagnostic endpoint routing that reserves internal tool names for the Master pipeline rather than Ward-gating their normal invocation;
- client-supplied tool forwarding, which bypasses Arcanum's server tool loop and therefore its Ward record.

No wording change may imply that removing Wards removed one of these boundaries.

### 3.3 Covenant reversal

`retire_covenant` follows the same record-only path as every other server tool. Eligible retirement is no longer conditioned on Wards being enabled, attendance, auto-approval, or an operator-consent receipt. New retirements carry a null Ward-evidence digest while historical receipt vocabulary remains readable.

`propose_covenant` retains its attended-only proposal/bootstrap boundary because that is a capability-admission rule, not a Ward decision. Documentation must keep this distinction explicit.

## 4. Scope

### 4.1 In scope

- Add a TDD contract that binds the 31-name server-tool vocabulary in `Arcanum.DESIGN.md` to the existing no-Ward enumeration.
- Extend the same inventory coverage so every name is distinct and `ToolRiskClassifier.RequiresWard` returns false under both legacy Campaign arms and maximally restrictive `ForbiddenArts` input.
- Retain the existing internal MCP `tools/list` to handler-registry parity test and make the documentation test distinguish conditionally advertised tools and the recognized-but-not-advertised `browse_web` compatibility alias.
- Add focused documentation wording contracts for the canonical record-only claims and the most error-prone diagnostic CLI description.
- Sweep the governed Markdown documents, `Arcanum.ConstraintInventory.json`, and code-owned CLI help descriptions for stale approval, auto-deny, or execution-gate claims.
- Make `Arcanum.DESIGN.md` sections 10.14 and 11.14 one coherent account, including the explicit Covenant rule reversal.
- Publish the full 31-name inventory in `Arcanum.DESIGN.md` between stable, test-readable markers.
- Keep `Arcanum.API.md`, `Compendium.README.md`, `Arcanum.Command.Reference.md`, `Arcanum.Design.Human.md`, `Arcanum.DEBUGGING.Human.md`, `Arcanum.CHAT-LOOP.md`, `ArcanumOATH.Human.md`, and the root `README.md` consistent with that account.
- Regenerate `docs/Arcanum.CommandMap.json` from the live CLI tree after updating its code-owned descriptions.
- Run and record the required documentation grep, explaining every surviving match as record vocabulary, compatibility vocabulary, advertisement policy, or an independent boundary.
- Qualify the finished branch with a clean Release `--no-incremental` build, full applicable suites, coverage, fresh Native AOT/IL evidence, and repository-specific static gates.
- Confirm live GitHub state for #197, #216-#221, and #230 during closeout.

### 4.2 Out of scope

- Rewriting dated reviews or prior plans/specifications.
- Removing `ForbiddenArts`, `UnattendedMode`, `ToolPolicy.NoForbiddenArts`, the retained Ward engine, `/api/wards`, CLI Ward commands, Forge Gatehouse compatibility, or historical origins.
- Changing server-tool advertisement or invocation behavior.
- Changing diagnostic MCP endpoint routing merely because its documentation currently uses the overloaded word "blocked."
- Changing Covenant preflight, disclosure, eligibility, persistence, proposal attendance, or retirement behavior.
- Implementing any Annals change from #230.
- Closing #197, merging `remove-wards` into `main`, or changing `main`.

## 5. Considered approaches

### 5.1 Contract-driven canonical sweep - selected

Add focused failing tests for the missing DESIGN inventory and misleading live descriptions, then update the canonical documentation and regenerate structured output.

This meets the requested TDD standard, preserves historical records, and prevents future tool or terminology drift without adding runtime architecture.

### 5.2 Documentation-only manual sweep - rejected

Edit prose, run the issue's grep once, and rely on review.

This is quicker but leaves the 31-name inventory duplicated without enforcement and permits a later tool or help description to reintroduce the same contradiction.

### 5.3 Production-generated documentation catalog - rejected

Introduce a new runtime catalog and generate the DESIGN inventory directly from it.

This would create the strongest single source, but it changes production architecture, Native AOT closure, and service composition to deliver a documentation qualification issue. The existing runtime registries plus a test-owned contract are sufficient.

## 6. Documentation architecture

### 6.1 Standing tool inventory

`Arcanum.DESIGN.md` section 11.14 will contain a table bounded by stable HTML comments. Each row names exactly one tool and records:

- functional group;
- exact tool name;
- catalog status: normally advertised, conditionally advertised, or recognized compatibility alias;
- Ward decision: `None` for every row;
- the independent availability or containment boundary, when one exists.

The 31 names are the existing inventory established by the Ward-removal tests:

- workspace/editing: `apply_patch`, `workspace_check`, `write_file`, `replace_text_block`, `search_workspace`, `read_file_chunk`, `list_directory`;
- host process: `execute_command`, `run_spell_script`, `read_command_output`;
- durable memory: `delete_lexicon`, `scribe_lexicon`, `read_saga`, `search_archives`;
- Covenant: `retire_covenant`, `propose_covenant`;
- session state: `attach_session_file`, `refresh_session_file`;
- orchestration: `delegate_task`, `petition_dungeon_master`, `ask_human`, `adjust_initiative`;
- messaging: `cast_sending`, `continue_sending`, `dispatch_sending`, `send_commlink_alert`;
- web: `web_search`, `read_url`, `browse_web`;
- host information: `get_local_system_time`, `get_arcanum_system_info`.

The table must not claim all 31 are simultaneously advertised. Runtime feature flags, session/capability context, edition policy, platform eligibility, and the `browse_web` alias explain catalog availability without turning those facts into Ward decisions.

### 6.2 Canonical terminology

Canonical documents use these distinctions consistently:

- **Ward record**: the informational pair and compatibility engine.
- **Forbidden Arts advertisement filter**: request-selected omission from the model-visible catalog.
- **Unavailable or reserved on a diagnostic endpoint**: an internal name cannot bypass the Master execution pipeline through direct external-MCP invocation.
- **Denied by Sanctum/containment/capability policy**: an independent live boundary returned through typed tool-result evidence.
- **Historical origin**: retained wire and persisted vocabulary that no current server-tool path produces.

Avoid "gated," "requires approval," "operator's only chance to refuse," "auto-denied," or "blocked Forbidden Art" when describing normal server-tool execution. A legitimate independent boundary must name its owner instead of borrowing Ward vocabulary.

### 6.3 Generated command map

`Arcanum.CommandMap.json` remains a projection of the live `System.CommandLine` tree. The implementation changes descriptions at their source in CLI command construction, proves the old description fails a focused test, and then regenerates the map with `ARCANUM_UPDATE_COMMAND_MAP=1`.

The generated file is never hand-edited. Its diff must contain only the reviewed description changes implied by this sweep.

### 6.4 Issue-reference convention

The existing `DocumentationIssueReferenceTests` inventory remains the definition of governed standalone documents. Those documents explain constraints directly rather than relying on tracker context. `README.md` and `Arcanum.OATH.md` remain the two product documents allowed to carry issue references.

Historical review, plan, and specification artifacts remain outside that test by approved decision. The test comments keep that exception explicit so later work does not accidentally broaden or silently narrow it.

## 7. TDD design

### 7.1 Baseline characterization

The clean feature branch first runs the existing documentation-reference, tool-risk, and CLI-surface clusters. This distinguishes pre-existing failure from the new RED tests without repeating a full suite.

### 7.2 Inventory RED/GREEN cycle

Add a test that reads the marked DESIGN table and extracts the exact tool-name column. Before the table exists, the test fails because the markers/inventory are absent.

The GREEN implementation adds the 31 rows. The test then proves:

- exactly 31 distinct names;
- exact set equality with the established `KnownToolNames` inventory;
- `RequiresWard` is false for each name under both legacy Campaign values while that same name appears in `ForbiddenArts`;
- `browse_web` is identified as a compatibility alias rather than a currently advertised native tool;
- the existing all-feature internal `tools/list` remains equal to its registered-handler surface after documented conditional exclusions.

The test reads documentation as data but does not move the inventory into production code.

### 7.3 Wording RED/GREEN cycle

Add narrowly calibrated assertions over governed documents and the live CLI surface. They must fail on the current claims that `delete_lexicon` "is a Forbidden Art" without the advertisement qualifier and that diagnostic MCP invocation leaves Forbidden Art tools "blocked server-side."

The assertions require the corrected owner-specific language and reject known stale execution-gate phrases. They do not ban legitimate `warded` event names, historical origins, the `ForbiddenArts` configuration property, or descriptions of non-Ward safety denials.

### 7.4 Command-map RED/GREEN cycle

After the CLI source description changes, the committed-map parity test fails against the old generated JSON. Regenerate through the existing test switch, rerun parity, and review the exact diff.

This is the TDD boundary for the generated artifact; no direct JSON edit is permitted.

### 7.5 Documentation sweep

With the focused contracts green, sweep every governed document and structured inventory. Run the issue's exact case-insensitive search with `RIPGREP_CONFIG_PATH=/dev/null`, classify each surviving hit, and fix any statement that implies a Ward decision.

The closeout evidence lists every surviving hit or groups identical generated/help occurrences by one shared source and justification. A surviving hit without a precise record, compatibility, advertisement, or independent-boundary explanation blocks delivery.

## 8. Error and scope handling

- A failing new test must fail for the intended missing inventory or stale wording before its documentation/source correction is written.
- If a RED test exposes existing runtime behavior inconsistent with #216-#220, stop and request a scope decision; do not change runtime behavior automatically.
- If command-map regeneration changes parser shape, aliases, options, or arguments, stop and investigate. This slice authorizes description changes only.
- If a canonical statement cannot distinguish a Ward decision from an independent boundary, name the concrete enforcing component and its recovery path before editing.
- If live GitHub state shows a prerequisite slice reopened or not Done, stop closeout. An intentionally open #230 is reported, not treated as a Ward prerequisite failure.
- Network or GitHub failures do not invalidate local verification, but they block push/issue/project completion claims until current state is read back successfully.

## 9. Verification and review

### 9.1 Focused gates

- new inventory/document wording contract tests;
- `ToolRiskClassifierTests`;
- internal MCP advertised/handler parity;
- CLI help and command-map parity;
- documentation issue-reference tests;
- existing Ward event/API/Command Center compatibility tests affected by edited claims.

Run each RED/GREEN cluster only as needed. Do not replace the final suite with focused success.

### 9.2 Independent review

Request one bounded, read-only review over the complete feature-branch diff. The reviewer checks issue alignment, 31-name accuracy, test honesty, historical-artifact boundary, retained security/capability wording, generated-map provenance, and absence of production behavior changes.

Every Critical or Important finding is resolved before qualification. An observable fix begins with a focused failing test. Do not start an open-ended review loop.

### 9.3 Final qualification

Run the complete locally applicable verification matrix once on the reviewed feature tree:

- Release solution build with `--no-incremental`, `--disable-build-servers`, single-node MSBuild, and zero errors/warnings;
- coverage threshold, which supplies the one complete non-Perf Arcanum suite;
- separate Compendium and The Forge suites;
- command-map, documentation, C# formatting/alignment, shell, workflow, packaging, and runtime-regex gates applicable on the host;
- fresh Native AOT/IL verification against a validated cleared generated publish/object tree;
- native SQLCipher provenance for `osx-arm64`;
- `git diff --check` and clean tracked status.

Inspect every wrapper before invoking it. Do not rerun a green complete suite. External production-only platform lanes remain deferred unless the repository's current verification contract explicitly makes them locally applicable.

## 10. Delivery

- Keep every #221 commit on `codex/issue-221-ward-documentation-sweep` until review and qualification are green.
- Fetch and revalidate `origin/main` and `origin/remove-wards` identities before integration. Do not absorb an unexpected remote advance silently.
- Merge the verified branch into local `remove-wards` with a history-only merge and prove the merge tree is byte-identical to the verified feature tree. Do not repeat the complete suite for an identical tree.
- Delete every local feature branch created for #221 after confirming it is merged. Preserve unrelated worktrees and branches.
- Push only `remove-wards`; do not push the temporary feature branch and do not modify `main`.
- Post closeout evidence, close #221 as completed, and verify its Feature Tracker item is `Done`.
- Verify #216-#220 remain closed/Done, #197 remains open/In Progress, and #230 remains open/Ready for the next slice.
- Report the pushed merge id, exact verification evidence, surviving documentation-search justifications, branch cleanup, and remaining #230 boundary.
