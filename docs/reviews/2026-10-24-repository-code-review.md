# Repository code review, 2026-10-24 (partial: 15 of 76 units)

Baseline: `ac0d2d8ea9fb49264e7cc77f037a27733d68eed0` (the tip of `main` and `general-enhancements` on 2026-10-24). Lenses: reliability, performance and usability. Method: the production tree (about 685k lines of C#, plus schema SQL, scripts, CI and operator docs) was cut into 76 units of about 12k lines; each unit was assigned one reviewer per lens (reliability on Claude Opus 5.5, the other two lenses on Claude Opus 5.5 in the first runs and Claude Sonnet 5.5 after), with the instruction to read every file of the unit, read the owning sections of `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`, `docs/Arcanum.Command.Reference.md` and `docs/Compendium.README.md`, and prove each finding with a probe test, a deliberate break, a measurement, or a traced call path. A verification stage of independent skeptics was planned for every finding; it did not run (the account's usage allowance ran out), so **every finding below is reviewer-asserted and unchecked by a second reader**. Findings whose evidence describes a probe that ran are marked `probe`; the others are marked `trace`. The executor must reproduce each finding with a failing test before changing production code, which the remediation plan requires anyway.

Scope reached: 33 of 228 reviewer passes finished (272 findings: 3 Critical, 76 Important, 193 Minor). The review is not complete: 61 units were not reviewed at all and 8 of the 15 reached units were reviewed under fewer than three lenses. The earlier reviews (the October 7–8 whole-tree review, remediated and merged at `8e082a4c`; the 2026-10-21 risk review in `docs/reviews/2026-10-21-repository-code-review.md`) stand; their owner rulings were given to every reviewer and are not re-reported here. The per-reviewer reports, including the probe code each ran, are under `docs/reviews/2026-10-24-review-reports/`.

## Coverage

| Unit | Project | Area | Lenses finished | Findings (C/I/M) | Checks found sound |
| --- | --- | --- | --- | --- | --- |
| U01 | Arcanum.Api | Intelligence (1) | reliability, usability | 0/5/17 | 33 |
| U02 | Arcanum.Api | Intelligence (2) | reliability, usability | 0/5/11 | 31 |
| U03 | Arcanum.Api | Intelligence (3) | performance, reliability, usability | 0/9/15 | 35 |
| U04 | Arcanum.Api | Intelligence (4) | performance, reliability, usability | 0/5/19 | 34 |
| U05 | Arcanum.Api | Tower | performance, reliability, usability | 0/13/21 | 38 |
| U06 | Arcanum.Cli | CommandCenter (1) | reliability | 0/3/12 | 13 |
| U07 | Arcanum.Cli | CommandCenter (2) | reliability | 0/4/10 | 12 |
| U23 | Arcanum.Infrastructure | Covenant (1) | performance, reliability, usability | 0/4/12 | 31 |
| U24 | Arcanum.Infrastructure | Covenant (2) | performance, reliability, usability | 0/3/20 | 36 |
| U25 | Arcanum.Infrastructure | Data (1) | performance, reliability, usability | 0/6/19 | 32 |
| U26 | Arcanum.Infrastructure | Data (2) | performance, reliability, usability | 1/9/10 | 25 |
| U27 | Arcanum.Infrastructure | Data (3) | reliability, usability | 0/3/12 | 24 |
| U28 | Arcanum.Infrastructure | Data (4) | reliability, usability | 0/4/8 | 29 |
| U29 | Arcanum.Infrastructure | Data (5) | reliability | 2/0/4 | 16 |
| U30 | Arcanum.Infrastructure | Data/Covenant (1) | usability | 0/3/3 | 13 |

### Not reviewed

Partially reviewed, lenses still owed: U01 (Arcanum.Api Intelligence (1)): performance; U02 (Arcanum.Api Intelligence (2)): performance; U06 (Arcanum.Cli CommandCenter (1)): performance, usability; U07 (Arcanum.Cli CommandCenter (2)): performance, usability; U27 (Arcanum.Infrastructure Data (3)): performance; U28 (Arcanum.Infrastructure Data (4)): performance; U29 (Arcanum.Infrastructure Data (5)): performance, usability; U30 (Arcanum.Infrastructure Data/Covenant (1)): performance, reliability.

Not reviewed under any lens: U08 (Arcanum.Cli Commands (1)); U09 (Arcanum.Cli Commands (2)); U10 (Arcanum.Cli Commands (3)); U11 (Arcanum.Cli Infrastructure); U12 (Arcanum.Cli Services (1)); U13 (Arcanum.Cli Services (2)); U14 (Arcanum.Core Configuration); U15 (Arcanum.Core Covenant (1)); U16 (Arcanum.Core Covenant (2) + Covenant (3)); U17 (Arcanum.Core Intelligence); U18 (Arcanum.Core Storage); U19 (Arcanum.Infrastructure A2A); U20 (Arcanum.Infrastructure Backup (1)); U21 (Arcanum.Infrastructure Backup (2)); U22 (Arcanum.Infrastructure Backup (3)); U31 (Arcanum.Infrastructure Data/Covenant (2)); U32 (Arcanum.Infrastructure Data/Covenant (3)); U33 (Arcanum.Infrastructure Data/LongRest); U34 (Arcanum.Infrastructure Data/Schema); U35 (Arcanum.Infrastructure GrimoireTransitions); U36 (Arcanum.Infrastructure Hosting (1)); U37 (Arcanum.Infrastructure Hosting (2)); U38 (Arcanum.Infrastructure InstallationReset (1)); U39 (Arcanum.Infrastructure InstallationReset (2) + InstallationReset (3)); U40 (Arcanum.Infrastructure Intelligence); U41 (Arcanum.Infrastructure Mcp); U42 (Arcanum.Infrastructure Mcp/InternalTools); U43 (Arcanum.Infrastructure Memory); U44 (Arcanum.Infrastructure Process); U45 (Arcanum.Infrastructure Repositories); U46 (Arcanum.Infrastructure Security (1)); U47 (Arcanum.Infrastructure Security (2)); U48 (Arcanum.Infrastructure Weave (1)); U49 (Arcanum.Infrastructure Weave (2) + Weave (3)); U50 (Arcanum.Infrastructure Workspaces/CodingTools (1)); U51 (Arcanum.Infrastructure Workspaces/CodingTools (2)); U52 (TheForge.Ux Services); U53 (TheForge.Ux ViewModels (1)); U54 (TheForge.Ux ViewModels (2)); U55 (TheForge.Ux ViewModels (3)); U56 (Arcanum.Api.DevHost (root)); U57 (Arcanum.Api A2A+(root)+Conclave+Configuration+Daemons+Data+Health+Hosting+Intelligence/Familiars+Intelligence/Guardrails); U58 (Arcanum.Api Intelligence/MemoryDelegation+Intelligence/MemoryRecall+Intelligence/OpenAi+Intelligence/Subagents+Intelligence/Tools+Intelligence/TurnEngine+Mcp+Middleware+Models+Operations+Perception+Primitives); U59 (Arcanum.Api ProvingGrounds+Security+Serialization+Spells+Streaming+Telemetry+Workspaces); U60 (Arcanum.Cli Diagnostics+(root)+UX); U61 (Arcanum.Core Annals+(root)+Backup+Chronosync+Cli+CommLink+Conclave+Daemons+DataLifecycle+Desktop+Environment+Events+Hosting+Lexicon+Logging); U62 (Arcanum.Core LongRest+Mcp+Memory+Operations+Pattern+Performance+Platform); U63 (Arcanum.Core Primitives+ProvingGrounds+Resilience+Sanctum+Security+Serialization+Telemetry+Tower+Wards); U64 (Arcanum.Core Weave+Workspaces); U65 (Arcanum.Infrastructure Caching+Chronosync+CommLink+Configuration+Coordination+Daemons+Data/Annals+Data/MemoryDelegation+Data/MemoryEvaluation+Data/Migrations); U66 (Arcanum.Infrastructure DependencyInjection+Diagnostics+Familiars+Generated); U67 (Arcanum.Infrastructure Lexicon+Logging+Mcp/ConnectionManager+Mcp/Protocol); U68 (Arcanum.Infrastructure Operations+Pattern+Platform+Resilience+Storage+Telemetry+Theme+Tower); U69 (Arcanum.Infrastructure Workspaces); U70 (Arcanum.Secrets Security); U71 (Compendium.Ux (root)+Converters+Models+Services+ViewModels+Views); U72 (TheForge.Core Chronicle+IO+Models+Serialization+Services); U73 (TheForge.Ux (root)+Converters+Markdown+Models+Views); U74 (Repository Schema SQL (Infrastructure/Data/Schema/**/*.sql)); U75 (Repository Scripts, packaging and CI workflows); U76 (Repository Operator documentation (README, Command Reference, Human docs, Compendium.README, DEBUGGING)). The unit file lists are in the remediation plan's Appendix A.

## Findings

Status key: `probe` = the reviewer reports running a probe, deliberate break or measurement whose output is quoted; `trace` = reasoned from the code without execution. None has been checked by a second reader.

| ID | Sev | Lens | Unit | File:line | Status | Finding |
| --- | --- | --- | --- | --- | --- | --- |
| RV-001 | Critical | reliability | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:4102` | probe | A prune with more than 10,000 candidates writes a checkpoint its own recovery rejects, blocking every retention operation for good |
| RV-002 | Critical | reliability | U29 | `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:2153` | probe | Orphan sweep deletes every bound attachment whose file name ends in .tmp or contains .tmp. |
| RV-003 | Critical | reliability | U29 | `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:2404` | probe | Attachment blob paths collide on case- and normalization-insensitive volumes: a later upload overwrites an earlier one, or the orphan sweep deletes it |
| RV-004 | Important | reliability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupMaintenance.cs:138` | probe | One Session whose contribution cannot be prepared fails every Campaign-bound operator turn and stalls the Campaign fold |
| RV-005 | Important | usability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/BatchProcessingService.cs:385` | probe | A batch whose input cannot be opened stays in_progress and /reset calls the file 'missing' |
| RV-006 | Important | usability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/BudgetMonitor.cs:34` | probe | Budget enabled with the default zero limit enforces nothing, and budget status then reports $0 spent / $0 remaining |
| RV-007 | Important | usability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupMaintenance.cs:282` | probe | Deferral of Campaign rollup maintenance is invisible; a Familiar or non-OpenAI-compatible maintenance model never produces a summary and nothing says so |
| RV-008 | Important | usability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs:625` | probe | A Campaign-rollup summary failure ends the user's turn as HTTP 400 Covenant.InvalidContent, and the policy rejects ordinary /route tokens as host paths |
| RV-009 | Important | reliability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/LongRestProviderClientFactory.cs:110` | probe | The Long Rest lane ignores the provider's declared output-token field |
| RV-010 | Important | reliability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/SessionContextPinMaterializer.cs:126` | probe | One corrupt pinned attachment fails every turn of its Session |
| RV-011 | Important | usability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/Guardrails/GuardrailsPipeline.cs:541` | probe | Guardrails PII detectors reject epoch timestamps and long IDs, rescan whole transcripts, and name no kind |
| RV-012 | Important | usability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/LongRestProviderFailure.cs:84` | probe | A parked Long Rest sweep cannot be diagnosed: HTTP status dropped, no provider/model, swallowed exceptions, no logger |
| RV-013 | Important | usability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/OpenAi/OpenAiStreamErrorMapper.cs:69` | probe | /v1 turns a context overflow and a daily-budget refusal into 429 plus 'Inference failed. See server logs for details.' |
| RV-014 | Important | performance | U03 | `src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs:753` | probe | BudgetReservations is full-table scanned by SumOutstandingAsync on every turn gate, reservation and provider round |
| RV-015 | Important | reliability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:783` | probe | Accumulated research summaries starve every source; long runs return zero citations |
| RV-016 | Important | reliability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:915` | probe | Attachment-store exceptions discard the billed search/browse/research answer |
| RV-017 | Important | reliability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:1141` | probe | Research synthesis mixes provider-numbered summaries with renumbered sources, so citations point at wrong URLs |
| RV-018 | Important | reliability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:4069` | probe | No-progress detector falsely stops memory-delegation turns after the delegation root is consumed |
| RV-019 | Important | usability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:2041` | probe | Sanctum preflight reports a malformed or oversized apply_patch as a Sanctum block and hides the parser's diagnosis |
| RV-020 | Important | usability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/TurnContextGuards.cs:213` | probe | Context-window overflow is HTTP 429, hidden behind a generic message on /v1, and names no remedy |
| RV-021 | Important | usability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:509` | probe | Research keeps, and cites, sources whose page was never read; fetch failures are silent |
| RV-022 | Important | usability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:599` | probe | Research reports a token-budget-truncated synthesis as complete and drops synthesis warnings |
| RV-023 | Important | performance | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:5577` | probe | Lexicon fallback extractor makes an extra LLM call on every user turn, even when the Lexicon is empty |
| RV-024 | Important | performance | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:7866` | probe | Covenant admission Measure() charges every tool schema into each fragment, so a Confirmed Covenant is withheld on mid-size windows (and each call re-estimates all tools) |
| RV-025 | Important | performance | U04 | `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/SpellScanner.cs:761` | probe | Every turn after a 5 s idle re-walks the entire working-directory tree looking for SPELL.md |
| RV-026 | Important | usability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:1368` | probe | Production streaming withholds every pre-commit frame (status, sessionBound, context, protected-maintenance notice) until the provider's first token |
| RV-027 | Important | usability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:7990` | probe | Context-window overflow error names no provider/model or remedy, maps to HTTP 429, and is flattened to a generic message on /v1 |
| RV-028 | Important | performance | U05 | `src/RetroDownfall.Arcanum.Api/Tower/CampaignEndpoints.cs:691` | probe | Campaign import re-parses every spell once per imported spell, under the workspace write lock |
| RV-029 | Important | performance | U05 | `src/RetroDownfall.Arcanum.Api/Tower/CampaignEndpoints.cs:914` | probe | Campaign export repeats the full tree walk once per spell |
| RV-030 | Important | performance | U05 | `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs:1209` | probe | POST /api/memory/search is a full-corpus scan with a per-row lower() copy; the FTS index is unused |
| RV-031 | Important | performance | U05 | `src/RetroDownfall.Arcanum.Api/Tower/SpellAuthoringEndpoints.cs:48` | probe | GET /api/spells/search scans every registered Campaign's tree on each call |
| RV-032 | Important | performance | U05 | `src/RetroDownfall.Arcanum.Api/Tower/SpellExecutionEndpoints.cs:70` | probe | Every spell lookup walks the whole workspace tree with no cache |
| RV-033 | Important | performance | U05 | `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/SagaRankingReader.cs:21` | probe | Saga ranking evidence is recomputed from scratch on every Saga read and every Saga recall when Long Rest is on |
| RV-034 | Important | reliability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/CampaignEndpoints.cs:274` | probe | Campaign PUT and campaign import write back the whole row they read, silently reverting a concurrent Sanctum change |
| RV-035 | Important | reliability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/CodexEndpoints.cs:275` | probe | GET codex reports an unreadable or over-cap CODEX.md as an existing empty file, so the next save destroys it |
| RV-036 | Important | reliability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/LoreEndpoints.cs:141` | probe | A Lore key containing '/' can be written but never read or deleted |
| RV-037 | Important | reliability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs:1390` | probe | Memory search workspaceId never matches the id GET /api/workspaces returns and answers 'complete, zero results' |
| RV-038 | Important | usability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/CodexEndpoints.cs:279` | probe | GET codex reports an over-cap, non-UTF-8 or aliased CODEX.md as exists:true with empty content |
| RV-039 | Important | usability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs:1390` | probe | memory search --workspace never matches the workspace ID that workspace list prints; an unresolved ID returns a silent empty result |
| RV-040 | Important | usability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/ProviderTestEndpoints.cs:72` | probe | Provider test tells the operator to 'See server logs for detail' but the detail is logged at Debug, which the host never keeps; the Forge then discards even the generic message |
| RV-041 | Important | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:1584` | probe | Ctrl+Q during an ask_human prompt replaces the hard modal, wipes the answer, Esc cannot cancel, and the 'Enter submit' footer quits Command Center |
| RV-042 | Important | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterState.cs:296` | probe | Older transcript paging is offset-based against a total captured at resume; live turns shift pages and the oldest entries become unreachable |
| RV-043 | Important | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterWindow.cs:1077` | probe | Long ask_human question is truncated to four wrapped rows (tall ones clipped) and is shown nowhere else |
| RV-044 | Important | reliability | U07 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs:593` | probe | Staged attachment reference outlives its session and then blocks every later turn |
| RV-045 | Important | reliability | U07 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:236` | probe | /fork, /branch parent|child and /session archive <active> switch sessions while a turn is generating |
| RV-046 | Important | reliability | U07 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:268` | probe | /fork alternative duplicates the user prompt in the new branch |
| RV-047 | Important | reliability | U07 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandParser.cs:243` | probe | /fork confirm forks the whole session, discarding the cutoff or alternative that required confirmation |
| RV-048 | Important | performance | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchOutboxWorker.cs:150` | probe | A single outbox sequence larger than 512 rows is never applied; one cleanup batch can create one, wedging the search accelerator permanently and silently |
| RV-049 | Important | reliability | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceCoordinators.cs:44` | probe | Every owner-cleanup pass revokes all live memory-delegation grants, even when there is nothing to clean |
| RV-050 | Important | usability | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceHostedService.cs:216` | probe | Covenant refusals and the per-turn absence reason are logged at Debug, which the host pipeline never emits; host.minLogLevelInBuffer cannot lower the floor |
| RV-051 | Important | usability | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantManagementService.cs:726` | probe | List and search report constant shadow/materialization (NotShadowed, Eligible) whenever an evaluation Campaign is named, including for Retired and shadowed heads |
| RV-052 | Important | usability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:56` | probe | Covenant review refusals name neither the failing decision nor the next step, any apply in a lane silently stales every other token, and the 5-minute token life is never disclosed |
| RV-053 | Important | usability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.cs:194` | probe | Operator set/correct preflight approves writes the commit will refuse for Section capacity or a retired head |
| RV-054 | Important | usability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionHostedService.cs:103` | trace | A draining schema transition is invisible to the operator: logs lack tier/sweep/version, 'advanced N runs' reports runs seen, no doctor or status surface reads the journal |
| RV-055 | Important | performance | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs:753` | probe | Budget outstanding-reservation sum scans every BudgetReservations row ever written, inside the write lock and on every turn |
| RV-056 | Important | performance | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignRollupStore.cs:131` | probe | Each Campaign rollup revision copies every prior source: campaign_rollup_sources grows as N(N+1)/2 and every publish makes several O(N) passes under the write lock |
| RV-057 | Important | performance | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignSummaryLifecycle.cs:200` | probe | campaign_rollup_sources is full-scanned by SessionId on entry/session deletes, and ReadSessionClosureAsync materialises the whole closure into huge strings |
| RV-058 | Important | usability | U25 | `src/RetroDownfall.Arcanum.Core/Operations/LongRunningOperationRecoveryRegistry.cs:171` | trace | arcanum doctor gives a stuck Covenant reset the repair guidance for an ordinary retention policy change |
| RV-059 | Important | usability | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs:2515` | probe | Covenant erasure coordinator leaves admission closed on three failure paths without writing any log line |
| RV-060 | Important | usability | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs:4264` | probe | A failed Covenant reset or factory erasure tells the operator nothing about what happened or what to do, contrary to the docs |
| RV-061 | Important | performance | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:1465` | trace | Entry/attachment/embedding candidate and apply statements scan BudgetReservations for every run of the entry's session, per entry |
| RV-062 | Important | performance | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:1946` | probe | Entry-embedding candidate loop is O(n^2) in time and allocation: candidates.Any(c => c == prefix + NormalizeGuid(entryId)) over the plan-wide list |
| RV-063 | Important | performance | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:2629` | trace | Accounting prune planning is quadratic (retained sessions) and cubic (with cost adjustments): no usable RunId index on BudgetReservations, BillableOperations or CostAdjustments |
| RV-064 | Important | performance | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:3479` | trace | Prune checkpoint rewrites the entire candidate list twice per journal-bearing candidate (O(N^2) bytes written) |
| RV-065 | Important | performance | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:5332` | probe | Candidate identity lookups use lower(replace(Id,'-','')) on primary keys with no expression index; every candidate scans its table (plan and apply are O(candidates x table)) |
| RV-066 | Important | reliability | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs:31` | probe | Factory reset leaves Campaign Summary content (model-written summaries of the erased Sessions) in the Grimoire |
| RV-067 | Important | reliability | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:3692` | probe | Past 5 MiB of checkpoint the prune can never start and deletes nothing, with a non-actionable error |
| RV-068 | Important | usability | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs:1217` | probe | Factory-reset inventory refusals name no path or root and surface as 'Grimoire could not be opened' or a 500 |
| RV-069 | Important | usability | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:4102` | probe | Prune plan is unbounded but its checkpoint rejects more than 10,000 candidates on read and about 50,400 on write |
| RV-070 | Important | usability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:2308` | trace | Covenant reset that rolls back before erasing reports only 'did not reach a committed reopen disposition' |
| RV-071 | Important | usability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:7249` | probe | Retention mutation recovery parks rows as Data.ReconciliationFailed from nine branches with no log and swallows the reason |
| RV-072 | Important | usability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/FtsMatchQuerySanitizer.cs:25` | trace | search_archives schema promises FTS5 query text but the sanitizer strips all operators and turns queries into an AND of exact tokens |
| RV-073 | Important | reliability | U28 | `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs:922` | probe | An unobserved cancellation is resumed as Running instead of cancelled |
| RV-074 | Important | usability | U28 | `src/RetroDownfall.Arcanum.Cli/Commands/OperationCommands.cs:22` | probe | arcanum operation list splits operation IDs across lines on terminals narrower than about 140 columns |
| RV-075 | Important | usability | U28 | `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireOrdinaryConnectionFactory.cs:139` | probe | Connection factories discard why a Grimoire open failed, log nothing, and callers report it as a maintenance window |
| RV-076 | Important | usability | U28 | `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs:1215` | trace | Retry/cancel of an owner-bound retention operation is refused with contradictory text and no surface names the real remedy |
| RV-077 | Important | usability | U30 | `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignRollupStore.Read.cs:64` | probe | One oversized Entry wedges Campaign continuity for the whole Campaign, with an unmapped code and no identifier |
| RV-078 | Important | usability | U30 | `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureInventorySource.cs:41` | trace | Erasure inventory/preflight refusals name no label, kind or cause, and a transient fault reads as corruption |
| RV-079 | Important | usability | U30 | `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureStartupRecoveryOwnerAdopter.cs:333` | probe | Refused startup adoption of the Covenant erasure owner reports one sentence for every cause, logs nothing, and points the operator at logs |
| RV-080 | Minor | reliability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/BatchProcessingService.cs:874` | probe | An unexpected batch failure after every line of a page has started leaves no resume note and silently drops all later pages |
| RV-081 | Minor | reliability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/BatchRecoveryService.cs:305` | probe | A non-cryptographic read failure of one stranded batch input aborts host startup |
| RV-082 | Minor | reliability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/CovenantDispatchGate.cs:207` | probe | A Campaign preparation that completes after its turn scope is disposed is never disposed, leaking the claim and the Covenant turn lease |
| RV-083 | Minor | reliability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/CovenantProviderCallFreezer.cs:73` | probe | CovenantProviderCallFreezer.TryFreeze throws instead of returning a typed refusal when a message part cannot be enveloped |
| RV-084 | Minor | reliability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/EmbeddingsResetEndpoints.cs:68` | probe | The embeddings-reset route reports Grimoire maintenance or any other InvalidOperationException as 'manual artifact erasure required' |
| RV-085 | Minor | reliability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:415` | trace | On the protected tool-interaction path, an event-hub publication failure after a successful commit fails the turn |
| RV-086 | Minor | reliability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:1288` | trace | A committed claimed reply is reported as not saved when settling its claim fails, and the retry is then refused |
| RV-087 | Minor | usability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/BatchProcessingService.cs:376` | probe | A batch whose input file is missing at processing time fails with no note and no log |
| RV-088 | Minor | usability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/BatchProcessingService.cs:930` | probe | Operator-cancelled batch lines are sealed with 'The host stopped after this request...' |
| RV-089 | Minor | usability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/BudgetMonitor.cs:73` | trace | Budget.Exceeded message does not say the day is UTC, when it resets, or which key to raise |
| RV-090 | Minor | usability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs:585` | trace | Turn error for deferred protected Long Rest maintenance discards the reason code and operation id it holds |
| RV-091 | Minor | usability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/ClientToolCapabilityValidator.cs:131` | probe | Native ping client-tool errors name tools[i] (field is clientTools), and clientTools without forwardClientTools is silently ignored |
| RV-092 | Minor | usability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/ContextCompressionService.cs:195` | probe | Compaction no-ops are indistinguishable from an empty session and give no reason |
| RV-093 | Minor | usability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/EmbeddingsResetEndpoints.cs:107` | probe | POST /api/embeddings/reset?scope= (empty value) resets every embedding table |
| RV-094 | Minor | usability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:918` | trace | Turn-writer and Covenant-gate warnings omit the Session/turn id and the failure code |
| RV-095 | Minor | usability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/InferenceContextBuilder.cs:604` | probe | Unparseable image_url parts pass Scrying validation and are silently dropped, so the model answers without the image |
| RV-096 | Minor | usability | U01 | `src/RetroDownfall.Arcanum.Api/Intelligence/InferenceProviderFailureMessage.cs:15` | probe | Provider failure text points the operator at the API key for rate limits and server errors |
| RV-097 | Minor | reliability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.LongRest.cs:102` | probe | A pre-send refusal in Long Rest loses its code and parks the sweep as needs-operator |
| RV-098 | Minor | reliability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/SessionAttachmentRequestValidator.cs:52` | probe | Internal exception text reaches the client as an attachment validation error |
| RV-099 | Minor | usability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:918` | probe | Turn-writer begin/finalize failure logs carry no session id, entry id or error code |
| RV-100 | Minor | usability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/Guardrails/GuardrailsPipeline.cs:486` | probe | Guardrail topic rules: an allowed-topic miss is reported as 'matched' and an invalid blocked-topic regex silently disables the rule |
| RV-101 | Minor | usability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/MemoryDelegation/MemoryDelegationEndpoints.cs:94` | probe | Memory-delegation refusals all carry one constant message, including body-shape and validation faults |
| RV-102 | Minor | usability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.cs:196` | probe | Provider-call failure logs name neither provider, model nor HTTP status, yet the user message sends the operator to the logs |
| RV-103 | Minor | usability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/OpenAi/OpenAiChatRequest.cs:39` | trace | logprobs, top_logprobs and embeddings dimensions are accepted and silently ignored |
| RV-104 | Minor | usability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/OpenAi/OpenAiModel.cs:26` | trace | API.md describes a 'reasoning' capability object on /v1/models that neither models endpoint emits |
| RV-105 | Minor | usability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/SemanticSpellRouter.cs:99` | trace | Semantic spell routing degrades to the paid LLM router with only Debug-level logs |
| RV-106 | Minor | usability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/SessionAttachmentTurnService.cs:511` | trace | Attachment materialization swallows the exception and drops the extractor's own failure reason |
| RV-107 | Minor | usability | U02 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:8769` | probe | Input-stage guardrail audit records never carry the session id |
| RV-108 | Minor | performance | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:326` | probe | Research discovery loop keeps billing search passes after the synthesis prompt budget is already consumed, then fetches and cites nothing |
| RV-109 | Minor | performance | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:427` | probe | The 50-citation research fetch ceiling has no test; removing it passes every related test |
| RV-110 | Minor | performance | U03 | `src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs:699` | probe | SumCommittedAsync reads and decimal-parses every billable row of the UTC day under the write lock |
| RV-111 | Minor | reliability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/EmbeddingGeneratorFactory.cs:134` | probe | Embedding batches use the SDK's 100 s per-try timeout and 4 tries (outside this unit's files) |
| RV-112 | Minor | reliability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:961` | probe | refresh_session_file reports provenance and hash failures as attachment budget exhausted |
| RV-113 | Minor | reliability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/TurnAccountingHandle.cs:346` | probe | A failed reservation settlement skips run completion, leaving InferenceRuns Running forever |
| RV-114 | Minor | reliability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:940` | trace | Research question bound assumes fewer than 1,000 passes but the loop has no ceiling |
| RV-115 | Minor | usability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:672` | probe | Model-facing tool error text leaves the model without a next step (refresh selector, unknown tool, truncation marker) |
| RV-116 | Minor | usability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/ToolLoopProgressDetector.cs:37` | trace | The no-progress stop names neither the tool nor the arguments that looped |
| RV-117 | Minor | usability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/TurnContextGuards.cs:121` | trace | Silently trimmed tool exchanges can make the model repeat a call that the no-progress detector then treats as a loop |
| RV-118 | Minor | usability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/WeaveService.cs:256` | probe | Embedding failures are under-diagnosed and a failed accounting completion is logged at Debug without ids |
| RV-119 | Minor | usability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:698` | probe | Web workflow validation messages do not name the offending field or rule; API message cites a CLI flag |
| RV-120 | Minor | usability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/WebWorkflowEndpoints.cs:159` | trace | Research failure frames carry no trace id or spend, the log id differs from the envelope id, and the 200-with-error-frame contract is undocumented |
| RV-121 | Minor | usability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.ContextPreview.cs:150` | trace | Model-resolution failures lose their cause: the preview logs nothing and the live path logs only the exception type |
| RV-122 | Minor | usability | U03 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.ContextPreview.cs:1006` | probe | Context preview cannot say why retrieval contributed nothing (disabled, embedding failed, or no hits look identical) |
| RV-123 | Minor | performance | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/InferenceTokenizerResolver.cs:88` | probe | Tokenizer vocabulary is built lazily inside the first turn after daemon start (no warm-up) |
| RV-124 | Minor | performance | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:3271` | probe | Dispatch-time token breakdown never attributes Covenant tokens, so streamed Context frames and audit breakdowns disagree with inspect |
| RV-125 | Minor | performance | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:5405` | probe | Same prompt is embedded twice per turn when SemanticSpellRouting and any retrieval feature are both enabled |
| RV-126 | Minor | performance | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:7943` | probe | Three identical full-transcript estimates plus four payload hashes per provider round |
| RV-127 | Minor | performance | U04 | `src/RetroDownfall.Arcanum.Infrastructure/Weave/SpellWeaveCache.cs:74` | probe | SpellWeaveCache keeps one catalog snapshot, so alternating workspaces re-embed the whole spell catalog every turn |
| RV-128 | Minor | reliability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:1825` | probe | On the production streaming path the 'Finishing protected memory maintenance...' status is held in the pre-commit buffer until after the maintenance runs; the only test uses a path production never takes |
| RV-129 | Minor | reliability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:3759` | probe | Claimed turns store the generic Hub.SessionTurnRestoredInterrupted/409 outcome for most failures, so an idempotent retry replays 409 instead of the real error (buffered and streaming disagree) |
| RV-130 | Minor | reliability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:4325` | probe | Native responseFormat is matched case-insensitively for the provider wire but case-sensitively for host schema validation, so 'JSON_SCHEMA' silently skips strict enforcement |
| RV-131 | Minor | usability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/InferenceProviderFailureMessage.cs:18` | trace | Provider HTTP error text gives the same advice for 401, 404, 429 and 5xx |
| RV-132 | Minor | usability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:1346` | probe | Failures that are not the provider's are reported to the operator as provider failures |
| RV-133 | Minor | usability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:3557` | probe | Every inference-failure log line omits provider, model, HTTP status and session although the public text says 'see server logs for detail' |
| RV-134 | Minor | usability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:3724` | probe | The 'model does not support tools' restart is logged at the wrong levels, without provider/model/config key, and buffered callers get no signal |
| RV-135 | Minor | usability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:4255` | probe | The no-progress stop names no tool and gives no next step; its log carries only a hash |
| RV-136 | Minor | usability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:5359` | probe | An explicit spell version that cannot be loaded is silently dropped and the turn runs with no spell |
| RV-137 | Minor | usability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:6231` | probe | Retrieval failures (Saga, Tapestry, attachments, workspace index) are invisible: Debug logs are never emitted in production and context inspect gives one generic reason |
| RV-138 | Minor | usability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:7312` | probe | Forwarded client tools log a Warning on every turn |
| RV-139 | Minor | usability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:7392` | probe | toolPolicy readOnlyTools uses a stale allowlist and the API doc points at a DESIGN section that does not define it |
| RV-140 | Minor | usability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:7570` | probe | API.md documents reasoning-output resolution that no longer exists and omits the features.reasoningSummaries gate |
| RV-141 | Minor | usability | U04 | `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:8122` | probe | Out-of-range sampling parameters and unknown response_format values are silently altered or ignored |
| RV-142 | Minor | performance | U05 | `src/RetroDownfall.Arcanum.Api/Tower/PromptEndpoints.cs:44` | probe | Prompt list: the OR predicate defeats index order, and list routes read full rows (templates) to build summaries |
| RV-143 | Minor | performance | U05 | `src/RetroDownfall.Arcanum.Api/Tower/SagaEndpoints.cs:462` | probe | DELETE /api/saga?confirm=true retains one result object per memory |
| RV-144 | Minor | performance | U05 | `src/RetroDownfall.Arcanum.Api/Tower/SessionDivinationEndpoints.cs:134` | probe | Session Divination ranks the whole installation, filters afterwards, and may scan twice |
| RV-145 | Minor | performance | U05 | `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs:98` | probe | GET /api/sessions/analytics scans the whole Entries table for the by-model counts |
| RV-146 | Minor | reliability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/CampaignEndpoints.cs:938` | trace | Filtered prompt listings and Campaign export silently stop at 10,000 prompts |
| RV-147 | Minor | reliability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs:1209` | probe | Memory search is case-insensitive for ASCII only |
| RV-148 | Minor | reliability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs:1752` | trace | Genuine Grimoire connection faults are reported as planned maintenance and logged only at Debug |
| RV-149 | Minor | reliability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/ProviderTestEndpoints.cs:144` | probe | Provider connectivity probe has no deadline once response headers arrive |
| RV-150 | Minor | reliability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/SessionDivinationEndpoints.cs:284` | probe | Session status filters are case-sensitive in SQL and unvalidated, returning silent empty pages |
| RV-151 | Minor | reliability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs:1230` | probe | Default context-pin label is cut inside a surrogate pair |
| RV-152 | Minor | reliability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs:1289` | probe | POST /api/sessions/{id}/entries persists an undefined MessageRole |
| RV-153 | Minor | reliability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs:1818` | trace | Pinned-entry cap is check-then-act and a pin of a vanished entry reports success |
| RV-154 | Minor | reliability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs:1870` | trace | Manual compaction deletes transcript sized to the default model's provider, not the Session's model |
| RV-155 | Minor | usability | U05 | `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs:728` | probe | An unconvertible optional query value returns a bare 400 with no body, contradicting API.md section 8.4 |
| RV-156 | Minor | usability | U05 | `src/RetroDownfall.Arcanum.Api/ApiRequestJson.cs:277` | probe | A wrong value or type in a syntactically valid JSON body is reported as 'could not be parsed as valid JSON' with no field |
| RV-157 | Minor | usability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/InferenceExecuteWriter.cs:300` | probe | A provider-originated cancellation ends an NDJSON stream with 'See server logs for details' but writes no log line |
| RV-158 | Minor | usability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/PromptEndpoints.cs:143` | probe | POST /api/prompts without a template is a 500 Hub.Unhandled |
| RV-159 | Minor | usability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/SagaEndpoints.cs:84` | probe | GET /api/saga?sessionId=<unknown> answers 200 with an empty list while every memory route answers 404 Session.NotFound |
| RV-160 | Minor | usability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/SessionDivinationEndpoints.cs:84` | probe | Status filters are case-sensitive: divine accepts 'Archived' then matches nothing, and GET /sessions silently returns an empty page for any non-exact status |
| RV-161 | Minor | usability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs:1286` | probe | POST /api/sessions/{id}/entries without 'role' silently stores a system entry |
| RV-162 | Minor | usability | U05 | `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs:1721` | probe | 'Memory management is disabled.' names no configuration key on four routes whose gate defaults to off |
| RV-163 | Minor | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterApp.cs:113` | trace | If Application.Init fails after touching the terminal, the app is never disposed and terminal modes are never restored |
| RV-164 | Minor | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterAttachmentDriftMonitor.cs:304` | probe | Attachment drift monitor revalidates on every file event in the whole working tree; each call double-SHA-256s every tracked source |
| RV-165 | Minor | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:254` | trace | A FileSystemWatcher failure at start-up ends the whole Command Center as CrashedAfterStart |
| RV-166 | Minor | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:793` | trace | Quitting with an unresponsive auto-launched host waits up to five minutes before the process exits |
| RV-167 | Minor | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:1085` | probe | Ctrl+PgUp/PgDn page loads bypass the action gate; overlapping loads corrupt SessionPageHistory |
| RV-168 | Minor | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:1452` | probe | Resume from the Sessions pane keeps the previous session's attachment rows; the footer shows the wrong session's indexing status |
| RV-169 | Minor | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:1732` | probe | Model drop-down lists the same id once per provider, but selection stores only the id; the confirmation names a provider the host will not use |
| RV-170 | Minor | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:2286` | trace | Chat turns bypass the action gate, so a message sent during a gated resume goes to the old session and sessionBound re-binds to it |
| RV-171 | Minor | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:2321` | trace | EndTurn runs before TurnCts is cleared, so a turn admitted in that window has its CTS disposed and becomes uncancellable |
| RV-172 | Minor | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:2332` | probe | Turn completion moves focus to the composer whatever the operator is doing; next keys are swallowed (overlay) or typed into the composer (Sessions) |
| RV-173 | Minor | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterUiUpdatePump.cs:44` | probe | UI update pump coalesces before a non-blocking hand-off, so refreshes arriving while the UI thread is busy are not folded |
| RV-174 | Minor | reliability | U06 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterWindow.cs:2004` | probe | Overlay frame width is measured in UTF-16 units, clipping CJK and other wide text |
| RV-175 | Minor | reliability | U07 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/PersistedToolInteraction.cs:12` | probe | Tool-markup detection ignores the entry role, hiding user prompts on resume |
| RV-176 | Minor | reliability | U07 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionLogBuffer.cs:534` | probe | WrapLine never breaks at the column just past the window |
| RV-177 | Minor | reliability | U07 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs:370` | trace | Transcript paging and entry selection go stale after live turns |
| RV-178 | Minor | reliability | U07 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs:515` | probe | Fork created on the host but not reloaded is reported as a plain failure; retry creates another branch |
| RV-179 | Minor | reliability | U07 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:324` | probe | /branch child on a New Session opens an unrelated root session |
| RV-180 | Minor | reliability | U07 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:824` | trace | /pin file containment check is case- and alias-sensitive on case-insensitive volumes |
| RV-181 | Minor | reliability | U07 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:1156` | trace | /config help and Command Reference promise the configuration file path; the handler never prints it |
| RV-182 | Minor | reliability | U07 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandParser.cs:92` | probe | /attach and /pin collapse runs of whitespace inside the path |
| RV-183 | Minor | reliability | U07 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandParser.cs:342` | probe | Bare /branch, /session or /ward suggests itself |
| RV-184 | Minor | reliability | U07 | `src/RetroDownfall.Arcanum.Cli/CommandCenter/StreamingUiCoalescer.cs:229` | probe | Coalescer cancellation/dispose flushes throw ChannelClosedException after the UI channel completes |
| RV-185 | Minor | performance | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceCoordinators.cs:78` | probe | Idle maintenance passes bump the Covenant availability generation twice per pass; that counter is hashed into the DCI v2 prompt-cache scope marker at the start of the system prompt |
| RV-186 | Minor | performance | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceHostedService.cs:127` | trace | Owner-deletion cleanup drains at most 64 events per five-minute pass, from one ordered queue shared by Sessions and Campaigns |
| RV-187 | Minor | performance | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantEntryErasurePlan.cs:1080` | probe | Live entry erasure scans every version, receipt and provenance row in the installation three times while the Covenant scope is closed |
| RV-188 | Minor | reliability | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantEntryErasureService.cs:351` | probe | A refused, replayed or not-found Covenant erase still revokes every live memory-delegation grant |
| RV-189 | Minor | reliability | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantFamilyReinitializeCoordinator.cs:208` | probe | Every resumed family reinitialize re-runs the pre-drop protected-state erasure outside the phase guard (latent) |
| RV-190 | Minor | reliability | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantFamilyReinitializeCoordinator.cs:577` | probe | Family-reinitialize recovery reports a ReopenedVerified checkpoint as Completed although publication is still owed (latent) |
| RV-191 | Minor | reliability | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantIndexRebuildCoordinator.cs:256` | trace | A stale index rebuild 'restarts' into an orphaned operation that nothing drives and whose identity is discarded (latent) |
| RV-192 | Minor | usability | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.cs:589` | probe | Marker lifecycle refusals drop the Campaign id and closed blocker code they already hold and never log (restore cleanup and full-installation reset); latent until Campaign registration ships |
| RV-193 | Minor | usability | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantEntryErasureService.cs:551` | probe | Entry erase discards the commit-failure cause and logs nothing, leaving the operator with 'retry it' or 'restart the host' and no reason |
| RV-194 | Minor | usability | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantFamilyReinitializeCoordinator.cs:312` | trace | CovenantFamilyReinitializeCoordinator.RunAsync discards the reason every time it keeps admission closed (unwired class) |
| RV-195 | Minor | usability | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantManagementService.cs:510` | trace | 'arcanum memory status' on a default (Covenant disabled) installation prints a 'Refused' census |
| RV-196 | Minor | usability | U23 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantManagementService.cs:642` | probe | covenant explain reports Admission=Admitted and zero tokens as if measured; DTO and DESIGN docs say the admission code runs |
| RV-197 | Minor | performance | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:135` | probe | ListAsync and PrepareAsync open BEGIN IMMEDIATE (the single writer lock) for reads, so a review page waits behind every writer, blocks every writer, and surfaces a long writer as a raw SqliteException |
| RV-198 | Minor | performance | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:1186` | probe | ListAsync persists a review-marker row for any campaign id a caller names; rows are keyed by user input and only a campaign-deletion event removes them |
| RV-199 | Minor | performance | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:1298` | probe | Review-queue statements bind the campaign with IS $campaign, so the partial queue indexes are never used and every page, frontier read and apply scans the installation-wide review-event rowid window |
| RV-200 | Minor | performance | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionHostedService.cs:63` | probe | The schema-transition driver sleeps 15 s after every pass regardless of pass cost, capping backfill throughput at 16 x MaxRowsPerBatch / 15 s (137-273 rows/s) while the Covenant capability stays unavailable |
| RV-201 | Minor | performance | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionHostedService.cs:78` | trace | A schema-transition pass refused admission, or one that failed, is scheduled like an idle pass (5 minutes) |
| RV-202 | Minor | reliability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:134` | probe | Review list and prepare, both reads, take the database write lock, so any writer blocks them and a busy database surfaces as a 500 |
| RV-203 | Minor | reliability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.cs:509` | probe | Operator Covenant set, retire, correct and curate commits turn a busy database into a generic 500 after up to 30 s (a new angle on R-171) |
| RV-204 | Minor | reliability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairStartupRecovery.cs:192` | probe | A restart treats every ReopenPending schema-repair journal as durably mutated, so an interrupted no-op repair is kept closed or filed as Completed |
| RV-205 | Minor | reliability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairStartupRecovery.cs:208` | probe | A failed schema repair leaves an active Prepared journal that closes Covenant admission on every later start, with no way out |
| RV-206 | Minor | usability | U24 | `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.Review.cs:405` | probe | Human-mode review list omits the proposed content (outside U24, in the Cli unit; confirm intent) |
| RV-207 | Minor | usability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:499` | probe | Review apply diagnostics drop the cause, 13 of 14 IntegrityFailure sites log nothing, and a retryable busy is logged at Error |
| RV-208 | Minor | usability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:967` | probe | Review confirmation screen identifies bulk targets by GUID only; the key is omitted although the server has it |
| RV-209 | Minor | usability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:1133` | probe | Hash casing differs between the review list and every other Covenant read of the same DTO |
| RV-210 | Minor | usability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.cs:731` | probe | Covenant.InvalidContent from the operator write and review paths names an internal parameter, not the offending character, and the refusal rule is undocumented |
| RV-211 | Minor | usability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantOperationGate.cs:14` | trace | Gate class remarks say acquisition touches no database; the Campaign-exclusive paths read it and can throw |
| RV-212 | Minor | usability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantOperationGate.cs:633` | trace | The gate's refusal says 'closing' for a closure that may be kept closed indefinitely, and never names the operation |
| RV-213 | Minor | usability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairJournal.cs:105` | trace | Startup refusal 'See logs for recovery steps' has no log behind it when the repair journal cannot be read (dormant today) |
| RV-214 | Minor | usability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantTurnHeadProbe.cs:118` | probe | Documentation attached to the wrong member in CovenantTurnHeadProbe |
| RV-215 | Minor | usability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionCoordinator.cs:116` | trace | GrimoireSchemaTransitionPassOutcome.Advanced does not mean what its remarks say and can spin a stuck run every 15 seconds while logging 'advanced' |
| RV-216 | Minor | usability | U24 | `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.CovenantTools.cs:104` | probe | propose_covenant refuses uncompilable input with the wrong code and a sentence the model cannot act on (outside U24; found while tracing TryCompile) |
| RV-217 | Minor | performance | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsErasurePlan.cs:106` | probe | Subject-scoped Annals erasure appends OR NOT EXISTS orphan clauses that force full scans of long_rest_receipt_inputs and long_rest_suppressions |
| RV-218 | Minor | performance | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetAlertRepository.cs:19` | probe | After the alert threshold every turn attempts a duplicate BudgetAlerts insert, throws, and logs a warning; the cheap pre-check is unused |
| RV-219 | Minor | performance | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs:733` | probe | Committed-spend sum re-reads and BigInteger-adds every bill completed today on each reserve/adjust, inside the write transaction |
| RV-220 | Minor | performance | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.cs:340` | trace | Sensitivity-label keyset pagination uses ($after IS NULL OR LabelId > $after), so every page rescans the index from the start |
| RV-221 | Minor | performance | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.cs:663` | probe | Every tainted-artifact write re-reads all of the Session's sensitivity labels to recompute the merged provenance digest |
| RV-222 | Minor | performance | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignRollupStore.Manifests.cs:16` | probe | Validating a bound Campaign rollup re-verifies every source with ~5 queries each, twice per provider dispatch |
| RV-223 | Minor | reliability | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/AttachmentMemoryProvenanceStore.cs:106` | probe | Attachment consultation window compares with millisecond julianday() against 100 ns watermarks |
| RV-224 | Minor | reliability | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs:1889` | trace | DESIGN says the erasure coordinator checkpoints ReopenedVerified; it never does, and the resume arm for it is dead |
| RV-225 | Minor | reliability | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.CampaignSummaries.cs:44` | probe | Campaign-summary retention plans protected summaries that apply can never delete (permanent PlanChanged conflict) |
| RV-226 | Minor | usability | U25 | `src/RetroDownfall.Arcanum.Cli/Infrastructure/Surface/CliSurfaceExamples.cs:310` | probe | The help example for data retention set does not work |
| RV-227 | Minor | usability | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContextOptionsConfigurator.cs:50` | trace | Connection failure is logged as one fixed line with no exception type or SQLite result code, and EF's own event is suppressed |
| RV-228 | Minor | usability | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs:12` | trace | Stale and stranded documentation comments |
| RV-229 | Minor | usability | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/BlobEncryptionMetadataStore.cs:170` | trace | One bad row aborts every blob-encryption command and the error names no row |
| RV-230 | Minor | usability | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetAlertRepository.cs:58` | probe | Every turn after the alert threshold logs a Warning for an expected duplicate budget alert; HasAlertedTodayAsync has no caller |
| RV-231 | Minor | usability | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs:145` | probe | Budget refusals read as arithmetic errors and omit the request, the UTC day and the governing setting |
| RV-232 | Minor | usability | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs:2919` | probe | Coordinator error lines omit operation id and phase, and the pre-erasure rollback line drops the reason |
| RV-233 | Minor | usability | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionPolicyStore.cs:177` | probe | data retention set: refusals name neither the value nor the valid classes, one rule is shared by several class names, and the accounting floor silently replaces the request |
| RV-234 | Minor | usability | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.CovenantInventory.cs:352` | trace | data status and the factory path drop the cause when the Covenant inventory cannot be measured |
| RV-235 | Minor | usability | U25 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryActivation.cs:336` | trace | Factory-erasure error logs carry no operation id |
| RV-236 | Minor | performance | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:299` | trace | Prune plan and blocker lists are unbounded: one blocker and one diagnostics query per protected entry or aged run |
| RV-237 | Minor | performance | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:1553` | probe | Statement fan-out per retention candidate: repeated sqlite_master probes, duplicated checks and identical per-session reads |
| RV-238 | Minor | performance | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.WorkspaceReset.cs:246` | trace | Workspace reset scans tapestry_nodes about 18 times (no scope index) and recomputes the full fingerprint for the post-commit reconcile |
| RV-239 | Minor | reliability | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs:1271` | probe | Factory reset does not sweep prune quarantine directories (.arcanum-retention-*) under the logs root, yet reconciles as clean |
| RV-240 | Minor | reliability | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:3349` | probe | In production the frozen prune authority is never found, so apply recomputes cutoffs and fails the whole prune on one Session status change |
| RV-241 | Minor | reliability | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:6380` | probe | The prune log glob matches non-dated names that apply can never delete, and any checkpoint holding one is unrecoverable |
| RV-242 | Minor | usability | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:2404` | probe | Expired-lease idempotency claims are blocked forever with the code Data.IdempotencyLeaseActive and a 'reconciliation' no route performs |
| RV-243 | Minor | usability | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:2716` | probe | Default configuration yields one 'Blocked' plan entry per historical inference run, and held or pinned entries one per entry, in an unbounded, uncapped plan |
| RV-244 | Minor | usability | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:3519` | probe | Reconciliation and recovery failures name no candidate, table or file, and the recovery arms log nothing |
| RV-245 | Minor | usability | U26 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:3545` | probe | Planned labelled artifacts are applied as 'Data.PlanChanged: changed or became protected after planning' although the plan did not change |
| RV-246 | Minor | reliability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:1396` | probe | DELETE of an in-flight (not Bound) attachment answers 404 Data.NotFound instead of its Data.AttachmentInFlight blocker |
| RV-247 | Minor | reliability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:1406` | probe | A Campaign-targeted reset of a store with no owner is refused as 409 Data.Blocked, not the documented Data.InvalidRequest, after the CLI has already asked for confirmation |
| RV-248 | Minor | reliability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:3260` | probe | A failed in-process restore after a rolled-back delete marks the operation Failed and strands the attachment bytes in quarantine |
| RV-249 | Minor | reliability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:4808` | probe | Retention mutations block a thread ~31 s on a held write lock, ignore cancellation, then report a no-op as 'operator review' and terminalize the operation |
| RV-250 | Minor | reliability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:7954` | probe | GET /api/data/status throws FileNotFoundException when a managed file disappears during tree measurement |
| RV-251 | Minor | reliability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs:738` | probe | A promoted initiating request that survives a proven stage-one abort is missing from the next closure's census |
| RV-252 | Minor | usability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:1765` | trace | Retention failure results and the hosted-sweep warning carry no operation id |
| RV-253 | Minor | usability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:2463` | trace | Plan blocker and conflict reason codes are scattered string literals absent from ErrorCodes and from every document |
| RV-254 | Minor | usability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:2692` | probe | reset-memory --campaign refusal names the wrong stores and is Data.Blocked after the confirmation prompt, not Data.InvalidRequest as documented |
| RV-255 | Minor | usability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:3107` | trace | Delete-session/attachment errors say 'request a new dry-run' but those commands have no dry-run; 'Data was pruned' is used for every operation |
| RV-256 | Minor | usability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:5017` | trace | Global-reset conflict messages give no remedy, and the documented remedy needs a started host while factory-reset needs a stopped one |
| RV-257 | Minor | usability | U27 | `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs:569` | probe | Admission gate timeouts name nothing about what held the database; three gate codes are undocumented, unmapped and answer 500 |
| RV-258 | Minor | reliability | U28 | `src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs:371` | probe | Idempotency claims stranded in Running by a crash are never swept, contrary to DESIGN |
| RV-259 | Minor | reliability | U28 | `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs:334` | probe | Fork copy-time blob identity is never reused in production; tests only exercise the path production cannot take |
| RV-260 | Minor | reliability | U28 | `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs:695` | probe | Missing-file sweep permanently deletes a live attachment row when containment or existence cannot be proven |
| RV-261 | Minor | usability | U28 | `docs/Arcanum.DESIGN.md:1093` | trace | Docs name an arcanum operations verb that does not exist and show a typed-looking operation list --json | jq example |
| RV-262 | Minor | usability | U28 | `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs:550` | probe | operation list silently shows only the newest 100 operations; the store silently clamps limit and the CLI cannot page |
| RV-263 | Minor | usability | U28 | `src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureGuard.cs:110` | trace | MemoryErasureGuard.KeyUnavailableError omits the remedy its sibling KeyLostError carries |
| RV-264 | Minor | usability | U28 | `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs:642` | probe | Saga memory text search is ASCII-only case-insensitive but documented as 'Case-insensitive' |
| RV-265 | Minor | usability | U28 | `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs:481` | probe | Attachment source revalidation turns any non-IO exception into a persisted CorruptMetadata with a generic reason and no log |
| RV-266 | Minor | reliability | U29 | `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:779` | probe | A stored logical key with leading whitespace cannot be looked up by its own key |
| RV-267 | Minor | reliability | U29 | `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:2137` | probe | Orphan sweep unlinks a new attachment whose bytes landed before the sweep but whose row has not committed |
| RV-268 | Minor | reliability | U29 | `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:2351` | trace | Promotion deletes the fsynced pending blob after publishing an unsynced copy |
| RV-269 | Minor | reliability | U29 | `src/RetroDownfall.Arcanum.Infrastructure/Data/UploadedFileRepository.cs:324` | probe | An uploaded-file delete interrupted after quarantine strands the file permanently |
| RV-270 | Minor | usability | U30 | `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantConnectionDrain.cs:226` | probe | Connection-level refusals name no holder and the open-failure log carries no cause |
| RV-271 | Minor | usability | U30 | `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureExposureReader.cs:191` | probe | Transient SQLite faults are reported as integrity failures at five sites, against the kernel's own classification and the API doc |
| RV-272 | Minor | usability | U30 | `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureWriter.cs:117` | probe | Disclosure writer closes for good after one failed open, says 'maintenance', and never logs why |

### U26: Arcanum.Infrastructure — Data (2)

#### RV-001 (Critical, reliability, crash-consistency, effort M): A prune with more than 10,000 candidates writes a checkpoint its own recovery rejects, blocking every retention operation for good

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:4102`

**Claim.** The prune planner is uncapped (`const int limit = int.MaxValue;`, line 299), and DESIGN says retention applies the complete plan with no total-item restriction. `SavePruneCheckpointAsync` (3672-3716) serializes every candidate into every checkpoint and refuses only above 5 MiB. `ParsePruneCheckpoint` throws `InvalidDataException("...exceeds the maximum sweep size")` when there are more than 10,000 candidate lines. Recovering any interrupted prune with 10,001 to about 36k-45k candidates therefore yields `RequiresAttention(operation.checkpoint_corrupt)`. While that `data-retention-prune` row exists, `TryStartSingleFlightAsync` refuses every data-retention-* start: prune, delete-session, delete-attachment, reset-memory and factory reset. The documented remedy `operation retry` (`ResetForRetryAsync`) keeps `CheckpointPayload`, so recovery re-reads the same bytes and strands the row again.

**Failure scenario.** Default rules (WorkspaceIndexes 30d at one candidate per chunk, SessionEntryEmbeddings 30d, IdempotencyClaims 7d, LongRunningOperations 90d) put more than 10,000 candidates in a first prune on an established installation. The prune is interrupted: Ctrl-C (surrendered to ReconciliationRequired), a host restart, lease adoption, or a hosted DeferredForMaintenance resume. The reconciler then marks the row checkpoint_corrupt. From then on `arcanum data retention prune`, session deletion, memory reset and factory reset all answer Data.Conflict naming that row. `operation retry` and `reconcile` reproduce the same state; `cancel` leads to the same handler (traced). The only exits are a full installation reset or manual SQL.

**Evidence (probe).** Probe U26Probe_PruneOver10000Candidates_WritesACheckpointItsOwnRecoveryRejects (scratch worktree at ac0d2d8e): 10,001 aged IdempotencyKeys were planned and applied (succeeded in 1m45s, checkpoint 760,200 bytes). The service's own persisted checkpoint was then replayed as an interrupted row through the real LongRunningOperationReconciler + DataRetentionRecoveryHandler. Output: `reconcile completed=0 attention=1 state=ReconciliationRequired code=operation.checkpoint_corrupt`; `next prune success=False code=Data.Conflict message=Another data-retention operation is already active: data-retention-prune ... is ReconciliationRequired.`; `retry reset=True retried.attention=1 ... code=operation.checkpoint_corrupt payloadKept=True`; `delete-session success=False code=Data.Conflict`. Code: Pruning.cs:4102 `if (lines.Length - candidateStart > 10_000) throw new InvalidDataException(...)`; LongRunningOperationReconciler.cs:706 maps InvalidDataException to CorruptCheckpoint; LongRunningOperationStore.cs:284-360 single-flight NOT EXISTS over data-retention-% rows in Pending/Running/Waiting/Cancelling/ReconciliationRequired; LongRunningOperationStore.cs:1254 ResetForRetryAsync leaves CheckpointPayload. No existing test exceeds 10,000 candidates.

**Suggested fix.** Stop embedding the full candidate list in each checkpoint. Either page the sweep (plan and apply bounded batches per durable slice) or persist the frozen candidate set once and checkpoint only the cursor. Make the writer's bound equal the reader's and enforce it at plan time. As a backstop, let prune recovery of an unreadable checkpoint re-plan from scratch (the kind is RestartIdempotently) rather than strand the row. Add a test that writes and recovers a checkpoint above 10,000 candidates.

### U29: Arcanum.Infrastructure — Data (5)

#### RV-002 (Critical, reliability, crash-consistency, effort S): Orphan sweep deletes every bound attachment whose file name ends in .tmp or contains .tmp.

`src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:2153` — related: `src/RetroDownfall.Arcanum.Core/Storage/SessionAttachmentPathSanitizer.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs`

**Claim.** SweepOrphanAttachmentFilesAsync unlinks any file older than its snapshot whose name EndsWith(".tmp") or Contains(".tmp.") (OrdinalIgnoreCase), and does so before it checks knownPaths or the live-row re-check. The blob file name is the sanitized original file name. SessionAttachmentPathSanitizer accepts names like notes.tmp and results.tmp.csv, and the upload MIME check ignores unknown extensions. ReconcileCoreAsync runs this sweep (inside DeleteStalePendingAsync) before SweepMissingFileRowsAsync, so one startup reconcile deletes both the bytes and the row. DESIGN states 'Bound files are not background-pruned'.

**Failure scenario.** An operator attaches export.tmp.csv, or the model attaches build.tmp.log from the workspace, and it works for the rest of the session. At the next `arcanum serve` start, SessionAttachmentPendingGcHostedService reconciles and the attachment silently disappears; the only trace is a 'row whose file is missing' log warning.

**Evidence (probe).** Probe U29Probe_reconcile_deletes_bound_attachments_whose_file_name_contains_tmp passed. Output: 'notes.txt: row=True file=True', 'notes.tmp: row=False file=False', 'data.tmp.csv: row=False file=False'. Code at SessionAttachmentStore.cs:2151-2159: `if (fileName.EndsWith(".tmp", ...) || fileName.Contains(".tmp.", ...)) { TryDeleteFile(absolute); continue; }`, which runs before `if (knownPaths.Contains(relative))` at :2163. The sanitizer (Core SessionAttachmentPathSanitizer.cs:44-154) only strips separators, reserved characters, `..` and leading or trailing dots.

**Suggested fix.** Recognise staging files only by the exact shapes their producers create, all made to start with '.'. Sanitized names never start with '.', because the sanitizer strips leading dots. The shapes are: EncryptedBlobStore's .{name}.tmp.{32hex}; the clock probe .sweep-clock.tmp.{32hex}; and AtomicCopyFileAsync staging, changed from destination + ".tmp" to the same dot-prefixed unique form. Run the knownPaths and live-claim checks before any name rule. Add the probe as a regression test.

#### RV-003 (Critical, reliability, crash-consistency, effort M): Attachment blob paths collide on case- and normalization-insensitive volumes: a later upload overwrites an earlier one, or the orphan sweep deletes it

`src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:2404` — related: `src/RetroDownfall.Arcanum.Infrastructure/Storage/EncryptedBlobStore.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs`, `src/RetroDownfall.Arcanum.Core/Storage/SessionAttachmentPathSanitizer.cs`

**Claim.** Logical keys are distinct under SQLite BINARY comparison (the FindLatest/GetByLogical predicates, UX_SessionAttachments_Bound, and the Ordinal BoundGateKey). BuildRelativePath, however, puts the raw logical key and file name into the on-disk path {session:N}/{logicalKey}/v{n}/{fileName}. On default APFS (case- and normalization-insensitive) and NTFS (case-insensitive), two keys that differ only by case or by NFC/NFD form share one directory, and one file if the names also match. EncryptedBlobStore.ReplaceOrMove then File.Replace-s the earlier blob. Reads are not bound to the row: the header's authenticated metadata (the row id) is never compared, and ReadBytesAsync does not re-hash against ContentSha256. So the earlier row silently serves the later bytes. When only the directory collides, the orphan sweep's Ordinal knownPaths set and BINARY RelativePath re-check do not recognise the on-disk casing. The sweep unlinks the file and SweepMissingFileRowsAsync deletes the row. PromotionsInFlight is also Ordinal on macOS.

**Failure scenario.** Default macOS or Windows install. In one session the operator attaches README.md and the model later attaches readme.md, or the operator re-uploads Report.pdf as report.pdf, or attaches café typed in a browser (NFC) and café read from a macOS workspace (NFD). Two outcomes, both without any error: (a) the first attachment now returns the second's content to every later turn and pin, or throws a length-mismatch error when the sizes differ; (b) the second attachment's bytes and row are deleted by the next startup reconcile.

**Evidence (probe).** Probes in reports/C/U29-reliability-probes/U29ProbeTests.cs, all passing (defect present). (1) U29Probe_case_only_duplicate_name_overwrites_the_earlier_attachment_bytes printed: "first row '.../notes.txt/v1/notes.txt' reads: SECOND-note; second row '.../Notes.txt/v1/Notes.txt' reads: SECOND-note; files on disk: 1". (2) U29Probe_case_only_logical_keys_share_a_directory_and_the_sweep_unlinks_one printed: rows '.../Report/v1/a.txt' and '.../report/v1/b.txt'; on disk '.../Report/v1/b.txt' and '.../Report/v1/a.txt'; after ReconcileAsync 'Report/a.txt row=True; report/b.txt row=False'. (3) U29Probe_normalization_only_logical_keys_share_a_directory_on_apfs printed: 'NFC key row=True; NFD key row=False; distinct keys=True'. Code: BuildRelativePath at SessionAttachmentStore.cs:2393-2405; sweep comparison at :2161-2171 (Ordinal HashSet plus `"RelativePath" = @relativePath`); EncryptedBlobStore.cs:86-96 `if (!File.Exists(destinationPath)) File.Move(...) else File.Replace(...)`; OpenReadAsync :1327-1364 passes no expected id. All 205 existing tests over these classes pass.

**Suggested fix.** Derive on-disk components from collision-free values for new rows, e.g. {session:N}/{attachmentId:N} or a lowercase hex digest of the logical key plus v{n}/{attachmentId:N}. Keep LogicalKey and OriginalFileName only in the row; existing rows keep their stored RelativePath. Publish attachment blobs create-new: refuse an existing destination instead of ReplaceOrMove. Before unlinking, have the orphan sweep check whether the file maps to a claimed row path under the volume's own comparison, not Ordinal. Bind reads to the row: pass the expected id and verify the header's authenticated metadata, or verify ContentSha256 on read. Add the three probes as regression tests.

### U01: Arcanum.Api — Intelligence (1)

#### RV-004 (Important, reliability, crash-consistency, effort M): One Session whose contribution cannot be prepared fails every Campaign-bound operator turn and stalls the Campaign fold

`src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupMaintenance.cs:138` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignRollupStore.Read.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Hosting/Loremaster.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`, `docs/Arcanum.DESIGN.md`

**Claim.** ProcessCampaignAsync goes through every pending Session of the Campaign (ORDER BY s.Id) and returns on the first failed contribution. Later Sessions are never processed and the Campaign fold never runs. ProcessPagesAsync (lines 216-221) passes on every store failure except ForbiddenAuthority. CampaignRollupTurnPreparer turns any maintenance failure into a turn failure (lines 628 and 643), and WizardIntelligenceProvider (1834-1843) ends the turn with that error before dispatch. A live turn calls ProcessCampaignAsync with a capability, which sets the cutoff to DateTimeOffset.MaxValue, so every other Session of the Campaign can fail it. There is a deterministic trigger: CampaignRollupStore.PrepareContributionAsync throws campaign_rollup.input_too_large whenever the first unconsumed native Entry is larger than 32768 minus the previous contribution's bytes (CampaignRollupStore.Read.cs:60-65), and the watermark never advances. This contradicts DESIGN §5.4.2: 'These allocation bounds do not cap Campaign lifetime or the number of contributors'.

**Failure scenario.** features.campaignRollups is on. A user pastes a 30 KB log into Session A of Campaign C. From the next turn on, every operator turn in A fails with campaign_rollup.input_too_large before any provider call (ProcessSessionAsync). Every turn in every other Session of C also fails, at ProcessCampaignAsync, because A is pending in C. Loremaster's background pass stops at A as well (Debug log only), so C's summary never folds again. The only remedies are a restart with rollups off, or deleting the Entry. The same head-of-line effect follows from two invalid paid summaries (paid again on every retry), a FastModel context window too small for a 32 KiB page (the default ContextWindowLimit is 8192), or a concurrent turn in another Session of C (EnsureCurrentAsync throws, which becomes Hub.Error).

**Evidence (probe).** Probe P1 (scratch worktree): the real CampaignRollupMaintenance.ProcessCampaignAsync over pending [poisoned, healthy], with the store refusing the first with campaign_rollup.input_too_large. Result: failure with that code; store.Prepared == [poisoned]; PrepareRollupAsync calls == 0; test passed. Probe P2: the real CampaignRollupTurnPreparer and WizardIntelligenceProvider in the existing Campaign harness, with maintenance returning that error from ProcessSessionAsync (row false) or ProcessCampaignAsync (row true). ExecutePromptAsync fails with campaign_rollup.input_too_large and chat.AllBufferedCalls is empty; both rows passed. The store behaviour is pinned by CampaignRollupStoreTests.An_oversized_entry_fails_before_materialization_and_does_not_pay_a_cursor (34,000-byte Entry). The CampaignRollupMaintenanceTests StoreDouble returns no pending Sessions, so the per-Session loop has no test.

**Suggested fix.** (a) In the store, carry an oversized Entry rather than refuse it: render a fixed [omitted: N bytes] marker or a rune-safe prefix and advance the watermark, as Long Rest does. (b) In ProcessCampaignAsync, log and skip a failing Session, keep going, and still run the fold over surviving contributions. (c) In CampaignRollupTurnPreparer, treat maintenance failures other than authority, claim or configuration staleness as best-effort, like the Long Rest clean-schedule refusal at 475-494: proceed with the current artifact. (d) Add a multi-Session ProcessCampaignAsync test.

#### RV-005 (Important, usability, error-message, effort S): A batch whose input cannot be opened stays in_progress and /reset calls the file 'missing'

`src/RetroDownfall.Arcanum.Api/Intelligence/BatchProcessingService.cs:385` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/BatchRecoveryService.cs`, `src/RetroDownfall.Arcanum.Api/OpenAiV1BatchesEndpoints.cs`, `docs/Arcanum.API.md`

**Claim.** OpenCompatibleReadAsync is called outside the stop-as-failed guard, so InvalidDataException/CryptographicException at open escapes to ProcessBatchWithCleanupAsync and leaves the row in_progress with no error-file note. BatchRecoveryService.InputExistsAsync maps the same exceptions to false, and the reset endpoint answers 400 'input file is missing' (code not_found) although the file exists but is unreadable. API.md:285 and the Command Reference promise that an input-read failure ends the batch as failed with a note.

**Failure scenario.** A restored or bit-rotted uploaded input (bad header, not an encrypted envelope) makes the batch hang in_progress; the only operator remedy, POST /v1/batches/{id}/reset, says the file is missing, and only a host restart turns the batch to failed, with a log line only.

**Evidence (probe).** Probe P3 (existing harness, input file overwritten): header flip and non-envelope file give thrown InvalidDataException, status=in_progress, no output/error file, then ResetStuckBatchAsync => InputMissing. A tail-byte flip (failure after open) gives status=failed, error file, Error log naming AuthenticationTagMismatchException, i.e. the documented behaviour. Code: BatchProcessingService.cs:385-387 vs guarded lookahead 405-435; BatchRecoveryService.cs:305-309; OpenAiV1BatchesEndpoints.cs:86-92.

**Suggested fix.** Wrap the open in the same TryStopAfterUnexpectedFailure bookkeeping (note on line 1, publish failed); make InputExistsAsync tri-state so reset says 'input file is present but cannot be read or decrypted; re-upload and create a new batch'; keep the 'missing' text for a truly absent file.

#### RV-006 (Important, usability, config-surprise, effort S): Budget enabled with the default zero limit enforces nothing, and budget status then reports $0 spent / $0 remaining

`src/RetroDownfall.Arcanum.Api/Intelligence/BudgetMonitor.cs:34` — related: `src/RetroDownfall.Arcanum.Api/Health/BudgetEndpoints.cs`, `src/RetroDownfall.Arcanum.Cli/Commands/BudgetCommands.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs`, `docs/Compendium.README.md`

**Claim.** CheckAsync returns success when Enabled is true but DailyLimitUsd <= 0 (the default). The same rule is in BudgetReservationService (lines 50, 155, 293). GET /api/budget and `arcanum budget` only query spend when dailyLimit > 0, so they print Daily limit $0.00, Spent today $0.00, Remaining $0.00. No validator rule, doctor row or doc sentence says that 0 means unlimited.

**Failure scenario.** Operator turns cost.budget.enabled on in Compendium (the descriptor allows 0), leaves dailyLimitUsd at its default 0, and believes spend is capped. Inference is unmetered; the status command shows 'Remaining $0.00', which reads as an exhausted budget rather than an unenforced one.

**Evidence (probe).** BudgetMonitor.cs:34 `if (!budget.Enabled || budget.DailyLimitUsd <= 0) return Result.Success();`. BudgetEndpoints.cs:41-48 `localSpend = enabled && dailyLimit > 0 ? ... : 0m`. BudgetCommands.cs:63-70 prints the zero figures. ConfigurationValidator.cs has no Cost.Budget rule (grep: only the 'budget moved to Cost.Budget' migration line). Compendium.README.md:272-273 'Maximum UTC-day spend when enforcement is enabled'. ConfigurationPresetPlanner.cs:507 already treats 'enabled && limit > 0' as the only real policy. Probe P9: `new BudgetPolicySettings{Enabled=true}` has DailyLimitUsd=0.

**Suggested fix.** Add a ConfigurationValidator error/warning for Enabled && DailyLimitUsd <= 0 naming both keys; add a doctor/health row; make /api/budget always report real spend and an `enforced` flag; make the CLI print 'Not enforced: set Arcanum:Cost:Budget:DailyLimitUsd'; state the rule in Compendium.README.md.

#### RV-007 (Important, usability, diagnostics, effort S): Deferral of Campaign rollup maintenance is invisible; a Familiar or non-OpenAI-compatible maintenance model never produces a summary and nothing says so

`src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupMaintenance.cs:282` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupProviderClientFactory.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Hosting/Loremaster.cs`, `docs/Compendium.README.md`

**Claim.** When the maintenance model's provider is not OpenAICompatible, ResolveClientAsync returns no lease and ProcessPagesAsync returns Deferred('This provider cannot prove one frozen physical maintenance attempt.'). CampaignRollupTurnPreparer ignores the result's Deferred/reason; Loremaster logs only a Debug 'remains pending' line without the reason. Compendium.README.md:181 does not state the provider restriction and ConfigurationValidator has no campaignRollups rule.

**Failure scenario.** Installation with only a Claude Code or Codex Familiar provider turns on features.campaignRollups. Turns succeed, no Campaign summary is ever written, no warning, health or doctor row appears; the only trace is a Debug line with no reason.

**Evidence (probe).** CampaignRollupMaintenance.cs:247, 282-285; CampaignRollupProviderClientFactory.cs:80-83; CampaignRollupTurnPreparer.cs:625-646 (only IsFailure checked); Loremaster.cs:271,283 (LogDebug, no reason). Probe P10b: maintenance returning Deferred(reason) gives turn success=True with nothing surfaced. Compendium.README.md:181 vs :182 (Long Rest row names OpenAI-compatible).

**Suggested fix.** Log Deferred reason at Warning (rate-limited per Campaign) with Campaign and Session ids; add a validator/doctor warning 'campaignRollups needs an OpenAI-compatible FastModel/DefaultModel'; document it in Compendium.README.md.

#### RV-008 (Important, usability, error-message, effort M): A Campaign-rollup summary failure ends the user's turn as HTTP 400 Covenant.InvalidContent, and the policy rejects ordinary /route tokens as host paths

`src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs:625` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupMaintenance.cs`, `src/RetroDownfall.Arcanum.Core/Storage/CampaignSummaryPolicy.cs`, `docs/Arcanum.DESIGN.md`

**Claim.** PrepareLiveAsync returns contributed.Error / folded.Error from CampaignRollupMaintenance (provider errors, or the second invalid-summary error) as the turn's own failure. Covenant.InvalidContent maps to 400 with the text 'Campaign continuity must be one bounded summary without host paths or attachment bytes.', which reads as a bad client request and names no remedy. CampaignSummaryPolicy rejects any token that starts with '/' and is longer than one character, so a summary mentioning /api/health, /v1/batches or /clear fails.

**Failure scenario.** Rollups enabled on a coding Campaign; the FastModel writes a summary containing '/api/health' on both attempts. Every chat turn in that Campaign now returns 400 InvalidContent (progress is not advanced, so the same fold reruns on the next turn) until the operator guesses to disable features.campaignRollups. A transient FastModel provider outage fails the same way. DESIGN says Long Rest's clean schedule is best-effort; nothing says a rollup failure must end the turn.

**Evidence (probe).** CampaignRollupTurnPreparer.cs:625-631, 640-646; CampaignRollupMaintenance.cs:469-471, 485-492; ArcanumErrorMapper.cs:160; CampaignSummaryPolicy.cs:148 (Core). Probe P8: ParseStructuredResponse rejects '/api/health', 'POST /v1/batches', '/clear' with Covenant.InvalidContent; accepts 'src/Foo.cs' and '~/.config/...'. Probe P10a (real WizardIntelligenceProvider harness with maintenance returning that failure): turn success=False, code=Covenant.InvalidContent, same message, 0 provider calls. Not executed end to end with a real model returning two bad summaries (trace).

**Suggested fix.** Treat maintenance failure at turn preparation as non-fatal (log Warning with Campaign and Session ids, proceed with the existing frozen artifact) or wrap it in a distinct maintenance error naming features.campaignRollups and FastModel; narrow the path rule so URL routes and slash commands are not 'host paths' (require an existing path under a registered root, or a drive/UNC/home prefix).

### U02: Arcanum.Api — Intelligence (2)

#### RV-009 (Important, reliability, api-contract, effort S): The Long Rest lane ignores the provider's declared output-token field

`src/RetroDownfall.Arcanum.Api/Intelligence/LongRestProviderClientFactory.cs:110` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/OutputTokenLimitWirePolicy.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupProviderClientFactory.cs`, `src/RetroDownfall.Arcanum.Api/ProvingGrounds/MemoryEvaluation/MemoryEvaluationProviderClientFactory.cs`, `docs/Compendium.README.md`

**Claim.** Options() builds the SDK options without the OutputTokenLimitWirePolicy that ChatClientFactory.cs:199 installs. `providers.outputTokenLimitWireDialect` is a provider-level contract (Compendium README:114: the exact output-limit field the endpoint accepts, `maxTokens` for endpoints that require the legacy field, including Ollama 0.40.6). Long Rest summary and adjudication requests to such a provider therefore carry max_completion_tokens: 2048, which the endpoint does not honour. The Campaign rollup and memory-evaluation owned factories have the same omission.

**Failure scenario.** An operator on a maxTokens endpoint enables Long Rest. Every summary and adjudication call goes out with no output bound the endpoint honours. The 2048-token worst-case reservation (LongRestProvider.cs:73-79) understates the spend, and a reasoning model can run long. An answer over 65,536 characters is discarded by ReadLongRestText (ModelCallExecutor.LongRest.cs:264-266) after it has been paid for, which becomes LongRest.InvalidOutput, a NeedsOperator park.

**Evidence (probe).** Probe Probe_long_rest_client_ignores_max_tokens_dialect, provider with OutputTokenLimitWireDialect=MaxTokens: the long-rest body was `{"messages":[...],"model":"model","max_completion_tokens":2048}`, and the interactive client with the policy sent `{..."max_tokens":2048}`. LongRestProviderDispatchTests inspects the Long Rest body (tools, strict) but never the output-limit field. CampaignRollupProviderClientFactory.cs:91-95 and MemoryEvaluationProviderClientFactory.cs:93-97 also build options without the policy.

**Suggested fix.** Add `options.AddPolicy(new OutputTokenLimitWirePolicy(selection.Provider.OutputTokenLimitWireDialect), PipelinePosition.PerCall)` for the chat client, with the Enum.IsDefined check from ChatClientFactory.cs:99. Do the same in CampaignRollupProviderClientFactory and MemoryEvaluationProviderClientFactory, add a wire-body test per lane, and record it in DESIGN 5751.

#### RV-010 (Important, reliability, crash-consistency, effort S): One corrupt pinned attachment fails every turn of its Session

`src/RetroDownfall.Arcanum.Api/Intelligence/SessionContextPinMaterializer.cs:126` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.ContextPreview.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Storage/EncryptedBlobStore.cs`, `tests/RetroDownfall.Arcanum.Tests/Intelligence/SessionContextPinMaterializerTests.cs`

**Claim.** MaterializeAsync turns only IOException, UnauthorizedAccessException and InvalidOperationException into a per-pin Error block. The attachment store reports a truncated, mismatched or tampered blob with InvalidDataException (a SystemException, not an IOException) or CryptographicException, and lookups fail with DbException. These escape MaterializeAsync, and neither caller catches them (WizardIntelligenceProvider.cs:2011-2016 for every bound turn, WizardIntelligenceProvider.ContextPreview.cs:228-238 for context inspect). DESIGN §10.2.6 says a single failure becomes an error/status block.

**Failure scenario.** A Session pins a text attachment, and its encrypted blob is later truncated or swapped (disk fault, interrupted restore, row/blob mismatch). Pins are re-materialized before every bound turn, so every later turn in that Session, and every /intelligence/context/inspect, fails with a generic error until the operator finds and unpins that one pin.

**Evidence (probe).** Filter at line 126: `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)`. Throw sites: SessionAttachmentStore.OpenReadAsync:1350-1353 throws InvalidDataException on a length mismatch; EncryptedBlobStore.cs:575-712 throws InvalidDataException on a truncated header or chunk, and line 627 throws CryptographicException. Probe Probe_pin_materializer_corrupt_attachment_escapes (a NoOpSessionAttachmentStore whose readBytes throws) printed `escaped: System.IO.InvalidDataException` and `escaped: System.Security.Cryptography.CryptographicException`. The existing SessionContextPinMaterializerTests pass at ac0d2d8e (76/76 in the combined run) but cover only IOException (test line 1468).

**Suggested fix.** Around each pin, catch every exception that is not the caller's cancellation. Add a constant DescribeFailure entry for corrupt or unreadable sources (InvalidDataException, CryptographicException). Add tests for both exception types and for a store lookup that throws.

#### RV-011 (Important, usability, error-message, effort M): Guardrails PII detectors reject epoch timestamps and long IDs, rescan whole transcripts, and name no kind

`src/RetroDownfall.Arcanum.Api/Intelligence/Guardrails/GuardrailsPipeline.cs:541` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`, `docs/Compendium.README.md`, `docs/Arcanum.API.md`

**Claim.** With Features:Guardrails on and the default DetectPii true, any 10-digit run (phone pattern) or 13-19 digit run (card pattern, no Luhn check) is rejected. On /v1 the whole stateless transcript, including old assistant and tool messages, is scanned on every turn. The rejection says only 'personally identifiable information detected. Redact PII and retry.' with no kind and no span. The single boolean security.guardrails.detectPii is the only control, so the only remedy is turning all PII detection off.

**Failure scenario.** An operator enables guardrails to catch card numbers. A developer pastes a log with a Unix timestamp and is refused. In an agent conversation over /v1 an earlier assistant reply or tool result contains a timestamp, so every later turn of that conversation is refused permanently, and nothing in the message says which text triggered it.

**Evidence (probe).** GuardrailsPipeline.cs:541 CreditCardPattern \b(?:\d[ \-]?){13,19}\b; :544 PhonePattern accepts a bare \d{3}\d{3}\d{4}; :157-175 ConcatenateMessageText joins every message regardless of role; WizardIntelligenceProvider.cs:8754-8770 passes all StatelessMessages; BuildError :469-491 ignores the violation type apart from the pii- prefix. Probe P4: 'epoch 1729800000' -> Guardrails.PiiDetected (audited pii-phone); 'created_at_ms=1729800000123', 'order 4000123456789012 shipped', 'build number 20261024123456' -> Guardrails.PiiDetected (pii-credit-card). Wizard probe: stateless transcript [user 'when did the job start?', assistant 'It started at epoch 1729800000.', user 'thanks, and when did it end?'] -> Guardrails.PiiDetected 'Input rejected: personally identifiable information detected. Redact PII and retry.' Compendium.README.md:129 documents only the boolean detectPii.

**Suggested fix.** Add a Luhn check and digit-boundary rules for card/phone patterns; scan only the new user turn(s) or messages after the last assistant message; include the violation type in the message; add per-detector toggles in Security:Guardrails.

#### RV-012 (Important, usability, diagnostics, effort M): A parked Long Rest sweep cannot be diagnosed: HTTP status dropped, no provider/model, swallowed exceptions, no logger

`src/RetroDownfall.Arcanum.Api/Intelligence/LongRestProviderFailure.cs:84` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.LongRest.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/LongRestProviderClientFactory.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/LongRestMaintenance.Dispatch.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/LongRestMaintenance.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRest/LongRestSweepCoordinator.Run.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Hosting/LongRestSweepHostedService.cs`, `docs/Arcanum.Command.Reference.md`

**Claim.** LongRestProviderFailure.Classify parses the HTTP status and then discards it, returning the same ProviderRejected/ProviderUnavailable text for 400/401/404. Logs and errors name neither provider nor model. LongRestProviderClientFactory.ResolveCredentialAsync swallows every exception and returns Hub.Error with a message that blames reachability and promises server logs, but the class has no logger. LongRestMaintenance has no ILogger and its settle/release/heartbeat catch blocks are empty or cancel silently. The coordinator parks with only the code and logs nothing.

**Failure scenario.** After a Keychain change the Long Rest embedding call returns 401. arcanum operation show and doctor report LongRest.ProviderRejected; the log has only 'Long Rest provider call failed (ClientResultException)'. The Command Reference tells the operator to 'Fix the provider configuration (model, credential, request limits)'; there is nothing to choose between the three. A credential-resolution exception surfaces as terminal code Hub.Error, which is not in the documented table.

**Evidence (probe).** LongRestProviderFailure.cs:84-93 (status parsed at 97-116, then only two codes returned). Probe P3: Classify for 'HTTP 401 (invalid_api_key...)', 'HTTP 404 (model_not_found...)' and 'Status: 400' all -> 'LongRest.ProviderRejected: The Long Rest provider rejected the call.' ModelCallExecutor.LongRest.cs:104 and :185 log only exception type. LongRestProviderClientFactory.cs:104-107 catch (Exception) -> Hub.Error / NativeGenericFailure; ctor :24-31 takes no logger; Probe P3: resolver that throws -> code=Hub.Error message='Inference failed. Ensure the provider is running and reachable, then try again. See server logs for details.' LongRestMaintenance.cs:19-22 has no ILogger; LongRestMaintenance.Dispatch.cs:239-261 (empty catches) and :311-314 (heartbeat failure -> dispatch.CancelAsync with no record). LongRestSweepCoordinator.Run.cs catch (Refused) -> RequiresAttention(code) with no log; no ILogger in Infrastructure/Operations/LongRest/; LongRestSweepHostedService.cs:167-172 logs only exception type. Command Reference line 1134 guidance for ProviderRejected. With the real ProviderApiKeyResolver a store failure yields a null key (status result, not a throw), so the common credential fault arrives as an HTTP 401 ProviderRejected.

**Suggested fix.** Keep the status in the error (message 'rejected the call (HTTP 401)' or a field on Error); log provider name, model, status and operation id once per refusal at Warning; add ILogger to LongRestMaintenance and LongRestProviderClientFactory and log the swallowed catch sites; document Hub.Error for the credential case or give it a LongRest.* code and a specific message.

#### RV-013 (Important, usability, api-contract, effort S): /v1 turns a context overflow and a daily-budget refusal into 429 plus 'Inference failed. See server logs for details.'

`src/RetroDownfall.Arcanum.Api/Intelligence/OpenAi/OpenAiStreamErrorMapper.cs:69` — related: `src/RetroDownfall.Arcanum.Api/OpenAiV1Endpoints.cs`, `src/RetroDownfall.Arcanum.Api/Primitives/ArcanumErrorMapper.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/TurnContextGuards.cs`, `docs/Arcanum.API.md`, `docs/Arcanum.DESIGN.md`

**Claim.** The native routes return the real text for Hub.ContextBudgetExceeded and Budget.Exceeded. The OpenAI-compatible routes (buffered and streaming error frame) replace both with type api_error, code inference_failed and the generic message, and keep HTTP 429. Hub.ContextBudgetExceeded is also reused for internal staleness (stale payload, stale admission), so it cannot be mapped to a context-length answer as it stands. The overflow message names no action although DESIGN says it does.

**Failure scenario.** An editor or agent framework sends a long transcript to /v1/chat/completions and gets 429. OpenAI SDKs retry 429 with backoff, so the same deterministic refusal is sent again, then the user sees 'Inference failed. See server logs for details.' The client cannot tell overflow from a rate limit or from the operator's daily budget, cannot compact or switch model, and an OpenAI-style context_length_exceeded handler never fires.

**Evidence (probe).** OpenAiStreamErrorMapper.cs:69-73 default arm -> ResolveMessage lets only 4 fixed strings through (lines 10-16, 85-98). OpenAiV1Endpoints.cs:279-292 buffered path uses MapPublicOpenAiError (default arm line 1194) and ResolvePublicInferenceFailureMessage (default arm line 1209); status from ArcanumErrorMapper.cs:134-135 and 320-321 = 429. Probe P1 (ArcanumWebApplicationFactory, FakeIntelligence.NextFailure): /v1/chat/completions -> '429 {"error":{"message":"Inference failed. See server logs for details.","type":"api_error","param":null,"code":"inference_failed"}}' for both codes; /api/intelligence/ping -> 429 with 'Context budget exceeded: 9000 accounted tokens (8000 input + 1000 reserved) exceed the 8192-token window using profile p.' and 'Daily budget of 5.00 USD reached.'; streaming frame via OpenAiStreamErrorMapper.Map gives the same generic text. DESIGN.md:1707-1708 says the error 'names ... the smaller-request, compaction, or model-selection action'; the native text (TurnContextGuards.CheckContextBudget) names counts only. API.md:917 documents 429 for Hub.ContextBudgetExceeded only as 'Confirmed Covenant content could not fit'. Other uses of the same code for non-overflow states: ModelCallExecutor.cs:599-606 ('replan before dispatch'), 653, 745, 778; ModelCallExecutor.LongRest.cs:235; ModelCallExecutor.CampaignMaintenance.cs:109.

**Suggested fix.** On /v1 only: map Hub.ContextBudgetExceeded to 400 invalid_request_error / context_length_exceeded with the counts message; map Budget.Exceeded to insufficient_quota with a message naming the daily limit key. First give the stale-payload and stale-admission conditions their own error code. Add the action to the native overflow message (arcanum session compact, raise contextWindowLimit, choose a larger model). Update API.md:917.

### U03: Arcanum.Api — Intelligence (3)

#### RV-014 (Important, performance, missing-index, effort M): BudgetReservations is full-table scanned by SumOutstandingAsync on every turn gate, reservation and provider round

`src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs:753` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnAccountingHandle.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/WeaveService.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/BudgetMonitor.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/DailySpendAuthority.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/BudgetReservations.sql`, `src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs`

**Claim.** SumOutstandingAsync (lines 741-770) filters on (BudgetPeriod = @period AND Status = @reserved) OR Status = @uncertain. The only indexes are (BudgetPeriod, Status) and (ExpiresAt), so the Status-only OR term cannot use an index and SQLite scans the whole BudgetReservations table. It runs inside BEGIN IMMEDIATE write transactions in ReserveAsync and AdjustAsync, and as a read in RecheckDailyLimitAsync and the turn-start budget gate (BudgetMonitor -> DailySpendAuthority). Rows accumulate without bound by default: retention.automaticSweepsEnabled defaults to false and the accounting floor is 365 days. WeaveService opens its own run plus reservation for every standalone EmbedBatchAsync call and WorkspaceIndexingService calls it once per file page, so an index run adds many rows.

**Failure scenario.** Budget-enabled install (cost.budget.enabled=true) after months of use or one large index run. Per turn the code does 2 + R full scans (BudgetMonitor gate, ReserveAsync, one Adjust or Recheck per provider round R), and two of those hold the SQLite write lock. Measured on the SQLCipher fixture (Debug build, loaded machine so upper bounds): 20k rows 7.7 ms per sum, 100k rows 85.6 ms (Reserve+Release 95.9 ms), 300k rows 162.5 ms (Reserve+Release 221 ms). A 5-round turn at 100k rows adds about 0.6 s of synchronous SQLite reading plus about 90 ms write-lock holds during which streamed entries, tool-interaction rows, Long Rest and daemon writers queue on busy_timeout.

**Evidence (probe).** BudgetReservationService.cs:753 `WHERE ("BudgetPeriod" = @period AND "Status" = @reserved) OR "Status" = @uncertain`. Schema/Tables/BudgetReservations.sql:14-15 defines only IX_BudgetReservations_BudgetPeriod_Status and IX_BudgetReservations_ExpiresAt. Callers: TurnAccountingHandle.cs:221 (AdjustAsync), :251 (RecheckDailyLimitAsync), :447 (ReserveAsync), BudgetMonitor.cs:50 via DailySpendAuthority (WizardIntelligenceProvider.cs:675,1053), WeaveService.cs:130. Probe A2 (scratch worktree U03GrimoireProbe, real BudgetReservationService on GrimoireFixture): EXPLAIN QUERY PLAN -> `SCAN BudgetReservations`; reservationRows=100000 recheck_avg_ms=85.242 sumOutstanding_avg_ms=85.579 reserve+release_avg_ms=95.944; 300000 rows -> 171.603 / 162.476 / 221.042 ms; 0 rows -> 0.141 / 0.045 / 0.730 ms. Probe A3 fix check: after CREATE INDEX IX_BudgetReservations_Status ON BudgetReservations(Status) the plan is MULTI-INDEX OR over both indexes and at 300k rows sumOutstanding went 444.518 ms -> 0.008 ms and reserve+release 368.657 ms -> 0.391 ms (back-to-back on the same loaded machine). Compendium.README.md lines 274-276 give the retention defaults.

**Suggested fix.** Add `CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_Status" ON "BudgetReservations" ("Status")` (or a partial index on Status IN (Reserved, UncertainPaid)) in Schema/Tables/BudgetReservations.sql, add the next-version transition so evolved installs receive it, update the expected-index list and manifest tests, and note it in DESIGN section 22.2. Optionally let the turn-start gate and the first Ensure share one read.

#### RV-015 (Important, reliability, correctness, effort S): Accumulated research summaries starve every source; long runs return zero citations

`src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:783`

**Claim.** Every pass's answer is retained and placed before any source. ResolveRetainedContentBudget subtracts all of them from the 32,768-char synthesis budget. After enough passes the fetch phase stops at source 1, and the result carries no citations at all. With no hop counter (owner policy), run length decides whether any source is read.

**Failure scenario.** A research run without --sources whose passes keep finding a new URL, each with a typical 3.5k-char Perplexity answer. After about 9 passes the budget is used up. The run reports 'Stopped fetching at source 1 of N', reads no page, and returns an answer whose Citations list is empty, while the summaries' [n] markers dangle.

**Evidence (probe).** Lines 787-800 subtract every summary length. Lines 1159-1166 emit every summary before sources. MaxPingPromptChars defaults to 32,768 (IntelligenceSettings.cs:102). Probe P4: 11 passes, 'source_budget_reached: Stopped fetching at source 1 of 10', pages read 0, citations returned 0, prompt 32,768 chars.

**Suggested fix.** Give summaries a bounded share of the synthesis budget (for example newest-first within a third of it) and reserve the remainder for fetched sources in both ResolveRetainedContentBudget and BuildSynthesisPrompt.

#### RV-016 (Important, reliability, error-handling, effort S): Attachment-store exceptions discard the billed search/browse/research answer

`src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:915` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/WebWorkflowEndpoints.cs`, `tests/RetroDownfall.Arcanum.Tests/Api/WebWorkflowEndpointTests.cs`

**Claim.** AttachAsync turns only 'attachments disabled' and 'session not found' into a Result. Every exception thrown by ISessionAttachmentStore.PersistNewAsync escapes after the provider work is billed: AttachmentLimitExceededException at the code-owned 256 MiB MaxBytesPerSession, blob/encryption faults, disk full. That breaks the documented non-terminal attachmentError / attachment_failed contract (DESIGN 11.27, API 8.10.3).

**Failure scenario.** An operator runs `arcanum research --attach-to-session S` and S already holds about 256 MiB of attachments. Every search pass and the synthesis call are billed, PersistNewAsync throws, and the stream ends with the generic Hub.Error frame 'The research stream failed unexpectedly' with no result frame. `arcanum search`/`browse` with the same target return HTTP 500 instead of the answer with attachmentError.

**Evidence (probe).** Line 915: `SessionAttachmentRecord record = await attachments.PersistNewAsync(...)` has no try/catch. Lines 130-134, 247-251 and 651-656 assume AttachAsync only fails through Result. Probe P1: 1 billed provider call, then AttachmentLimitExceededException escapes SearchAsync. Probe P2: synthesis ran, frames Limits/searching/searching/source_exhausted/fetching/rendering/synthesizing, then the exception, no Result frame. WebWorkflowEndpointTests covers only the VanishingSessionRepository (Result) path.

**Suggested fix.** Wrap PersistNewAsync in try/catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested). Return Attachment.LimitExceeded for AttachmentLimitExceededException and a generic attachment error otherwise. Add a test with a throwing store for all three workflows.

#### RV-017 (Important, reliability, correctness, effort M): Research synthesis mixes provider-numbered summaries with renumbered sources, so citations point at wrong URLs

`src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:1141` — related: `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/WebResearch/PerplexityWebProvider.cs`

**Claim.** BuildSynthesisPrompt copies each pass's provider answer verbatim, with its own per-pass [n] markers. Sources are renumbered contiguously across passes and after denials (sources.Count + 1). The model is told to 'Cite claims using only the supplied [n] numbers', so a claim taken from any summary after pass 1 (or from pass 1 after a denied, skipped or deduplicated citation) is cited to a different URL.

**Failure scenario.** Pass 1 cites a.test/1 and a.test/2. Pass 2's Perplexity answer says 'Gamma is true [1]' with its [1] = c.test/3 (new) and [2] = a.test/1. The prompt shows that sentence beside Source [1] = a.test/1, while c.test/3 is Source [3]. The final answer cites Gamma to a.test/1.

**Evidence (probe).** Lines 1159-1166 append searches[index].Answer unmodified. Line 550 numbers sources by sources.Count + 1. PerplexityWebProvider.MapResponse keeps provider indices (index + 1). Probe P3 (passes): the prompt contains 'Gamma is true [1].', result citation [1] = https://a.test/1 and [3] = https://c.test/3.

**Suggested fix.** Rewrite each summary's [n] markers to the research index of the same URL: per pass, provider index -> citation URL -> research source index. Drop markers whose URL was not read or emitted. Alternatively strip the markers from summaries before framing them.

#### RV-018 (Important, reliability, correctness, effort S): No-progress detector falsely stops memory-delegation turns after the delegation root is consumed

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:4069` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/ToolLoopProgressDetector.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/CovenantDispatchGate.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/MemoryDelegation/MemoryDelegationRootBinding.cs`

**Claim.** Once covenantScope.Protection is non-null (bound at :701 to memoryDelegation.CaptureOutputProtection, non-null once the root is consumed), each round's ToolLoopProgressEntry is recorded with empty arguments and result. Rounds that call the same tool on different inputs then hash identically, and the turn fails with Hub.NoProgressDetected. That contradicts DESIGN 10.7.3 / CHAT-LOOP: different arguments or a new result is progress.

**Failure scenario.** POST /api/intelligence/memory-delegation: the parent calls delegate_task, then read_file_chunk(a) in round 2 and read_file_chunk(b) in round 3. Round 3's fingerprint equals round 2's {1,'read_file_chunk','',''}, so the turn stops with Hub.NoProgressDetected although it was making progress.

**Evidence (probe).** Lines 4069-4073 and 3890-3894: `covenantScope?.Protection is null ? processed.ArgsSnapshot : string.Empty` (same for ResultText). The stop site is 4252-4269. MemoryDelegationRootBinding.CaptureOutputProtection returns non-null once Root.IsConsumed. Probe P5: blanked rounds give first=False, second=True; the same calls unblanked give False/False.

**Suggested fix.** Pass the detector a keyed digest (HMAC-SHA256 with a per-turn random key) of the protected arguments and result instead of empty strings, so equality survives without a stable content digest. Suppress RepeatedSignature logging for protected turns.

#### RV-019 (Important, usability, error-message, effort S): Sanctum preflight reports a malformed or oversized apply_patch as a Sanctum block and hides the parser's diagnosis

`src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:2041` — related: `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/UnifiedDiffParser.cs`, `src/RetroDownfall.Arcanum.Core/Sanctum/SanctumResult.cs`

**Claim.** ValidateToolPathsAndNetworkAsync parses the patch to list touched paths; on failure TryParseApplyPatchManifest returns only parsed.Success and discards parsed.Code/Message, then UnparsablePatchDenial is rendered by SanctumDenialMessage as 'The Sanctum Guard has blocked this action: PatchPathPreflight - apply_patch target paths could not be validated within the active policy limits..' followed by 'The Dungeon Master must update the Sanctum config to allow this operation.' The UnifiedDiffParser knows the real cause (invalid_hunk, patch_too_large with 'Split the diff into smaller apply_patch calls') and the apply_patch tool itself reports it when the Sanctum is off.

**Failure scenario.** A Campaign with an enabled Sanctum; the model sends a unified diff with a bad hunk header (the commonest apply_patch mistake) or a diff over MaxPatchBytes. The model is told a policy blocked it and that a human must change Sanctum config, so it cannot repair the patch and retries blindly or gives up; the operator reading the transcript is pointed at the wrong control. The same footer is wrong for SanctumDenialKind.PolicyUnavailable (the fix is repairing the Campaign, not 'allowing' the operation). With the Sanctum off the same patch yields the precise parser message, so behaviour differs by an unrelated setting.

**Evidence (probe).** ToolExecutionPipeline.cs:2041-2050 (TryParseApplyPatchManifest false -> verdicts.Observe(UnparsablePatchDenial)), 2203-2218 (fixed DenyReason, BreachType PatchPathPreflight), 2224-2253 (parsed.Code/Message dropped), 2385-2393 (footer text). Probe P3 (reflection on SanctumDenialMessage + UnifiedDiffParser.Parse) printed: model text 'The Sanctum Guard has blocked this action: PatchPathPreflight - apply_patch target paths could not be validated within the active policy limits..\nThe Dungeon Master must update the Sanctum config to allow this operation.' while the parser said 'code=invalid_hunk message=The hunk header is malformed.' and for an oversized diff 'code=patch_too_large ... Split the diff into smaller apply_patch calls.' 199 existing related tests pass on the unmodified tree, none asserts this text.

**Suggested fix.** Make TryParseApplyPatchManifest return the parse result, put parsed.Code/Message into the denial DenyReason (do not frame it as a policy breach), choose the footer by SanctumDenialKind (no 'update the Sanctum config' for PolicyUnavailable or parse failures), and say 'operator' instead of 'Dungeon Master' in model-facing text. Add a pipeline test with an enabled Sanctum and a malformed diff asserting the hunk diagnosis reaches the model.

#### RV-020 (Important, usability, api-contract, effort M): Context-window overflow is HTTP 429, hidden behind a generic message on /v1, and names no remedy

`src/RetroDownfall.Arcanum.Api/Intelligence/TurnContextGuards.cs:213` — related: `src/RetroDownfall.Arcanum.Api/Primitives/ArcanumErrorMapper.cs`, `src/RetroDownfall.Arcanum.Api/OpenAiV1Endpoints.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/OpenAi/OpenAiStreamErrorMapper.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.cs`, `docs/Arcanum.API.md`, `docs/Arcanum.DESIGN.md`

**Claim.** CheckContextBudget returns Hub.ContextBudgetExceeded with 'Context budget exceeded: N accounted tokens (I input + R reserved) exceed the L-token window using profile P.' It names neither provider/model nor any action, contradicting DESIGN 1708-1709 (the context-boundary error 'names ... the smaller-request, compaction, or model-selection action' and 'the exact retrieval, continuation, configuration, or recovery action'). ArcanumErrorMapper maps the code to 429 (a retryable rate-limit signal) although the overflow is permanent for the request, and API.md:917 documents that 429 only for 'Confirmed Covenant content could not fit'. On /v1/chat/completions the code falls through MapPublicOpenAiError to api_error/inference_failed with 'Inference failed. See server logs for details.', and the stream mapper sanitizes the message the same way. The same code is also reused for internal integrity failures (stale payload, cache proof unavailable).

**Failure scenario.** An OpenAI-SDK client (default 2 retries on 429) sends a long conversation to /v1/chat/completions. It receives 429 inference_failed, retries twice with backoff, then raises a rate-limit error; no client-side logic keyed on context_length_exceeded can trim, and the operator must read server logs to learn the cause. On the native route the user gets numbers but no hint to compact the Session, shorten the prompt, switch model, or raise providers.contextWindowLimit.

**Evidence (probe).** TurnContextGuards.cs:213-217. Probe P1 printed: 'native message: Context budget exceeded: 10024 accounted tokens (9000 input + 1024 reserved) exceed the 8192-token window using profile 'test'.'; 'native http status: 429'; 'v1 buffered status: 429'; 'v1 buffered code: inference_failed'; 'v1 stream message: Inference failed. See server logs for details. | type=api_error | code=inference_failed'. ArcanumErrorMapper.cs:134-135; OpenAiV1Endpoints.cs:1179-1209,1235; OpenAiStreamErrorMapper.cs AllowedMessages; ApiBootstrapper.cs:107-138 (real rate limiter emits rate_limit_exceeded + Retry-After); ModelCallExecutor.cs:599-605,653,745,778 (internal uses of the code); TurnContextGuardsTests asserts only the code, CovenantErrorContractTests pins 429.

**Suggested fix.** Add provider/model and the remedy to the message ('shorten the prompt, run arcanum session compact, start a new Session, choose a model with a larger window, or raise providers.contextWindowLimit'). Map a true overflow to a non-retryable status (400/413) with a distinct /v1 code context_length_exceeded and a safe message (it contains only counts), keep 429 for budget and rate limits, give the internal-integrity uses their own code, and update API.md plus CovenantErrorContractTests.

#### RV-021 (Important, usability, api-contract, effort S): Research keeps, and cites, sources whose page was never read; fetch failures are silent

`src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:509` — related: `docs/Arcanum.DESIGN.md`, `tests/RetroDownfall.Arcanum.Tests/Intelligence/WebResearchCampaignBindingTests.cs`, `src/RetroDownfall.Arcanum.Cli/Commands/WebWorkflowCommands.cs`

**Claim.** In the fetch loop only WebResearch.SsrfBlocked drops a citation. Any other read failure (timeout, 403 bot protection, JavaScript-only shell, unsupported content, redirect overflow, response too large) and the absence of a ReadUrl-capable reader leave content empty and fall through to sources.Add(...) after a 'rendering' progress frame. The empty source is emitted into the synthesis prompt as an empty fenced block and listed in result.citations because EmittedSourceIndexes includes it. No per-source frame names the URL, host or outcome. DESIGN 11.27 promises 'Source [n] numbering stays contiguous over the sources that were actually read' and that a refused citation is not kept as an empty source.

**Failure scenario.** arcanum research "..." --sources 5 where two of the pages return 403 or time out (routine). The CLI prints 'Fetching source 2 of 5' / 'Rendering source 2 of 5' for each, the final answer cites [2] from a URL and title the model never read, the citation list presents it as evidence, and nothing tells the operator which pages failed or why. Even denied sources are identifiable only by index, so the operator must consult the Sanctum breach list to learn which host to allow.

**Evidence (probe).** WebResearchWorkflowService.cs:509-553 (read result used only if success; only SsrfBlocked continues; sources.Add unconditional), 599-614 (citations filtered only by EmittedSourceIndexes). Probe P2 (reader returns WebResearch.Timeout for bad.test): frames were Fetching 2 of 2, Rendering 2 of 2, Synthesizing, Result with no failure frame; 'result citations: [1] https://good.test/a ; [2] https://bad.test/b'; synthesis prompt around Source [2]: 'URL: https://bad.test/b\nTitle: Source 2\n\n\n```' (empty body). DESIGN.md:4047 vs code; WebResearchCampaignBindingTests header states refused citations are 'reported as denied rather than kept as an empty source'. 199 existing related tests pass.

**Suggested fix.** On any non-success read emit a 'source_unreadable' progress frame carrying host and error code and drop the source (or at least exclude it from citations), as is done for denied sources; emit one explicit frame when no read provider exists; include the host in source_denied/source_unreadable messages. Add a test where the reader fails with a non-SSRF code.

#### RV-022 (Important, usability, api-contract, effort S): Research reports a token-budget-truncated synthesis as complete and drops synthesis warnings

`src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:599` — related: `src/RetroDownfall.Arcanum.Api/Models/WebWorkflowModels.cs`, `src/RetroDownfall.Arcanum.Core/Intelligence/Models/PromptTurnResult.cs`, `docs/Arcanum.API.md`

**Claim.** ExecutePromptAsync returns PromptTurnResult.FinishReason ('length' when the provider cut the answer) and Warnings, but ResearchAsync uses only Text and Usage. result.Truncated is computed only from the search provider (searches.Any(search => search.Truncated)). Secondary result-metadata problems: Model echoes request.Model ?? DefaultModel ?? 'server-default' rather than the model used (PromptTurnResult does not carry it), and usage.costUsd is search-provider cost only while the token counts include synthesis; API.md enumerates none of the result fields so truncated/model/costUsd meanings are undocumented.

**Failure scenario.** Default tokenBudget is 2000 and reasoning models spend it on reasoning; the synthesis stops mid-sentence with finish_reason=length. The CLI/Forge print the clipped Markdown and citations as a finished report, truncated:false, no progress or warning frame; the operator has no signal to rerun with a larger --token-budget.

**Evidence (probe).** WebResearchWorkflowService.cs:569-638. Probe P2b (intelligence stub returns FinishReason 'length'): 'result.truncated=False answer='The 2026 change was that the committee decided to' frames=Limits,Progress:searching,...,Progress:synthesizing,Result' with no warning frame. WizardIntelligenceProvider.cs:4776-4785 shows the buffered PromptTurnResult carries streamFinishReason and Warnings. WebWorkflowModels.cs has no finish-reason or warning member.

**Suggested fix.** When synthesis.Value.FinishReason == 'length' set result.Truncated and emit a warning progress frame naming --token-budget; forward PromptTurnResult.Warnings; document the research result fields (truncated, model, usage.costUsd = search-provider cost) in API.md 8.10.3; report the model actually used or drop the claim.

### U04: Arcanum.Api — Intelligence (4)

#### RV-023 (Important, performance, redundant-llm-call, effort M): Lexicon fallback extractor makes an extra LLM call on every user turn, even when the Lexicon is empty

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:5577` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/LexiconEntityExtractor.cs`, `src/RetroDownfall.Arcanum.Core/Lexicon/ILexiconService.cs`, `src/RetroDownfall.Arcanum.Core/Configuration/ArcanumRuntimeDefaults.cs`

**Claim.** RetrieveLexiconEntriesAsync runs LexiconEntityExtractor.ExtractAsync (a buffered 128-token JSON LLM call, 15 s preflight timeout) whenever the SemanticRouter supplied no entities. That is every user turn on a default install: Features.Lexicon defaults to true, spell routing makes no LLM call when there are no spells (SemanticRouter returns null on availableSpells.Count==0), and FastModel is unset by default so the call goes to the main model client. There is no check that the Lexicon holds any entry for the scope, and ILexiconService has no cheap emptiness query.

**Failure scenario.** Fresh installation, Lexicon empty, no spells: every `arcanum run`/Forge turn first waits for a full extra LLM round trip (on the main model, e.g. a local 27B, or a model swap if FastModel differs), billed and ledgered as Extraction, before the main request is even built; worst case 15 s on a hung provider. The extractor result can never match anything because MatchEntitiesAsync has nothing to search.

**Evidence (probe).** WizardIntelligenceProvider.cs:2449 call site; 5565-5577 `if (!settings.Value.ResolveIntelligence().EnableLexiconSystem) return null; ... if (entities.Count == 0) {`; 5606 `await LexiconEntityExtractor.ExtractAsync(extractorClient, GetSemanticRouterUserProbe(request), preflight, ...)`. PublicConfigurationSettings.cs:27 `Lexicon { get; set; } = true`; ArcanumSettings.cs:20 `string? FastModel` (no default); SemanticRouter.cs:39 `if (availableSpells.Count == 0) return null;`. Probe A (ScriptingChatClient, empty FakeLexiconService, no spells, DisableMcpTools): buffered `lexicon=True bufferedCalls=2 ... firstCallHead=Extract the named entities (people, projects, APIs, or other`; `lexicon=False bufferedCalls=1`; streaming `lexicon=True bufferedCalls(preflight)=1 streamingCalls(main)=1`. WizardIntelligenceProviderTests.DefaultSettings sets Lexicon=false precisely so the extractor does not fire, so no scenario test covers the default. DESIGN (Lexicon read path, ~1514) documents the extractor as deliberate 'so memory retrieval stays available' but not that it must run when there is nothing to retrieve.

**Suggested fix.** Add ILexiconService.HasEligibleEntriesAsync(LexiconScope) (`SELECT 1 FROM lexicon_entries WHERE <scope> AND RetiredAtUtc IS NULL LIMIT 1`, Campaign tier then Global) and skip the extractor (return null) when it is false; additionally start the extractor concurrently with the query embedding and retrievals rather than ahead of them. Update FakeLexiconService, DESIGN Lexicon read path and add a test counting provider calls with an empty Lexicon.

#### RV-024 (Important, performance, context-budget-double-count, effort S): Covenant admission Measure() charges every tool schema into each fragment, so a Confirmed Covenant is withheld on mid-size windows (and each call re-estimates all tools)

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:7866` — related: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantAdmissionPlanner.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/CovenantDispatchGate.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/ModelTokenEstimator.cs`

**Claim.** ResolveCovenantAdmission measures headroom as contextWindow minus a baseline EstimateContext over the real chatOptions (tools included), then prices each Covenant fragment with Measure(text), which calls EstimateContext on [System(text)] with the same chatOptions. EstimateContext charges Options.Tools on every call, so every Measure result is fragment + all tool schemas. MeasureSections sums up to three Measure calls (Global Confirmed, Campaign Confirmed, Proposed), so the tools are charged up to 3x on top of the baseline that already contains them. CovenantAdmissionPlanner.Plan refuses Confirmed when measureSections(confirmedOnly) > headroom. The planner also calls Measure up to 2+32 times for sections plus once per eligible candidate (<=160), about 194 full estimates per provider attempt.

**Failure scenario.** 32,768-token window, unknown-model profile (every local Ollama model, token cost is bytes-based), 25 MCP tools of about 560 bytes, 4 Global-Confirmed Covenant entries (about 0.5k tokens): estimated input without Covenant is 18,295 tokens, real headroom about 13k, yet Measure returns about 16k, the planner refuses, CovenantConfirmedNoFit=true and the Covenant is not sent. With 40 tools the real headroom is about 3.1k and the section needs about 0.5k, still refused. The operator's standing agreement silently disappears while headroom is unused. Separately each Covenant turn burns about 60 ms (40 tools) to about 450 ms (120 tools) of CPU re-hashing tool schemas before the first token.

**Evidence (probe).** Code: lines 7854-7864 `int baseline = ModelTokenEstimator.EstimateContext(new ModelTokenizationRequest(lease.Provider, lease.ResolvedModel, messages, chatOptions, ...)).TotalTokens; ulong headroom = ...`; lines 7866-7877 `ulong Measure(string text) => text.Length == 0 ? 0UL : (ulong)ModelTokenEstimator.EstimateContext(new ModelTokenizationRequest(lease.Provider, lease.ResolvedModel, [new MeAiChatMessage(ChatRole.System, text)], chatOptions, 0, 0)).TotalTokens;`; ModelTokenEstimator.cs:209 EstimateTools(estimates, request.Options.Tools, ...). Probe J (32,768 window): fragment text alone = 63 tokens; Measure(fragment) = 72 (0 tools), 29,822 (10 tools), 119,143 (40 tools), 357,377 (120 tools). Probe K (full turn, real CovenantDispatchGate, 4 Confirmed entries): window 32768 tools 0 -> covenantReachedModel=True NoFit=False; tools 10 -> True/False; tools 25 -> covenantReachedModel=False CovenantConfirmedNoFit=True estimatedInputTokens=18295; tools 40 -> False/True 28576; window 131072 tools 25/40/60 -> admitted. Deliberate break (patch Measure-deliberate-break.patch, only line 7874 `chatOptions` -> `new ChatOptions()`): at 32768 tools 25 and 40 the Covenant reaches the model (est. input 18,461 and 28,742) and the 18 existing tests in WizardIntelligenceProviderCovenantTests, CovenantContextPressureTests, CovenantAdmissionPlannerTests still pass, i.e. no existing test catches it (they all use DisableMcpTools:true, 2 built-in tools). Probe F (text cached, fixed per-call cost): Measure with 0/10/40/120 tools = 0.011/0.087/0.31/2.35 ms. CovenantLimits: 64+64+32 eligible entries, MaxProposedRemovals=32.

**Suggested fix.** Measure fragments without the tool schemas: pass `new ChatOptions()` (or use EstimateText plus the existing per-section framing allowance) in the Measure delegate so the planner prices only the Covenant bytes against a headroom that already excludes the tools. Add a Covenant dispatch test with 25+ tools on a 32k window asserting the section is admitted (the plausible regression every current test passes is Measure including tools).

#### RV-025 (Important, performance, io-per-turn-scales-with-workspace, effort M): Every turn after a 5 s idle re-walks the entire working-directory tree looking for SPELL.md

`src/RetroDownfall.Arcanum.Infrastructure/Workspaces/SpellScanner.cs:761` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`, `src/RetroDownfall.Arcanum.Core/Configuration/SpellSettings.cs`

**Claim.** ResolveRoutedSpellAsync (WizardIntelligenceProvider.cs:5376) calls SpellScanner.ScanMetadataAsync(workspaceRoot, ..., ttl) on every user-facing turn. The metadata cache TTL is 5 s (SpellSettings.cs:30), shorter than any human-paced chat gap, so almost every turn takes the miss path, which walks the whole working directory breadth-first: two readdir passes per directory (EnumerateFiles to find one filename, then EnumerateDirectories), Path.GetFullPath, two IsPathUnderWorkspace and a Directory.ResolveLinkTarget per subdirectory, synchronous I/O on a thread-pool thread. Only dot-directories and node_modules/bin/obj/out/dist are pruned (not target, build, vendor, venv, Pods, ...). Cost is linear in workspace size and is paid before any inference.

**Failure scenario.** Developer working in a monorepo or a Rust/Python/Unity tree with about 10k directories / 100k files: each turn spends about 0.5 s CPU (0.8-1.5 s wall warm-cache, more cold) walking the tree before the router or model is touched, then does it again for the next turn five seconds later.

**Evidence (probe).** SpellScanner.cs:364-373 (ttlSeconds, `DateTimeOffset.UtcNow - cachedEntry.CachedAt < TimeSpan.FromSeconds(ttlSeconds)`), 761-843 EnumerateSpellFiles, 1024 EnumerateDirectoryEntriesSafely, 1092 ResolveCanonicalDirectory; HeavyDirectoryNames at 262-269; SpellSettings.cs:30 `MetadataScanCacheTtlSeconds { get; set; } = 5`. Probe H (SpellScanner.ScanMetadataAsync ttl=0, isolated ARCANUM_TEST_HOME, load average about 110): this repo 389 dirs/5,896 files = 23 ms process CPU (295 ms wall first, 23-48 ms warm); synthetic 2,000 dirs/19,990 files = 91-97 ms CPU (261-654 ms wall); synthetic 10,000 dirs/99,990 files = 523-600 ms CPU (844-1,559 ms wall). TTL behaviour: second call inside 5 s = 0.02-0.08 ms; after 5.2 s idle full rescan again (85 ms CPU at 2k dirs, 538 ms CPU at 10k dirs).

**Suggested fix.** Replace the wall-clock TTL with change detection (directory mtime stamps or the file watcher WorkspaceIndexingService already registers for the working directory) so an unchanged tree costs a stat, and/or restrict discovery to the documented spell roots (spells/, .arcanum/spells) instead of the whole workspace, and/or extend HeavyDirectoryNames (target, build, vendor, venv, Pods, packages). At minimum raise the default TTL to minutes.

#### RV-026 (Important, usability, ui-responsiveness, effort M): Production streaming withholds every pre-commit frame (status, sessionBound, context, protected-maintenance notice) until the provider's first token

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:1368` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs`, `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.LongRestStatus.cs`, `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`

**Claim.** In the resilience branch of StreamPromptCoreAsync the outer loop buffers Status/SessionBound/ConversationBound/Context frames in preCommitBuffer and yields them only when the first non-pre-commit event arrives. IProviderHealthTracker is always registered in the host, so every production stream takes this branch; the tracker-less passthrough branch is test-only. The 'Finishing protected memory maintenance...' status is therefore delivered only after the maintenance it announces, and its acknowledgement (finally at line 1830) is released before the client has been told.

**Failure scenario.** A local model with a 30-60 s prefill: the client sees only blank NDJSON keep-alives, no 'Mage is generating response...', no compression notice, no session id (a client that disconnects in that window never learns the id of the Session the turn created). With protected Long Rest debt, the operator's 'Finishing protected memory maintenance...' line appears after all paid maintenance sends instead of before them.

**Evidence (probe).** Code: lines 1363-1405 (gate loop adds to preCommitBuffer and calls MoveNextAsync), 1447-1489 (buffer flushed only after the gate event), 1635-1639 IsPreCommitStreamingEvent, 1782 Status yield, 1819-1832 maintenance status with finally AcknowledgeProtectedMaintenance. Production composition: Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs:1830 TryAddSingleton<IProviderHealthTracker>; Api/ApiBootstrapper.cs:650 ActivatorUtilities.CreateInstance. Probe S1 (provider stream held on a TaskCompletionSource): withTracker=True -> 0 frames received while the provider was silent, Status/SessionBound/ConversationBound/Context all tagged AFTER-release; withTracker=False -> the same 4 frames tagged BEFORE-release. Probe S1b (the existing protected-maintenance scenario rerun with a ProviderHealthTracker): 'provider sends already made when the client received it = 3 (total 3)' versus 0 without a tracker. Existing test WizardIntelligenceProviderTests.LongRestStatus_a_streamed_turn_with_protected_debt_says_so_once_before_the_assistant_begins asserts sendsWhenStatusArrived == 0 but passes because the harness CreateWizard defaults healthTracker to null (tests/.../WizardIntelligenceProviderTests.cs:9563).

**Suggested fix.** Yield informational pre-commit frames as they are produced; commitment should only decide whether a fallback restart is still allowed. On a fallback restart suppress frames already emitted (the shared TurnContextSeed keeps the Session id stable). Add a health tracker to the LongRestStatus streaming test and add a 'status arrives before first token' test in the fallback test class.

#### RV-027 (Important, usability, api-contract, effort M): Context-window overflow error names no provider/model or remedy, maps to HTTP 429, and is flattened to a generic message on /v1

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:7990` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnContextGuards.cs`, `src/RetroDownfall.Arcanum.Api/Primitives/ArcanumErrorMapper.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/OpenAi/OpenAiStreamErrorMapper.cs`, `src/RetroDownfall.Arcanum.Api/OpenAiV1Endpoints.cs`, `docs/Arcanum.API.md`, `docs/Arcanum.DESIGN.md`

**Claim.** The general context-overflow failure is produced by TurnContextGuards.CheckContextBudget as 'Context budget exceeded: N accounted tokens (I input + R reserved) exceed the L-token window using profile P.' It names neither provider nor model, does not say the limit comes from providers[].contextWindowLimit (default 8192), and gives no action. DESIGN section 10.7.3 requires the context-boundary error to name the provider/model fact and the smaller-request, compaction or model-selection action. The code Hub.ContextBudgetExceeded maps to HTTP 429 (retry-later semantics) and API.md documents it only for the Covenant case. On /v1 both the streaming and buffered mappers replace it with 'Inference failed. See server logs for details.' / code inference_failed.

**Failure scenario.** An Ollama user with default contextWindowLimit 8192 and a long Session (or a /v1 tool such as an editor agent) overflows the window. Native clients get an unactionable sentence and a 429; OpenAI-SDK clients retry the 429 with backoff, resending a prompt that can never succeed, and cannot trigger their own truncation because they never see context_length_exceeded or 400.

**Evidence (probe).** Probe S10 (provider ContextWindowLimit=600, 900-word prompt): buffered code=Hub.ContextBudgetExceeded, providerCalls=0; message "Context budget exceeded: 11492 accounted tokens (10468 input + 1024 reserved) exceed the 600-token window using profile 'fallback:OpenAICompatible:provider-a:wizard-fallback-test-model:o200k_base'."; ArcanumErrorMapper.ResolveStatusCode -> 429; OpenAiStreamErrorMapper.Map -> {Message='Inference failed. See server logs for details.', Type=api_error, Code=inference_failed}. Code: TurnContextGuards.cs:201-218; ArcanumErrorMapper.cs:134; OpenAiStreamErrorMapper.cs default arm + SanitizeMessage; OpenAiV1Endpoints.cs MapPublicOpenAiError/ResolvePublicInferenceFailureMessage (generic). Docs: DESIGN 10.7.3 terminal-event table row 'provider or context boundary'; API.md:917 lists the code as 'Confirmed Covenant content could not fit'. Sibling attachment message at WizardIntelligenceProvider.cs:3017-3021 does include the action.

**Suggested fix.** Put provider, model, the limit's source (providers[].contextWindowLimit) and the actions (shorten the prompt, compact or start a new Session, raise contextWindowLimit to the model's real window, choose a larger-context model) in the message. Map the general overflow to 400 (native) and invalid_request_error/context_length_exceeded (/v1), keeping 429 only where Covenant admission truly needs it, and document the general case in API.md section 8.23.

### U05: Arcanum.Api — Tower

#### RV-028 (Important, performance, n-plus-one, effort M): Campaign import re-parses every spell once per imported spell, under the workspace write lock

`src/RetroDownfall.Arcanum.Api/Tower/CampaignEndpoints.cs:691` — related: `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/Spells/SpellRepository.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/SpellScanner.cs`

**Claim.** The import loop (CampaignEndpoints.cs:679-691) calls spellRepo.ImportAsync per bundle spell. ImportCreateStagedAsync (SpellRepository.cs:1493) acquires the workspace write lock and then runs SpellScanner.ScanAsync (:1535), a full parse of every SPELL.md under the global folder and the whole Campaign tree, only to evaluate FindByName(allSpells, trimmedName). Scan cost grows with the number of spells already present, so the loop is also quadratic in bundle size, and every other spell writer in that workspace waits behind the lock for each scan.

**Failure scenario.** Importing a 40-spell bundle into a 20k-file Campaign takes 12-15 s (about 290-370 ms per spell) when the same import into an empty workspace takes about 40 ms per spell; the Campaign's spell editing is blocked behind the lock for the duration of each scan.

**Evidence (probe).** Probe P2 and P2b: 40 imports into a 1,000-dir/20,000-file tree: total 11,745.8 ms and 15,291.7 ms, per-import medians about 290 and 370 ms; baseline into an empty workspace median 38.4-40.4 ms; SpellScanner.ScanAsync of that tree alone 95-385 ms x5. Code: SpellRepository.cs:1535 'await SpellScanner.ScanAsync(workspace, ...)' immediately after 'using IDisposable writeLockReleaser = await _workspaceLocks.AcquireAsync(...)'.

**Suggested fix.** Decide name collisions from ScanMetadataAsync names (no body parse), and give the import route a batch entry point that scans once under one lock and tracks the names it adds while it runs.

#### RV-029 (Important, performance, n-plus-one, effort S): Campaign export repeats the full tree walk once per spell

`src/RetroDownfall.Arcanum.Api/Tower/CampaignEndpoints.cs:914` — related: `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/Spells/SpellRepository.cs`

**Claim.** ComposeCampaignExportAsync calls spellRepo.ListAsync(campaign.Path) once (one walk, :899), then for every non-built-in spell calls spellRepo.ExportAsync(summary.Name, campaign.Path) (:914-916). ExportAsync begins with GetAsync (SpellRepository.cs:505-507), i.e. a full uncached ScanMetadataAsync of the global folder and the Campaign root per spell. Cost is spells x tree.

**Failure scenario.** Exporting a Campaign with 25 spells in a 20k-file repository takes about 2.7-3.1 s of tree walking before a byte is serialized, versus the 73-85 ms one walk costs; a monorepo Campaign or 100 spells takes tens of seconds, holding the Covenant read lease (CovenantProtectedJsonResult) the whole time.

**Evidence (probe).** Probe P2 (U05SpellWalkProbe.P2): 1,000 dirs / 20,000 files, 25 spells: ListAsync 73.2 ms and 84.8 ms; 25 x ExportAsync 3,087.9 ms and 2,652.8 ms (123.5 and 106.1 ms each, 35-40x the list). Code: CampaignEndpoints.cs:914 'spellRepo.ExportAsync(summary.Name, campaign.Path, ...)' inside 'foreach (SpellSummary summary in summaries)'.

**Suggested fix.** Scan metadata once and export from it: add ISpellRepository.ExportAllAsync(workingDirectory) (or an ExportAsync overload that takes the already-found SpellMetadata) and use it in ComposeCampaignExportAsync.

#### RV-030 (Important, performance, full-scan, effort M): POST /api/memory/search is a full-corpus scan with a per-row lower() copy; the FTS index is unused

`src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs:1209` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/FullTextSearch/Entries_fts.sql`

**Claim.** SearchSessionAsync filters with 'instr(lower(e.Content), lower(@query)) > 0 ... ORDER BY e.CreatedAt DESC, e.Sequence DESC LIMIT @limit' (:1204-1213; session summaries :1265-1276), SearchAttachmentsAsync with three instr(lower()) terms (:1330-1342) and SearchWorkspaceAsync with two (:1413-1423). With no sessionId every row of Entries, Sessions.Summary, session_attachment_chunks and workspace_file_chunks is read, decrypted and lower-cased whatever the query and limit; the ORDER BY prevents any early exit, and scope=all does this five times in one request. Entries_fts exists (Data/Schema/FullTextSearch/Entries_fts.sql) and is used by the session list, but not here.

**Failure scenario.** On a mature installation (hundreds of thousands of entries, a whole repository in workspace_file_chunks) 'arcanum memory search foo' takes seconds per scope even when nothing matches and the user asked for 20 results.

**Evidence (probe).** Probe P5 (U05GrimoireProbe.P5, SQLCipher db 281 MiB, 60,000 entries x 1.5 KiB = 88 MiB): the statement verbatim takes 285-900 ms per call for a term that matches nothing and 284-600 ms for one that matches everything (rows=101), versus Entries_fts MATCH LIMIT 101 at 0.0-1.4 ms and the same statement scoped to one session at 0.1-0.7 ms. Linear in corpus bytes (about 5-6 s at 1M such entries); workspace chunks, the largest corpus, use the identical SQL shape (extrapolated, not measured).

**Suggested fix.** Add an FTS5 trigram table (substring, case-insensitive) for entries, attachment chunks and workspace chunks with a Core transition and backfill; until then bound the scan (newest N rows or a time budget) and report hasMore, and use the existing Entries_fts for single-token queries.

#### RV-031 (Important, performance, repeated-work, effort S): GET /api/spells/search scans every registered Campaign's tree on each call

`src/RetroDownfall.Arcanum.Api/Tower/SpellAuthoringEndpoints.cs:48` — related: `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/Spells/SpellSearchService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/Spells/SpellRepository.cs`, `src/RetroDownfall.Arcanum.Api/Tower/CampaignEndpoints.cs`

**Claim.** The handler loads up to 10,000 full Campaign rows (Settings and SanctumConfigJson included, :48-51) and passes them to SpellRepository.SearchAsync -> SpellSearchService.SearchAsync (SpellSearchService.cs:55-110), which runs one uncached SpellScanner.ScanSummariesAsync per Campaign (each walk also re-walks the global folder because ScanMetadataAsync merges global and local), even when campaignId or source narrows the answer to one Campaign. With no Campaigns registered, SpellRepository.SearchAsync (:421-441) runs the same 10,000-row query a second time.

**Failure scenario.** A typed-ahead spell search in a Forge session with 20 registered repository Campaigns costs 1-3.6 s per keystroke batch, linear in registered Campaigns, and keeps thread-pool threads busy walking directories.

**Evidence (probe).** Probe P3 (U05SpellWalkProbe.P3, 500 dirs / 5,000 files per Campaign, x3 each, two runs): 1 Campaign 24-250 ms; 5 Campaigns 204-1,339 ms; 10 Campaigns 340-2,497 ms; 20 Campaigns 990-3,591 ms.

**Suggested fix.** Select only Id and Path from Campaigns, skip the scan of any Campaign the campaignId/source filter excludes, and route the scans through the cached metadata scan (see the first finding) so repeat searches within the TTL cost nothing.

#### RV-032 (Important, performance, repeated-work, effort M): Every spell lookup walks the whole workspace tree with no cache

`src/RetroDownfall.Arcanum.Api/Tower/SpellExecutionEndpoints.cs:70` — related: `src/RetroDownfall.Arcanum.Api/Tower/SpellAuthoringEndpoints.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/Spells/SpellRepository.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/SpellScanner.cs`

**Claim.** Spell_Execute, execute-stream, versions (SpellExecutionEndpoints.cs:70, :193, :295) and validate/export (SpellAuthoringEndpoints.cs:88, :123) call ISpellRepository.GetAsync. SpellRepository.GetAsync (SpellRepository.cs:99-132) calls SpellScanner.ScanMetadataAsync with the default metadataScanCacheTtlSeconds = 0 (SpellScanner.cs:331-335; cache read :368 and write :404 are both guarded by ttlSeconds > 0), so a breadth-first walk of the global spells folder plus the entire Campaign root runs on a Task.Run thread-pool thread on every request. The inference turn that follows reads the same data through the 5 s TTL cache (WizardIntelligenceProvider.cs:5378-5382), so the handler's pre-flight GetAsync (used only for the 404 check unless a version is given) is the uncached walk. GET .../versions also fully parses every SPELL.v*.md sequentially just to read a description (SpellExecutionEndpoints.cs:342-358, :720-748).

**Failure scenario.** Operator runs a spell in a Campaign whose root is a 20k-file repository: each execute pays 56-314 ms before the model is called; in a 100k-file monorepo 0.5-1.1 s. Concurrent requests each occupy a pool thread for the walk; only identical (global, workspace) keys are coalesced.

**Evidence (probe).** Probe P1 (U05SpellWalkProbe.P1, SpellRepository.GetAsync, two runs under load average 56-106): 100 dirs/1k files 6-62 ms; 1,000 dirs/20k files 56-314 ms; 5,000 dirs/100k files 539-1,113 ms; this repo (about 390 dirs / 5.9k files) 17-29 ms. The same lookup through SpellScanner.ScanMetadataAsync(ttl: 5) is 0.0-0.1 ms after the first call. No invalidation hook exists (grep MetadataScanCache finds only the three uses in SpellScanner.cs), so adding the TTL needs one.

**Suggested fix.** Pass ArcanumRuntimeDefaults.Spells.MetadataScanCacheTtlSeconds from SpellRepository.GetAsync/ListAsync and SpellSearchService, and add a per-workspace invalidation (generation counter) bumped by SpellRepository's own writers (create, update, delete, clone, import, version create/update/activate) so a write is never shadowed. Skip the pre-flight lookup in Spell_Execute/execute-stream when no version is requested, since the turn resolves the spell itself.

#### RV-033 (Important, performance, repeated-work, effort M): Saga ranking evidence is recomputed from scratch on every Saga read and every Saga recall when Long Rest is on

`src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/SagaRankingReader.cs:21` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/SagaRankingEvidenceReader.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Weave/DivinationService.cs`, `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs`, `src/RetroDownfall.Arcanum.Api/Tower/SagaEndpoints.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`

**Claim.** SagaRankingReader.CreateAsync (SagaRankingReader.cs:21-29) calls SagaRankingEvidenceReader.ReadAsync whenever Features.LongRest is on and the Core schema is at least 20. That reads up to MemoryEvaluationLimits.RetainedRegistrations (1,024) evaluation roots and, per root, a full MemoryEvaluationEvidenceStore.ReadAsync plus CurrentCleanSourceAsync (three statements, one re-hashing the memory text) and SuccessfulUsesAsync. The result depends only on immutable reports, the dataset generation and asOf, yet nothing is kept between calls. Tower reaches it at MemoryEndpoints.cs:251-252 (ReadRankingStatusAsync), :449-454 (ExplainRankingAsync), :1473-1481 (ListCurationRowsAsync), SagaEndpoints.cs:90-92 (ListAsync) and :166-187 (/saga/divine), and every Saga-recalling turn reaches it through WizardIntelligenceProvider.cs:5963-5975 -> DivinationService.cs:277, :346, :398. The file is outside U05; it is filed here because five Tower routes pay for it.

**Failure scenario.** An installation with Long Rest enabled and a few hundred retained single-source evaluations adds tens to hundreds of ms to every 'memory status', Saga listing, Saga search and every turn that recalls Saga memory, independent of page size; at the 1,024 cap about 300 ms per call.

**Evidence (probe).** Probe P9 (U05RankingEvidenceProbe, reusing SagaStoreHarness): with 10 / 50 / 150 retained evaluations, SagaRankingEvidenceReader.ReadAsync takes 2.5-9.7 / 12.9-20.7 / 40.7-47.8 ms; SagaRankingReader.CreateAsync 2.6-3.0 / 12.0-16.4 / 41.5-47.0 ms; SagaMemoryStore.ListAsync(limit 10) 2.8-8.5 / 12.3-14.8 / 38.9-45.4 ms; ReadRankingStatusAsync 2.5-4.3 / 12.1-14.4 / 40.3-44.2 ms, i.e. about 0.29 ms per retained evaluation per call regardless of the 10-row page.

**Suggested fix.** Cache the evidence dictionary per (dataset generation, change stamp of memory_evaluation*/saga_committed_uses, asOf rounded to the next instant a retained report's validity edge can cross) and drop it when a report or a committed use lands.

#### RV-034 (Important, reliability, race, effort M): Campaign PUT and campaign import write back the whole row they read, silently reverting a concurrent Sanctum change

`src/RetroDownfall.Arcanum.Api/Tower/CampaignEndpoints.cs:274` — related: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/CampaignRepository.cs`, `src/RetroDownfall.Arcanum.Api/Security/SanctumEndpoints.cs`, `src/RetroDownfall.Arcanum.Api/Tower/PromptEndpoints.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/PromptRepository.cs`

**Claim.** PUT /api/campaigns/{id} reads the Campaign with a raw-SQL GetByIdAsync (detached entity, line 220), edits a few fields, and calls repo.UpdateAsync (line 274), which does _db.Campaigns.Update(campaign) (CampaignRepository.cs:481-491) and so rewrites every column, including SanctumConfigJson, from the snapshot taken at the start of the request. The import route does the same for settings (read at 472, write at 670-674, after a prompt-replace transaction, so its window is wide). SanctumEndpoints.cs:70-96 and Prompt PUT (PromptEndpoints.cs:221/330 + PromptRepository.cs:165-170) share the pattern. SessionEndpoints.cs:1361-1363 documents this exact hazard and fixed it for Sessions with PatchSessionAsync; Campaigns and Prompts were not fixed.

**Failure scenario.** Operator enables Sanctum (PUT /api/campaigns/{id}/sanctum {"enabled":true}) while The Forge saves the Campaign's name/settings or a campaign import with settings is running. The second commit restores the old SanctumConfigJson: Sanctum is silently disabled although both requests answered 200. Related by trace: a rename losing the NameLower unique race throws DbUpdateException from UpdateAsync (no constraint mapping) -> 500; a Prompt PUT on a row deleted after the read gets DbUpdateConcurrencyException -> 500.

**Evidence (probe).** Probe P2 (in-process host, real Grimoire): read the row in DI scope A exactly as the PUT handler does, commit PUT /sanctum over HTTP, then UpdateAsync the scope-A entity with only Name changed. Output: 'sanctum PUT 200 {"data":{"enabled":true,...}}', 'after B: enabled=True', 'after A: enabled=False'. CampaignRepository.UpdateAsync: `_db.Campaigns.Update(campaign); _ = await EfSaveChangesRetry.ExecuteAsync(_db, ...)`.

**Suggested fix.** Give ICampaignRepository field-scoped writes (header: name/type/description/settings; sanctum: config) as single parameterized UPDATEs naming only those columns, as PatchSessionAsync does; map the NameLower unique violation to Campaign.DuplicateName; do the same for Prompt PUT. Add a regression test that interleaves the two writes like P2.

#### RV-035 (Important, reliability, crash-consistency, effort S): GET codex reports an unreadable or over-cap CODEX.md as an existing empty file, so the next save destroys it

`src/RetroDownfall.Arcanum.Api/Tower/CodexEndpoints.cs:275` — related: `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodexReader.cs`, `src/RetroDownfall.TheForge.Ux/Services/Services/CampaignService.cs`

**Claim.** ReadCodexDtoAsync returns Success(new CodexContentDto(fullPath, content ?? string.Empty, File.Exists(fullPath))) (lines 275-279). CodexReader.TryReadAsync returns null for every non-success SecureFileReadStatus (TooLarge, InvalidUtf8, Rejected, AccessDenied, IoError), so a real read fault becomes a successful {exists:true, content:""} indistinguishable from an empty file; PUT then atomically replaces the file with whatever the client sends.

**Failure scenario.** A CODEX.md saved in Windows-1252 (one accented character) or larger than the 256 KiB code-owned cap is opened in The Forge codex editor (CampaignService.GetCodexAsync/PutCodexAsync): it shows an empty document, the operator types a line and saves, and the original file is gone. CLI and inference also silently see nothing.

**Evidence (probe).** Probe P3 calling the internal helpers: 'latin1: success=True exists=True contentLength=0', then WriteCodexAsync(content + "new line") -> 'after save: new line' (original replaced); 'oversize: success=True exists=True contentLength=0'. Existing CodexEndpointTests/CodexRouteTests (19 pass in the shared tree) cover links and round-trips only.

**Suggested fix.** Return the SecureFileReadStatus from CodexReader.ReadCodexFileAsync and make ReadCodexDtoAsync fail with typed codes (Codex.ContentTooLarge, new Codex.InvalidEncoding / Codex.Unreadable) instead of success; document in API.md; have The Forge refuse to open a codex whose read failed.

#### RV-036 (Important, reliability, api-contract, effort S): A Lore key containing '/' can be written but never read or deleted

`src/RetroDownfall.Arcanum.Api/Tower/LoreEndpoints.cs:141` — related: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.cs`, `src/RetroDownfall.TheForge.Ux/Services/Services/LoreService.cs`

**Claim.** POST /api/lore takes the key in the body and accepts any trimmed key up to 256 chars (lines 120-131); GET and DELETE take it as one route segment /lore/{key} (lines 42-57, 141-157). Routing leaves %2F encoded in a route value, so the CLI's and Forge's Uri.EscapeDataString(key) arrive as 'team%2Fstandards' and never match the stored 'team/standards'. System.Uri also collapses '.'/'..' segments (by trace, not probed). There is no other delete route.

**Failure scenario.** `arcanum lore set team/standards "use tabs"` succeeds; `arcanum lore get team/standards` and `arcanum lore delete team/standards` answer 404 Grimoire.LoreNotFound forever while `lore list` still shows the entry; the operator can only overwrite its value, never remove it.

**Evidence (probe).** Probe P1: 'POST 200 {"data":{"key":"team/standards",...}}', 'GET /api/lore/team%2Fstandards -> 404 Grimoire.LoreNotFound', 'DELETE /api/lore/team%2Fstandards -> 404 Grimoire.LoreNotFound', 'LIST contains key: True'. Clients: ArcanumApiClient.cs:2405,2486 and TheForge LoreService.cs:24,37 use Uri.EscapeDataString. LoreEndpointTests (3 tests, pass) never use a reserved character.

**Suggested fix.** Refuse '/', '\', '.' and '..' keys at POST with a validation error so no new orphan is created, and add a body/query-addressed get/delete (e.g. GET|DELETE /api/lore?key=) so existing ones can be removed; update CLI, Forge and API.md.

#### RV-037 (Important, reliability, api-contract, effort S): Memory search workspaceId never matches the id GET /api/workspaces returns and answers 'complete, zero results'

`src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs:1390` — related: `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CampaignBackedWorkspaceRegistry.cs`, `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs`

**Claim.** SearchWorkspaceAsync resolves the filter with `SELECT Path, Name FROM Campaigns WHERE Id = @id OR NameLower = lower(@id)` binding the raw string (1389-1392), and returns with no results when nothing resolves (1406-1409). Campaigns.Id is stored uppercase-dashed (GrimoireEntitySql.Format), the workspace registry publishes campaign.Id.ToString("N") (CampaignBackedWorkspaceRegistry.cs:274-276) and CampaignDto serializes lowercase D, so neither published id form ever matches; the scope reports count 0 and hasMore:false.

**Failure scenario.** `arcanum memory search needle --scope workspace --workspace <id from arcanum workspace list>` (Command Reference: 'Optional workspace ID') returns no hits while the unfiltered search returns them; the operator concludes the workspace has no match.

**Evidence (probe).** Probe P8 (one seeded workspace_file_chunks row under the Campaign path; /api/workspaces returned id '69773cce6fef404a8c34524e9d6a3478'): none -> 1 result; N form -> 0, hasMore:false; lowercase D -> 0; uppercase D -> 1; name -> 1. No test in the project passes workspaceId.

**Suggested fix.** Guid.TryParse the workspaceId (any format) and bind GrimoireEntitySql.Format(guid), else treat it as a name; when it resolves to nothing return 404 Workspace.NotFound instead of an empty complete page; add a test.

#### RV-038 (Important, usability, api-contract, effort M): GET codex reports an over-cap, non-UTF-8 or aliased CODEX.md as exists:true with empty content

`src/RetroDownfall.Arcanum.Api/Tower/CodexEndpoints.cs:279` — related: `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodexReader.cs`, `src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/CodexViewModel.cs`

**Claim.** ReadCodexDtoAsync builds CodexContentDto(fullPath, content ?? string.Empty, File.Exists(fullPath)). CodexReader.ReadCodexFileAsync returns null for every non-Success SecureFileReader status (over the 262,144-byte default cap, invalid UTF-8, aliased or multi-link file), so the endpoint cannot tell an unreadable file from an empty one and reports it as present and empty with HTTP 200. Prompt assembly (CodexReader.ReadCodexAsync) drops the same file with no log.

**Failure scenario.** A CODEX.md edited outside Arcanum (for example saved as Windows ANSI with an accented character, or grown past 256 KiB) loads in The Forge as 'Loaded.' with an empty editor; the operator types new content and Save atomically replaces their file. arcanum campaign codex get prints nothing, and the model never sees the codex because inference silently ignores the file. API.md:183 documents only exists:false for an absent file.

**Evidence (probe).** CodexEndpoints.cs:275-279. Probe Probe_Codex_Oversize_And_InvalidUtf8: 300,000-byte file => 200 {"content":"","exists":true}; Latin-1 bytes 'caf\xE9 notes' => 200 {"content":"","exists":true}; 'small ok' => content returned. CodexReader.TryReadAsync: 'if (readResult.Status is not SecureFileReadStatus.Success) return null;'. TheForge.Ux CodexViewModel.cs:114 'StatusText = Exists ? "Loaded." : ...' and ApplyDto (~291-296) assigns Content = dto.Content ?? string.Empty. PUT already refuses content over the cap (CodexEndpoints.cs:339), so only external edits reach this state.

**Suggested fix.** Return the reader's status: answer a typed error (new Codex code, 400/422) or add readable:false plus a reason (TooLarge with the cap, NotUtf8, Aliased) so clients can refuse to overwrite; log once at Information when prompt assembly drops an unreadable codex. Update API.md and the Forge editor.

#### RV-039 (Important, usability, api-contract, effort S): memory search --workspace never matches the workspace ID that workspace list prints; an unresolved ID returns a silent empty result

`src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs:1390` — related: `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CampaignBackedWorkspaceRegistry.cs`, `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs`, `docs/Arcanum.Command.Reference.md`, `docs/Arcanum.API.md`

**Claim.** SearchWorkspaceAsync resolves workspaceId with 'SELECT Path, Name FROM Campaigns WHERE Id = @id OR NameLower = lower(@id)' bound to the raw trimmed string. Campaigns.Id is TEXT with BINARY collation and holds the uppercase dashed Guid, while the ID an operator holds (GET /api/workspaces, arcanum workspace list) is Guid.ToString('N') (32 lowercase hex), and the Campaign API id is lowercase dashed. Neither matches. Lines 1406-1409 then return when an id was supplied but nothing resolved, so the scope contributes nothing and the response is HTTP 200 with no error.

**Failure scenario.** arcanum workspace list, copy the id, then 'arcanum memory search needle --scope workspace --workspace <id>' prints no hits and exits 0, so the operator concludes the index holds no match. The Command Reference documents --workspace as 'Optional workspace ID'. Same silent-empty outcome for any typo'd id, while memory search with an unknown sessionId correctly answers 404.

**Evidence (probe).** MemoryEndpoints.cs:1390 'SELECT Path, Name FROM Campaigns WHERE Id = @id OR NameLower = lower(@id)' with AddParameter(resolve, "@id", workspaceId.Trim()); :1406-1409 'if (!IsNullOrWhiteSpace(workspaceId) && workspacePath is null) return;'. CampaignBackedWorkspaceRegistry.ToWorkspaceInfo returns campaign.Id.ToString("N"). Probe Probe_MemorySearch_WorkspaceIdForms (one Campaign, one matching workspace_file_chunks row, scope=workspace): workspaceId='probe-ws' (name) => 200 results=1; '0a27a71fe0f24783994e30adaab557e2' (id exactly as GET /api/workspaces returned it) => 200 results=0; lowercase dashed => results=0; UPPERCASE dashed => results=1; 'no-such-workspace' => 200 results=0. rg over tests finds no memory-search test that names workspaceId, so every existing test passes. Contrast SessionEndpoints.cs:733 (attachment reference resolves the same id through IWorkspaceRegistry.GetAsync, which uses Guid.TryParse).

**Suggested fix.** Guid.TryParse the supplied value and compare against the canonical stored form (or resolve through IWorkspaceRegistry.GetAsync like the attachment-reference route), keep the name fallback, and return Workspace.NotFound (404) when an id was given and nothing resolved. Add tests for N form, D form, name and unknown id.

#### RV-040 (Important, usability, error-message, effort M): Provider test tells the operator to 'See server logs for detail' but the detail is logged at Debug, which the host never keeps; the Forge then discards even the generic message

`src/RetroDownfall.Arcanum.Api/Tower/ProviderTestEndpoints.cs:72` — related: `src/RetroDownfall.Arcanum.Infrastructure/Logging/LoggingBootstrapper.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Logging/AuditLogPageReader.cs`, `src/RetroDownfall.TheForge.Ux/ViewModels/Arsenal/ModelsProvidersDataSource.cs`, `src/RetroDownfall.TheForge.Ux/ViewModels/Arsenal/ModelsProvidersViewModel.cs`

**Claim.** The URL-guard refusal (line 72-76) and the connection/DNS/TLS failure (line 172) are logged with LogDebug, while the client text says 'See server logs for detail.' (lines 81, 178). The Serilog pipeline floor is a hard MinimumLevel.Information(), so a Debug event reaches neither the rolling file, nor the ring buffer behind GET /api/logs, nor arcanum logs: the detail exists nowhere. A malformed URL is coded Security.BlockedOutboundUrl, and a 200 that is not a model list reports isReachable:true with zero models.

**Failure scenario.** An operator tests 'localhost:11434/v1' (no scheme) or has Ollama stopped. The API answers 400 or 'unreachable ... See server logs'; The Forge (the only client) maps any non-success envelope to null and shows 'Test failed.' with no reason; arcanum logs shows nothing. A stopped-vs-misconfigured-vs-blocked endpoint cannot be told apart from any surface. A web server returning an HTML 200 is shown 'Reachable in 8 ms - 0 model(s)'.

**Evidence (probe).** LoggingBootstrapper.cs:45 '.MinimumLevel.Information()'; AuditLogPageReader.cs:246-250 states a Debug event 'reaches neither the rolling file, nor the ring buffer behind GET /api/logs, nor arcanum logs'. Probe Probe_ProviderTest_Messages_And_Logs: closed loopback port => 200 isReachable=false 'The provider probe failed (connection, DNS, or TLS error). See server logs for detail.'; 'banana', 'file:///etc/passwd', 'http://169.254.169.254/v1' => 400 Security.BlockedOutboundUrl 'The endpoint URL failed validation (invalid, unresolvable, or blocked). See server logs for detail.'; HTML-200 listener => isReachable:true, modelsFound:[]; GET /api/logs?limit=500 afterwards holds this host's Information entries (buffer is live) and no ProviderTest/probe line. TheForge.Ux ModelsProvidersDataSource.cs:43 'response is { IsSuccess: true, Data: { } result } ? result : null' and ModelsProvidersViewModel else-branch 'Test failed.'.

**Suggested fix.** Log both outcomes at Warning (endpoint host only, no userinfo or query); return a sanitized reason class in Error.Message/details (InvalidUrl, Blocked, DnsFailed, ConnectionRefused, Tls, Unauthorized -> check apiKey, NotAModelList) that leaks no resolved IP; code a malformed URL as a validation error rather than BlockedOutboundUrl; make The Forge display Error.Message for a failed envelope.

### U06: Arcanum.Cli — CommandCenter (1)

#### RV-041 (Important, reliability, ui-responsiveness, effort S): Ctrl+Q during an ask_human prompt replaces the hard modal, wipes the answer, Esc cannot cancel, and the 'Enter submit' footer quits Command Center

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:1584` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterWindow.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterKeymap.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterGuidance.cs`

**Claim.** Ctrl+Q is a global chord (app.Keyboard.KeyDown, lines 598-637) that reaches RequestQuit with no BlockAuxiliaryWhileHardModal check. While generating (always true during an ask_human prompt) it opens QuitConfirm. CommandCenterWindow.ShowOverlay (lines 936-944) hides and clears OverlayAnswer. Esc, which the overlay names as 'cancel', is consumed by app.Keyboard as CloseOverlayOrFocusComposer. Lines 1012-1020 check humanPromptCoordinator.IsActive first, set the footer to HumanPromptFooter ('Enter submit · …') and leave QuitConfirm up. Enter then maps to ConfirmPending and quits.

**Failure scenario.** The model asks 'Which port…?'. The operator types '8080', presses Ctrl+Q, then Esc. The quit dialog stays and the footer says 'Enter submit'. They press Enter: Command Center exits, abandoning the turn and the prompt and stopping the auto-launched host. The only other way out is Ctrl+C, which also cancels the turn.

**Evidence (probe).** Probe 1 (ReviewU06ReliabilityProbeTests.Probe1, real CommandCenterHost via reflection, real Terminal.Gui app on the ANSI driver). Output: 'after Ctrl+Q: overlay=QuitConfirm answer='' answerVisible=False promptActive=True'; 'after Esc: overlay=QuitConfirm footer='Enter submit · Ctrl+J newline · Ctrl+C cancel turn''; 'after Enter: requestExit=True'. All 783 existing CommandCenter tests pass, so none catches this.

**Suggested fix.** Gate Quit like the other auxiliary overlays while a hard modal is active (footer: answer or Ctrl+C). If quitting must stay possible, test Overlay is QuitConfirm/DiscardConfirm before the HITL check in CloseOverlayOrFocusComposer, and on cancel re-show the pending prompt with its preserved answer. Never clear OverlayAnswer in ShowOverlay while a prompt is pending. Add a host test.

#### RV-042 (Important, reliability, data-integrity, effort M): Older transcript paging is offset-based against a total captured at resume; live turns shift pages and the oldest entries become unreachable

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterState.cs:296` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs`, `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs`

**Claim.** HasOlderTranscriptEntries uses SessionEntryCount, set only by ApplySessionMeta at resume or fork. Older pages are fetched by newest-first offset (TranscriptEntryOffset + LoadedTranscriptEntries.Count). Live turns append entries on the host, but neither the total nor the offset is ever adjusted. Each turn's K new entries shift every older page by K. The next Ctrl+PgUp re-shows the page already on screen, and the stale total reports no older page while K of the oldest entries were never shown. The host already offers a keyset cursor (beforeCreatedAt/beforeId).

**Failure scenario.** Resume a 450-entry session (view shows entries 449..250) and run 100 turns (200 entries). Ctrl+PgUp returns 449..250 again, the next returns 249..50, then 'No older transcript page is available'. Entries 0..49 cannot be reached in Command Center.

**Evidence (probe).** Probe 10 (real SessionWorkspaceService against a paging fake host): 'first page 250..449; offset=200 range=250..449 overlapWithFirst=200; offset=400 range=50..249 overlapWithFirst=0; unreachable original entries: 50 (0..49)'. Code: SessionWorkspaceService.cs:356-371 and 732-734; grep shows SessionEntryCount is assigned only in ApplySessionMeta and ClearSessionBinding.

**Suggested fix.** Page older entries with the keyset cursor (beforeCreatedAt + beforeId of the oldest loaded entry) instead of offsets. Decide 'has older' from a limit+1 read rather than a total captured at resume. Page newer by re-resuming or by a forward cursor.

#### RV-043 (Important, reliability, ui-responsiveness, effort M): Long ask_human question is truncated to four wrapped rows (tall ones clipped) and is shown nowhere else

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterWindow.cs:1077` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/OverlayLayout.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/IncantationFormatter.cs`

**Claim.** ShowHumanPromptOverlay wraps the question with OverlayLayout.WrapLines, which cuts every logical line to MaxWrappedPreviewRows=4 rows ending in '…' (a cap meant for argument previews). The body is a fixed-height Label clamped by OverlayLayout.MeasureHeight, so a question with many lines is clipped with no scrolling. The question is not written to the transcript. The Incantations row shows only 'ask_human [pending]', because 'question' is not a summary key. The operator must answer a question they cannot fully read.

**Failure scenario.** The model asks one 578-character paragraph ending 'Which option should I take: A, B or C?'. At 80x24 and at 120x40 the overlay shows 4 rows plus '…', and the actual question is never displayed.

**Evidence (probe).** Probe 9 (Theory 80x24 and 120x40): 'question 578 chars; shown rows: 10; ends with ellipsis row: True'. The rendered rows do not contain 'Which option should I take'. Code: OverlayLayout.cs:51-88, CommandCenterWindow.cs:1077-1089 and 2020-2040, IncantationFormatter.cs:385-421.

**Suggested fix.** Render the question in a scrollable ListView, or wrap it without the preview cap and let the overlay grow to the body height and scroll. Also append the full question to the transcript as a Status entry when the prompt opens.

### U07: Arcanum.Cli — CommandCenter (2)

#### RV-044 (Important, reliability, data-integrity, effort S): Staged attachment reference outlives its session and then blocks every later turn

`src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs:593` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterState.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterChatRunner.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/SessionAttachmentRequestValidator.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/SessionAttachmentTurnService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs`

**Claim.** `/attachments add` stages a host attachment id valid only for the current session (ShellCommandDispatcher.cs:450). No session change clears staged references: ClearSessionBinding, StartNewSession (593), ApplySessionMeta/ApplyTranscriptPage (701, used by resume/fork/branch), `/clear` (ShellCommandDispatcher.cs:83) and archive-current all leave CommandCenterState._stagedAttachmentReferences intact. The next turn sends the stale id, the host refuses it, and because the chat runner clears the staged snapshot only after a successful Result (CommandCenterChatRunner.cs:435-437) and no command unstages, the same id is resent on every later message.

**Failure scenario.** In session A run `/attachments add notes`, then `/clear` (or `/resume B`, `/fork`, Ctrl+N). Every subsequent message is refused by the host ("AttachmentReferences require a SessionId." for a new session; "Attachment ... does not belong to session ..." / "is not a bound attachment for this session" otherwise) until Command Center is restarted. A fork copies attachments under new ids, so the fork of A also refuses A's id.

**Evidence (probe).** Probe P4 (real dispatcher + workspace + ArcanumApiClient over a fake host, real host SessionAttachmentRequestValidator): `after add: staged=66666666-...`; `after /clear: SessionId=null staged=66666666-...`; `host validator verdict for next turn: AttachmentReferences require a SessionId.`; `after /resume 1111...: SessionId=1111... staged=66666666-...` (validator proceeds to ValidateReferencesAsync, which throws 'does not belong to session' at SessionAttachmentStore.cs:1543-1546). Grep: the only mutations are StageAttachmentReference, SnapshotStaged, ClearStaged (CommandCenterState.cs:116-158).

**Suggested fix.** Add CommandCenterState.ClearStagedReferences() and call it wherever the bound session changes (ClearSessionBinding, ApplySessionMeta when the id differs), reporting how many were dropped; optionally clear the snapshot when the host rejects a reference so one bad id cannot wedge the composer. Add tests: stage then /clear and stage then /resume other both leave no staged references.

#### RV-045 (Important, reliability, race, effort S): /fork, /branch parent|child and /session archive <active> switch sessions while a turn is generating

`src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:236` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionLogBuffer.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterChatRunner.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterSessionMutationGuard.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs`

**Claim.** The generating guard (TryDenySessionMutationWhileGenerating, 'Cancel it with Ctrl+C before switching sessions') is applied to /resume (221) and /clear (73) but not to SessionFork (236), BranchParent (313), BranchChild (323) or archive of the active session (1282). Slash commands run under the host's action gate, which a chat turn does not hold (HandleSubmitAsync dispatches slash input while TryBeginTurn is held), so these run mid-turn, rebind the session and replace the log under the running turn. The streaming answer goes into an entry no longer in the log, the turn's tool calls land in the new session's Incantations, and if no reasoning frame has arrived yet SessionLogBuffer.InsertBefore (146-150) throws ArgumentException, which the chat runner (CommandCenterChatRunner.cs:164) treats as a turn failure and abandons the stream.

**Failure scenario.** Send a prompt to a reasoning model; while it is still 'Thinking' type `/branch parent` or `/fork`. The view switches; the first reasoning event throws, the turn ends with 'Error: The anchor entry does not belong to this log. (Parameter 'before')' and the answer is lost (host run Abandoned). `/session archive <active-id>` additionally archives the session the turn is writing to.

**Evidence (probe).** Probe P1 with state.TryBeginTurn() held and a streaming assistant entry: `/resume ... | SessionId=2222... anchorInLog=True footer='A turn is still generating...'`; `/clear` likewise denied; `/branch parent | SessionId=1111... anchorInLog=False footer='' ... firstReasoningInsert=> ArgumentException: The anchor entry does not belong to this log. (Parameter 'before')`; `/fork | SessionId=3333... forkPosts=1 ... ArgumentException`; `/session archive 2222... | SessionId=null archiveCalls=1 ... ArgumentException`. Existing tests (CommandCenterTests.cs:635-661) cover only /resume and /clear.

**Suggested fix.** Deny every session-mutating kind while generating in one place before the switch (SessionFork, BranchParent, BranchChild, and SessionArchive when the target is state.SessionId). Make InsertBefore append instead of throw when the anchor is gone. Extend the generating-deny tests to these commands.

#### RV-046 (Important, reliability, data-integrity, effort M): /fork alternative duplicates the user prompt in the new branch

`src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:268` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs`, `docs/Arcanum.DESIGN.md`, `docs/Arcanum.Command.Reference.md`

**Claim.** The cutoff is the preceding user prompt's entry id, and the host's upToEntryId is inclusive (DESIGN 11.16.1; SessionRepository.cs:520-533 uses cutoffEntry.Sequence as the maximum copied sequence), so the branch already ends with that prompt. The dispatcher then queues the same text as PendingAlternativePrompt (269) and the host submits it as a new turn (CommandCenterHost.cs:2276-2280), appending a second identical user entry before the regenerated answer. The model receives the question twice and the persisted branch keeps the duplicate; attachments bound to the original prompt are copied but the resent prompt carries none.

**Failure scenario.** Select an assistant answer and run `/fork alternative`: the branch transcript reads 'Dungeon Master: Q' / 'Dungeon Master: Q' / 'Mage: <new answer>', and every later turn in the branch carries the duplicate.

**Evidence (probe).** Probe P2: `fork request UpToEntryId=4444... (prompt entry=4444..., answer entry=5555...)`; branch reload last entry `user/What is 2+2?`; `PendingAlternativePrompt='What is 2+2?'`. No test exercises /fork alternative (grep: only dispatcher, parser, state and host reference it).

**Suggested fix.** Cut off at the entry before the prompt (promptIndex - 1, loading the previous page when the prompt is the first loaded entry; an empty/exclusive-cutoff fork contract when the prompt is the first entry), or keep the inclusive cutoff and have the host regenerate from the trailing prompt rather than resending it. Add a dispatcher test for the cutoff and a host-level test asserting a single user entry before the regenerated answer.

#### RV-047 (Important, reliability, api-contract, effort S): /fork confirm forks the whole session, discarding the cutoff or alternative that required confirmation

`src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandParser.cs:243` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs`

**Claim.** `/fork confirm` parses with no argument and nothing remembers the pending request, so the dispatcher (ShellCommandDispatcher.cs:290-299) always sends ForkSessionRequest(UpToEntryId: null). When `/fork at <id>`, `/fork at` (selected) or `/fork alternative` trips the 10-attachment/10 MiB confirmation, confirming creates a complete branch: no cutoff, every attachment copied, no regenerate. The threshold (SessionWorkspaceService.cs:486-494) is measured over all source attachments, so a cutoff fork that would copy few or none is still made to confirm.

**Failure scenario.** Session with 10 attachments; select an early entry and run `/fork at`; the CLI asks for `/fork confirm`; confirming yields a full copy of the session and all attachment bytes instead of the branch at the selected entry. `/fork alternative` + confirm yields a full copy and no regeneration.

**Evidence (probe).** Probe P3, both '/fork at 4444...' and '/fork alternative': first reply 'This fork will copy 10 attachment(s) (0.0 MiB). Run `/fork confirm` to continue.' (0 POSTs), then `/fork confirm -> POSTs=1 UpToEntryId=null (whole session) PendingAlternativePrompt=null`.

**Suggested fix.** Store the pending fork (source id, cutoff, alternative prompt) in state when confirmation is required, replay it on `/fork confirm`, and drop it on any session change; measure the threshold over the attachments the cutoff will actually copy (entry-bound rows at or before the cutoff; entry-less rows only for a full fork, as SessionRepository does).

### U23: Arcanum.Infrastructure — Covenant (1)

#### RV-048 (Important, performance, starvation, effort S): A single outbox sequence larger than 512 rows is never applied; one cleanup batch can create one, wedging the search accelerator permanently and silently

`src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchOutboxWorker.cs:150` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceHostedService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceCoordinators.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCleanupWorker.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchHealthRule.cs`, `src/RetroDownfall.Arcanum.Core/Covenant/CovenantScopeCapacity.cs`

**Claim.** SynchronizeAsync reads maxRows+1 pending rows and ChooseContiguousTarget (216-238) breaks at the first sequence group that does not fit in maxRows. If the FIRST pending sequence alone holds more than maxRows (512) rows, target stays equal to applied and lines 152-155 return a success outcome with no writes and RebuildRequired=true. Every later pass reads the same rows and returns the same answer, and because the stuck sequence is the oldest no later sequence is reachable either. CovenantCleanupWorker gives every delta of a 64-event batch the same sequence (lines 118-124, 161-164) and a Campaign emits one outbox row per head, so one cleanup batch over a few well-populated Campaigns creates such a sequence. The driver (CovenantMaintenanceHostedService.cs:143-153) ignores RebuildRequired and logs only '0 projection(s)' at Debug; CovenantSearchHealthRule then reports Synchronizing / WaitForSynchronization forever. No remedy exists in this release (ICovenantMaintenanceService is registered nowhere; nothing starts an index rebuild).

**Failure scenario.** An operator with Campaigns holding about 100 heads each deletes six of them within one five-minute window (or the host was down and the deletions queue up). The 64-event cleanup batch removes 600 heads and appends 600 outbox rows under one sequence. Every later outbox pass applies nothing (measured: 5 consecutive passes, appliedSeq stays 600 while canonical is 601, consumed=0, pendingRows=600), a later ordinary one-row mutation is stuck behind it too (pendingRows=601), search stays on the canonical fallback (2,048-candidate cap, Truncated beyond it), status says Synchronizing indefinitely, and the pending outbox grows toward MaxPendingSearchOutboxRows (65,536) at which canonical writes are refused. Only a Covenant reset recovers it.

**Evidence (probe).** Code: CovenantSearchOutboxWorker.cs:113 'ReadPendingAsync(transaction, applied.Value, maxRows + 1, ...)', :150 'long target = ChooseContiguousTarget(pending, applied.Value, maxRows);', :152-155 'if (target <= applied.Value) { return new CovenantOutboxSyncOutcome(applied.Value, 0, 0, 0, RebuildRequired: pending.Length > maxRows); }', :227 'if (checked(counted + size) > maxRows) { break; }'. Probe P1 (scratch test on the real CovenantCleanupWorker and CovenantSearchOutboxWorker, scratch SQLCipher Grimoire): 6 Campaigns x 100 heads, projection caught up (applied=canonical=600), delete all six, one cleanup batch -> 'P1 cleanup: campaigns=6 headsRemoved=600', 'P1 outbox sequence 601 holds 600 rows (DefaultBatchRows=512)', then 'P1 sync pass 1..5: appliedSeq=600 consumed=0 written=0 removed=0 rebuildRequired=True | db applied=600 canonical=601 pendingRows=600' and 'after one later ordinary mutation: appliedSeq=600 consumed=0 pendingRows=601 canonical=602 applied=600'. Existing tests: A_bounded_batch_stops_on_a_sequence_boundary_and_resumes seeds one head per sequence so no test exceeds the bound; all 26 tests in CovenantSearchOutboxWorkerTests, CovenantMaintenanceCoordinatorTests and CovenantOwnerCleanupTests pass on the unmodified tree with the wedge present. Fix verified in a scratch worktree (14-line change: when target<=applied, re-read with limit MaxPendingSearchOutboxRows+1 and set target=pending[0].SearchSequence): P1 then gives 'sync pass 1: appliedSeq=601 consumed=600 ... pendingRows=0' and 'after one later ordinary mutation: appliedSeq=602 consumed=1 pendingRows=0 canonical=602 applied=602', and the same 26 tests still pass. Output files: reports/C/U23-probe-output-baseline.txt and U23-probe-output-with-fix.txt.

**Suggested fix.** A sequence is the unit of atomicity, so when the first pending sequence alone exceeds the bound apply it whole (bounded by the 65,536-row pending ceiling): in SynchronizeAsync, when ChooseContiguousTarget returns applied, re-read pending with limit CovenantLimits.MaxPendingSearchOutboxRows + 1 and set target to pending[0].SearchSequence. Have CovenantMaintenanceHostedService log a Warning when an outbox outcome has RebuildRequired and ProjectionsWritten==0. Add a worker test with one sequence larger than the bound and a cleanup-over-many-Campaigns test.

#### RV-049 (Important, reliability, cancellation, effort S): Every owner-cleanup pass revokes all live memory-delegation grants, even when there is nothing to clean

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceCoordinators.cs:44` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceHostedService.cs`, `src/RetroDownfall.Arcanum.Core/Intelligence/MemoryDelegation/MemoryDelegationCoordinator.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCleanupWorker.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantOwnerDeletionReader.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.TurnCommit.cs`

**Claim.** CovenantOwnerCleanupCoordinator.RunBatchAsync takes the memory-delegation mutation barrier (memoryDelegation.BeginOptionalAsync) as its first statement, unconditionally. That happens before the gate lease, before the owner-deletion journal is read, and whether or not any event is pending. MemoryDelegationCoordinator.BeginMutationAsync calls RevokeAll(), which marks every live grant terminal, cancels the root and child Revocation tokens, and then waits for admitted effects to drain. CovenantMaintenanceHostedService runs this pass every 5 minutes while Covenant is enabled and healthy, so an idle installation revokes every live delegation grant and cancels in-flight delegated child effects every 5 minutes. A gate refusal revokes too, because the barrier is already held by then.

**Failure scenario.** Covenant is on, and an operator calls POST /api/intelligence/memory-delegation (it requires Covenant read authority). Grants live at most 5 minutes and the sweep fires every 5 minutes, so a grant that is live at a sweep boundary is lost even though no Campaign or Session was deleted. An unconsumed grant is refused (MemoryDelegation.StaleSource). A consumed child has its in-flight provider effect cancelled and its result withheld, while its provider usage is still billed (WizardIntelligenceProviderTests.MemoryDelegation pins Assert.Null(returned) for a mutation that lands mid-run). Roughly child-run-time/300s of delegations fail this way. The pass also waits behind the effect's teardown while it holds the Grimoire work lease and the delegation admission semaphore.

**Evidence (probe).** CovenantMaintenanceCoordinators.cs:44 `await using MemoryDelegationMutation? memoryMutation = await memoryDelegation.BeginOptionalAsync(cancellationToken)` precedes `gate.AcquireCleanupAsync` (46) and the worker's journal read. MemoryDelegationCoordinator.cs:332-350: BeginMutationAsync -> `Task.WhenAll(RevokeAll())`. RevokeAll (375-430) sets Terminal and cancels every registration; child Revocation is linked to root Revocation (MemoryDelegationCapabilities.cs:21,45). The hosted service drives this on every pass (CovenantMaintenanceHostedService.cs:121-137, Interval 5 min at line 60). The other call sites use the conditional shape: GrimoireRepository.TurnCommit.cs:39 skips the barrier when `request.Mutations.IsEmpty`. Probes, in a scratch worktree at ac0d2d8e, both passed, which confirms the defect. (1) Probe_an_empty_owner_cleanup_pass_revokes_a_live_memory_delegation_grant: on an empty journal the outcome is CampaignsCleaned=0, SessionsCleaned=0, HeadsRemoved=0, yet root.Revocation.IsCancellationRequested == true and a later ConsumeAsync fails. (2) Probe_an_empty_owner_cleanup_pass_cancels_an_in_flight_delegated_effect_and_waits_for_it: the admitted effect's Revocation is cancelled, and the pass task is still incomplete after 500 ms until the effect is disposed.

**Suggested fix.** Before taking the barrier, read the cleanup cursor and probe for at least one pending owner-deletion event (or FullSweepRequired) using CovenantOwnerDeletionReader on the scope connection. When nothing is pending, return the zero outcome without the barrier, republishing availability if that is still wanted. Keep barrier -> cleanup lease -> BEGIN IMMEDIATE for the non-empty case. The worker re-reads the cursor inside its transaction, so an event that lands after the probe is only deferred to the next pass. Add probe (1) as a regression test asserting that an empty pass leaves a live grant unrevoked.

#### RV-050 (Important, usability, diagnostics, effort M): Covenant refusals and the per-turn absence reason are logged at Debug, which the host pipeline never emits; host.minLogLevelInBuffer cannot lower the floor

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceHostedService.cs:216` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantContextProvider.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/CovenantDispatchGate.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Logging/LoggingBootstrapper.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Logging/SerilogLogRingBufferSink.cs`, `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantContextProvider.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.ContextPreview.cs`, `docs/Compendium.README.md`

**Claim.** RunSweepAsync records every refused Covenant sweep (owner cleanup, search outbox, receipt compaction) with LogDebug (lines 216 and 225). Only a thrown exception reaches LogError (229). The typed CovenantTurnAbsence that CovenantContextProvider produces (lines 39-56, 69-71) is documented as what an operator reads in 'context inspect' and as replacing 'a log line'. Its only consumer, CovenantDispatchGate.Record, writes it with LogDebug and calls that 'answerable from a log'. The Serilog pipeline floor is a hard-coded MinimumLevel.Information() (LoggingBootstrapper.cs:45). host.minLogLevelInBuffer (Compendium label 'Debug information level', docs list trace and debug) only filters inside SerilogLogRingBufferSink.Emit after that floor, so it can raise the floor but never lower it. 'context inspect' gives a zero-token Covenant lane the generic reason 'No effective content was available for this source.' whatever the cause.

**Failure scenario.** A scope left closed after an uncertain erase commit, or a canonical-tier fault that makes the cleanup worker return Covenant.IntegrityFailure or StaleSnapshot, makes all three sweeps refuse every five minutes. Deleted Campaigns' Covenant rows are not removed and the outbox is not drained. Nothing in the rolling log, 'arcanum logs', 'memory status' or 'doctor' says so. The operator sets host.minLogLevelInBuffer to debug as the Compendium label suggests and still sees nothing. 'Why was my standing preference not injected on that turn' (FeatureDisabled, CapabilityUnavailable, NoCampaign, Empty) cannot be answered from any surface.

**Evidence (probe).** CovenantMaintenanceHostedService.cs:214-225 'logger.LogDebug("Covenant {Sweep} sweep did not run this pass: {ErrorCode} {ErrorMessage}" ...)' and ':225 logger.LogDebug("Covenant {Sweep} sweep applied {Applied}."'. LoggingBootstrapper.cs:45 '.MinimumLevel.Information()'. AuditLogPageReader.cs:246 states 'a Debug event reaches neither the rolling file, nor the ring buffer behind GET /api/logs, nor arcanum logs'. SerilogLogRingBufferSink.cs:25-31 'if (arcanumLevel < minLevel) return;'. CovenantDispatchGate.cs:378 logger.LogDebug("Covenant turn {LogicalTurnId} resolved to {CovenantAbsence}..."). 'rg Absence' finds no other consumer. WizardIntelligenceProvider.ContextPreview.cs:1006 default reason. DESIGN.md:2009 and :1941 claim typed reasons and independent reporting. Probe P3 (U23UsabilityProbeTests, scratch worktree, AddArcanumSerilog with Host.MinLogLevelInBuffer=Debug): 'P3 IsEnabled(Debug)=False IsEnabled(Information)=True IsEnabled(Trace)=False' and 'P3 ring buffer entries=Information:P3 information marker'. No test references MinLogLevelInBuffer, and the existing hosted-service tests never reach the outcome.IsFailure branch.

**Suggested fix.** Log refused sweeps at Warning on first occurrence and whenever the error code changes (sweep name plus code, no content), keep Debug for the all-clear. Carry CovenantTurnAbsence into the context-inspect DTO, or log it at Information for CapabilityUnavailable. Either drive the Serilog floor from a LoggingLevelSwitch bound to host.minLogLevelInBuffer, or remove trace and debug from the documented values and the Compendium label. Log the feature flip in CovenantFeatureConfigurationPublisher. Add a test that a refused sweep emits a log at or above Information.

#### RV-051 (Important, usability, api-contract, effort M): List and search report constant shadow/materialization (NotShadowed, Eligible) whenever an evaluation Campaign is named, including for Retired and shadowed heads

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantManagementService.cs:726` — related: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantReadContracts.cs`, `src/RetroDownfall.Arcanum.Cli/Commands/Tower/CovenantCommands.cs`, `docs/Arcanum.API.md`

**Claim.** Head() returns CovenantEffectiveShadowState.NotShadowed and, for every non-Proposed head, CovenantEffectiveMaterialization.Eligible whenever effectiveForCampaignId is non-null. It never reads item.Lifecycle, never compares heads, and the list query does not receive the evaluation Campaign (CovenantListQuery built at lines 71-77 without it). ShadowedByCampaignConfirmed, Shadowed and Ineligible are never produced. The enum remarks and API.md:1015 define these as effective for the evaluated Campaign, 'deliberately separate from lifecycle', and warn that conflating them 'would tell an operator their preference is active when nothing has ever rendered it'.

**Failure scenario.** 'arcanum memory covenant list --campaign <C> --lifecycle Retired --json' (or --lifecycle Any) prints every retired entry as "materialization": "Eligible", so a script or reader concludes retired preferences still reach prompts. Over HTTP, a client sending scope Global or AllScopes with effectiveForCampaignId gets NotShadowed for a Global head that a same-key Campaign Confirmed head shadows. Search has the same behaviour.

**Evidence (probe).** CovenantManagementService.cs:726-733: 'effectiveForCampaignId is null ? NotEvaluated : NotShadowed' and '... : item.Lane is CovenantLane.Proposed ? ReviewOnly : Eligible'. CovenantCommands.cs:382,475 pass 'EffectiveForCampaignId: campaignId'; CovenantCommands.cs:804-805 copies Shadow and Materialization into CovenantEntryPayload. Probe P1 (list with EffectiveForCampaignId=C, lifecycle Any): 'Global//tone lifecycle=Set shadow=NotShadowed materialization=Eligible', 'Campaign/C/tone ... NotShadowed Eligible', 'Campaign/C/old.rule lifecycle=Retired shadow=NotShadowed materialization=Eligible'. 'rg NotShadowed tests' returns nothing; only NotEvaluated is asserted (CovenantCommandTests.cs:1962, MemoryErasureCommandTests.cs:1256), so every existing test passes with the constants.

**Suggested fix.** Compute both fields from data in the page's snapshot (same-key Campaign Confirmed head, lifecycle, lane, curation mask), pass the evaluation Campaign into the store query where needed, or report NotEvaluated for whatever is not computed and at least Ineligible for Retired. Add tests that a Retired head and a shadowed Global head do not report Eligible/NotShadowed.

### U24: Arcanum.Infrastructure — Covenant (2)

#### RV-052 (Important, usability, error-message, effort M): Covenant review refusals name neither the failing decision nor the next step, any apply in a lane silently stales every other token, and the 5-minute token life is never disclosed

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:56` — related: `src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryReviewTokenCodec.cs`, `src/RetroDownfall.Arcanum.Core/Memory/MemoryReviewTokenFacts.cs`, `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.Review.cs`, `docs/Arcanum.API.md`, `docs/Arcanum.Command.Reference.md`

**Claim.** The operator-facing refusals of memory covenant review list/prepare/apply are three shared static Error instances (InvalidToken 'invalid, expired, stale, or bound to another queue'; StaleObservation 'A reviewed Covenant version or its review position changed before this operation could commit'; UnseenObservation) plus the codec's own text ('invalid, expired, or has the wrong purpose'). None tells the operator to run review list again. DecodeObservations (888-924) and ValidateObservedTarget (926-965) return the shared instance without the decision ordinal or event sequence, and the compile catch (958-961, 1520-1523) forwards the BCL message, so in a 1-50 decision batch the failing decision is never named. Lines 329-332 and 549-552 say 'Proposed content must be retired or confirmed separately', but the review action Confirm is acknowledgement only and never promotes; no verb is named. All memory-review tokens (observation, cursor, plan) live exactly 5 minutes (MemoryReviewLimits.TokenLifetime) and die on a host restart (per-process key); neither the review list output, the DTO, the Command Reference nor API 8.34 says so for observation or cursor tokens (only the plan token is called five-minute). Every successful apply also bumps the lane's marker Revision (AdvanceMarkerAsync 1891-1919), which every token binds (MatchesCommon 1042-1070); a request carries exactly one action, so a review that retires 3 and confirms 47 needs two applies and the second one's tokens from the same list all fail with StaleObservation. The sibling operator write path is actionable ('This token has expired. Prepare the operation again.') and the CLI erasure verbs add their own expiry hint (MemoryCommands.Erasure.cs:623); the review verbs add nothing.

**Failure scenario.** Operator runs review list, reads the page (that is the purpose of review), builds a decision file and runs review apply 8 minutes later: 400 MemoryReview.InvalidToken 'invalid, expired, or has the wrong purpose' with no hint that age is the cause or that re-listing is the fix; a Next cursor printed on page 1 dies the same way. Or: apply Retire for 3 items, then Confirm for the other 47 with tokens from the same list: 409 StaleObservation for items nobody touched. Or: one of 50 proposals was superseded by the agent meanwhile, or one Correct replacement carries a zero-width space: the error does not say which decision.

**Evidence (probe).** Executed probe (scratch worktree, CovenantMemoryReviewServiceTests harness): 'ONE OF THREE DECISIONS SUPERSEDED' -> FAILED code=MemoryReview.StaleObservation message="A reviewed Covenant version or its review position changed before this operation could commit." (no item named). 'OBSERVATION TOKENS 6 MINUTES OLD' -> FAILED code=MemoryReview.InvalidToken message="The memory-review token is invalid, expired, or has the wrong purpose." (same text for a garbage cursor). 'BULK CORRECT, DECISION #2 OF 3 HAS A ZERO-WIDTH SPACE' -> FAILED code=Validation.InvalidBody message="Covenant authored content contains a Unicode 17 Format scalar. (Parameter 'authoredContent')" (no decision number). 'TOKEN OF A DIFFERENT ITEM FROM THE SAME LIST, AFTER ONE APPLY IN THAT LANE' -> FAILED code=MemoryReview.StaleObservation. Code: Core/Memory/MemoryReviewTokenFacts.cs:15 TokenLifetime = 5 min; Infrastructure/Memory/MemoryReviewTokenCodec.cs:411-490 Issue/Read apply it to every purpose with ProcessKey per process, message at :48; Cli MemoryCommands.Review.cs:405-421 prints only 'observation: <token>'; Command Reference line 734 says only 'Refresh rather than reusing a token after the version changes' and the example at line 745 is "token from review list"; API 8.34 and DESIGN 21.14 state five minutes only for the plan; Core/Covenant/CovenantEnvelopeContracts.cs:488-490 is the actionable sibling text. Report: reports/C/U24-usability.md, probe output scratchpad/review/probe-u24-usability/review-probe-final.txt.

**Suggested fix.** Put 'decision #n (event e)' in every per-item refusal (DecodeObservations, ValidateObservedTarget, compile catch); end each token/stale refusal with the recovery ('Run arcanum memory covenant review list again'); say in StaleObservation that any apply in the lane invalidates other tokens, or stop binding MarkerRevision into an observation token whose event is still unreviewed and current; add ExpiresAtUtc to CovenantReviewPageDto and print it in the CLI footer; document the 5-minute lifetime of observation and cursor tokens in API 8.34 and the Command Reference; consider a longer lifetime for observation/cursor tokens than for the plan token; reword the 'confirmed separately' sentence to name memory covenant set.

#### RV-053 (Important, usability, api-contract, effort M): Operator set/correct preflight approves writes the commit will refuse for Section capacity or a retired head

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.cs:194` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMutationKernel.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantQuotaGuard.cs`, `src/RetroDownfall.Arcanum.Core/Covenant/CovenantSectionCapacity.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.CovenantTools.cs`, `docs/Arcanum.API.md`

**Claim.** PrepareAsync measures the mutation's effect and epochs only; CovenantMutationPreflightDto carries no capacity or lifecycle fact. The commit enforces both: CovenantMutationKernel.cs:145 -> CovenantQuotaGuard.cs:236,283 -> CovenantSectionCapacity.Refusal (4,096 rendered bytes and 64 entries per Section, 32 for Campaign Proposed, CovenantLimits.cs:9-19, while one authored entry may be 2,048 bytes), and ValidateLifecycle refuses a set of a retired head without Reactivate (CovenantMutationKernel.cs:525-529). API 8.28 'Both revisions on the preflight' states the principle (the refusal arriving after the operator had approved it), CovenantQuotaGuard.cs:279-283 says a second copy of the comparison is how a proposal is accepted at the tool and refused at the commit, and the agent path pre-checks exactly this (ArcanumInternalToolServer.CovenantTools.cs:436-500, RefuseWhatTheCommitWouldRefuseAsync); correct pre-checks its target (ResolveTargetAsync). set and retire do neither.

**Failure scenario.** Operator with a few 1 KB preferences runs memory covenant set, reads the preflight (byte cost, effect), confirms, and only then gets 409 Covenant.CapacityExceeded 'This mutation would render the GlobalConfirmed Section at 4196 bytes, past its 4096-byte bound.' with no hint to retire or shorten another entry. Or re-sets a retired key without --reactivate and is refused after confirming. The Section ceiling is low enough (4 KB) that heavy users will hit it routinely.

**Evidence (probe).** Executed probe (CovenantMutationServiceTests harness, real SQLCipher fixture): three 1,049-byte Global Confirmed entries committed; a fourth: 'prepare: success byteCost=1049 effect.localDecision=HeadCreated' then 'commit (after the operator confirmed): FAILED code=Covenant.CapacityExceeded message="This mutation would render the GlobalConfirmed Section at 4196 bytes, past its 4096-byte bound."'. Retired key: 'prepare: success currentLaneRevision=2 expected=2' then 'commit: FAILED code=Covenant.LifecycleConflict message="This Covenant head is retired and reactivation was not requested."'. CLI grep: no capacity or section handling in Cli/Commands/Tower/CovenantCommands.cs. Output: scratchpad/review/probe-u24-usability/mutation-probe-run1.txt.

**Suggested fix.** In PrepareAsync read scope/section occupancy through the existing ICovenantStore.ReadQuotaSnapshotAsync / ReadSectionOccupancyAsync (as CovenantTurnHeadProbe does) and apply CovenantScopeCapacity.Refusal / CovenantSectionCapacity.Refusal, returning the same CapacityExceeded error at prepare with a remediation sentence (retire or shorten an entry); refuse Lifecycle == Retired && !reactivate at prepare with the commit's text plus 'pass --reactivate'. Alternatively add trailing optional capacity/lifecycle fields to the preflight DTO.

#### RV-054 (Important, usability, diagnostics, effort M): A draining schema transition is invisible to the operator: logs lack tier/sweep/version, 'advanced N runs' reports runs seen, no doctor or status surface reads the journal

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionHostedService.cs:103` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionCoordinator.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaBackfillRunner.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs`, `docs/Arcanum.DEBUGGING.Human.md`, `docs/Arcanum.Command.Reference.md`

**Claim.** The only progress signal is 'A Grimoire schema transition pass advanced {Runs} run(s) through {Batches} batch(es) and {Rows} row(s).' where {Runs} is RunsSeen (every run in flight, including ones that failed or did nothing), with no tier, sweep name, from/target version, cursor or cumulative BackfillRowsProcessed. The failure warning (coordinator 149-152) names the tier but not the sweep, and the durable LastDurableErrorCode is the single constant Grimoire.Schema.TransitionPassFailed for every cause (coordinator line 60). GrimoireSchemaBackfillRunner and the journal have no logger. Nothing reads grimoire_schema_transitions for an operator: readers are only the coordinator, installer, planner, runner and BackupRestoreSchemaDrain; arcanum doctor has no schema-transition check; memory status shows only the Covenant block's diagnostic code (CovenantManagementService.cs:570). DEBUGGING.Human.md item 31 sends an engineer to a debugger and the encrypted journal, and says batches are 'a few seconds apart' while the code waits 15 s (5 min after a pass that did not advance).

**Failure scenario.** Core has eight chained sweeps (steps 2,3,5,7,9,12,16,18) at 128-256 rows per batch, 16 batches per pass, one pass per 15 s: at most about 200-270 rows per second (an estimate from the constants, not a measurement), so an installation with a million rows in the swept tables needs over an hour. The tier reports TransitionIncomplete throughout and DESIGN says a Core tier mid-run stands its dependents down (for an install coming from Core below v11, Lexicon curation answers 503 'temporarily unavailable while the Core schema transition completes'). The operator cannot tell progress from a stall, which sweep is running, or whether a failing pass is retrying.

**Evidence (trace).** Code: GrimoireSchemaTransitionHostedService.cs:103-110; GrimoireSchemaTransitionCoordinator.cs:60,146-170 (RunsSeen passed as {Runs}); GrimoireSchemaVersionChains.cs Backfills table (steps 2,3,5,7,9,12,16,18); batch bounds in CampaignForkFrontierBackfill (200), UtcInstantCanonicalizationBackfill (256), LongRestSourceIdentityBackfill (128); grep for grimoire_schema_transitions|GrimoireSchemaTransitionJournal in src shows no doctor/status reader; Command Reference diagnostic table has no schema-transition row; LexiconService.cs:65 and API 1215 for the 503. Trace, not executed.

**Suggested fix.** Log tier, sweep name, CompletedThroughVersion->TargetVersion and cumulative rows for each run (and the sweep name on failure); count only runs that advanced; add a read-only doctor check (grimoire.schema_transition, Degraded with sweep name and rows processed) and a line in memory status; record a distinct LastDurableErrorCode per failure class; correct the cadence statement in DEBUGGING.Human.md.

### U25: Arcanum.Infrastructure — Data (1)

#### RV-055 (Important, performance, missing-index, effort S): Budget outstanding-reservation sum scans every BudgetReservations row ever written, inside the write lock and on every turn

`src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs:753` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/BudgetReservations.sql`, `src/RetroDownfall.Arcanum.Api/Intelligence/DailySpendAuthority.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/BudgetMonitor.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/TurnAccountingHandle.cs`, `src/RetroDownfall.Arcanum.Core/Configuration/RetentionSettings.cs`

**Claim.** SumOutstandingAsync (741-772) filters with (BudgetPeriod=@period AND Status=@reserved) OR Status=@uncertain. The only index is (BudgetPeriod, Status); the second OR branch has Status as its trailing column and no statistics are ever gathered (no ANALYZE or PRAGMA optimize anywhere in src), so SQLite does a full table scan. It runs inside the BEGIN IMMEDIATE of ReserveAsync (82-88) and AdjustAsync (210-221), in RecheckDailyLimitAsync (335-351, before each provider round per DESIGN 22.2) and in GetTodayOutstandingReservationsAsync (every turn via BudgetMonitor.CheckAsync, WizardIntelligenceProvider.cs:675/1053, and /api/budget). Reservation rows are never deleted by default (RetentionSettings.AutomaticSweepsEnabled=false; Accounting retention is 365 days even when on), one per accounted operation.

**Failure scenario.** Operator with cost.budget.enabled=true after months of use: with 100k historical reservations each outstanding sum costs 45 ms CPU, ReserveAsync 80 ms while holding the single write lock, and a 3-round turn performs about 4+2R=10 such scans. Cost grows linearly forever; at 730k rows (a year at 2k ops/day) plain sqlite3 takes 27 ms per scan vs 0.06 ms for the indexed half.

**Evidence (probe).** EXPLAIN QUERY PLAN on the real DB: '2|0|SCAN BudgetReservations'. Probe P1 (SQLCipher, CPU ms per call; bills today / historical reservations): 0/0 -> Reserve 0.54, Outstanding 0.06; 2,000/5,000 -> 7.0, 0.51; 10,000/30,000 -> 21.9, 12.5; 30,000/100,000 -> 79.5, 45.2. Indexable half only (BudgetPeriod=? AND Status=?): 0.075 ms at 100k rows. Plain sqlite3 at 730k rows: 27 ms vs 0.064 ms. Verified fix: CREATE INDEX (Status, BudgetPeriod) plus UNION ALL of the two branches gives two SEARCH ... USING INDEX plans and 0.05 ms at 730k rows.

**Suggested fix.** Add IX_BudgetReservations_Status_Period ON BudgetReservations(Status, BudgetPeriod) in Schema/Tables/BudgetReservations.sql (replace the current (BudgetPeriod, Status) index) and rewrite SumOutstandingAsync as two equality-predicate SELECTs (or UNION ALL). Add a plan test pinning SEARCH rather than SCAN.

#### RV-056 (Important, performance, unbounded-growth, effort M): Each Campaign rollup revision copies every prior source: campaign_rollup_sources grows as N(N+1)/2 and every publish makes several O(N) passes under the write lock

`src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignRollupStore.cs:131` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignRollupStore.Manifests.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/campaign_rollup_sources.sql`, `src/RetroDownfall.Arcanum.Core/Configuration/RetentionSettings.cs`

**Claim.** PublishRollupAsync inserts INSERT ... SELECT ... FROM campaign_rollup_sources WHERE RollupArtifactId=$previous (131-142) plus one INSERT per addition (144-156), so each revision stores a full copy of the source set. Nothing in the store deletes older revisions, and CampaignSummaries retention is Enabled=false with no automatic sweeps by default. Inside the same BEGIN IMMEDIATE it also re-runs PrepareRollupWithinAsync (line 109, which VerifySources the previous revision: ~5 queries per source) and FullSourceManifestAsync (Manifests.cs:86-150, one ReadMetadataAsync per prior source).

**Failure scenario.** Operator with features.campaignRollups and Covenant on, one long-lived Campaign gaining a Session roughly per fold: after 600 Sessions the table holds 180,300 rows and each fold holds the write lock ~49 ms; at ~2,000 Sessions ~2M rows and ~160 ms (extrapolated). Storage and every scan of the table grow quadratically with Campaign age.

**Evidence (probe).** Probe P8 (SQLCipher, one fold per added Session): rows at 10/50/100/200/400/600 Sessions = 55/1,275/5,050/20,100/80,200/180,300 (exactly N(N+1)/2); PublishRollupAsync CPU 1.1/3.2/7.0/11.9/29.5/48.9 ms (~0.08 ms per source, linear).

**Suggested fix.** Delta-encode revisions (store only additions plus a base-revision pointer and carry the digest incrementally) or delete rollup revisions older than the newest one no live turn has bound; avoid re-verifying the whole previous revision inside the write transaction.

#### RV-057 (Important, performance, missing-index, effort M): campaign_rollup_sources is full-scanned by SessionId on entry/session deletes, and ReadSessionClosureAsync materialises the whole closure into huge strings

`src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignSummaryLifecycle.cs:200` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/campaign_rollup_sources.sql`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.CampaignSummaries.cs`

**Claim.** Statements filtering campaign_rollup_sources WHERE SessionId=$ownerId (lines 40, 141, 200, 210) have no usable index: the table has only PK (RollupArtifactId, SessionId) and an index on ContributionArtifactId, so each is SCAN ... COVERING INDEX. They run in EnsureSessionUnlabeledAsync (reached from GrimoireRepository.cs:747 entry delete inside BEGIN IMMEDIATE, CampaignRepository.cs:505, and each retention Session candidate DataRetentionService.Pruning.cs:5475) and in ReadSessionClosureAsync (179-250), which also appends every closure row to two StringBuilders and SHA-256s authority.ToString(); the retention planner (DataRetentionService.cs:3882) and applier (4145) each call it per Session candidate. campaign_maintenance_checkpoints is scanned by the OR at line 207. Combined with the quadratic table, a Session present in R revisions yields ~R*N closure rows.

**Failure scenario.** Operator deletes one Entry (or retention evaluates a Session) in a Campaign with 600 Sessions: the delete holds the write lock for a 29 ms scan; a retention pass over a Session of that Campaign builds a 17.7M-char string and allocates 418 MiB in a single call (about 1.1 GiB at 500k rows, extrapolated), per candidate, twice.

**Evidence (probe).** EXPLAIN QUERY PLAN: 'SCAN campaign_rollup_sources USING COVERING INDEX sqlite_autoindex_campaign_rollup_sources_1' for DISTINCT RollupArtifactId WHERE SessionId=?; checkpoint query 'SCAN campaign_maintenance_checkpoints'. Probe P8 (SQLCipher): lookup 3.5 ms CPU at 20,100 rows, 28.7 ms at 180,300 rows; ReadSessionClosureAsync(oldest Session) 56 ms/20,302 rows/2.0M chars at 20,100 rows and 534 ms/180,902 rows/17.7M chars/418 MiB allocated at 180,300 rows.

**Suggested fix.** Add idx_campaign_rollup_sources_session ON campaign_rollup_sources(SessionId, RollupArtifactId) and indexes on campaign_maintenance_checkpoints(SessionId) and (SourceSessionId); stream closure rows into an IncrementalHash and keep only the key set actually consumed instead of two unbounded StringBuilders.

#### RV-058 (Important, usability, docs-mismatch, effort S): arcanum doctor gives a stuck Covenant reset the repair guidance for an ordinary retention policy change

`src/RetroDownfall.Arcanum.Core/Operations/LongRunningOperationRecoveryRegistry.cs:171` — related: `src/RetroDownfall.Arcanum.Infrastructure/Operations/DurableOperationDiagnostics.cs`, `src/RetroDownfall.Arcanum.Core/Operations/DurableOperationDiagnosticsContracts.cs`, `docs/Arcanum.Command.Reference.md`

**Claim.** Outside the U25 file list, but it is where the failures of the findings above end up. A parked Covenant reset is a DataRetentionMutation row at checkpoint version 4 (DataRetentionService.cs:1857) in ReconciliationRequired with terminal code Covenant.MaintenanceFailed. DurableOperationDiagnostics.cs:54 groups by (kind, terminal code) but looks guidance up by kind only, so the doctor panel prints 'DataRetentionMutation x1 (Covenant.MaintenanceFailed): Inspect arcanum data retention show before re-applying the policy change.' The descriptor's own RecoveryIntent directly above says a version-4 row is an interrupted Covenant reset whose admission stays closed until it is resumed or an operator resolves it, and Command Reference 1143 says 'Do not start a second erasure.'

**Failure scenario.** An operator with a closed Covenant gate follows the doctor line, runs data retention show (irrelevant), and re-applies the reset, the one action the Command Reference says not to take.

**Evidence (trace).** Read of LongRunningOperationRecoveryRegistry.cs:158-171, DurableOperationDiagnostics.cs:48-56 (Find(kind)?.ManualRepairGuidance), DurableOperationDiagnosticsContracts.cs:48-51 (the rendered line) and DataRetentionService.cs:1857 (reset uses kind DataRetentionMutation). Not executed.

**Suggested fix.** Select guidance by terminal code or checkpoint version: for Covenant.* codes return 'arcanum operation show <id>; admission stays closed on purpose; do not start a second erasure', per Command Reference 1143.

#### RV-059 (Important, usability, diagnostics, effort S): Covenant erasure coordinator leaves admission closed on three failure paths without writing any log line

`src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs:2515` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantSensitiveRetentionPurgeCoordinator.cs`, `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantErasureCoordinatorTests.cs`

**Claim.** The coordinator is the only place an erasure failure can be logged: the storage-health, canonical-transaction, both kernels, inventory-source and checkpoint-initiator files contain no logging at all. In three paths the coordinator logs nothing either. (1) Any exception that is not a CovenantErasureStepFailedException is turned into MaintenanceFailure() and closed KeepClosed: catch (OperationCanceledException) at 2484 and catch (Exception) at 2515 and 2591, RunLifecycleAsync at 4258, WithRequiredLedgerAsync at 1485, PrepareAuthenticatedRecoveryAsync at 996. Neither the exception type nor the phase is logged. The sibling CovenantSensitiveRetentionPurgeCoordinator.cs:358-368 already logs exception.GetType().Name and explains why the message is withheld. (2) After the erasure is proven and published, a failing _disclosureWriter.ReopenAsync goes to CloseAsync(KeepClosed, writer.Error.Code) at 2652-2664 with no log, although the published.IsFailure branch just above it logs. (3) ReconcileBeforeDispositionAsync parks the transition at 3908, 3940, 3983 and 3996 with no log; the comment at 3879-3881 lists four distinct causes of a missing terminal winner, plus a parent-receipt publish failure, and all end as the same Covenant.MaintenanceFailed.

**Failure scenario.** A reset or factory erasure hits a disk-full or permission error after the canonical erasure. The installation stays closed. arcanum doctor shows DataRetentionMutation x1 (Covenant.MaintenanceFailed). The rolling log has no line about the operation, so the operator cannot tell transient I/O from corruption, or whether the erasure already happened. In the writer-restart case the erasure was fully proven and nothing at all says so.

**Evidence (probe).** Probes in a scratch copy of CovenantErasureCoordinatorTests with the harness NullLogger replaced by a capturing logger. PROBE_unexpected_exception_mid_erasure_is_logged_nowhere (fault seam throws IOException('disk full /secret/path') before phase WalTruncated): 'PROBE1 success=True disposition=KeepClosed blocking=Covenant.MaintenanceFailed' and 'PROBE1 log line count=0'. PROBE_writer_restart_failure_after_proven_erasure_is_logged_nowhere (DisclosureWriter.ReopenFails=true): 'PROBE2 success=True disposition=KeepClosed localSecure=True blocking=Covenant.MaintenanceFailed' and 'PROBE2 log line count=0'. rg -c for ILogger/.Log* in CovenantLocalErasureStorageHealth, CovenantCanonicalErasureTransaction, CovenantProtectedArtifactErasureKernel, CovenantManagedFileErasureKernel, CovenantErasureInventorySource and CovenantResetCheckpointInitiator printed no matches. The existing suite passes NullLogger everywhere (CovenantErasureCoordinatorTests.cs 3152, 3208), so it cannot see this. Park sites (3) verified by reading, not executed.

**Suggested fix.** In each catch block log OperationId, state.Phase and exception.GetType().Name (type only, matching the purge coordinator and the content-free policy). Log writer.Error.Code and Message (fixed strings) at 2652. Log the park cause at the four Parked sites. Add capturing-logger assertions to the crash-matrix cases.

#### RV-060 (Important, usability, error-message, effort M): A failed Covenant reset or factory erasure tells the operator nothing about what happened or what to do, contrary to the docs

`src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs:4264` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryActivation.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantLabeledArtifactGuard.cs`, `docs/Arcanum.API.md`, `docs/Arcanum.Command.Reference.md`

**Claim.** Every failed or parked erasure reaches the operator as a code plus a fixed sentence: 'The Covenant erasure lifecycle could not be completed safely.' (4264-4267), 'The Covenant reset did not reach a committed reopen disposition.' (DataRetentionService.cs:2308-2313, used at FactoryActivation.cs:485,492,501), 'Healthy-catalog factory erasure requires one current installation inventory and its exclusive lifecycle.' (FactoryActivation.cs:102-107), 'The committed factory-erasure checkpoint could not be reloaded.' (:362). None names the phase, the operation id, whether data was erased, or a next step. CovenantErasureCompletion computes CanonicalResetApplied, LocalSecureErasureComplete and ExternalDisclosuresNotRevocable and its doc (435-455) says an operator needs all three and that collapsing them would let a half-proven erasure read as finished; rg finds no reader of those properties outside the coordinator and CovenantErasureProgress, so the service discards them on KeepClosed and RollbackAndReopen. Docs contradicted: API.md:919 ('Each names an operator action rather than a retry'), Command Reference 1143 ('follow the reported restore, Covenant-family reinitialize, or full-installation-reset remedy', but none is reported) and 1144. The CovenantLabeledArtifactGuard refusals (305-325) name 'the sensitivity purge boundary' but no command, while DESIGN 10.20.2 says the operator's action is to repair the Grimoire.

**Failure scenario.** arcanum data reset-memory --scope covenant --apply stops after the canonical erasure. The CLI prints 'Covenant.MaintenanceFailed: The Covenant reset did not reach a committed reopen disposition.' (DataRetentionCommands.cs:518), exits 1, and the API answers 503. The operator cannot tell 'nothing erased' from 'everything erased and only the writer restart failed', is not told to run arcanum operation show <id>, and is not told not to start a second reset.

**Evidence (probe).** Probe 2 (see finding 1) is exactly this state: disposition=KeepClosed with localSecure=True, which the service reports with the fixed sentence above. rg for LocalSecureErasureComplete|CanonicalResetApplied|ExternalDisclosuresNotRevocable outside the coordinator finds only CovenantErasureProgress.cs and a comment in CovenantCanonicalErasureTransaction.cs. Error has only Code, Message, Details (Core/Primitives/Error.cs), so the Message is the only vehicle. ArcanumErrorMapper.cs:206-224 maps the codes to 503 as documented; the messages are the gap.

**Suggested fix.** Make CovenantResetFailure take the completion and say, content-free, whether the canonical erasure was applied and whether local erasure was proven, plus the operation id and 'Admission stays closed. Run arcanum operation show <id>; do not start a second reset.' Reword the guard refusals to name the supported path.

### U26: Arcanum.Infrastructure — Data (2)

#### RV-061 (Important, performance, n-plus-one, effort M): Entry/attachment/embedding candidate and apply statements scan BudgetReservations for every run of the entry's session, per entry

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:1465` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/BudgetReservations.sql`

**Claim.** AddEntryCandidatesAsync, AddAttachmentCandidatesAsync, AddEntryEmbeddingCandidatesAsync, the three *ProtectionDiagnostics queries, DeleteEntryCandidateAsync, DeleteEntryEmbeddingCandidateAsync and ReadSessionConflictsAsync each evaluate, per entry row, EXISTS(BudgetReservations JOIN InferenceRuns ON lower(replace(run.Id))=lower(replace(reservation.RunId)) WHERE lower(replace(run.SessionId))=lower(replace(entry.SessionId)) AND Status IN (..)). With no BudgetReservations.RunId index the plan is SEARCH run (session index) then SCAN reservation, costing runs_in_session x reservations per entry, although the answer is a property of the session.

**Failure scenario.** 20,000 aged entries in sessions with ~20 runs each and ~20,000 reservations: ~20,000 x 20 x 20,000 = 8e9 evaluations (~170 s) per statement, and the diagnostics query, the candidate query, and the attachment/embedding equivalents each repeat it; apply repeats a reservation join per deleted entry.

**Evidence (trace).** EXPLAIN of the production entries candidate query (explain.txt): 'CORRELATED SCALAR SUBQUERY 4 / SEARCH run USING INDEX IX_InferenceRuns_SessionId_Norm (<expr>=?) / SCAN reservation'. Entries_with_runs_plan_scaling, 400 aged entries of 2,000, 20 sessions, runs per session 2/10/40 (reservations 40/200/800), plan only (results-5-batch2.txt): 221 / 256 / 1,170 ms process-cpu; at 800 reservations the two big entry statements take 277 and 275 ms each and the per-candidate ReadSessionConflicts reservation join 272 ms over 400 calls. Fitted cost ~21 ns x E_old x runs/session x reservations.

**Suggested fix.** Share the BudgetReservations(lower(replace(RunId,..))) index with finding 2 and compute the conflicted-session set once per plan (active operations, running runs, outstanding reservations -> HashSet<Guid>) or use uncorrelated NOT IN (SELECT ...) subqueries that SQLite materialises once, instead of per-entry correlated EXISTS.

#### RV-062 (Important, performance, allocation, effort S): Entry-embedding candidate loop is O(n^2) in time and allocation: candidates.Any(c => c == prefix + NormalizeGuid(entryId)) over the plan-wide list

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:1946` — related: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs`

**Claim.** For every aged entry-embedding row AddEntryEmbeddingCandidatesAsync scans the whole candidates List<string> (every class so far, growing by one per accepted row) and, per element, re-parses and re-formats the Guid and concatenates the prefix. The SessionEntryEmbeddings rule is enabled by default (30 days).

**Failure scenario.** data retention prune --dry-run (and the apply's replan) on an installation with 40,000 aged entry embeddings burns ~21 CPU-seconds and allocates ~159 GB of short-lived strings in this loop, stalling the host through GC pauses; 8,000 rows already takes 9.2 s wall in the Debug test host versus 131 ms at 1,000 rows.

**Evidence (probe).** Release console micro-benchmark of exactly the expression (probes/U26/micro): n=10,000 1,511 ms CPU / 9,919 MB allocated; n=20,000 5,355 ms / 39,675 MB; n=40,000 21,125 ms / 158,696 MB (x4 per doubling). Real PlanAsync with only SessionEntryEmbeddings enabled (results-1-embedding.txt, Debug, loaded machine): 1,000 rows 131 ms; 2,000 485 ms; 4,000 2,823 ms; 8,000 9,171 ms wall of which SQL is 1,956 ms (88,010 statements). Code lines 1943-1950.

**Suggested fix.** Build a HashSet<string> (or HashSet<Guid>) of the entry: candidates once before the loop and test membership with a precomputed key; never re-format the Guid per comparison.

#### RV-063 (Important, performance, n-plus-one, effort M): Accounting prune planning is quadratic (retained sessions) and cubic (with cost adjustments): no usable RunId index on BudgetReservations, BillableOperations or CostAdjustments

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:2629` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/BudgetReservations.sql`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/BillableOperations.sql`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/CostAdjustments.sql`, `src/RetroDownfall.Arcanum.Core/Configuration/RetentionSettings.cs`

**Claim.** AddAccountingCandidatesAsync runs correlated per-run subqueries (EXISTS/COUNT over BudgetReservations, BillableOperations, CostAdjustments keyed by lower(replace(RunId,'-',''))) plus a nested 'adjustment.BillableOperationId IN (SELECT Id FROM BillableOperations WHERE lower(replace(RunId..)) = lower(replace(run.Id..)))' that is re-materialised per adjustment row. BudgetReservations has no RunId index at all, BillableOperations only a plain one (unusable for the wrapped column), CostAdjustments none.

**Failure scenario.** Accounting retention is enabled by default (365 days, RetentionSettings.cs:71). On an installation older than a year every data retention prune (preview, then the apply's replan, then crash-recovery replan) pays the cost: runs in retained sessions are quadratic (1,000 old runs 338 ms CPU, 4,000 old runs 5,282 ms CPU, so ~9 CPU-minutes for 40,000), and when CostAdjustments rows exist the nested IN makes planning cubic (200 runs 2.85 s CPU, 400 runs 21.2 s CPU, x7.4 per doubling; ~12 CPU-hours extrapolated at 5,000 candidate runs).

**Evidence (trace).** EXPLAIN of the production candidate query (explain.txt): 'SCAN run / CORRELATED SCALAR SUBQUERY .. SCAN reservation / SCAN billable USING COVERING INDEX IX_BillableOperations_RunId / SCAN adjustment / CORRELATED LIST SUBQUERY 4 SCAN billable'. Raw production statement with 1 billable op + 1 reservation + 1 adjustment per run (results-3-accounting-raw.txt): 25 runs 6 ms, 50 runs 107 ms, 100 runs 1,262 ms, 200 runs 7,213 ms; the same statement without the nested IN: 76 ms at 200. Through PlanAsync (results-4-accounting-service.txt): runs=200 plan 2,848 ms process-cpu, runs=400 plan 21,214 ms process-cpu; apply at 200 runs 3,300 ms CPU (replan ~2.85 s). Retained-session path (results-6-batch3.txt): runs=1000 338 ms CPU, runs=4000 5,282 ms CPU and 4,000 blockers, from the 'blocked' query at lines 2546-2574 whose select-list EXISTS scans all reservations per run. Delete side (6506-6651) does ~9 more full scans per run candidate. Wall times were 3-10x higher because the machine was at load average ~100; CPU time is quoted.

**Suggested fix.** Add expression indexes BudgetReservations(lower(replace(RunId,'-',''))), BillableOperations(lower(replace(RunId,'-',''))), CostAdjustments(lower(replace(RunId,'-',''))) and CostAdjustments(BillableOperationId); and replace the per-run correlated counts with one grouped pass (LEFT JOIN on a GROUP BY of the normalised RunId) and hoist the nested IN into an uncorrelated subquery so SQLite materialises it once.

#### RV-064 (Important, performance, crash-consistency, effort M): Prune checkpoint rewrites the entire candidate list twice per journal-bearing candidate (O(N^2) bytes written)

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:3479` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs`, `docs/Arcanum.DESIGN.md`

**Claim.** ApplyUnifiedPruneCoreAsync saves a checkpoint before (3479) and after (3532) every candidate whose journal is non-null (file:, session:, attachment:, audit-log:, guardrail-log:), and SerializePruneCheckpoint (3718) encodes all N candidates (two base64 strings each, ~125 bytes per line) into an UPDATE of LongRunningOperations.CheckpointPayload each time, so a sweep writes ~2N x 125N bytes. Uploaded files (enabled by default, 30 days) are the common trigger.

**Failure scenario.** A batch user with 10,000 expired uploaded files: 20,001 checkpoint saves of ~1.25 MB each = ~25 GB through SQLCipher and the WAL; at ~8 us per KB of payload (loaded) that is minutes of pure checkpoint I/O versus a few seconds of deletes; 40,000 files exceeds the 5 MiB cap at line 3692 and the apply throws before deleting anything. (Reliability cross-reference: ParsePruneCheckpoint rejects more than 10,000 candidates at line 4102, so a crash during a larger sweep is unrecoverable.)

**Evidence (trace).** UploadedFile_prune_scaling through the real service (results-5-batch2.txt), apply process-cpu per candidate: 250 files 2.9 ms (716 ms), 500 files 3.2 ms (1,613 ms), 1,000 files 4.5 ms (4,546 ms), 2,000 files 6.9 ms (13,897 ms). Profile for 2,000 files: UPDATE "LongRunningOperations" SET CheckpointPayload.. 4,001 x = 7,946 ms of 10,104 ms SQL (79%); the same UPDATE averages 0.49 ms at 500 files (62 KB) and 1.99 ms at 2,000 files (250 KB). Serialisation alone is cheap (Checkpoint_serialize_micro: 10,000 candidates 8 ms CPU, 976 KB, 9 MB allocated), so the cost is the blob rewrite.

**Suggested fix.** Persist the immutable candidate list and frozen cutoffs once at start; make the per-candidate checkpoint carry only the cursor and the pending journal (hundreds of bytes), in a separate row/column or a split payload. Keep the existing wall-clock lease renewal.

#### RV-065 (Important, performance, n-plus-one, effort M): Candidate identity lookups use lower(replace(Id,'-','')) on primary keys with no expression index; every candidate scans its table (plan and apply are O(candidates x table))

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:5332` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/Entries.sql`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/UploadedFiles.sql`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/Sessions.sql`, `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionNormalizedIdentityQueryPlanTests.cs`

**Claim.** The retention sweep looks each candidate up by lower(replace(Id,'-','')) = @id on Entries, Sessions, UploadedFiles, IdempotencyClaims, InferenceRuns, LongRunningOperations, Batches, CostAdjustments, BudgetAlerts, SanctumBreaches (and SessionAttachments.EntryId). The V6/V14/V15 expression indexes cover only session-keyed and file-role columns, so SQLite cannot use the primary key and scans the table once per statement; apply issues 5-7 such statements per entry candidate and planning one more.

**Failure scenario.** Operator enables the Entries (or session) rule on an installation with 100k-1M entries (rows carry KB of content, so each scan reads the whole table) and runs data retention prune: the plan scans Entries once per selected entry and apply scans it 6-7 more times per entry, so pruning tens of thousands of entries takes hours while holding the single-flight retention lease. Measured: pruning 150 entries from tables of 2,000 / 8,000 / 24,000 entries costs 548 ms / 2,711 ms / 17,805 ms apply (3.7 / 18 / 119 ms per candidate) with 300-byte rows; plan-only for all-old tables of 1,500 / 3,000 / 6,000 entries costs 957 / 2,149 / 7,465 ms CPU.

**Evidence (probe).** EXPLAIN QUERY PLAN on the real schema (probes/U26/explain.txt): 'SELECT SessionId, IsPinned, rowid FROM Entries WHERE lower(replace(Id,...)) = @id' -> SCAN Entries; UploadedFiles -> SCAN UploadedFiles; Sessions -> SCAN Sessions; Batches/IdempotencyClaims/LongRunningOperations/InferenceRuns/CostAdjustments/BudgetAlerts/SanctumBreaches -> SCAN ... USING COVERING INDEX sqlite_autoindex_*; UPDATE SessionAttachments SET EntryId=NULL WHERE lower(replace(EntryId..)) -> SCAN SessionAttachments. Statement profile of the 24,000-row apply: DELETE FROM Entries WHERE lower(replace(Id..) 150x = 6,163 ms; COUNT(*) FROM Entries WHERE lower(replace(Id..) AND julianday(CreatedAt)<=.. 150x = 4,916 ms; COUNT(*) FROM Entries WHERE lower(replace(Id..) 300x = 4,439 ms (93% of apply). Sites: Pruning.cs lines 1603-1607, 3174-3178, 4593-4606, 4614-4618, 4640-4648, 4757, 4779, 4826, 4974-4988, 5083, 5101, 5139, 5166-5200, 5244-5253, 5307-5311, 5331-5332, 5425-5436, 5518, 5522-5561, 5597-5601, 6410-6433, 6446-6465, 6478-6493, 6585-6643. Schema/Tables/Entries.sql defines only IX_Entries_SessionId_Norm. The existing DataRetentionNormalizedIdentityQueryPlanTests pins only the session/file-role predicates, so dropping or never adding the Id indexes passes every test. Probe: tests Data/RetentionPerfProbe.cs (saved as probes/U26/RetentionPerfProbe.cs.txt), results-2-entries.txt and results-6-batch3.txt.

**Suggested fix.** Add expression indexes in the exact predicate shape lower(replace("Id",'-','')) for Entries, Sessions, UploadedFiles, IdempotencyClaims, InferenceRuns, LongRunningOperations, Batches, CostAdjustments, BudgetAlerts, SanctumBreaches and SessionAttachments(EntryId) through a Core schema transition (as V14/V15 did) and extend DataRetentionNormalizedIdentityQueryPlanTests to pin them. Where a table is already canonical (UploadedFiles since V15) compare Id = @id with the canonical spelling instead.

#### RV-066 (Important, reliability, data-integrity, effort M): Factory reset leaves Campaign Summary content (model-written summaries of the erased Sessions) in the Grimoire

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs:31`

**Claim.** Neither `FactoryPlanTables` nor `FactoryDeletionTables` (line 113) names `campaign_rollup_artifacts`, `campaign_rollup_state` or `campaign_maintenance_checkpoints`. `campaign_rollup_artifacts` holds `Content` (up to 8 KiB) and is owned by `Campaigns ON DELETE CASCADE`. Factory reset keeps Campaigns, so the aggregate summaries survive while their source Sessions, contributions and source edges are removed by cascade. `ReconcileFactoryResetAsync` checks only its own lists and reports success. This contradicts DESIGN: "a summary of an erased transcript is erased transcript content, not a rebuildable cache the reset may leave behind". It is also inconsistent with the CampaignSummary memory reset, which deletes these tables. The Covenant protected kernel removes only labelled rollups.

**Failure scenario.** With features.campaignRollups on, an operator runs a factory reset to erase everything. The reset reports Completed and Reconciled, yet the Grimoire still holds each Campaign's summary text derived from every erased Session. It is visible to SQL, backups and retention status, and is removable only by a CampaignSummary memory reset or prune.

**Evidence (probe).** Probe U26Probe_FactoryResetLeavesCampaignRollupContent: seeded a Campaign with a contribution, a rollup and a campaign_rollup_sources edge (the existing SeedRetentionSummaryAsync helper), then ran the real ordinary factory reset through the V0 recovery arm (BuildFactoryResetPlanCoreAsync + ApplyFactoryResetAsync, the same pair the V2 continuation runs). Output: `factory plan items=[ActiveSessions:2/0]`; `factory completed=1 attention=0 sessions=0 contributions=0 rollupsWithContent=1 rollupPointers=0`. Schema: Tables/campaign_rollup_artifacts.sql `CampaignId ... REFERENCES Campaigns ON DELETE CASCADE`, `Content TEXT`. No existing factory-reset test seeds campaign summaries.

**Suggested fix.** Add campaign_rollup_artifacts (Physical, CampaignSummaries) and campaign_maintenance_checkpoints to both factory lists. Clear or reset campaign_rollup_state first, since its CurrentArtifactId FK has no ON DELETE. Run the deletes under the SessionRetention/owner-cleanup authorization their delete guards require, include them in reconciliation, and add a factory test with campaign summaries.

#### RV-067 (Important, reliability, unbounded-growth, effort M): Past 5 MiB of checkpoint the prune can never start and deletes nothing, with a non-actionable error

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:3692`

**Claim.** This has the same root cause as the 10,000 cap. At roughly 33k-45k candidates (76-150 bytes per checkpoint line), the first `SavePruneCheckpointAsync`, taken at line 3359 before any candidate, throws `InvalidOperationException("The retention checkpoint exceeds its bounded payload size.")`. The request path marks the row Failed and returns Data.ReconciliationFailed "The retention operation failed; its durable history requires operator review." The hosted sweep fails the same way every interval. Nothing is pruned, so the candidate count only grows and the failure perpetuates itself.

**Failure scenario.** An installation that enables a rule late, or has not pruned for a long time, accumulates about 40k aged workspace chunks or entry embeddings. Every manual prune and every automatic sweep fails before deleting anything. The operator is not told why or how to recover (they would have to raise rule days, prune, then lower them stepwise).

**Evidence (probe).** Probe U26Probe_PruneWhoseCheckpointExceeds5MiB_CannotStartAndDeletesNothing: 36,000 aged keys with 64-char hashes. Output: `first success=False code=Data.ReconciliationFailed message=The retention operation failed; its durable history requires operator review.`, `second success=False code=Data.ReconciliationFailed`, `remaining keys=36000`. Code: Pruning.cs:3359 initial save, 3692 throw; DataRetentionService.cs:1740-1766 generic catch marks Failed.

**Suggested fix.** Fix together with the 10,000-candidate finding (paged sweep or a cursor-only checkpoint). Until then, bound the plan before apply and return a typed, actionable error instead of ReconciliationFailed.

#### RV-068 (Important, usability, error-message, effort S): Factory-reset inventory refusals name no path or root and surface as 'Grimoire could not be opened' or a 500

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs:1217` — related: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs`, `src/RetroDownfall.Arcanum.Api/Data/DataRetentionEndpoints.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`

**Claim.** CaptureManagedTree, TryEnsureStrictFactoryRoot, EnsureStrictFactoryDirectory, IsFactoryReparsePoint and EnumerateStrictFactoryEntries throw IOException with fixed texts that name neither the offending entry nor which managed root (attachments, files or logs) it is in. The operator-facing mapping then discards even that text. The local `data factory-reset --global/--all --dry-run` and `--apply` path swallows the IOException in InstallationResetExistingGrimoire.ExecuteUnderStoppedHostAuthorityAsync (lines 640-655) with no logging and returns `Data.InventoryUnavailable` 'The existing Grimoire could not be opened without creating or repairing state.' The API plan routes `/api/data/prune/plan` and `/api/data/factory-reset/plan` let the IOException reach ArcanumExceptionHandler, which gives 500 `Hub.Unhandled` 'An internal error occurred.' The apply-side catch (DataRetentionService.cs:1376) says 'request a new dry-run', which would fail the same way.

**Failure scenario.** The Arcanum data directory has `files/` or `attachments/` symlinked to a larger disk, or contains a symlink, an unreadable subdirectory, or on Windows a OneDrive or cloud-placeholder reparse point (the last is plausible but not tested here). The operator runs `arcanum data factory-reset --global --dry-run`. The error says the Grimoire could not be opened, which is false, and there is no log line and no path. The operator cannot tell which of three roots or thousands of entries is refused, and the dry-run cannot be made to succeed.

**Evidence (probe).** FactoryReset.cs: :1217 'Factory reset refused a managed entry whose no-follow identity could not be captured.', :1227 '...identity changed during inventory.', :1241 '...not an owned regular file or directory.', :1480 'Factory reset refused a managed root that was not an owned ordinary directory.', :1501 'Factory reset could not inspect a managed root.', :1461 '...managed directory whose no-follow identity could not be captured.' None interpolate the path. Probe in a scratch worktree: a symlink nested at attachments/session-a/nested/linked gave `IOException: Factory reset refused a managed entry whose no-follow identity could not be captured.`, with the message not containing the attachments root, 'linked' or 'session-a'. With the `files` root replaced by a symlink the message was `IOException: Factory reset refused a managed root that was not an owned ordinary directory.`, with no root named. InstallationResetExistingGrimoire.cs:640-655 catches IOException, UnauthorizedAccessException and others and returns Unavailable<T>() with no logging; its Unavailable text is at :932. Endpoint: DataRetentionEndpoints.cs:141 and :335 have no catch around PlanAsync or PlanAdmissionAsync. I did not run the published binary, because it was not on disk. The local-reset mapping is a code trace; the thrown messages are probe-verified.

**Suggested fix.** Include the root role (attachments, files or logs) and the path relative to the managed root in every IOException message. These are paths inside the Arcanum data root, not protected content. Log the exception at Warning in ExecuteUnderStoppedHostAuthorityAsync and map managed-tree refusals to a distinct code (for example `Data.ManagedTreeRefused`), or carry the exception message into the Error. Catch IOException in the plan endpoints and answer with a typed 409 or 422 instead of a 500. Add a test that plans a factory reset over a symlinked entry and asserts the message names the entry.

#### RV-069 (Important, usability, api-contract, effort M): Prune plan is unbounded but its checkpoint rejects more than 10,000 candidates on read and about 50,400 on write

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:4102` — related: `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationReconciler.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`, `docs/Arcanum.DESIGN.md`, `docs/Arcanum.Engineering.md`, `docs/Arcanum.DEBUGGING.Human.md`, `docs/Arcanum.Command.Reference.md`, `src/RetroDownfall.Arcanum.Core/Configuration/ConfigurationValidator.cs`

**Claim.** BuildUnifiedPrunePlanCoreAsync plans with `const int limit = int.MaxValue` (line 299), so the plan has no candidate ceiling. The durable checkpoint has two hidden ceilings. `SavePruneCheckpointAsync` throws InvalidOperationException above 5 MiB (line 3692). `ParsePruneCheckpoint` throws InvalidDataException above 10,000 candidates (line 4102). At 104 bytes per candidate, writes fail near 50,400 candidates, while reads already fail at 10,001. The ceilings are in no doc and no test. The 'bounded' wording in docs is stale, and the failures surface under misleading names.

**Failure scenario.** (1) An operator enables the Entries rule, or leaves workspace-chunk or accounting retention on a mature install, and the plan holds more than about 50,400 candidates. `data prune --apply` fails on its first checkpoint with `Data.ReconciliationFailed` and 'The retention operation failed; its durable history requires operator review.' Nothing is deleted. Every retry fails the same way, and the real cause (the 5 MiB checkpoint cap) is only in the host log. (2) With 10,001 to about 50,400 candidates the apply works until it is interrupted by Ctrl-C, a host stop or a crash. The row is left ReconciliationRequired. The reconciler's RecoverPruneAsync then hits ParsePruneCheckpoint, and the reconciler logs 'has a corrupt recovery checkpoint' and records `operation.checkpoint_corrupt`, although the checkpoint is not corrupt. The next prune is refused with 'Another data-retention operation is already active: data-retention-prune <id> is ReconciliationRequired' until the operator runs `arcanum operation cancel`. A retry of the operation hits the same parse failure.

**Evidence (probe).** Code: Pruning.cs:299 `const int limit = int.MaxValue;`, :3692 `if (payload.Length > 5 * 1024 * 1024) throw new InvalidOperationException("The retention checkpoint exceeds its bounded payload size.")`, :4102 `if (lines.Length - candidateStart > 10_000) throw new InvalidDataException("The retention checkpoint exceeds the maximum sweep size.")`. The reconciler maps InvalidDataException to CorruptCheckpoint (Operations/LongRunningOperationReconciler.cs:708). The generic apply catch returns the ReconciliationFailed text (DataRetentionService.cs ~1738). Docs: DESIGN.md:709 'Retention applies the complete selected plan ... It is not a public total-item restriction' and :754 'A sweep processes the complete selected plan ... reads remain page-bounded'. ConfigurationValidator.cs:62 says 'Eligible work continues automatically through internal checkpoints until it completes or the operator cancels it.' Stale: docs/Arcanum.Engineering.md:746 'Sweeps are bounded by `maxItemsPerSweep`', docs/Arcanum.DEBUGGING.Human.md:111 'Set `maxItemsPerSweep=1`' (the validator rejects that key as obsolete), docs/Arcanum.Command.Reference.md:1226 'bounded deletion plan'. Probes in a scratch worktree at ac0d2d8e (since removed). Codec round trip with SerializePruneCheckpoint and ParsePruneCheckpoint by reflection: 9,999 and 10,000 parse ok; 10,001 'exceeds the maximum sweep size'; 50,000 (5,200,120 bytes) still serializes; 50,500 (5,252,120 bytes) exceeds the save cap. End to end with SanctumBreaches rows: 1,200 candidates apply fine (rowsDeleted=1200). 52,000 candidates: ApplyAsync returned `Data.ReconciliationFailed`, 'The retention operation failed; its durable history requires operator review.', and the log said 'InvalidOperationException: The retention checkpoint exceeds its bounded payload size.' 10,500 candidates cancelled after 3 s left 10,259 rows, the op ReconciliationRequired, and `ReconcileNowAsync` produced RequiresAttention with code `operation.checkpoint_corrupt`. The next prune returned `Data.Conflict` 'Another data-retention operation is already active: data-retention-prune ac473c7c-... is ReconciliationRequired.' No existing test references these ceilings (rg for 10_000, 'maximum sweep', 'bounded payload' in tests/Data finds nothing).

**Suggested fix.** Make the checkpoint independent of candidate count. Persist the plan id, generatedAt, the per-class frozen cutoffs (about 20 values) and a cursor, and have recovery rebuild the candidate list deterministically and resume after the cursor. If that is too large, at least make the two ceilings equal and enforced at plan time. Fail a plan that exceeds them with a typed `Data.PlanTooLarge` error naming the count, the limit and the remedy (re-run prune, which converges), and map InvalidDataException from a size limit to a message other than 'corrupt'. Add boundary tests at 10,000, 10,001 and 50,500 candidates, including an interrupt followed by recovery. Correct DESIGN 5.4.7, Engineering 746, DEBUGGING 111 and the Command Reference wording in the same change.

### U27: Arcanum.Infrastructure — Data (3)

#### RV-070 (Important, usability, error-message, effort S): Covenant reset that rolls back before erasing reports only 'did not reach a committed reopen disposition'

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:2308` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs`, `docs/Arcanum.API.md`, `tests/RetroDownfall.Arcanum.Tests/Api/GrimoireMaintenanceAdmissionTests.Outcomes.cs`

**Claim.** CovenantResetFailure keeps the specific error code (for example Grimoire.WorkDrainTimeout, HTTP 503) but replaces the message with the fixed 'The Covenant reset did not reach a committed reopen disposition.' for both the RollbackAndReopen (2004-2011) and KeepClosed (2013-2017) dispositions. The API contract for that code is 'nothing was erased, ordinary admission is open again, and asking once the database is quieter is the action' (API.md:905, ErrorCodes.cs remarks). The CLI prints '{Code}: {Message}' with no per-code hint.

**Failure scenario.** An operator runs 'arcanum data reset-memory --scope covenant' while an Apprentice run or chat turn longer than the gate's fixed 5 s drain window is open. Stage one times out, the coordinator rolls back and reopens, and the operator sees code Grimoire.WorkDrainTimeout with the sentence about a committed reopen disposition. For an irreversible erase the key fact (nothing was erased; retry when idle) is absent, and nothing says what to wait for.

**Evidence (trace).** DataRetentionService.cs:2308-2313 'new(string.IsNullOrWhiteSpace(errorCode) ? ...MaintenanceFailed : errorCode, "The Covenant reset did not reach a committed reopen disposition.")'. Trace (not executed end to end): GrimoireConnectionAdmissionGate.DrainRequestAndWorkAsync timeout (569-573) -> CovenantErasureCoordinator.CloseGrimoireAsync (1092) -> AbortBeforeErasureAsync (2869, CloseAsync(... error.Code ...)) -> RollbackAndReopen -> CovenantResetFailure. The message is pinned by GrimoireMaintenanceAdmissionTests.Outcomes.cs:61,569 (which only forbids paths/owner in the body). CLI: DataRetentionCommands.cs:516-520.

**Suggested fix.** Choose a content-free, per-code message (for example 'The reset did not start: ordinary work did not finish within the drain window. Nothing was erased; retry when the host is idle.') and keep the generic sentence only for unknown codes; update the pinned test constant accordingly.

#### RV-071 (Important, usability, diagnostics, effort S): Retention mutation recovery parks rows as Data.ReconciliationFailed from nine branches with no log and swallows the reason

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:7249` — related: `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationReconciler.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs`, `docs/Arcanum.Command.Reference.md`

**Claim.** RecoverMutationAsync (7189-7328) returns RequiresAttention(ErrorCodes.Data.ReconciliationFailed) from nine distinct branches and contains no logger call at all. Both catch (InvalidDataException) blocks (7249, 7269) discard the exceptions whose messages say exactly which check failed. The reconciler only logs when a handler throws, so a returned RequiresAttention is just a metric. Completed and Failed outcomes are silent too, and the Failed row (mutation never committed, nothing deleted, line 7325) carries the same code as the 'needs a human' row. The operation error-code table in the Command Reference has no Data.ReconciliationFailed row.

**Failure scenario.** A delete-attachment/delete-session apply is interrupted after quarantine. On restart the quarantine directory has an unexpected shape or a bad journal. The row goes ReconciliationRequired with Data.ReconciliationFailed. That pair is in StartupRecoveredAttention, so every host start re-runs the handler with the same silent answer, and TryStartSingleFlightAsync refuses every new data-retention-* operation (prune, delete-session, reset-memory, factory reset). The operator sees only 'Another data-retention operation is already active: DataRetentionMutation <id> is ReconciliationRequired.' with no cause, no log, and no documented remedy; operation retry re-drives the same handler.

**Evidence (probe).** DataRetentionService.cs:7249-7253 and 7269-7273 'catch (InvalidDataException) { return ...RequiresAttention(ErrorCodes.Data.ReconciliationFailed); }'; awk over lines 7189-7328 finds 0 'logger.' calls and 9 'RequiresAttention'. LongRunningOperationReconciler.cs:706-721 logs only thrown exceptions. LongRunningOperationStore.cs:717-718 (StartupRecoveredAttention) and 337-355 (single-flight NOT EXISTS over 'data-retention-%' in ReconciliationRequired). Command.Reference.md:1121-1148 table lacks the code. PROBE (executed in scratch worktree, U27Probe_MutationRecovery_UnexpectedQuarantineShape_ParksSilently and ..._CorruptJournal_ParksSilently): 'U27PROBE state=ReconciliationRequired code=Data.ReconciliationFailed logLines=0' for both, with a capturing ILogger<DataRetentionService>.

**Suggested fix.** Log a Warning at every RequiresAttention/Failed return with OperationId, journal subtype and the specific reason (include InvalidDataException.Message); log one Information line for Completed; add a Data.ReconciliationFailed row to the Command Reference operation error-code table that distinguishes Failed (never committed) from parked (needs repair) and says what to check.

#### RV-072 (Important, usability, model-tool, effort S): search_archives schema promises FTS5 query text but the sanitizer strips all operators and turns queries into an AND of exact tokens

`src/RetroDownfall.Arcanum.Infrastructure/Data/FtsMatchQuerySanitizer.cs:25` — related: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.Schemas.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs`

**Claim.** The model-facing schema says query is 'Keywords or FTS5 query text' (Schemas.cs:579; description 'keyword matching' at ArcanumInternalToolServer.cs:1042-1046). Sanitize keeps only letters/digits/underscore/marks, joins tokens with spaces (implicit AND) and double-quotes AND/OR/NOT/NEAR, so OR, phrases and prefix * cannot work, and Entries_fts (default unicode61) has no stemming. A query with no letters or digits sanitizes to empty and GrimoireRepository.SearchArchivesAsync (993-996) answers 'No matching archives found.' without running a search. The no-hit text never says all words must match.

**Failure scenario.** The model asks search_archives for 'what did we decide about the schema migration' or 'schema OR migration' or 'migrat*': each returns 'No matching archives found.' although the transcript says 'We decided on the schema yesterday' / 'The migration runs nightly'. The model concludes the topic was never discussed. 'C++' is searched as 'C'; '???' is reported as no archives. The same AND semantics apply to session list --search, undocumented.

**Evidence (trace).** Real FtsMatchQuerySanitizer.cs compiled stand-alone and run: 'schema OR migration' => [schema "OR" migration]; '"schema migration"' => [schema migration]; 'migrat*' => [migrat]; '???' => []; 'C++' => [C]. System sqlite3 3.54 FTS5: raw 'schema OR migration' matches 2 rows, sanitized form 0; raw 'migrat*' matches 1, sanitized 0; the natural-language sentence matches 0 while 'schema migration' matches 1. Schemas.cs:579 text. Entries_fts.sql has no tokenize option.

**Suggested fix.** Rewrite the schema/description to say: plain keywords, every word must appear, operators/quotes/* unsupported. Return a distinct message when the sanitized query is empty ('no searchable words'), echo the searched words in the no-hit message, and optionally let the sanitizer support quoted phrases, OR and trailing * safely.

### U28: Arcanum.Infrastructure — Data (4)

#### RV-073 (Important, reliability, cancellation, effort M): An unobserved cancellation is resumed as Running instead of cancelled

`src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs:922` — related: `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationReconciler.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationStartupHostedService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Storage/BlobEncryptionLifecycleService.cs`, `tests/RetroDownfall.Arcanum.Tests/Data/LongRunningOperationStoreTests.cs`, `docs/Arcanum.DESIGN.md`, `docs/Arcanum.Command.Reference.md`

**Claim.** AcquireLeaseAsync rewrites an expired Cancelling row to Running for every kind except long-rest-sweep (SET "State" = CASE WHEN "Kind" = @longRest AND "State" = @cancelling THEN @cancelling ELSE @running END). FindExpiredAsync (line 638) discovers expired/unleased Cancelling rows on purpose so the reconciler can settle cancellations nobody observed, but the reconciler (LongRunningOperationReconciler.cs ~229-302) passes the post-acquisition row (State=Running) to the handler, and SettleResultAsync (594-608) only remaps Long Rest. The handler cannot see the cancellation, so resume-capable kinds (blob-encryption-migration / key-rotation, data-retention-prune, legacy factory-reset V0, covenant-index-rebuild) redo and complete the work the operator cancelled. RequestCancellationAsync (1225) also admits ReconciliationRequired rows, so cancelling an attention row turns it into a fresh recovery run of the same handler.

**Failure scenario.** A blob-encryption migration (or retention prune) is running when the host is killed. After restart the operator runs `arcanum operation cancel <id>` on the orphaned row, so the row becomes Cancelling. Within a minute the background recovery pass takes the lease, the row becomes Running, and BlobEncryptionLifecycleService.RecoverAsync re-runs the whole migration, retires the unreferenced keys and records Completed. The operator's cancellation is lost with no trace.

**Evidence (probe).** Probe P1 (passes at ac0d2d8e, scratch worktree): create a blob-encryption-migration row, TryAcquireLeaseAsync by 'dead-host' with an expired lease, RequestCancellationAsync, then LongRunningOperationReconciler.ReconcileAsync with a recording handler. The handler was called once with operation.State == Running, summary.Completed == 1, and the persisted state is Completed. The existing test LongRunningOperationStoreTests.AnUnobservedCancellationIsRecoverableOnceItsLeaseLapses asserts only recovery.Acquired, so it passes whatever state the lease leaves. DESIGN 10.8 line 1788 states the Cancelling->Running rewrite, and Command Reference line 1119 describes it as the reconciler settling the cancellation, which for these kinds is the opposite of what happens.

**Suggested fix.** Keep Cancelling across lease acquisition for every kind by generalising the Long Rest CASE. In SettleResultAsync, settle a row that was Cancelling at discovery or at re-read as Abandoned (e.g. operation.cancelled) unless the handler reports the work already terminal, or give handlers an explicit cancelled entry point. Assert the post-acquisition state in the existing test, and update DESIGN 10.8 and the Command Reference.

#### RV-074 (Important, usability, cli-ergonomics, effort S): arcanum operation list splits operation IDs across lines on terminals narrower than about 140 columns

`src/RetroDownfall.Arcanum.Cli/Commands/OperationCommands.cs:22` — related: `src/RetroDownfall.Arcanum.Cli/UX/ListingTable.cs`, `docs/Arcanum.Command.Reference.md`

**Claim.** OperationCommands.List renders a default Spectre Table with five unconstrained columns (ID, Kind, State, Attempts, PublicSummary). Summaries written by the ledger's callers are long (e.g. 'Applying automatic data-retention plan {guid}.' is 83 chars), the table exceeds the console width, Spectre shrinks the widest columns and character-wraps the GUID. The Command Reference promises 'never wrapped or shortened' IDs for saga list and apprentice list via ListingTable (lines 668, 890); operation list does not use it. This file is outside U28's file list; reported because it is the operator face of this unit's ledger and the entry point of the repair flow that doctor and the Command Reference point to.

**Failure scenario.** arcanum doctor -> arcanum operation list --state ReconciliationRequired on an 80- or 120-column terminal prints the ID as '3f2c9a4e-7b1d-' / '4c58-9e0a-5d6f' / '1a2b3c4d'; the operator cannot paste it into operation show/retry/cancel. Captured --json output (text envelope) is rendered at the default width too.

**Evidence (probe).** Probe P5 (Spectre table built exactly as OperationCommands.List builds it, one realistic row): width 80 -> ID on 3 lines; width 120 -> '3f2c9a4e-7b1d-4c58-9e0a-5d6' / 'f1a2b3c4d'; widths 160 and 200 intact. OperationCommands.cs:25-37.

**Suggested fix.** Reuse ListingTable (drop Kind/Summary columns first and name them on stderr) or mark the ID column no-wrap.

#### RV-075 (Important, usability, error-message, effort M): Connection factories discard why a Grimoire open failed, log nothing, and callers report it as a maintenance window

`src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireOrdinaryConnectionFactory.cs:139` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireMaintenanceConnectionFactory.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeUnavailableException.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs`, `src/RetroDownfall.Arcanum.Api/Health/GrimoireLivenessProbe.cs`, `src/RetroDownfall.Arcanum.Api/Middleware/ArcanumExceptionHandler.cs`, `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs`, `src/RetroDownfall.Arcanum.Api/Tower/SessionDivinationEndpoints.cs`, `src/RetroDownfall.Arcanum.Api/Workspaces/WorkspaceDivinationEndpoints.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`

**Claim.** GrimoireOrdinaryConnectionFactory (lines 107-110, 139-144, 215-218, 257-264, 324-333) and GrimoireMaintenanceConnectionFactory (178-183, 217-227, 258-265) catch Exception without binding or logging it and return one fixed sentence ('The ordinary Grimoire connection could not be opened.' / 'could not be initialized.' / 'native runtime is unavailable.'). The journal factory has no logger at all. SqliteNativeRuntimeUnavailableException was designed with a pasteable message (RID, expected asset filename) and stable code Grimoire.NativeRuntimeUnavailable 'for logs and support'; the factories throw that away. The Result carries Covenant.Unavailable for a real failure and Grimoire.MaintenanceUnavailable only for a closed gate so callers can tell them apart, but seven callers ignore the code and throw GrimoireMaintenanceUnavailableException on any IsFailure.

**Failure scenario.** The Grimoire file is moved or loses permissions, the disk is full, or the key no longer matches while the host runs. GET /api/health reports the Grimoire component Unhealthy with detail 'Grimoire live probe failed: GrimoireMaintenanceUnavailableException.'; endpoints that open through the factory answer 503 'The Grimoire is temporarily unavailable while maintenance owns connection admission.'; the only log line is Debug 'A request was refused because Grimoire maintenance owns connection admission.' with no exception. arcanum doctor and the logs both tell the operator to wait out a maintenance window that does not exist, and no log at any level holds the cause.

**Evidence (probe).** Probe P1 (production factory, isolated ARCANUM_TEST_HOME, capturing logger): file absent -> 'IsFailure=True Code=Covenant.Unavailable Message=The ordinary Grimoire connection could not be opened.' log entries: 0; 4 KiB zero-filled file with a passphrase (corrupt/key-mismatch stand-in) -> identical message, log entries: 0. Probe P1c (real GrimoireLivenessProbe over a factory that refuses with that error): 'ok=False detail=Grimoire live probe failed: GrimoireMaintenanceUnavailableException.' Code: GrimoireOrdinaryConnectionFactory.cs:139-144 'catch (Exception) { await TerminalizeOpenFailureAsync(...); return Refused("The ordinary Grimoire connection could not be opened."); }'; GrimoireLivenessProbe.cs:107-110 and :145; ArcanumExceptionHandler.cs:71 (LogDebug, no exception); GrimoireMaintenanceRefusal.cs:36-37. Same throw-on-any-failure pattern at WorkspaceDivinationEndpoints.cs:255-258, SessionDivinationEndpoints.cs:236-239, MemoryEndpoints.cs:1752-1755, WizardIntelligenceProvider.cs:6870-6872, CovenantCampaignScopeProbe.cs:99-101, ICovenantConnectionSource.cs:157-160, LongRunningOperationStore.cs:1074-1077. HTTP 503 mapping traced, not driven end to end.

**Suggested fix.** Bind the exception in each catch and log it at Warning with kind/mode/purpose (never the connection string); put the SQLite error code/type in the Error message (and SqliteNativeRuntimeUnavailableException.Message + ErrorCode for the runtime branch); give GrimoireMaintenanceConnectionFactory an ILogger. At the seven call sites throw the maintenance exception only when acquired.Error.Code == ErrorCodes.Grimoire.MaintenanceUnavailable; otherwise surface acquired.Error (503 Covenant.Unavailable, probe detail = the error message).

#### RV-076 (Important, usability, docs-mismatch, effort M): Retry/cancel of an owner-bound retention operation is refused with contradictory text and no surface names the real remedy

`src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs:1215` — related: `src/RetroDownfall.Arcanum.Api/Operations/LongRunningOperationEndpoints.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationReconciler.cs`, `src/RetroDownfall.Arcanum.Core/Operations/LongRunningOperationRecoveryRegistry.cs`, `docs/Arcanum.Command.Reference.md`, `docs/Arcanum.API.md`, `tests/RetroDownfall.Arcanum.Tests/Api/LongRunningOperationEndpointTests.cs`

**Claim.** RequestCancellationAsync (1215-1245) excludes current data-retention-mutation V4 / data-retention-factory-reset V2 rows inside its SQL and returns a bare bool; the endpoint then answers 409 Operation.StateConflict 'The operation cannot be cancelled in its current state.' For retry the endpoint classifies the row as owner-bound and answers 409 'Only Failed, Abandoned, or ReconciliationRequired operations can be retried.' although the row IS ReconciliationRequired. Other surfaces disagree: Command Reference row for Covenant.ErasureIncomplete / Covenant.MaintenanceFailed (line 1144) says 'retry or reconcile the same operation'; operation reconcile skips these rows (LongRunningOperationReconciler.cs:204-210, metric awaiting_exact_owner) and prints only a skipped count; the registry ManualRepairGuidance says 'Re-run the factory reset' / 'Inspect arcanum data retention show' but a new start is refused while the ReconciliationRequired row exists (single flight counts it, LongRunningOperationStore.cs:346-358; DescribeRetentionConflictAsync names it). The only path that adopts them is the authenticated owner at host start (AdoptUnderInstallationLockAsync 793-817; StartupRecoveredAttention 715-722), and no operator-readable text says so. The Command Reference terminal-code table has no row for Data.ReconciliationFailed, the code carried by three of the five StartupRecoveredAttention pairs.

**Failure scenario.** A Covenant reset ends ReconciliationRequired / Covenant.MaintenanceFailed. The operator follows the documented runbook: operation list --state ReconciliationRequired, then operation retry <id> (409, says ReconciliationRequired can be retried), operation cancel <id> (409, no reason), operation reconcile (summary 'skipped'), data ... --apply (conflict naming the same row). Every route appears broken and none says the host re-adopts the row at its next start.

**Evidence (trace).** Existing tests pin both strings with a ReconciliationRequired owner-bound row: LongRunningOperationEndpointTests.cs:171 and :240 (4 theory cases, run --no-build, pass). Predicate at LongRunningOperationStore.cs:1226-1228 'AND NOT (("Kind" = @retentionMutation AND "CheckpointVersion" = @mutationOwnerVersion) OR ...)'. Endpoint texts LongRunningOperationEndpoints.cs:94-103, 133-157. API.md:256-257 documents the 409 but gives no operator remedy. grep ReconciliationFailed docs/Arcanum.Command.Reference.md returns nothing. I did not start a host to drive the restart path; the remedy is read from the adoption code.

**Suggested fix.** Classify before the compare-and-swap for both verbs and give the owner-bound case its own message naming reason and action (e.g. owned by an authenticated offline transition, resumed by the host when it next starts; restart the host and re-check operation show). Have reconcile report owner-bound rows as awaiting host restart instead of folding them into skipped. Add Data.ReconciliationFailed and the owner-bound exception to the Command Reference table and make the Covenant.MaintenanceFailed row and registry guidance say the same.

### U30: Arcanum.Infrastructure — Data/Covenant (1)

#### RV-077 (Important, usability, error-message, effort M): One oversized Entry wedges Campaign continuity for the whole Campaign, with an unmapped code and no identifier

`src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignRollupStore.Read.cs:64` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupMaintenance.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Hosting/Loremaster.cs`, `src/RetroDownfall.Arcanum.Api/Primitives/ArcanumErrorMapper.cs`, `docs/Compendium.README.md`, `docs/Arcanum.DESIGN.md`

**Claim.** PrepareContributionWithinAsync budgets a page as 32768 minus the previous summary bytes and, when the first unsummarized Entry does not fit, throws a bare-string error code 'campaign_rollup.input_too_large' ('A native Entry exceeds the bounded Campaign contribution page.'). There is no skip, split or truncate path and the cursor cannot move past the Entry, so every later attempt fails identically. The code is not in ErrorCodes or ArcanumErrorMapper (ResolveStatusCode returns 500), and the message names no Entry, Session, Campaign, size or limit. The background Loremaster logs the failure at Debug without the code. Docs (DESIGN 466-467, Compendium features.campaignRollups) say the bounds do not cap Campaign lifetime and never mention that one large Entry stops the Campaign.

**Failure scenario.** With features.campaignRollups on, a user pastes a 30 KB log into a Campaign-bound Session whose Campaign already has a full 8 KB summary (limit then 24,576 bytes; 32,768 with no summary; default MaxEntryContentBytes is 1 MiB so the host accepts it). The next turn, and every turn of every other Session in that Campaign, fails with 500 'campaign_rollup.input_too_large: A native Entry exceeds the bounded Campaign contribution page.' because the turn lane returns the maintenance error (CampaignRollupTurnPreparer.cs:628-631, 643-646), ProcessPagesAsync returns the prepare failure unchanged (CampaignRollupMaintenance.cs:216-220) and ProcessCampaignAsync aborts the whole Campaign fold on the first failing Session (:138-140). Nothing at Information or above names the Session, and the operator cannot find the Entry to delete it.

**Evidence (probe).** CampaignRollupStore.Read.cs:50 'int available = InputPageUtf8Bytes - bytes(previous.Content)'; :60-65 'if (metadata.ContentBytes > available) { if (entries.Count == 0) throw Refuse(new Error("campaign_rollup.input_too_large", ...))'. Probe P1 (scratch worktree, existing StoreFixture, output in reports/C/U30-probes/output.txt): no prior summary: 24000/24577/32768-byte Entry ok, 32769 fails with code=campaign_rollup.input_too_large; with an 8192-byte prior summary: 24576 ok, 24577 and 30000 fail, and a second attempt fails again (ok=False); 'P1 status mapping of campaign_rollup.input_too_large = 500'. Loremaster.cs:271 and :283 LogDebug('... remains pending ...') drop contribution.Error; LoggingBootstrapper.cs:45 MinimumLevel.Information(). Existing test CampaignRollupStoreTests.cs:229-243 pins only a 34,000-byte Entry with no prior summary. The end-to-end turn failure was established by tracing the call chain, not by running CampaignRollupMaintenance.

**Suggested fix.** Make an unsummarizable Entry non-fatal: record it as consumed-but-skipped (or split it across pages) so the cursor advances, and let ProcessCampaignAsync skip a failing Session instead of aborting the fold. Add a typed ErrorCodes constant mapped in ArcanumErrorMapper with Session id, Entry id/sequence, byte size and limit in the message (all content-free). Log the failure at Warning once per Session and code from Loremaster. Document the limit next to the 32 KiB sentence in Compendium.README and DESIGN.

#### RV-078 (Important, usability, error-message, effort M): Erasure inventory/preflight refusals name no label, kind or cause, and a transient fault reads as corruption

`src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureInventorySource.cs:41` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs`, `docs/Arcanum.API.md`, `docs/Arcanum.Command.Reference.md`

**Claim.** CovenantErasureInventorySource returns static errors for everything: UnsafeInventory (Covenant.IntegrityFailure, 'The Covenant erasure inventory could not be proved from one bounded storage snapshot.') at 17 sites, and ManualManagedErasure (Covenant.ManualArtifactErasureRequired, 'A managed artifact does not have one exact adopted ownership record and requires manual erasure.') at lines 217, 316 and 483. The snapshot helpers end in bare 'catch { return UnsafeInventory; }' (lines 737-741, 821-825), so a locked file, full disk, missing table, unknown artifact kind (purge-rule Resolve failure) and a failed dispose give the same sentence. ArtifactSensitivityLedger.Materialize adds 'This artifact's sensitivity label is malformed and cannot authorize any read.' with no label id, and ReadPageWithinAsync hands it straight up the preflight. Docs promise otherwise: API.md:919 'Each names an operator action rather than a retry'; Command Reference 1144 'Repair the named content-free blocker, then retry'. No blocker is named. The coordinator logs only '{ErrorCode}' (not the message) when the abort reopens admission.

**Failure scenario.** 'arcanum memory covenant reset' (or the factory erasure) is refused because one managed file lost its ownership record. The operator gets Covenant.ManualArtifactErasureRequired 'a managed artifact ... requires manual erasure': it does not say which artifact (the label already carries kind and artifact id, both content-free GUIDs) or how to erase it by hand, and no list of affected labels exists in any surface this unit feeds. A merely busy file gives 'could not be proved from one bounded storage snapshot', which reads as corruption and gives no retry hint.

**Evidence (trace).** CovenantErasureInventorySource.cs:41-51 (static errors), :93,110,193,229,298,355,390,429,464,677,684,711,740,778,794,824,837 (UnsafeInventory), :217,316,483 (ManualManagedErasure), :737-741 and :821-825 (bare catch). ArtifactSensitivityLedger.cs:400-408 (Materialize) and :353-366 (page loop returns materialized.Error). CovenantErasureCoordinator.cs:2228-2247 (preflight abort) and :2918-2922 (Warning carries only {ErrorCode}). Docs quoted from API.md:919 and Command.Reference.md:1144. Read and traced; the managed-file arm was not executed (CovenantManagedFileErasureRequestReader lives in the other Data/Covenant units).

**Suggested fix.** Replace UnsafeInventory with a small closed set (StorageBusy, StorageFault, UnknownArtifactKind, SnapshotMismatch); put LabelId, ArtifactKind and the blocker class in ManualManagedErasure and in Materialize's error (from the reader's ordinal 0 when it parses); classify SqliteException with the existing StorageBlocker rule so a busy file says 'retry'; log one Warning per refusal with the operation id and the label id.

#### RV-079 (Important, usability, diagnostics, effort M): Refused startup adoption of the Covenant erasure owner reports one sentence for every cause, logs nothing, and points the operator at logs

`src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureStartupRecoveryOwnerAdopter.cs:333` — related: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`, `docs/Arcanum.Command.Reference.md`, `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantErasureStartupRecoveryOwnerAdopterTests.cs`

**Claim.** The adopter has nine 'return Refusal()' sites (lines 174, 192, 196, 208, 227, 251, 265, 285, 320) plus a catch-all that turns any Exception, including SqliteException, into the same fixed Covenant.ManualRecoveryRequired 'Durable Covenant erasure ownership could not be reconstructed safely.' The caller GrimoireDatabaseBootstrapper discards that Error and throws 'Protected Covenant startup recovery could not prove one durable owner. See logs for recovery steps.' Neither the adopter (no logger) nor the call site logs anything, so no recovery steps exist in any log. The operation id (a canonical GUID by then) is not reported, and no document describes this startup refusal. The documented remedy (Command Reference 1143: inspect 'arcanum operation show <id>') needs a running host and an id the message never gave.

**Failure scenario.** A power cut during 'arcanum memory covenant reset' leaves a malformed checkpoint, or the read hits a storage fault. 'arcanum serve' exits at startup with the one sentence above. The operator cannot tell a damaged row from two owners from a locked or missing table, cannot tell which operation, has no log to read, and cannot run 'operation show' because the host will not start.

**Evidence (probe).** CovenantErasureStartupRecoveryOwnerAdopter.cs:206-209 'catch (Exception) { return Refusal(); }', :333-337 'Refusal() => ... ManualRecoveryRequired, "Durable Covenant erasure ownership could not be reconstructed safely."'. GrimoireDatabaseBootstrapper.cs:1197-1204 'if (erasureOwner.IsFailure) { throw ProtectedRecoveryUnavailable(); }' (no Log call), :1340-1343 message. Probe P3 (existing adopter fixture, savepoint per corruption): payload NULL, State=99, RecoveryPolicy=99, CheckpointVersion=99, upper-cased CheckpointReference, a renamed LongRunningOperations table (storage fault) and a second valid owner ALL return code=Covenant.ManualRecoveryRequired with the identical message. 'rg "durable owner|could not prove" docs' finds no operator guidance. The adopter tests' comment explains the content-free intent (do not echo rejected bytes); a closed reason code and a validated operation id do not echo anything a corrupt row chose.

**Suggested fix.** Give Refusal a closed reason enum (MalformedRow, PolicyMismatch, UnsupportedVersion, UnreadableCheckpoint, WrongOperation, SecondOwner, GateRefused, StorageFault), carry the operation id when the row id parsed as a canonical GUID, log one Warning from the bootstrapper before throwing ('{OperationId} {Reason}'), and document the startup remedy (what to inspect without a host and what is safe to do) in the Command Reference Covenant.ManualRecoveryRequired row.

### U01: Arcanum.Api — Intelligence (1)

#### RV-080 (Minor, reliability, data-integrity, effort S): An unexpected batch failure after every line of a page has started leaves no resume note and silently drops all later pages

`src/RetroDownfall.Arcanum.Api/Intelligence/BatchProcessingService.cs:874` — related: `docs/Arcanum.DESIGN.md`

**Claim.** TryStopAfterUnexpectedFailureAsync writes the resume note only for the first page line that never started, or for lastFinishedLine+1 when no page was prepared. When every line of the failing page has a checkpoint, firstUnstarted is null and nothing is written. The batch is published as failed, its counters cover only the claimed lines, and no error file exists, so the rest of the input is never read and nothing says so. Any failure of the cancellation watcher (WatchForCancellationAsync catches only OperationCanceledException) always lands in this case, because StopAndJoinCancellationWatcherAsync raises it only after Parallel.ForEachAsync has finished every line.

**Failure scenario.** A 100-line batch at concurrency 1. A host fault, for example a SQLite disk-full or I/O error in TryBeginLineAsync or CompleteLineAsync, or a provider exception, on line 64 (the last line of the first page). The batch ends failed with total=64, completed=63, failed=1 and no error file. Lines 65-100 were never attempted. To the operator this looks like a 64-line batch whose last line failed, although DESIGN §11.21 promises a note telling the operator where to resubmit from.

**Evidence (probe).** Probe P5 (scratch worktree, real BatchProcessingService over the SQLCipher fixture). The provider throws on call 64. Output: 'P5: status=failed total=64 completed=63 failed=1 calls=64 errorFile=none'; assertions passed. Code: lines 872-894 handle only the cases firstUnstarted != null and page == null.

**Suggested fix.** When firstUnstarted is null, write the note on page[^1].Line + 1 (worded 'at or after line N'), or record a resume line on the batch row. Add a test with a failure on the last line of a page, and one with a watcher fault.

#### RV-081 (Minor, reliability, crash-consistency, effort S): A non-cryptographic read failure of one stranded batch input aborts host startup

`src/RetroDownfall.Arcanum.Api/Intelligence/BatchRecoveryService.cs:305` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/BatchProcessingService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Hosting/InstallationResetRecoveryAwareHostedService.cs`

**Claim.** InputExistsAsync reads the entire input to authenticate it, but catches only CryptographicException and InvalidDataException. An IOException or UnauthorizedAccessException escapes ReconcileStrandedAsync. Nothing above catches it: BatchProcessingService.StartAsync, then InstallationResetRecoveryAwareHostedService.StartAsync, then Generic Host start. One stranded batch with an unreadable input therefore stops the host from starting, and /v1/batches/{id}/reset returns a 500 for the same input.

**Failure scenario.** The host crashes with a batch in_progress. On Windows, an antivirus or backup scanner holds the input file under a share lock at the next start, or the file's permissions changed, or the disk returns EIO. arcanum serve fails in StartAsync with IOException, and keeps failing on every start until the file is repaired by hand.

**Evidence (probe).** Probe P3: the real BatchRecoveryService with the existing FailingInputBlobStore (IOException at end of stream) over one in_progress batch. Output: 'P3: thrown=System.IO.IOException'; the batch stays in_progress; test passed. Trace: BatchProcessingService.StartAsync:75 awaits ReconcileStrandedAsync with no catch, and InstallationResetRecoveryAwareHostedService.StartAsync does not catch either.

**Suggested fix.** Catch per batch inside ReconcileStrandedAsync's loop: log, then leave the batch in_progress for /reset or fail it with an 'input unreadable' reason, and continue. In InputExistsAsync, treat IOException and UnauthorizedAccessException as unavailable so /reset answers InputMissing.

#### RV-082 (Minor, reliability, leak, effort S): A Campaign preparation that completes after its turn scope is disposed is never disposed, leaking the claim and the Covenant turn lease

`src/RetroDownfall.Arcanum.Api/Intelligence/CovenantDispatchGate.cs:207` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantOperationGate.cs`

**Claim.** CovenantTurnScope.DisposeAsync disposes the CampaignRollupTurnSnapshot only if CampaignPreparation.IsCompletedSuccessfully at that moment. The streaming path deliberately yields 'Finishing protected memory maintenance...' while preparation is still running (WizardIntelligenceProvider.cs:1819-1832). If the consumer disposes the enumerator at that yield and the preparation later succeeds, PrepareLiveAsync has already transferred ownership (transferred = true). Nothing disposes the snapshot after that.

**Failure scenario.** A streaming Campaign turn with protected Long Rest maintenance. The enumerator is disposed during the maintenance status while the preparation's token is not cancelled, or the cancellation arrives after its last observed await, and the preparation then succeeds. The executable claim is never completed, so the Session answers Hub.SessionTurnBusy until the lease deadline. The CovenantTurnLease is never released, so a Covenant reset, Campaign delete or path mutation over that Campaign times out after 30 s with Covenant.MaintenanceFailed (CovenantOperationGate.DrainAsync) until the host restarts.

**Evidence (probe).** Probe P4. Control: a scope whose preparation had already completed, once disposed, completes the claim (count 1). Probe: a scope disposed while its TaskCompletionSource preparation was pending, then completed with a snapshot; after 250 ms the claim completion count is still 1, so the second snapshot was never disposed. Test passed. The race in the real streaming path is traced, not executed end to end.

**Suggested fix.** When CampaignPreparation is not complete at disposal, attach an observed continuation that disposes the snapshot if the task succeeds, or await it under a bounded wait.

#### RV-083 (Minor, reliability, api-contract, effort S): CovenantProviderCallFreezer.TryFreeze throws instead of returning a typed refusal when a message part cannot be enveloped

`src/RetroDownfall.Arcanum.Api/Intelligence/CovenantProviderCallFreezer.cs:73` — related: `src/RetroDownfall.Arcanum.Core/Covenant/ProviderCallEnvelope.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`

**Claim.** The type's contract is that a part it cannot freeze 'produces a refusal'. But FreezeMessages, and FreezeTools' envelope construction, run outside TryFreeze's ArgumentException catch, which wraps only the final ProviderCallEnvelope constructor. ProviderContentPartEnvelope validates through RequireText/StrictUtf8 and throws ArgumentException for an empty tool-call id or name, an empty media type, an empty message id, or invalid UTF-16. That exception escapes, so a Covenant-derived dispatch fails as an unhandled exception rather than a Covenant.InvalidContent refusal.

**Failure scenario.** A stateless /v1/chat/completions transcript, or a provider continuation, carries an assistant tool call with id "" on a turn that admits Covenant content. AcknowledgeCovenantDispatchAsync throws ArgumentException out of TryFreeze, and the turn ends with a generic inference error that has only the exception type in the log, instead of the typed refusal.

**Evidence (probe).** Probe P6: TryFreeze with an unpaired surrogate in a FunctionResultContent, and in a text part, throws ArgumentException (EncoderFallbackException); passed. Probe P6b: System.Text.Json refuses the escaped surrogate '\ud83d' (InvalidOperationException), so the surrogate case is not reachable from JSON input. P6 shows only that the escape path exists. The empty-id trigger is by trace: ProviderCallEnvelope.cs:125-133 RequireText(toolCallId) throws for empty values.

**Suggested fix.** Move FreezeMessages and FreezeTools inside the existing try, or catch ArgumentException in FreezePart, FreezeToolCall and FreezeToolResult and return Covenant.InvalidContent.

#### RV-084 (Minor, reliability, error-message, effort S): The embeddings-reset route reports Grimoire maintenance or any other InvalidOperationException as 'manual artifact erasure required'

`src/RetroDownfall.Arcanum.Api/Intelligence/EmbeddingsResetEndpoints.cs:68` — related: `src/RetroDownfall.Arcanum.Infrastructure/Weave/EmbeddingsResetService.cs`, `src/RetroDownfall.Arcanum.Api/Middleware/ArcanumExceptionHandler.cs`

**Claim.** The catch clause 'catch (InvalidOperationException blocked) when (blocked is not LabeledArtifactRefusalException)' targets the service's blocked-purge signal, which is a plain InvalidOperationException (EmbeddingsResetService.cs:438). It also catches GrimoireMaintenanceUnavailableException, GrimoireDatabaseUnavailableException, 'The Grimoire requires a SQLCipher connection' and any EF Core or Sqlite InvalidOperationException. All of these are answered as Covenant.ManualArtifactErasureRequired, with the raw exception message in the body. Without the filter, ArcanumExceptionHandler would have answered the maintenance case with the maintenance refusal.

**Failure scenario.** An embeddings reset runs while a backup or rekey has Grimoire admission closed, and the enrolment interceptor throws GrimoireMaintenanceUnavailableException on open. The operator is told a protected artifact must be erased by hand, instead of being told the Grimoire is in maintenance and to retry.

**Evidence (probe).** Probe P7: 'new GrimoireMaintenanceUnavailableException() is InvalidOperationException and not LabeledArtifactRefusalException' is true (passed). GrimoireDatabaseUnavailableException also derives from InvalidOperationException (Infrastructure/Hosting/GrimoireDatabaseUnavailableException.cs:12). ArcanumExceptionHandler.cs:61 maps the maintenance exception only when it reaches the handler.

**Suggested fix.** Throw a dedicated exception type for the blocked purge from EmbeddingsResetService, and catch only that type in the route.

#### RV-085 (Minor, reliability, exception-handling, effort S): On the protected tool-interaction path, an event-hub publication failure after a successful commit fails the turn

`src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:415` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`

**Claim.** After AppendProtectedToolInteractionAsync commits the call/result pair, the two PublishSavedEntryByIdAsync calls (415, 417) have no try/catch. Any exception from GetEntryByIdAsync or the hub propagates through PersistToolInteractionAsync (WizardIntelligenceProvider.cs:5250) and ends the turn. The unprotected path (435-455) and the begin path (984-994: 'Publication is best-effort on purpose: the rows are already committed') both treat publication as best-effort.

**Failure scenario.** On a Covenant-derived turn, a transient read failure right after the protected tool pair commits fails the turn with a generic error. The Session keeps the committed tool pair with no assistant continuation.

**Evidence (trace).** Code trace: lines 405-420 against 422-470 and 971-994.

**Suggested fix.** Wrap both publications in the same best-effort try/catch used elsewhere, rethrowing only OperationCanceledException.

#### RV-086 (Minor, reliability, crash-consistency, effort M): A committed claimed reply is reported as not saved when settling its claim fails, and the retry is then refused

`src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:1288` — related: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.TurnCommit.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs`

**Claim.** For a claimed turn, TryCommitProtectedAsync commits the reply, and only then does CompleteClaimAsync(committed: true) settle the claim, as a separate write. The interruption path settles inside the commit transaction (GrimoireRepository.TurnCommit.cs:715-745); the committed path does not. If the second write fails, the method returns false after setting IsFinalized = true. The caller emits PublicFinalizeFailureMessage although the reply is durably stored. The claim stays Begun, so a retry with the same idempotency key is refused at CampaignRollupTurnPreparer.cs:385-388.

**Failure scenario.** A transient SQLite busy or I/O error, or an admission close, between the commit and claims.CompleteAsync. The user is told 'The conversation reply could not be saved. Please try again.' The reply is visible in the Session, and the retry fails with 'This turn already began and has no replayable finalization'.

**Evidence (trace).** Code trace: lines 1243-1291, and CompleteClaimAsync 1157-1186, which returns false on failure. Not executed, because it needs a fault injected between two repository writes.

**Suggested fix.** Settle the Committed claim outcome inside CommitTurnAsync's transaction, as the interruption path already does. Otherwise return success, log the failure, and leave the claim to restart recovery.

#### RV-087 (Minor, usability, diagnostics, effort S): A batch whose input file is missing at processing time fails with no note and no log

`src/RetroDownfall.Arcanum.Api/Intelligence/BatchProcessingService.cs:376` — related: `docs/Arcanum.API.md`

**Claim.** When the UploadedFiles row or the file is gone, the batch is set to failed and the method returns: no error-file note, no log entry. API.md:285 says the batch object has no reason field and the reason is the error-file note plus the operator log. The service also logs nothing at Information when a batch starts, completes or is cancelled.

**Failure scenario.** An operator deletes or loses the input file while a batch is queued; the batch shows failed with no output_file_id, error_file_id or log line, so no source explains it.

**Evidence (probe).** BatchProcessingService.cs:376-383. Probe P2: status=failed, OutputFileId null, ErrorFileId null, captured logger entries = 0. grep of logger.Log* in the file: no lifecycle logging.

**Suggested fix.** Write the line-1 note as the unexpected-failure path does ('input file is missing or unreadable') and log Warning with batch and input file ids.

#### RV-088 (Minor, usability, error-message, effort S): Operator-cancelled batch lines are sealed with 'The host stopped after this request...'

`src/RetroDownfall.Arcanum.Api/Intelligence/BatchProcessingService.cs:930`

**Claim.** The cancellation paths (ProcessRequestPageAsync lines 576-580 and 628-633) call CompleteDispatchedLinesAsync with no detail, so CompleteInterruptedLineAsync uses its host-stop default text for a batch the operator cancelled.

**Failure scenario.** After POST /v1/batches/{id}/cancel, the client's output file says the host stopped, so the user investigates a crash or restart that never happened.

**Evidence (probe).** Probe P1 (copy of the existing CancelDuringProviderCall test): output line is {"code":"batch_interrupted_after_dispatch","message":"The host stopped after this request was durably marked for dispatch. Arcanum did not replay it..."}. Existing tests assert only the code.

**Suggested fix.** Pass a cancel-specific detail from both cancel sites ('The batch was cancelled after this request was dispatched; ... submit this line again explicitly if another attempt is desired').

#### RV-089 (Minor, usability, error-message, effort S): Budget.Exceeded message does not say the day is UTC, when it resets, or which key to raise

`src/RetroDownfall.Arcanum.Api/Intelligence/BudgetMonitor.cs:73` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs`

**Claim.** The refusal reads 'Daily budget limit of $X USD has been reached (current spend: $Y USD).' API.md:918 documents the UTC day and Arcanum:Cost:Budget:DailyLimitUsd, but the 429 body the operator sees (no Retry-After) names neither. BudgetReservationService.cs:144,282,393 use the same text.

**Failure scenario.** Operator west of UTC is refused mid-afternoon with a message that looks like a local-day cap, and does not know whether to wait, for how long, or which setting to raise.

**Evidence (trace).** BudgetMonitor.cs:71-74; DailySpendAuthority.cs doc ('UTC day'); API.md:918; Compendium.README.md:273.

**Suggested fix.** Append 'The budget day is UTC and resets at 00:00 UTC; raise Arcanum:Cost:Budget:DailyLimitUsd to continue.' in all four sites (shared constant).

#### RV-090 (Minor, usability, error-message, effort S): Turn error for deferred protected Long Rest maintenance discards the reason code and operation id it holds

`src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs:585` — related: `docs/Arcanum.API.md`

**Claim.** When resumed.Value.Phase is not Completed or DeferredReason is not null, the turn ends with LongRest.Unavailable 'Long Rest protected maintenance remains deferred.' although DeferredReason (a code such as LongRest.ProviderUnreachable or long_rest.authorized_request_required) and operation.Value.Id are in scope.

**Failure scenario.** A protected Campaign turn fails with a fixed sentence; the operator cannot tell a transient provider outage (wait) from a parked sweep that needs `arcanum operation retry <id>` (API.md:1384), and has no operation id to pass.

**Evidence (trace).** CampaignRollupTurnPreparer.cs:585-587. API.md:919 says the Covenant 503s 'name an operator action rather than a retry'; this one names none. Code trace (execution of the real sweep coordinator not feasible).

**Suggested fix.** Include the DeferredReason code and operation id in the message, e.g. 'Long Rest maintenance is deferred (LongRest.ProviderUnreachable, operation <id>); see `arcanum operation list`'.

#### RV-091 (Minor, usability, api-contract, effort S): Native ping client-tool errors name tools[i] (field is clientTools), and clientTools without forwardClientTools is silently ignored

`src/RetroDownfall.Arcanum.Api/Intelligence/ClientToolCapabilityValidator.cs:131`

**Claim.** ValidateToolDefinitions builds messages with the /v1 field name `tools[index]` and is shared with the native route, whose field is `clientTools`. ValidateRequest returns success when ForwardClientTools is false, so supplied clientTools/clientToolChoice are dropped, whereas /v1 returns 400 unsupported_parameter for the equivalent mistake.

**Failure scenario.** A native client gets 'tools[0].function.name is required.' for a body that has no `tools`; another client sends clientTools but forgets forwardClientTools=true, receives plain text answers with no tool calls and no error.

**Evidence (probe).** Probe P5a: ValidateRequest on ClientTools=[{type:function}] with ForwardClientTools=true => ClientTools.InvalidSchema 'tools[0].function.name is required.'. Probe P5b: ClientTools set, ForwardClientTools=false => success. OpenAiChatCompletionRequestValidator.cs:155-160 rejects the /v1 equivalent.

**Suggested fix.** Pass a field prefix ('clientTools' vs 'tools') into ValidateToolDefinitions; on the native route return ClientTools.InvalidSchema when clientTools/clientToolChoice are present and forwardClientTools is false.

#### RV-092 (Minor, usability, cli-ergonomics, effort S): Compaction no-ops are indistinguishable from an empty session and give no reason

`src/RetroDownfall.Arcanum.Api/Intelligence/ContextCompressionService.cs:195` — related: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/SessionCommands.cs`, `src/RetroDownfall.Arcanum.Core/Tower/SessionDtos.cs`, `docs/Arcanum.Command.Reference.md`

**Claim.** Compression disabled or fewer than CompressionPreflightMinMessages (default 6) returns CompactResult(0,0,0), which `arcanum session compact` prints as 'Compacted session X: 0 -> 0 tokens; 0 entries removed.' When every entry is pinned and the session is over the limit, the result is N -> N with no stop code and no log (the warning needs removed > 0).

**Failure scenario.** Operator runs `session compact` on a long session whose entries are pinned (or on a short one) and sees 'Compacted ... 0 entries removed' with no hint that pinned entries, the minimum-message rule, or a disabled feature is why the context stays over budget.

**Evidence (probe).** ContextCompressionService.cs:195-208, 232, 344. SessionCommands.cs:558. Probe P11a: 3-entry session => tokensBefore=0 tokensAfter=0 removed=0 stoppedBy=null. Probe P11b: 8 pinned over-limit entries => 36846 -> 36846, removed=0, stoppedBy=null.

**Suggested fix.** Add a reason (NotNeeded, Disabled, TooFewMessages, OnlyPinnedRemain) to CompactResult, print it in the CLI, document it in the Command Reference compact paragraph.

#### RV-093 (Minor, usability, api-contract, effort S): POST /api/embeddings/reset?scope= (empty value) resets every embedding table

`src/RetroDownfall.Arcanum.Api/Intelligence/EmbeddingsResetEndpoints.cs:107`

**Claim.** ParseScope uses string.IsNullOrEmpty, so an empty but present scope parameter takes the documented omitted-parameter default `all`.

**Failure scenario.** A script calls `...reset?scope=$SCOPE&confirm=true` with SCOPE unset; instead of a 400 it truncates all entry, workspace, Saga, attachment and Tapestry embeddings, which then need a paid re-index.

**Evidence (probe).** EmbeddingsResetEndpoints.cs:107-111. Probe P6: ParseScope("") => All, ParseScope(null) => All, ParseScope("workspace") => null (400). API.md:80 documents default `all` only for an omitted parameter.

**Suggested fix.** Return All only for null; treat empty/whitespace as Validation.InvalidBody with the allowed-scope list.

#### RV-094 (Minor, usability, diagnostics, effort S): Turn-writer and Covenant-gate warnings omit the Session/turn id and the failure code

`src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:918` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/CovenantDispatchGate.cs`

**Claim.** The begin-failure warning (lines 918 and 943) logs only the model name; the Error returned for a deleted Campaign, missing Session or binding mismatch is not logged. The finalize failure (line 1335), tool-interaction failures (450, 467) and claim settle failure (1175) carry no Session id either. CovenantDispatchGate.cs:344 and :682 log 'turn is refused'/'treated as tainted' without the logical turn id or Session id, although its Debug record at :378 carries both.

**Failure scenario.** A client reports 'The conversation reply could not be saved'; the host log shows 'Grimoire could not finalize assistant entry for model X' with no Session or entry id, so the operator cannot find which Session or correlate it with the request.

**Evidence (trace).** GrimoireTurnWriter.cs:918, 943, 1335, 467, 1175; CovenantDispatchGate.cs:344, 682 vs 378.

**Suggested fix.** Add {SessionId}, {AssistantEntryId}/{LogicalTurnId} and the Error code to these messages.

#### RV-095 (Minor, usability, api-contract, effort S): Unparseable image_url parts pass Scrying validation and are silently dropped, so the model answers without the image

`src/RetroDownfall.Arcanum.Api/Intelligence/InferenceContextBuilder.cs:604` — related: `src/RetroDownfall.Arcanum.Core/Intelligence/ScryingValidator.cs`

**Claim.** ScryingValidator.ValidateRequestImages skips data URIs without ';base64' and non-data URLs, 'leaving them to the provider/mapper to reject'; the mapper (TryBuildImageContent) returns null for non-absolute URLs and non-base64 data URIs and the part is dropped. The request returns 200.

**Failure scenario.** A /v1 client sends image_url.url = 'cat.png' or 'data:image/png,...' (no base64). Validation passes, the image count is spent, the model sees only the text, and answers as if no image was attached; a message whose only part is the image becomes an empty text message.

**Evidence (probe).** Probe P4: urls cat.png, images/dog.jpg, data:image/png,rawbytes, data:image/png;charset=utf-8,abc => ScryingValidator.ValidateRequestImages success and 0 DataContent/UriContent reach the model. InferenceContextBuilder.cs:536-551, 604-620; ScryingValidator.cs:147-155.

**Suggested fix.** Reject in ValidateRequestImages with Scrying.InvalidImageData naming messages[i].content[j].image_url.url (require absolute http(s) or data:...;base64).

#### RV-096 (Minor, usability, error-message, effort S): Provider failure text points the operator at the API key for rate limits and server errors

`src/RetroDownfall.Arcanum.Api/Intelligence/InferenceProviderFailureMessage.cs:15`

**Claim.** Every HTTP status gets 'returned HTTP N. Check the model, API key, and request; see server logs for detail.' ProviderFailureClassifier deliberately does not fall back on 429/5xx, so this sentence is the only text the client sees.

**Failure scenario.** A local or hosted provider answers 429 or 503; the operator re-checks the model name and rotates the key instead of waiting or retrying.

**Evidence (probe).** Probe P7: Build("ollama", 400/401/429/500/503) all end with the identical 'Check the model, API key, and request' sentence. Only caller: WizardIntelligenceProvider.cs:8802.

**Suggested fix.** Vary the hint by status class: 401/403 key, 404/400 model/request, 429 rate limit (retry later), 5xx provider-side error.

### U02: Arcanum.Api — Intelligence (2)

#### RV-097 (Minor, reliability, error-message, effort M): A pre-send refusal in Long Rest loses its code and parks the sweep as needs-operator

`src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.LongRest.cs:102` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/LongRestProvider.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/LongRestMaintenance.Dispatch.cs`, `src/RetroDownfall.Arcanum.Core/LongRest/LongRestFaultPolicy.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRest/LongRestSweepCoordinator.Run.cs`, `tests/RetroDownfall.Arcanum.Tests/LongRest/LongRestProviderDispatchTests.cs`

**Claim.** ExecuteLongRestChatAsync (and ExecuteLongRestEmbeddingAsync at 183-188) turn every non-cancellation exception raised inside beforeSend into Hub.Error. That includes the provider's own `InvalidOperationException(ErrorCodes.LongRest.StaleFence)` (LongRestProvider.cs:135-147) and the maintenance Refused(StaleInput / store codes) (LongRestMaintenance.Dispatch.cs:86-132). Hub.Error is not in LongRestFaultPolicy.KnownCodes, so it is classed NeedsOperator, while StaleFence, StaleInput and ConfigurationChanged are RestartGeneration. When the configuration epoch moved, LongRestProvider.cs:214-216 instead returns Stale() = LongRest.InvalidRequest (line 285), also NeedsOperator. A refusal after MarkSentAsync (Dispatch.cs:127, gates at 129-131 and provider 142-147) is also settled UsageUnknown (Dispatch.cs:157-161), charging the Unknown-Usage Cap of two although no SDK call began.

**Failure scenario.** The operator saves a configuration change (any provider, pricing, default or fast model) while a sweep is inside a send fence, or a source or claim fact moves between the unit's first gate and the fence. The sweep should settle Abandoned and restart a generation, but parks needing an operator under Hub.Error or LongRest.InvalidRequest. If the refusal falls after the Sent mark, the unit also burns a cap slot, and a second occurrence parks it under LongRest.TransportUnknown.

**Evidence (probe).** Executor: `return dispatched ? LongRestProviderFailure.Classify(exception) : new Error(ErrorCodes.Hub.Error, PublicInferenceErrorMessages.NativeGenericFailure);`. Probe Probe_long_rest_before_send_refusal_code_is_replaced on LongRestProviderFixture printed `fence=stalefence code=Hub.Error class=NeedsOperator sends=0`, `fence=source-before code=Hub.Error class=NeedsOperator sends=0`, and `fence=config-after-journal code=LongRest.InvalidRequest class=NeedsOperator sends=0`. The coordinator parks with the returned code (LongRestSweepCoordinator.Run.cs:137,146-149). The existing theory LongRestProviderDispatchTests.A_refused_or_stale_callback_never_reaches_HTTP (lines 112-138) asserts only IsFailure and zero sends, so it passes with any code. Post-mark settlement traced: Dispatch.cs:157-161 -> LongRestAttemptStore.SettleUnresolvedSendAsync:237-263 (FailureCount+1, TransportUnknown).

**Suggested fix.** Let fence exceptions out of the executor untranslated, or wrap them in a typed LongRestSendRefusedException(Error) that LongRestProvider and LongRestMaintenance unwrap. Map the provider's StaleFence throw and its post-call epoch check to LongRest.StaleFence / ConfigurationChanged instead of InvalidRequest. Settle a Sent attempt whose refusal came from the fence (no SDK call started) as NotSent, or document in DESIGN §10.8.2 that such refusals are charged to the cap. Assert the error code in the dispatch theory.

#### RV-098 (Minor, reliability, error-message, effort S): Internal exception text reaches the client as an attachment validation error

`src/RetroDownfall.Arcanum.Api/Intelligence/SessionAttachmentRequestValidator.cs:52` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/SessionAttachmentTurnService.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`

**Claim.** `catch (InvalidOperationException ex) { return ex.Message; }` treats the store's validation exception type as a catch-all. Server faults are caught too (ObjectDisposedException during shutdown, EF context misuse), and their raw message becomes the turn's ErrorMessage. WizardIntelligenceProvider.cs:2018-2023 returns it as Validation.AttachedFiles (400). This reports a server fault as a client error and leaks internal detail, against DESIGN §11.9.

**Failure scenario.** A turn with attachmentReferences arrives while the scoped DbContext is disposed or misused. The client receives HTTP 400 with 'Cannot access a disposed context instance ... Object name: ArcanumDbContext' instead of a sanitized server failure.

**Evidence (probe).** Probe Probe_reference_validator_returns_internal_fault_text (a DispatchProxy store whose ValidateReferencesAsync throws ObjectDisposedException) returned `validation message=Cannot access a disposed context instance. Internal detail. Object name: 'ArcanumDbContext'.`. SessionAttachmentStore.ValidateReferencesAsync:1522-1547 uses InvalidOperationException for its own not-found, not-bound and wrong-session results, which is why the catch is broad.

**Suggested fix.** Have ValidateReferencesAsync return a Result, or throw a dedicated SessionAttachmentReferenceException with code-owned messages, and catch only that type. Let other faults reach the turn's sanitized failure path.

#### RV-099 (Minor, usability, diagnostics, effort S): Turn-writer begin/finalize failure logs carry no session id, entry id or error code

`src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:918`

**Claim.** BeginAssistantReplyCoreAsync logs 'Grimoire could not begin assistant reply for model {ModelName}.' at both failure sites and returns the error to the caller, dropping its code from the log. The commit-with-label failures log only {ErrorCode}, and the finalize failure logs the model only; none names the session or assistant entry although other warnings in the file include AssistantEntryId.

**Failure scenario.** A client reports a 409 or 500 on a turn; the operator can match the server line only by timestamp and has no code, session id or entry id to search the Grimoire or the audit log with.

**Evidence (probe).** GrimoireTurnWriter.cs:918 and :943 (logger.LogWarning(beginFailureLogMessage, targetModel); templates at :111 and :128); :235 and :1255-1257 ('... could not be committed with its label: {ErrorCode}.'); :1335 (finalizeFailureLogMessage, targetModel); counter-example :305-308 includes {AssistantEntryId}. Not probed (rebuild queue); the templates are literal.

**Suggested fix.** Add {SessionId}, {AssistantEntryId} and {ErrorCode} to these templates.

#### RV-100 (Minor, usability, error-message, effort S): Guardrail topic rules: an allowed-topic miss is reported as 'matched' and an invalid blocked-topic regex silently disables the rule

`src/RetroDownfall.Arcanum.Api/Intelligence/Guardrails/GuardrailsPipeline.cs:486` — related: `src/RetroDownfall.Arcanum.Core/Configuration/ConfigurationValidator.cs`, `docs/Compendium.README.md`, `docs/Arcanum.API.md`

**Claim.** A topic-allowed violation (input did not match any allowed pattern) is reported through BuildError as 'content matched a guardrail policy (toxicity or topic)'. A BlockedTopics entry that is not a valid regex is skipped; nothing validates it at configuration load, the doctor does not check it, the only signal is one Warning per scan per request, and the docs never say the patterns are case-insensitive .NET regexes with a 500 ms budget.

**Failure scenario.** An operator whose allow list rejects everything is told the content 'matched' a policy. An operator who mistypes '(secret' believes the topic is blocked; it is allowed on every request and the log fills with identical warnings.

**Evidence (probe).** GuardrailsPipeline.cs:288-294 adds TypeTopicAllowed; :486-490 generic text. Probe P4: allowed-topic miss -> 'Guardrails.Blocked: Input rejected: content matched a guardrail policy (toxicity or topic).'; BlockedTopics ['(secret'] allowed 'tell me the secret now' three times and emitted 3 warnings 'Guardrails topic pattern (secret is invalid; skipped.' (lines 357-364). ConfigurationValidator.cs has no guardrail pattern rule. Compendium.README.md:132-133 'topic-pattern allowlist/blocklist'; API.md:953 'operator topic patterns'. The skip behaviour is pinned by FilterInputAsync_BlockedTopics_InvalidRegex_IsSkippedNotThrown (earlier ruling); the new angle is the missing config-time signal and per-request log spam.

**Suggested fix.** Add a 'did not match any allowed topic' message; validate patterns in ConfigurationValidator with a message naming security.guardrails.blockedTopics[i] and the fix; log an invalid pattern once per configuration epoch; document the regex semantics in Compendium.README.md.

#### RV-101 (Minor, usability, error-message, effort S): Memory-delegation refusals all carry one constant message, including body-shape and validation faults

`src/RetroDownfall.Arcanum.Api/Intelligence/MemoryDelegation/MemoryDelegationEndpoints.cs:94` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/MemoryDelegation/MemoryDelegationRequest.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/PingRequestPreflightValidator.cs`, `docs/Arcanum.API.md`

**Claim.** MemoryDelegationJsonResult replaces every failure message with 'The memory delegation request could not be completed.' Unknown JSON member, wrong Content-Type, oversize body and malformed UTF-8 all share MemoryDelegation.InvalidGrant (400) with that text, and the specific Validation.* messages from PingRequestPreflightValidator are overwritten too. Hiding which field is wrong does not protect Covenant content.

**Failure scenario.** A developer integrating with the strict closed DTO (UnmappedMemberHandling.Disallow) mistypes a field and gets 400 InvalidGrant with no hint; a request over the attached-file limit loses 'measured N bytes; limit M. Split the content ...'.

**Evidence (probe).** MemoryDelegationEndpoints.cs:94; MemoryDelegationRequest.cs:68 single Invalid(). Probe P2 (DefaultHttpContext): unknown member, text/plain body -> '400 ... MemoryDelegation.InvalidGrant ... The memory delegation request could not be completed.'; Error(Validation.InvalidPrompt, 'temperature must be between 0 and 2 (got 9).') -> code Validation.InvalidPrompt with the same constant message. API.md:561 says refusals are 'content-free'; no test or doc pins the constant text.

**Suggested fix.** Keep the constant only for StaleSource, Covenant.* and authority codes; return the specific message for body-shape faults (distinct reasons in MemoryDelegationRequestJson) and Validation.* codes.

#### RV-102 (Minor, usability, diagnostics, effort S): Provider-call failure logs name neither provider, model nor HTTP status, yet the user message sends the operator to the logs

`src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.cs:196` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/SemanticRouter.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/LexiconEntityExtractor.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/OpenAiRequestAugmentingHandler.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/PublicInferenceErrorMessages.cs`

**Claim.** ExecuteBufferedAsync logs 'Model call {ModelCallId} failed for purpose {Purpose}; exception type {ExceptionType}.' and returns NativeGenericFailure ('... See server logs for details.'). ModelCallContext holds Provider.Name and Model, and the HTTP status is available from InferenceProviderFailureMessage.TryGetHttpStatus, but none is logged. The router and Lexicon callers add only 'preflight failed (Hub.Error)'. OpenAiRequestAugmentingHandler's strict-retry warning has no provider or host.

**Failure scenario.** FastModel is misconfigured (provider returns 404). Every semantic-router and Lexicon preflight fails with ERROR lines that cannot be traced to a provider, model or status; with several providers the operator cannot tell which one is wrong.

**Evidence (probe).** ModelCallExecutor.cs:194-213; same shape ModelCallExecutor.CampaignMaintenance.cs:99-105; PublicInferenceErrorMessages.cs:9-10. Probe P5 (provider cloud-a, model fast-model, client throws ClientResultException 'HTTP 404 (model_not_found...)'): outcome 'Hub.Error: Inference failed. Ensure the provider is running and reachable, then try again. See server logs for details.' and the only log line 'log [Error] Model call 71bc12e6... failed for purpose LexiconExtraction; exception type System.ClientModel.ClientResultException.' SemanticRouter.cs:123-125 and LexiconEntityExtractor.cs:86-88 log only {ErrorCode}. OpenAiRequestAugmentingHandler.cs:96-97.

**Suggested fix.** Add {Provider}, {Model} and {HttpStatus} to these log templates (the status via InferenceProviderFailureMessage.TryGetHttpStatus); add the request host to the strict-retry warning.

#### RV-103 (Minor, usability, api-contract, effort S): logprobs, top_logprobs and embeddings dimensions are accepted and silently ignored

`src/RetroDownfall.Arcanum.Api/Intelligence/OpenAi/OpenAiChatRequest.cs:39` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/OpenAi/OpenAiEmbeddingRequest.cs`, `src/RetroDownfall.Arcanum.Api/OpenAiV1EmbeddingsEndpoints.cs`, `src/RetroDownfall.Arcanum.Api/OpenAiChatCompletionRequestValidator.cs`, `docs/Arcanum.API.md`

**Claim.** The request DTO declares logprobs and top_logprobs; ToPingRequest never reads them and every response sets logprobs null; no doc mentions them. For /v1/embeddings a 'dimensions' value is only logged server-side and the full-width vector is returned. The same validator rejects n != 1, tools and tool_choice with a named-parameter 400, so unsupported parameters are explicit in some places and silent in others.

**Failure scenario.** A client requests logprobs for calibration and gets nulls with 200; a client asks for dimensions 256 to match a vector index and receives the configured width, failing later at insert with no hint from the server response.

**Evidence (trace).** OpenAiChatRequest.cs:39-40; OpenAiChatCompletionMapper.ToPingRequest (no Logprobs); OpenAiV1Endpoints.cs:324 and similar 'Logprobs: null'; rg -i logprob docs README.md -> no matches. OpenAiEmbeddingRequest.cs:7-10 ('logged and ignored'); OpenAiV1EmbeddingsEndpoints.cs:182-191 LogWarning only; API.md does not describe the behaviour.

**Suggested fix.** Reject logprobs:true / top_logprobs and a dimensions value that differs from the configured width with 400 unsupported_parameter (as for n), or document both as ignored.

#### RV-104 (Minor, usability, docs-mismatch, effort S): API.md describes a 'reasoning' capability object on /v1/models that neither models endpoint emits

`src/RetroDownfall.Arcanum.Api/Intelligence/OpenAi/OpenAiModel.cs:26` — related: `docs/Arcanum.API.md`, `src/RetroDownfall.Arcanum.Core/Configuration/ModelInfoDto.cs`, `src/RetroDownfall.Arcanum.Api/OpenAiV1Endpoints.cs`

**Claim.** API.md:445 says /v1/models adds 'the same optional typed reasoning capability object returned by the native endpoint'. The code emits flat reasoning_wire_dialect and reasoning_max_budget_tokens (OpenAiModel) and the native ModelInfoDto carries flat WireDialect and MaxBudgetTokens; no reasoning object exists and the other capabilities (controlSupport, supportsSummary, ...) are not exposed.

**Failure scenario.** A client written from the documentation looks for a reasoning key in /v1/models, finds none, and cannot discover the real fields.

**Evidence (trace).** OpenAiModel.cs:26-29; Core/Configuration/ModelInfoDto.cs:5-18; OpenAiV1Endpoints.cs:~105-122 maps model.WireDialect and model.MaxBudgetTokens only; docs/Arcanum.API.md:445 (and :36 does not describe the fields either); rg for reasoning_wire_dialect in docs returns nothing.

**Suggested fix.** Correct API.md:445 and :36 to name the flat fields, or emit a reasoning object as documented.

#### RV-105 (Minor, usability, diagnostics, effort S): Semantic spell routing degrades to the paid LLM router with only Debug-level logs

`src/RetroDownfall.Arcanum.Api/Intelligence/SemanticSpellRouter.cs:99` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`

**Claim.** A failed prompt embedding, unavailable provider, unavailable catalog cache and any unexpected exception (logged with the exception) all log at Debug and fall back to the full-catalog LLM router. A persistent embedding outage is invisible at the default log level while every turn pays the LLM routing call the opt-in feature exists to avoid.

**Failure scenario.** An operator enables features.semanticSpellRouting; the embedding provider is down or misconfigured. Spell routing silently costs an LLM call per turn and nothing above Debug says why.

**Evidence (trace).** SemanticSpellRouter.cs:75, 84, 99-101, 154, 219 are LogDebug; only the dimension-mismatch case (146) is Warning. Other optional layers in the Wizard log the same class of failure at Warning, for example WizardIntelligenceProvider.cs:5812 'RAG query embedding threw; semantic retrieval for this turn will be skipped'. The fallback itself is documented (DESIGN.md:5381); the level is not.

**Suggested fix.** Log embedding failures and unexpected exceptions at Warning, throttled to once per configuration epoch.

#### RV-106 (Minor, usability, diagnostics, effort S): Attachment materialization swallows the exception and drops the extractor's own failure reason

`src/RetroDownfall.Arcanum.Api/Intelligence/SessionAttachmentTurnService.cs:511` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/SessionContextPinMaterializer.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/SessionAttachmentToolInjection.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`

**Claim.** MaterializeReferenceAsync ends in a bare catch (Exception) with no logger parameter and returns 'could not be read for model context. Retry the request or refresh the attachment.'; the whole turn then fails with Validation.AttachedFiles. For documents it discards extraction.FailureReason and says only 'could not be extracted for model context'; SessionContextPinMaterializer does the same. The model-facing path already has the right helper (NotExtractedReason).

**Failure scenario.** A stored attachment blob is unreadable (corrupt or undecryptable) or the document worker is busy. Both look identical, 'retry' is wrong advice for the first, nothing is logged, and the turn fails.

**Evidence (trace).** SessionAttachmentTurnService.cs:455-467 (extraction status and reason dropped) and :511-518 (catch, no variable, no logging); caller WizardIntelligenceProvider.cs:2854-2868 turns it into the turn failure. PrepareAsync in the same class logs session and entry ids on failure (lines 330-337). SessionContextPinMaterializer.cs:930-934 'Document text extraction failed or produced no text.' SessionAttachmentToolInjection.cs:247-254 NotExtractedReason returns the fixed, content-free reason.

**Suggested fix.** Reuse NotExtractedReason for the reference and pin paths; pass an ILogger into MaterializeReferenceAsync and log the exception type with the attachment id; drop 'Retry' from the message unless the status is Deferred.

#### RV-107 (Minor, usability, diagnostics, effort S): Input-stage guardrail audit records never carry the session id

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:8769` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/Guardrails/GuardrailsPipeline.cs`, `src/RetroDownfall.TheForge.Ux/ViewModels/AuditBrowser/AuditBrowserViewModel.cs`, `docs/Arcanum.API.md`

**Claim.** FilterGuardrailsInputAsync builds GuardrailAuditContext(null, request.Model), so input-stage violations are audited without a session even when the request names one; the output stage passes sessionId. request.Model can also be null when the default model is used.

**Failure scenario.** GET /api/guardrails/audit?sessionId=<id>, and The Forge audit browser's session filter and 'open session' action, never find the most common violation (a rejected prompt) for a session.

**Evidence (probe).** WizardIntelligenceProvider.cs:8769 vs :8792 (output passes sessionId?.ToString()). GuardrailsPipeline.cs:14-19 says the context exists 'so the audit record can name the session and model'. Wizard probe: request with SessionId set and a blocked SSN -> 'audit record: stage=Input type=pii-ssn sessionId=<null> model=wizard-test-model'. AuditBrowserViewModel.cs:277,337,350 use the record's SessionId.

**Suggested fix.** Pass request.SessionId?.ToString() and the resolved model into the input-stage GuardrailAuditContext.

### U03: Arcanum.Api — Intelligence (3)

#### RV-108 (Minor, performance, wasted-work, effort S): Research discovery loop keeps billing search passes after the synthesis prompt budget is already consumed, then fetches and cites nothing

`src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:326` — related: `docs/Arcanum.DESIGN.md`, `docs/Arcanum.Command.Reference.md`, `tests/RetroDownfall.Arcanum.Tests/Api/WebWorkflowEndpointTests.cs`

**Claim.** The discovery loop (while (true), lines 326-418) never consults ResolveRetainedContentBudget (lines 783-801), which is evaluated once after the loop at line 431. BuildSynthesisPrompt adds search summaries first, so once the cumulative summary length reaches the prompt budget (min(120000, MaxPingPromptChars=32768) minus the instruction), later passes cannot add a summary and no page can be fetched or cited, yet each pass is a billed provider search. The fetch loop then stops at line 478 (source_budget_reached) with zero sources and the result carries zero citations. DESIGN section 11.27 and Command Reference line 438 document open-ended passes while new URLs appear, so this is not about the absence of a pass ceiling but about passes that provably cannot change the output.

**Failure scenario.** `arcanum run --research` without --sources against a provider that keeps returning new URLs. With 4,000-char answers the budget is gone after about 8 passes (about 13 with 2,500-char answers); every later pass costs one search call plus its latency and cannot affect the prompt. Probe D1 (original code): 15 billed passes (0.15 USD at the scripted 0.01 each), 42 citations discovered, readsPerformed=0, frame 'Stopped fetching at source 1 of 42; the synthesis prompt budget is already covered.', citationsInResult=0, promptChars=32768.

**Evidence (probe).** WebResearchWorkflowService.cs:326 `while (true)` ... :410-417 only `newCitationCount == 0` and sourceTarget end the loop; :431 `int retainedContentBudget = ResolveRetainedContentBudget(searches);` (after the loop); :478 `if (retainedContentChars >= retainedContentBudget) { ... source_budget_reached ... break; }`; :783-801 ResolveRetainedContentBudget sums search.Answer.Length. IntelligenceSettings.cs:102 MaxPingPromptChars = 32_768; WebBrowsingSettings.MaxContentBytes = 50_000. Probe D1 output: `searchPasses=15 billedSearchCostUsd=0.15 readsPerformed=0 stages=[searching,source_exhausted,source_budget_reached,synthesizing] ... citationsInResult=0 promptChars=32768`. Existing test Research_stops_fetching_once_the_synthesis_prompt_budget_is_covered covers the fetch stop only, not discovery.

**Suggested fix.** After each pass compute ResolveRetainedContentBudget(searches); when it reaches 0 emit a progress stage (for example summary_budget_reached) and leave the discovery loop. Document the new stop reason in DESIGN section 11.27 and the Command Reference, and add a test with a scripted provider whose answers outgrow 32,768 characters asserting the pass count stops.

#### RV-109 (Minor, performance, test-coverage, effort S): The 50-citation research fetch ceiling has no test; removing it passes every related test

`src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:427` — related: `tests/RetroDownfall.Arcanum.Tests/Api/WebWorkflowEndpointTests.cs`

**Claim.** MaximumResearchSources (line 52, applied at lines 425-427) is the only bound on the fetch phase when pages come back empty or failed, because those do not advance retainedContentChars and the budget stop at line 478 never fires. No test supplies more than 8 unique citations without a sourceTarget, so nothing asserts the ceiling.

**Failure scenario.** A refactor raises or removes the ceiling. A research run whose search provider returns hundreds of URLs with blocked or empty pages then attempts every fetch one after another (up to the 15 s idle timeout each) and every existing test still passes.

**Evidence (probe).** Deliberate break 1 in a scratch worktree (remove the retainedContentChars >= retainedContentBudget stop at :478): WebWorkflowEndpointTests.Research_stops_fetching_once_the_synthesis_prompt_budget_is_covered FAILS with 'fetched 8 sources for a prompt budget that cannot hold them' - covered. Deliberate break 2 (replace `citations.Values.Take(MaximumResearchSources).ToArray()` with `citations.Values.ToArray()`): `dotnet test --no-build --filter WebWorkflowEndpointTests|WebResearch|DocumentationCodeContradictionTests|WebWorkflowCommandTests|WeaveServiceTests` -> Passed 226, Failed 0. StubWebProvider.ChangingCitationRounds is 5 or 8 in every research test (WebWorkflowEndpointTests.cs:562,1131,1196) and rg finds no test mentioning MaximumResearchSources. Break reverted with git checkout; worktree removed.

**Suggested fix.** Add a test with a scripted provider returning 120 unique citations and empty page bodies and no sourceTarget, asserting exactly 50 reads and no source_budget_reached frame; add one asserting Take(sourceTarget) for an explicit target.

#### RV-110 (Minor, performance, n-plus-one, effort M): SumCommittedAsync reads and decimal-parses every billable row of the UTC day under the write lock

`src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs:699` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnAccountingHandle.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/TurnRunWriter.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/BillableOperations.sql`

**Claim.** SumCommittedAsync (lines 699-739) selects ActualCostUsd for every BillableOperations row completed today (index range scan on IX_BillableOperations_CompletedAt, then a table fetch per row) and adds them with ExactUsdText.CheckedAdd in managed code. ReserveAsync and AdjustAsync do this inside a BEGIN IMMEDIATE transaction, and RecheckDailyLimitAsync and the turn-start budget gate repeat it, so every turn and every provider round pays O(billable operations today).

**Failure scenario.** Budget-enabled install on a busy day (every provider call, embedding batch and Long Rest call adds a row). Measured with the reservation table small: one admission round (EnsureReservationForContextAsync plateau path) costs 0.20 ms at 20 rows, 2.5 ms at 2,000, 16.8 ms at 10,000, 24.8 ms at 30,000 and 51.5 ms at 60,000 rows; ReserveAsync+Release (write lock held) costs 0.24, 2.5, 11.4, 24.7 and 78.5 ms. About 1 microsecond per row per sum, repeated every round.

**Evidence (probe).** BudgetReservationService.cs:699-739 (`SELECT "ActualCostUsd" FROM "BillableOperations" WHERE "CompletedAt" >= @dayStart AND "CompletedAt" < @dayEnd` then `total = ExactUsdText.CheckedAdd(total, ExactUsdText.Read(reader, 0))` per row); called from lines 84, 210, 335 and via GetTodayCommittedSpendAsync at 584. Probe A (U03GrimoireProbe.ProbeA_BudgetRoundCostByRowsToday) logs: rowsToday=10000 plateauEnsure_avg_ms=16.780 reserve+release_avg_ms=11.398; rowsToday=60000 plateauEnsure_avg_ms=51.450 reserve+release_avg_ms=78.489. EXPLAIN QUERY PLAN: `SEARCH BillableOperations USING INDEX IX_BillableOperations_CompletedAt`. The writes themselves are cheap (recordUsage 0.07-0.10 ms, markDispatched 0.03-0.07 ms), so the cost is the read.

**Suggested fix.** Keep a per-UTC-day committed total updated in the same transaction that inserts the BillableOperations row (preserving the exact text-decimal arithmetic), or read only rows added since a cached high-water mark; at minimum compute the sum outside the write transaction for the Recheck and gate paths.

#### RV-111 (Minor, reliability, retry, effort S): Embedding batches use the SDK's 100 s per-try timeout and 4 tries (outside this unit's files)

`src/RetroDownfall.Arcanum.Api/Intelligence/EmbeddingGeneratorFactory.cs:134` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/WeaveService.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs`, `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs`

**Claim.** OpenAIClientOptions is built with neither NetworkTimeout nor RetryPolicy (nothing under src sets NetworkTimeout), so the System.ClientModel defaults apply: 100 s per attempt and 3 retries. A batch that legitimately needs more than 100 s is cut and re-sent 3 more times, then reported ProviderUnavailable, contrary to DESIGN 21.1 ('no Arcanum whole-operation deadline'). The timed-out billable POSTs are never ledgered.

**Failure scenario.** A CPU-only or cold-loading local embedding model takes more than 100 s for a 32-chunk batch. Each EntryWeaving/WorkspaceIndexing tick spends about 7 minutes of provider work across 4 attempts, fails with ProviderUnavailable, and the next tick repeats it. A paid provider may bill the abandoned attempts.

**Evidence (probe).** EmbeddingGeneratorFactory.cs:134-140 sets only Endpoint and Transport. The named HttpClient has Timeout infinite (ApiBootstrapper.cs:549). Probe P7, a real EmbeddingClient over a slow handler with a short NetworkTimeout: 'AggregateException: Retry failed after 4 tries', 4 HTTP attempts. WeaveService then returned ProviderUnavailable, so WeaveService itself handles it correctly.

**Suggested fix.** Set NetworkTimeout = Timeout.InfiniteTimeSpan (caller cancellation already bounds the wait) and a retry policy with no retries for the billable embedding POST. Review ChatClientFactory's non-streaming chat calls the same way. Coordinate with the unit that owns these factories.

#### RV-112 (Minor, reliability, error-message, effort S): refresh_session_file reports provenance and hash failures as attachment budget exhausted

`src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:961` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs`

**Claim.** `catch (InvalidOperationException ex)` around PersistRefreshedAsync maps every InvalidOperationException to attachment_budget_exhausted / Attachment.LimitExceeded. That includes the store's own provenance and hash refusals and 'Only a bound session attachment can be refreshed', not only AttachmentLimitExceededException.

**Failure scenario.** A refresh whose resolved bytes no longer match the recorded hash: the model is told the budget is exhausted, and the operator route returns Attachment.LimitExceeded with the message 'The attachment source hash does not match the verified bytes.', which points the operator at deleting versions.

**Evidence (probe).** SessionAttachmentStore.ValidateResolvedSource throws InvalidOperationException at :277 and :286; AttachmentLimitExceededException derives from InvalidOperationException. Probe P8: IsFailure=True, code=Attachment.LimitExceeded, message='The attachment source hash does not match the verified bytes.'

**Suggested fix.** Catch AttachmentLimitExceededException for the budget code. Map other InvalidOperationExceptions from the store to source_unavailable (Attachment.SourceUnavailable).

#### RV-113 (Minor, reliability, crash-consistency, effort S): A failed reservation settlement skips run completion, leaving InferenceRuns Running forever

`src/RetroDownfall.Arcanum.Api/Intelligence/TurnAccountingHandle.cs:346` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/WeaveService.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs`

**Claim.** CompleteAsync settles the reservation before the run. If ReconcileAsync/ReleaseAsync throws, CompleteRunAsync is never attempted. The handle supports a retry, but no production caller retries: WeaveService swallows the failure at Debug and Wizard at Warning. The run stays Running permanently and the reservation stays Reserved.

**Failure scenario.** A standalone embedding batch (EntryWeaving or WorkspaceIndexing) or a Wizard turn finishes while the reservation write fails (SQLITE_FULL, I/O error). The InferenceRuns row stays Running, so retention reports Data.InferenceRunActive and protects the whole run chain forever, no sweep exists for runs, and the Reserved row counts in that day's outstanding spend. For embeddings the only trace is a Debug log.

**Evidence (probe).** Lines 346-382: the run disposition runs only after the reservation block completes without throwing. WeaveService.cs:245-257 catches and logs at LogDebug. WizardIntelligenceProvider.cs:4832-4846 logs a warning. DataRetentionService.Pruning.cs:2599-2620 blocks Running runs. Probe P6: CompleteAsync throws, CompleteRunAsync is never called, IsSettled=True. TurnAccountingHandleTests.CompleteAsync_RetriesFailedReservation... proves a retry works, but no caller retries.

**Suggested fix.** Attempt the run disposition in a finally even when the reservation step throws, then rethrow. Raise WeaveService's completion-failure log to Warning. Optionally add a startup sweep that terminalizes Running runs with no live owner.

#### RV-114 (Minor, reliability, correctness, effort S): Research question bound assumes fewer than 1,000 passes but the loop has no ceiling

`src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:940` — related: `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/WebResearch/PerplexityWebProvider.cs`

**Claim.** MaxResearchQuestionChars reserves room for BuildFollowUpQuery with a 3-digit pass number ('longer than any run reaches'), but the discovery loop has no pass ceiling. At pass 1000 a maximum-length question yields a 4,001-char query.

**Failure scenario.** A no-target research run with a question at MaxResearchQuestionChars that keeps finding a new URL for 999 passes. Pass 1000 is rejected by PerplexityWebProvider (RequestRejected), and the run ends in an error frame, discarding 999 billed passes.

**Evidence (trace).** Lines 931-941 build the reservation from BuildFollowUpQuery(string.Empty, 999). The loop at 326-418 has no pass bound. PerplexityWebProvider.cs:70-75 rejects query.Length > 4,000. Traced, not executed.

**Suggested fix.** Make the follow-up suffix's length independent of the pass number (fixed width, or no number), or clamp the question per pass so the query never exceeds MaxInputQueryChars.

#### RV-115 (Minor, usability, error-message, effort S): Model-facing tool error text leaves the model without a next step (refresh selector, unknown tool, truncation marker)

`src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:672` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/ToolResultMaterializer.cs`, `docs/Arcanum.ConstraintReduction.20260803.md`

**Claim.** (a) refresh_session_file with a malformed attachmentId and no logicalKey fails TryParseRefreshSelector and is answered 'Exactly one of attachmentId or logicalKey is required.' although the model supplied one; with a malformed id plus a valid logicalKey the id is silently ignored. (b) A call to an unadvertised tool returns "No local tool registered for 'x'." with no list of the advertised names. (c) The generic truncation marker '[truncated: tool result exceeded token/byte budget]' states neither how much was omitted nor how to get the rest, and head+tail slicing breaks JSON mid-token, whereas structured envelopes carry omitted counts. The ToolResultMaterializer class summary also says a tokenizer may be 'injected' but there is no injection point.

**Failure scenario.** A model passes attachmentId 'att_3' and is told it did not supply a selector, so it repeats the call. A model calls a tool name from its training data and gets no list of what exists. A 250k-character MCP result reaches the model as ~8 KB of head and tail with an ellipsis, and the model cannot tell what fraction it saw or that it should narrow the request.

**Evidence (probe).** ToolExecutionPipeline.cs:672-677, 1037-1077, 1263-1266; ToolResultMaterializer.cs:10-13, 19-20, 198-200. Probe P4: selector rejected for attachmentId='not-a-guid'. Probe P5: raw 248,900 chars -> 8,190 chars, middle '...,{"id":378},\n...\nd":19687},...', tail '...{"id":19999}]}\n[truncated: tool result exceeded token/byte budget]'. ConstraintReduction.20260803.md:396 says the 8,192-byte unit 'reports truncation/omitted counts'.

**Suggested fix.** Name the malformed selector ('attachmentId is not a valid GUID'); list advertised tool names (bounded) in the unknown-tool result; put shown/original character counts and a 'narrow the request or page' hint in the marker (ToolResultMaterialization already holds OriginalLength); fix the stale 'injected tokenizer' summary.

#### RV-116 (Minor, usability, diagnostics, effort S): The no-progress stop names neither the tool nor the arguments that looped

`src/RetroDownfall.Arcanum.Api/Intelligence/ToolLoopProgressDetector.cs:37` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`

**Claim.** ToolLoopProgressDetector holds each round's tool names but exposes only RepeatedSignature (an 8-byte hex digest). The Hub.NoProgressDetected error says 'the latest call/result round repeated a recent round and produced no new evidence' and the Warning log carries only the digest.

**Failure scenario.** A local model loops on one tool; the turn dies with a generic message and the operator has no hint which tool or arguments repeated, so cannot tell a prompt problem from a tool returning a constant result.

**Evidence (trace).** ToolLoopProgressDetector.cs:37, 80; WizardIntelligenceProvider.cs:4254-4262 (message text and LogWarning 'recurring round signature {ProgressSignature}'); DESIGN.md:1709 documents only the signature.

**Suggested fix.** Expose RepeatedToolNames (and a per-call argument digest) from the detector; include them in the error message and in the Warning together with the session id.

#### RV-117 (Minor, usability, error-message, effort M): Silently trimmed tool exchanges can make the model repeat a call that the no-progress detector then treats as a loop

`src/RetroDownfall.Arcanum.Api/Intelligence/TurnContextGuards.cs:121` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/ToolLoopProgressDetector.cs`

**Claim.** TryTrimOldestToolExchanges removes the oldest complete call/result pairs from the model's message list when over budget and leaves no marker. ToolLoopProgressDetector fingerprints rounds independently of the message list and compares against the last 8 rounds. A model that lost the evidence re-issues the identical call, the repeated call and result match a recent fingerprint, and the turn is ended with Hub.NoProgressDetected. Trace only; not executed.

**Failure scenario.** A small-context local model (8-32k window) reads a file in round 1, accumulates results until context pressure trims round 1's pair, then re-reads the file within the 8-round window; the identical round is declared 'no new evidence' and the turn fails although the model had no access to the earlier result.

**Evidence (trace).** TurnContextGuards.cs:121-199 (RemoveRange with no replacement message); WizardIntelligenceProvider.cs:7951-7956 (caller ignores the return and adds no note); ToolLoopProgressDetector.cs:20-86 and WizardIntelligenceProvider.cs:4254-4264 (fingerprints from toolLoopProgressEntries). Trace based on source; the combination was not run end to end.

**Suggested fix.** Replace a trimmed pair with a short placeholder tool result ('earlier result removed to fit the context window; re-run if needed') and exclude calls whose earlier result was trimmed from the repeat comparison.

#### RV-118 (Minor, usability, diagnostics, effort S): Embedding failures are under-diagnosed and a failed accounting completion is logged at Debug without ids

`src/RetroDownfall.Arcanum.Api/Intelligence/WeaveService.cs:256` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnAccountingHandle.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs`

**Claim.** If accounting.CompleteAsync throws in the finally block it is logged with LogDebug('Failed to complete standalone embedding accounting.') with no run id or reservation id, although TurnAccountingHandle documents 'Failures are logged by the caller' and the streaming path logs the same failure at Warning with the session id. Separately, every provider exception becomes 'The embedding provider is unavailable' and a Warning that names neither the configured provider nor model, so a 401, a 404 model and a timeout are indistinguishable to the caller.

**Failure scenario.** A locked database makes CompleteRunAsync fail: the embedding run row stays Running (the handle's own comment says no expiry sweep exists for runs) and the reservation stays Reserved (SweepExpiredAsync has no production caller), later surfacing as a daily-budget refusal with little real spend. At default log levels there is no trace of it. Likewise a revoked embedding key shows 'unavailable' and an unattributed Warning.

**Evidence (probe).** WeaveService.cs:233-240 and 254-257; TurnAccountingHandle.cs:18-19, 275-278, 431-434; WizardIntelligenceProvider.cs:4841-4846 (same failure at Warning with session). Probe P6 printed: 'result code=Embeddings.ProviderUnavailable message=The embedding provider is unavailable. See server logs for detail.'; 'log Warning: Embedding provider call failed; The Weave will be treated as unavailable for this request. [ex=HttpRequestException: ... 401 (Unauthorized).]'; 'log Debug: Failed to complete standalone embedding accounting. [ex=InvalidOperationException: database is locked]'. grep shows SweepExpiredAsync is defined in BudgetReservationService/IBudgetReservationService and called only from tests.

**Suggested fix.** Log the completion failure at Warning with RunId, ReservationId and exception type; add provider and model to the embedding failure log; map 401/403/404 to distinct client messages ('credential rejected', 'model not found') using ClientResultException.Status.

#### RV-119 (Minor, usability, error-message, effort S): Web workflow validation messages do not name the offending field or rule; API message cites a CLI flag

`src/RetroDownfall.Arcanum.Api/Intelligence/WebResearchWorkflowService.cs:698` — related: `src/RetroDownfall.Arcanum.Api/Models/WebWorkflowModels.cs`

**Claim.** BuildSearchOptions answers every violation (empty query, resultCount outside 1-20, bad freshness, bad domain filter) with one sentence 'Search requires a query, count 1-20, freshness day|week|month|year, and valid domain filters.' The domain rule (host only: no scheme, path, port or whitespace; at most 20 entries; at most 253 chars) is stated nowhere. ValidateResearchRequest likewise lists four requirements for any violation. BrowseAsync tells an HTTP caller 'retry with --render static' (a CLI flag; the API field is renderMode).

**Failure scenario.** An API client posts includeDomains:['https://example.com'] (a common mistake) or resultCount 50 and receives the identical generic 400 text, so it cannot tell which field to fix.

**Evidence (probe).** WebResearchWorkflowService.cs:698, 962, 158; AreValidDomains at 1298-1307. Probe P9 printed: "includeDomains=['https://example.com'] -> WebResearch.RequestRejected: Search requires a query, count 1-20, freshness day|week|month|year, and valid domain filters." and the identical text for resultCount=50; 'renderMode=javascript -> ...: JavaScript rendering is not configured; retry with --render static.'

**Suggested fix.** Validate field by field and report the first failure with the field name, the rule and the accepted range ('includeDomains[0] must be a bare host such as example.com'); use API field names (renderMode: "static") in API messages.

#### RV-120 (Minor, usability, diagnostics, effort S): Research failure frames carry no trace id or spend, the log id differs from the envelope id, and the 200-with-error-frame contract is undocumented

`src/RetroDownfall.Arcanum.Api/Intelligence/WebWorkflowEndpoints.cs:159` — related: `src/RetroDownfall.Arcanum.Api/Models/WebWorkflowModels.cs`, `docs/Arcanum.API.md`, `docs/Arcanum.Command.Reference.md`

**Claim.** The terminal research error frame says 'Check the host logs for details.' but WebResearchStreamFrame has no trace id; the log line 'Web research stream failed after {TraceId} started' is bound to httpContext.TraceIdentifier while the sibling search/browse envelopes report Activity.Current?.Id, so nothing can be correlated. The WebResearch.BudgetExceeded frame states the limit but not the spend, and the billed search results are discarded without synthesis, although the Command Reference lists 'an explicit token/cost policy is reached' beside stop conditions that do proceed to synthesis. Every research failure (including request validation) is HTTP 200 with an error frame, while API.md's status table lists HTTP statuses for the same codes and never says research is the exception.

**Failure scenario.** A user reports 'The research stream failed unexpectedly' after several paid passes; the operator cannot find the matching LogError among concurrent requests and cannot see what was spent. A script that checks HTTP status treats an invalid research request as success.

**Evidence (trace).** WebWorkflowEndpoints.cs:35-36, 157-175; WebWorkflowModels.cs (WebResearchStreamFrame: Type, Stage, Message, Code, Result); WebResearchWorkflowService.cs:388-398; WebWorkflowEndpoints.cs:73-79 vs 159-162 (Activity.Current?.Id vs TraceIdentifier); API.md:928-935 status table and 8.10.3; Command Reference 'arcanum research' description.

**Suggested fix.** Add traceId to WebResearchStreamFrame and log with the same id (Activity.Current?.Id ?? TraceIdentifier); include spend so far in the BudgetExceeded frame and say whether results were kept; state in API.md 8.10.3 that research failures arrive as 200 + error frame.

#### RV-121 (Minor, usability, diagnostics, effort S): Model-resolution failures lose their cause: the preview logs nothing and the live path logs only the exception type

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.ContextPreview.cs:150` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`

**Claim.** ChatClientFactory throws three distinct code-owned InvalidOperationExceptions with actionable messages (no model resolvable; undefined wire dialect; 'Unsupported provider type X for provider Y'). PreviewContextAsync catches InvalidOperationException and discards it without logging; the live buffered path logs only 'Hub model resolution failed; exception type InvalidOperationException'. All three reach the client as 'The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel.'

**Failure scenario.** An operator with a provider of an unsupported kind or a missing models list runs context inspect or a normal turn, is told to check Providers/DefaultModel generically, and finds no log line saying which of the three causes applies or which provider/model was requested.

**Evidence (trace).** WizardIntelligenceProvider.ContextPreview.cs:150-155 (catch without logger call); WizardIntelligenceProvider.cs:709-715; ChatClientFactory.cs:72, 101, 116, 167 (throw sites with specific messages); PublicInferenceErrorMessages.cs:15-16.

**Suggested fix.** Log the code-owned exception message and the requested model at Warning in both paths (these messages carry no secrets); use the specific text for the unsupported-provider case.

#### RV-122 (Minor, usability, diagnostics, effort M): Context preview cannot say why retrieval contributed nothing (disabled, embedding failed, or no hits look identical)

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.ContextPreview.cs:1006` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`

**Claim.** ResolveSourceReason gives explicit reasons only for noRetrieval and missing Session/Workspace; every other zero-token source, including retrieval features that are off, an unavailable Weave, and a failed query embedding, reports 'No effective content was available for this source.' The retrieval-embedding auxiliary row is added before the embedding call and says the embedding 'was invoked', never updated with the outcome. The real failure is logged at Debug only (and in a live turn no status event is sent).

**Failure scenario.** An operator whose embedding provider is down or misconfigured runs arcanum run --dry-run or POST /api/intelligence/context/inspect to find out why workspace RAG never appears; the preview succeeds and shows the same text as a legitimately empty search, and nothing is logged at default levels.

**Evidence (probe).** ContextPreview.cs:906-915 and 963-1007; WizardIntelligenceProvider.cs:5768-5812 (EmbedQueryAsync: AuxiliaryCall added before EmbedAsync; failure -> LogDebug 'RAG query embedding failed ({Code})'). Probe P8 printed, for both 'embedding-fails' (FailEmbed=true, embedCalls=1) and 'retrieval-features-off' (embedCalls=0): 'source=WorkspaceRag included=False reason=No effective content was available for this source.' (also TapestryRag, LexiconSaga), and in the failing case 'aux purpose=retrieval-embedding detail=The shared retrieval embedding was invoked; this provider does not report token usage.'

**Suggested fix.** Pass feature/availability flags and the embedding outcome code into ResolveSourceReason and the auxiliary row ('skipped: embeddings disabled', 'failed: Embeddings.ProviderUnavailable'); log embedding failure once per turn at Warning.

### U04: Arcanum.Api — Intelligence (4)

#### RV-123 (Minor, performance, cold-start, effort S): Tokenizer vocabulary is built lazily inside the first turn after daemon start (no warm-up)

`src/RetroDownfall.Arcanum.Api/Intelligence/InferenceTokenizerResolver.cs:88` — related: `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/ModelTokenEstimator.cs`

**Claim.** InferenceTextCounter constructs TiktokenTokenizer.CreateForEncoding("o200k_base") the first time any estimate runs in the process (first use is in the first turn's context gate or retrieval acceptance in WizardIntelligenceProvider.cs, e.g. line 7932/7956). Nothing in startup touches the resolver, so the first user turn after each `arcanum serve` pays the vocabulary build on the critical path, and the process then holds the vocabulary resident.

**Failure scenario.** First prompt after starting the host is delayed by the vocabulary build before the provider request is sent; resident heap grows by tens of MB for the life of a local-first daemon.

**Evidence (probe).** InferenceTokenizerResolver.cs:22-36 lazy ConcurrentDictionary GetOrAdd, 86-88 `private readonly TiktokenTokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding(encodingName);`; ApiBootstrapper.cs:593 AddSingleton<InferenceTokenizerResolver>() with no warm-up; grep for warm-up hosted work found none. Probe D (fresh test-host process, three runs, JIT'd Debug build, loaded machine, NOT Native AOT): `Resolve(o200k_base)=433ms/370ms/146ms/171ms/87ms wall, 87-94 ms process-cpu, retained heap delta=54-62MB; firstEstimate=7.0-34.1ms; secondEstimate=0.07-0.09ms`. No published AOT binary was available to time.

**Suggested fix.** Touch InferenceTokenizerResolver.Resolve(DefaultEncodingName) from a background startup task (or an IHostedService) so the cost is off the first turn; time it under the Native AOT build.

#### RV-124 (Minor, performance, api-contract, effort S): Dispatch-time token breakdown never attributes Covenant tokens, so streamed Context frames and audit breakdowns disagree with inspect

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:3271` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.ContextPreview.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/ModelTokenEstimator.cs`

**Claim.** The systemPromptAttribution lambda handed to every context gate (3271-3272 and 4400-4401) returns streamCachePrepared?.Build.Attribution, else null unless covenantScope.MemorySources is set. In the default path (cache v2 off, no memory delegation) the estimator therefore gets no attribution map and charges the Covenant text to SystemCodexSpell, while the preview path passes document.BuildResult().Attribution (WizardIntelligenceProvider.ContextPreview.cs:638). DESIGN (~2009) says the estimator charges the two Covenant lanes from the partition so mana and context inspect attribute the profile separately.

**Failure scenario.** A turn that really sent the Covenant streams a `Context token accounting` frame and writes an audit breakdown with CovenantConfirmed/CovenantProposed = 0 and the bytes lumped into SystemCodexSpell, while `context inspect` for the same turn shows a CovenantConfirmed row; operators cannot see what the standing agreement cost from the accounted turn.

**Evidence (probe).** Probe K, admitted case (32k window, 0 MCP tools, Covenant heading present in the dispatched system prompt): `ProbeK components(admitted,0 tools): SystemCodexSpell=793, Tools=343, CurrentPrompt=5, ProviderFraming=12, SafetyMargin=173, ReservedAnswer=1024` and `covenantTokens=0` from the last audit breakdown in every admitted row. Lines 3271-3272: `systemPromptAttribution: () => streamCachePrepared?.Build.Attribution ?? (covenantScope?.MemorySources is null ? null : streamSystemPromptDocument.BuildResult().Attribution)`.

**Suggested fix.** Return streamSystemPromptDocument.BuildResult().Attribution (rendered once per rebuild, not per call) whenever streamCovenantContent or campaign rollup content is present, in both lambdas; add a dispatch-path assertion that the audit breakdown carries a non-zero CovenantConfirmed row when the section was sent.

#### RV-125 (Minor, performance, duplicate-provider-call, effort S): Same prompt is embedded twice per turn when SemanticSpellRouting and any retrieval feature are both enabled

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:5405` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/SemanticSpellRouter.cs`

**Claim.** ResolveRoutedSpellAsync passes GetSemanticRouterUserProbe(request) to SemanticSpellRouter.ResolveAsync, which embeds it (SemanticSpellRouter.cs:92). Immediately afterwards ResolveRagQueryEmbeddingAsync embeds the identical string again via EmbedQueryAsync (line 5757, called at 2400) for Saga/Codebase/Attachment/Tapestry retrieval. The 'never pays for the same embedding twice' comment (5719) only covers Phases 3-4. Each call is a provider round trip and a billed ledger row.

**Failure scenario.** Operator with Embeddings + SemanticSpellRouting + Saga (or any retrieval) enabled: every turn makes two sequential embedding calls for the same text (about 100-300 ms each on a local embedding model) before inference.

**Evidence (probe).** Probe C (spell dir present, Features Embeddings+SemanticSpellRouting+Saga, FakeRagWeaveService counting calls): `ProbeC success=True EmbedAsync=2 EmbedBatch=1 bufferedCalls=1`. Existing test ScenarioSaga04_..._EmbedsQueryOnceNotTwice asserts 1 but runs with SkipSpellRouting=true, so the router path is not covered.

**Suggested fix.** Let SemanticSpellRouter.ResolveAsync return (or publish via the observeEmbedding/TurnContextSeed channel) the prompt embedding it computed, and have ResolveRagQueryEmbeddingAsync reuse it when the probe text is identical.

#### RV-126 (Minor, performance, repeated-work, effort S): Three identical full-transcript estimates plus four payload hashes per provider round

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:7943` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/ModelTokenEstimator.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/TurnContextGuards.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallPayloadFingerprint.cs`

**Claim.** Each provider round calls EnsureContextBudgetWithMaterializations, which runs EstimateContext three times over the same unchanged messages/options when under budget: CascadeSemanticDrops' isOverBudget Count (7945-7948), TryTrimOldestToolExchanges' initial countTokens (7951, TurnContextGuards.cs:126), and the breakdown (7956). The two Count passes need only TotalTokens, yet EstimateContext also SHA-256-hashes the entire payload (including every tool schema via GetRawText) for PayloadFingerprint (ModelTokenEstimator.cs:324), and ModelCallExecutor.ValidatePrecomputedBreakdown hashes it a fourth time immediately afterwards (ModelCallExecutor.cs:772). The deferred attachment-reference loop (2842-3050) repeats four passes plus a deep copy of the message list per referenced attachment; a Session turn's first round adds one more via TryApplyContextCompressionIfNeeded (static trace).

**Failure scenario.** A 20-round tool loop over a 150-600 KB transcript with 40-120 tools does 60 transcript estimates, about 80 fingerprints and 1.5-4 MB of garbage per round (about 3-20 ms/round); referencing 10-20 attachments multiplies that by 4 per attachment before the first token. Small next to LLM latency but pure repetition on the pre-token path.

**Evidence (probe).** Probe B (CountingEstimator wrapping the real ModelTokenEstimator, stateless 52 messages, 40 tools x 2.5 KB, tool rounds 0/1/5): `ProbeB streaming=False 0rounds: calls=3 | 1round: calls=6 | 5rounds: calls=18 => EstimateContext calls per extra tool round = 3.00; ms per extra round = 2.61`; streaming=True: 3.00 calls, 5.18 ms/round. Scaling (loaded host): 40 msgs x 1500ch 2.55 ms/round, 99 x 1500 18.75, 99 x 6000 7.74. Probe B2 warm single EstimateContext: 200 msgs/340 KB/40 tools 0.85 ms, 519 KB allocated; 800 msgs/1.2 MB 6.76 ms (fingerprint alone 4.90 ms), 723 KB; 200 msgs/120 tools 3.47 ms, 1,408 KB (fingerprint 1.95 ms); 200 msgs/0 tools 2.22 ms, 73 KB. Probe B3 cold tokenization of new text: 17-19 MB/s up to 100 KB, 3.8-7.9 MB/s at 1-4 MB, then cached by SHA-256 of the text (4,096-entry LRU).

**Suggested fix.** Compute the breakdown once per round and derive the over-budget decision from its TotalTokens; re-estimate only after a mutation (eviction/trim). Give ModelTokenizationRequest a flag (or add a CountTokens entry point) that skips PayloadFingerprint for probe-only calls, and reuse the fingerprint already computed by the breakdown in ValidatePrecomputedBreakdown.

#### RV-127 (Minor, performance, cache-thrash, effort S): SpellWeaveCache keeps one catalog snapshot, so alternating workspaces re-embed the whole spell catalog every turn

`src/RetroDownfall.Arcanum.Infrastructure/Weave/SpellWeaveCache.cs:74` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/SemanticSpellRouter.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`

**Claim.** The singleton SpellWeaveCache stores a single CacheSnapshot keyed by the entire (Name, Description)[] catalog plus provider/model/dimensions. The catalog the hub passes is global + the turn's workspace spells (WizardIntelligenceProvider.cs:5376-5405), so two working directories with different local spells (CLI in one repo, The Forge or a daemon in another) invalidate each other and each turn pays an EmbedBatch over the whole merged catalog.

**Failure scenario.** 50 global spells + 1 local spell per project, operator alternating between two repositories (or two concurrent sessions): every turn is a catalog-wide embedding batch call plus a ledger row instead of a lock-free cache hit.

**Evidence (probe).** SpellWeaveCache.cs:60-85 (`private volatile CacheSnapshot? _snapshot;` and `currentKey.Matches(snapshot.Key)` compares every name+description); registered singleton (ServiceCollectionExtensions.cs:1259). Probe I (6 turns alternating workspace A/B, 50 global + 1 local): `EmbedBatch calls=6 descriptionsEmbedded=306`; same workspace every turn: `EmbedBatch calls=1`.

**Suggested fix.** Cache vectors per spell (key = name + description hash + provider/model/dimensions) and only embed the missing ones, or keep a small LRU of snapshots keyed by workspace root.

#### RV-128 (Minor, reliability, api-contract, effort S): On the production streaming path the 'Finishing protected memory maintenance...' status is held in the pre-commit buffer until after the maintenance runs; the only test uses a path production never takes

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:1825` — related: `tests/RetroDownfall.Arcanum.Tests/Intelligence/WizardIntelligenceProviderTests.LongRestStatus.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs`, `docs/Arcanum.API.md`

**Claim.** API.md line 401 promises one status frame 'before the first paid protected work' and a wait of 'up to 5 seconds for the writer to flush it'. The inner iterator yields the Status and acknowledges the preparer in the finally around that yield (1823-1831), which runs when its consumer asks for the next frame. In production the consumer is the resilience loop of StreamPromptCoreAsync: IProviderHealthTracker is always registered (AddArcanumResilience, ServiceCollectionExtensions 1830), and ActivatorUtilities fills the optional parameter. That loop treats Status as pre-commit (IsPreCommitStreamingEvent, 1635-1639), adds it to preCommitBuffer and calls MoveNextAsync again at once (1372-1405). The preparer is released immediately and the protected Long Rest maintenance runs while the frame waits in the buffer. The buffer is only flushed when the provider commits (first token) or the attempt fails, so the 5-second hold never waits for the client. LongRestStatus_a_streamed_turn_with_protected_debt_says_so_once_before_the_assistant_begins builds the wizard without a health tracker and therefore tests the non-resilience path. More broadly, only 7 of 240 CreateWizard call sites in WizardIntelligenceProviderTests*.cs pass a tracker.

**Failure scenario.** With features.longRest and Covenant on, a streaming Session turn starts in a scope that has protected Long Rest debt. During the protected maintenance, which DESIGN says 'can take a while', the client gets no status. 'Finishing protected memory maintenance...' arrives only after all the paid maintenance sends, together with the first answer token. The existing test stays green.

**Evidence (probe).** Probe P1 ran the existing test body with and without healthTracker: new ProviderHealthTracker(...). Without the tracker: sendsWhenStatusArrived=0 of totalSends=3. With the tracker: sendsWhenStatusArrived=3 of totalSends=3. The frame order is identical in both runs (Status,Status,SessionBound,ConversationBound,Context,Token,Result), so an order-only assertion cannot catch it. The 3 existing LongRestStatus tests pass.

**Suggested fix.** Let this status pass the commit gate without being buffered. One way is a flush-now marker on the frame, or a separate emit-before-commit class in the resilience loop. Another is to acknowledge from the outer loop once it has actually yielded the frame. Re-run the LongRestStatus tests with a health tracker, or make the test CreateWizard default to one so it matches production.

#### RV-129 (Minor, reliability, api-contract, effort M): Claimed turns store the generic Hub.SessionTurnRestoredInterrupted/409 outcome for most failures, so an idempotent retry replays 409 instead of the real error (buffered and streaming disagree)

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:3759` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/session_turn_claims.sql`

**Claim.** Only one site records the turn's own typed error in the durable turn claim: the shared inferenceError block at 4298-4313, which passes interruptionOutcome: SessionTurnClaimOutcome.RestoredInterrupted(code, ResolveStatusCode(code), []). In buffered mode three early resolves run first and finalize the handle, so the later typed call is a no-op: 3759-3764 (provider failure), 3297-3302 (context/cost gate) and 3339-3344 (Covenant gate). Every other failure exit resolves with no outcome as well: 2032, 2305, 2365, 2737, 2820, 2870, 3033 and 4627; the finally at 4861-4867 (structured-output failure, finalize failure, exceptions); ResolveDeferredTurnAsync at 1581; and CampaignRollupTurnSnapshot.DisposeAsync for a claim that never began. GrimoireTurnWriter.ResolveInterruptedAsync (206-207) then uses its default, RestoredInterrupted(Hub.SessionTurnRestoredInterrupted, 409), and completes the claim Discarded with that code. CampaignRollupTurnPreparer.ReplayAsync (700-703) replays the stored code. DESIGN.md line 2230 says the writer commits the 'typed interrupted outcome', and the session_turn_claims schema says those fields 'reconstruct their typed error envelope'.

**Failure scenario.** An API client sends a buffered POST /api/intelligence/ping with an Idempotency-Key on a claimed Session turn, which is the documented continue-then-replay disconnect path (API.md lines 294 and 402). The provider fails, or the context/cost gate refuses. The client's connection drops and it retries with the same key. For that key it now always gets 409 Hub.SessionTurnRestoredInterrupted ('This turn ended before a complete reply was produced'), not the original Hub.Error/500, Hub.ContextBudgetExceeded, Budget.Exceeded or guardrail code. The same request streamed replays the correct code. Guardrail, attachment, spell-routing, client-tool and accounting failures replay 409 in both modes. The first-party CLI and Forge send no key for turns.

**Evidence (probe).** Probe P2 ran in a scratch worktree. It is a new test in partial class WizardIntelligenceProviderTests, using the Task7 fixture with the real SessionTurnClaimStore. The provider throws InvalidOperationException, and the turn is run buffered and then streaming, each followed by a retry under the same TurnIdempotencyAmbient identity. Result: 'buffered: result=Hub.Error claim=Hub.SessionTurnRestoredInterrupted/409 replay=Hub.SessionTurnRestoredInterrupted | streaming: result=Hub.Error claim=Hub.Error/500 replay=Hub.Error'. No existing test asserts a claim's TerminalErrorCode after a wizard failure; all 468 Intelligence.WizardIntelligenceProvider* tests pass at ac0d2d8e.

**Suggested fix.** Pass a typed outcome at every place the turn resolves. Add one helper that builds SessionTurnClaimOutcome.RestoredInterrupted(error.Code, ArcanumErrorMapper.ResolveStatusCode(error.Code), []). Remove the three buffered early resolves (3297, 3339, 3759) and let the 4298 block resolve; it already applies ShouldDeferTurnToNextCandidate. Have FailureFrameAsync record the error so the finally resolves with it. Add a RecordTerminal(Error) on CampaignRollupTurnSnapshot that the wizard sets before scope disposal. Add buffered and streaming tests that assert the claim's TerminalErrorCode equals the returned error code.

#### RV-130 (Minor, reliability, api-contract, effort S): Native responseFormat is matched case-insensitively for the provider wire but case-sensitively for host schema validation, so 'JSON_SCHEMA' silently skips strict enforcement

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:4325` — related: `src/RetroDownfall.Arcanum.Api/Tower/CanonicalPingResolution.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/OpenAi/OpenAiChatCompletionMapper.cs`

**Claim.** ApplyInferenceParameters (8159-8191) lower-cases PingRequest.ResponseFormat and sends the JSON schema to the provider. The two host checks compare case-sensitively: request.ResponseFormat is "json_schema" at 3077, which selects strict-mode token buffering, and at 4325, which selects validation and corrective retry. Native routes bind the value verbatim; there is no normalisation in CanonicalPingResolution or PingRequestBoundsValidator, while /v1 and the CLI do normalise. A strict schema request that spells the type in any other case gets no host validation, no retry and no warning.

**Failure scenario.** A native API client posts responseFormat 'JSON_SCHEMA' with a strict:true schema. The model returns JSON that violates the schema. The turn succeeds with that invalid answer and no warning, whereas 'json_schema' would have been corrected or failed.

**Evidence (probe).** Probe P3 sends a buffered turn with a strict schema; the first scripted answer {"attempt":0} is invalid and the second {"name":"accepted"} is valid. Result: 'json_schema: success=True calls=2 text={"name":"accepted"} warnings=1 | JSON_SCHEMA: success=True calls=1 text={"attempt":0} warnings=0'.

**Suggested fix.** Normalise ResponseFormat once (trim and lower-invariant) at the start of the turn and use the normalised value at 3077, 4325 and 8159, or compare with StringComparison.OrdinalIgnoreCase. Alternatively, reject unknown spellings in PingRequestBoundsValidator.

#### RV-131 (Minor, usability, error-message, effort S): Provider HTTP error text gives the same advice for 401, 404, 429 and 5xx

`src/RetroDownfall.Arcanum.Api/Intelligence/InferenceProviderFailureMessage.cs:18` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`, `tests/RetroDownfall.Arcanum.Tests/Intelligence/InferenceProviderFailureMessageTests.cs`

**Claim.** "Provider 'X' returned HTTP N. Check the model, API key, and request; see server logs for detail." is correct for 400/401/403/404 but misdirects for 408/429/5xx, where the action is to wait, check quota or switch provider. All of them are Hub.Error (HTTP 500), so a client cannot branch either; the test pins the advice for a provider 500.

**Failure scenario.** A rate-limited or overloaded provider yields 'returned HTTP 429/503. Check the model, API key, and request', sending the operator to audit a correct key and model.

**Evidence (trace).** InferenceProviderFailureMessage.cs:18; tests/.../InferenceProviderFailureMessageTests.cs:27 (expects the advice for HTTP 500); WizardIntelligenceProvider.cs:8798-8802 uses it for every provider status.

**Suggested fix.** Status-class-specific text (auth, model/endpoint, rate limit or quota, provider fault) and consider distinct typed codes for provider-rejected versus provider-unavailable.

#### RV-132 (Minor, usability, error-message, effort M): Failures that are not the provider's are reported to the operator as provider failures

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:1346` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/TurnEngine.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/PublicInferenceErrorMessages.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/InferenceProviderFailureMessage.cs`

**Claim.** Any exception escaping RunInferenceAttemptAsync before the first provider byte (Grimoire read/write, attachment preparation, spell routing, tool discovery) is caught in the resilient stream loop and formatted with BuildInferenceFailureMessage(candidateProvider, ex), i.e. "Provider 'X' is unreachable. Verify the service is running and Arcanum:Providers is configured correctly." On the buffered path the same exception reaches TurnEngine.ProduceAsync and becomes 'Inference failed. Ensure the provider is running and reachable, then try again.'

**Failure scenario.** The Grimoire is locked or failing while a streaming turn loads the Session: the operator is told the Ollama provider is unreachable and restarts it; the log line is the only hint that a SqliteException was the cause.

**Evidence (probe).** Probe S11: FakeGrimoireRepository.GetSessionAsync throws SqliteException('database is locked'). Stream: Error frame "Provider 'ollama-local' is unreachable. Verify the service is running and Arcanum:Providers is configured correctly." data=Hub.Error, provider calls=0; log "Provider ollama-local failed during pre-commit streaming (fallback candidate 1/1); exception type Microsoft.Data.Sqlite.SqliteException.". Buffered: code=Hub.Error msg='Inference failed. Ensure the provider is running and reachable, then try again. See server logs for details.' Code: WizardIntelligenceProvider.cs:1346-1349, 1439-1442, 1186, 1538; TurnEngine/TurnEngine.cs:140-147; PublicInferenceErrorMessages.cs:10; InferenceProviderFailureMessage.Build.

**Suggested fix.** Attribute by origin: only exceptions from lease construction or the model call get the provider wording and MarkFailed; everything else gets a distinct message and typed code (for example a local-component failure naming the component, 'see server logs'). Reword NativeGenericFailure so it does not assert the provider is the problem.

#### RV-133 (Minor, usability, diagnostics, effort S): Every inference-failure log line omits provider, model, HTTP status and session although the public text says 'see server logs for detail'

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:3557` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.cs`

**Claim.** Provider failures produce two Error lines (buffered) or one (stream) that contain only 'exception type X': 'Model call {id} failed for purpose ...' (ModelCallExecutor.cs:196), 'Hub inference failed' (3749), 'Streaming read failed' (3557), plus lines 1182, 1330, 1418, 1533. None carries provider, model, status code or session id, and only the first carries an id that appears nowhere else. Type-only logging is deliberate, but provider name, model and status code are the 'safe operation identifiers' the policy (DESIGN lines 1259, 3653) says to keep.

**Failure scenario.** With several providers configured, the public message names the provider but the server log cannot be matched to a request, session or model, so 'see server logs for detail' leads nowhere.

**Evidence (probe).** Probe S12: refused connection -> buffered log[Error] 'Model call 3251... failed for purpose MainInference; exception type System.Net.Http.HttpRequestException.' and log[Error] 'Hub inference failed; exception type System.Net.Http.HttpRequestException.'; streaming log[Error] 'Streaming read failed; exception type System.Net.Http.HttpRequestException.'

**Suggested fix.** Add {Provider}, {Model}, {HttpStatus} and {SessionId} to these lines and emit one line per failure.

#### RV-134 (Minor, usability, diagnostics, effort S): The 'model does not support tools' restart is logged at the wrong levels, without provider/model/config key, and buffered callers get no signal

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:3724` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.cs`, `docs/Compendium.README.md`

**Claim.** When an undeclared model rejects tools at runtime the turn restarts once without tools on every turn. The expected condition is logged as an Error ('Model call {id} failed for purpose MainInference', ModelCallExecutor.cs:196) followed by an Information line that names neither provider nor model nor the key that avoids the wasted call (providers.models.supportsTools=false). The buffered result has no warning (streaming emits a status line, still without the key). Only the English text 'does not support tools' is recognised, so other servers' wording ends as a bare 'HTTP 400'.

**Failure scenario.** An operator on a model that rejects tools pays a failed request on every turn and sees an Error line per turn in the logs, but cannot tell which model or what to configure; /v1 and buffered callers never learn tools were dropped.

**Evidence (probe).** Probe S2 (buffered): ok=True text='answered without tools' warnings=0 providerCalls=2; log[Error] 'Model call 3a13... failed for purpose MainInference; exception type System.InvalidOperationException.'; log[Information] 'Model does not support tools; retrying without local tools (exception type System.InvalidOperationException).' Probe S2b (stream): status 'This model does not support tools; continuing without local tools.' Code: WizardIntelligenceProvider.cs:3687-3746, 8698-8703. Compendium.README.md:111 documents the declaring key.

**Suggested fix.** Log the restart once per provider/model at Warning with {Provider}, {Model} and the supportsTools key; do not log the classified failure at Error in ModelCallExecutor; add the key to the status text and add a warning to the buffered result.

#### RV-135 (Minor, usability, error-message, effort S): The no-progress stop names no tool and gives no next step; its log carries only a hash

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:4255` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/ToolLoopProgressDetector.cs`

**Claim.** Hub.NoProgressDetected is a constant sentence ('Tool execution stopped because the latest call/result round repeated a recent round and produced no new evidence.') and the warning logs only the 8-byte round digest. The tool names of the repeated round are in toolLoopProgressEntries and are non-sensitive. DESIGN section 10.7.3 says a boundary error identifies the owner and the exact action.

**Failure scenario.** A long agentic turn is stopped; the operator cannot tell which tool looped, nor whether to change the request or fix the tool.

**Evidence (probe).** Probe S8 (two identical get_arcanum_system_info rounds): ok=False code=Hub.NoProgressDetected msg='Tool execution stopped because the latest call/result round repeated a recent round and produced no new evidence.' providerCalls=2; log[Warning] 'Tool loop stopped for no progress; recurring round signature 683A5DBB9F164B5F.' Code: WizardIntelligenceProvider.cs:4252-4270; ToolLoopProgressDetector.cs RepeatedSignature.

**Suggested fix.** Include the distinct tool names of the repeated round in the error and the log, plus a one-line hint.

#### RV-136 (Minor, usability, api-contract, effort S): An explicit spell version that cannot be loaded is silently dropped and the turn runs with no spell

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:5359` — related: `src/RetroDownfall.Arcanum.Api/Tower/SpellExecutionEndpoints.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/SpellScanner.cs`

**Claim.** For OverrideSpellPath (set only by POST /api/spells/{name}/execute?version=) a null SpellScanner.LoadFullAsync result returns Result.Success(null). The endpoint only checks File.Exists(versionPath). LoadFullAsync returns null for an oversize file, a frontmatter value rejected by SpellFrontmatterValidator, or a failed root/identity check. The same request by name fails loudly with Validation.SpellOverride.

**Failure scenario.** An operator executes spell 'foo' at version v2 whose SPELL.v2.md was hand-edited past the size limit or into an invalid value: the API returns 200 with an ordinary answer produced without the spell, with no error or warning.

**Evidence (probe).** Probe S9: valid SPELL.v1.md -> resolved; 2 MiB SPELL.v2.md -> 'success=True resolved=<null: turn runs with NO spell>'; contrast OverrideSpellName='does-not-exist' -> Error Validation.SpellOverride 'No spell matches OverrideSpellName ...'. Only the oversize trigger was reproduced; the other triggers are from reading SpellScanner.TryParseSpellFileAsync (SpellScanner.cs:1243+) and SpellFrontmatterValidator.ValidateScalar. Code: WizardIntelligenceProvider.cs:5355-5362; Tower/SpellExecutionEndpoints.cs:687-715.

**Suggested fix.** When the caller named a path, treat a null load as an error (Spell.NotFound or a new Spell.InvalidFile naming the file and the reason class).

#### RV-137 (Minor, usability, diagnostics, effort S): Retrieval failures (Saga, Tapestry, attachments, workspace index) are invisible: Debug logs are never emitted in production and context inspect gives one generic reason

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:6231` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.ContextPreview.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Logging/LoggingBootstrapper.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentRetrievalService.cs`

**Claim.** The exception handlers that turn a store/join/registration failure into 'continue without context' log at Debug (lines 5796, 5811, 5859, 5915, 6019, 6231, 6828, 8662). The host's Serilog floor is a hard-coded MinimumLevel.Information(), so those lines reach neither the rolling file, the ring buffer behind GET /api/logs, nor arcanum logs. The turn also carries no warning, and the operator's diagnostic (context inspect/mana) reports 'No effective content was available for this source.' for a disabled feature, a failed retrieval and a genuine no-hit alike.

**Failure scenario.** The Tapestry or Saga store throws on every turn (or RegisterWorkspace throws so codebase retrieval never works): answers silently lose context, nothing is logged at any visible level, and context inspect cannot distinguish it from 'nothing relevant'.

**Evidence (probe).** Probe S6: attachment retrieval throws InvalidOperationException; turn ok=True warnings=0; one entry 'log[Debug] Session attachment semantic retrieval failed; continuing without attachment context (exception type System.InvalidOperationException).'; 'entries at Information or above = 0'. Infrastructure/Logging/LoggingBootstrapper.cs:45 MinimumLevel.Information(); AuditLogPageReader.cs:246 comment states a Debug event reaches neither file, ring buffer nor arcanum logs. WizardIntelligenceProvider.ContextPreview.cs:1006 default arm of ResolveSourceReason. (Divination and Weave failures are already Warning in DivinationService.cs:111,178,238,599 and WeaveService.cs, so only store/join/registration exceptions are hidden.) The existing test AttachmentRetrievalFailure_LogsTypeOnly does not pin the level.

**Suggested fix.** Log these at Warning, type-only as the canary policy requires, with the source name (and session/workspace key where safe). Give ResolveSourceReason distinct texts: feature disabled (name the key), retrieval failed, no hit above threshold.

#### RV-138 (Minor, usability, noisy-log, effort S): Forwarded client tools log a Warning on every turn

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:7312`

**Claim.** BuildClientForwardedToolSet logs 'Client-supplied tools detected. These bypass Arcanum's tool loop and security controls.' at Warning each time it runs (once per provider attempt of every turn) for a feature the operator deliberately enabled with Arcanum:Features:ClientTools.

**Failure scenario.** An agent integration sends tools with every chat message: one identical Warning per message, no session/model/tool count, defeating any alert on Warning level.

**Evidence (probe).** Probe S3: 3 buffered turns with ForwardClientTools -> 'Warning-level Client-supplied tools detected lines across 3 turns = 3'. Code line 7312; call site 1749-1754.

**Suggested fix.** Warn once at startup/configuration time (or once per process); per turn use Debug or a counter.

#### RV-139 (Minor, usability, api-contract, effort S): toolPolicy readOnlyTools uses a stale allowlist and the API doc points at a DESIGN section that does not define it

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:7392` — related: `docs/Arcanum.API.md`, `docs/Arcanum.DESIGN.md`, `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.Registry.cs`

**Claim.** ReadOnlyToolNames contains 'read_lore', a name no tool registers (renamed to read_saga), while read_saga, web_search and read_url are absent; send_commlink_alert (outbound webhook), delegate_task and petition_dungeon_master are present. API.md says the policy's semantics are in DESIGN section 10.2.1, but that section never mentions readOnlyTools, so a client cannot learn what the policy advertises.

**Failure scenario.** A client that sends toolPolicy:readOnlyTools to keep a turn non-mutating loses Saga reads and web research, keeps a phantom entry, and can still trigger an outbound Comm Link alert; the only way to find out is to read the source.

**Evidence (probe).** Probe S5: allowlisted names that no tool registers: read_lore; registered/hub tools absent from the allowlist include read_saga, read_url, web_search; running the real ApplyToolPolicyFilters on [read_saga, read_lore, search_archives, web_search, read_url, list_directory, write_file, send_commlink_alert] keeps 'read_lore, search_archives, list_directory, send_commlink_alert'. tests/.../ArcanumInternalToolServerTests.cs:4122 asserts no tool named read_lore exists; ArcanumInternalToolServer.Registry.cs lists read_saga. docs/Arcanum.API.md:461 ('semantics in DESIGN section 10.2.1'); DESIGN 10.2.1 (lines 1263-1300) has no readOnlyTools text, the only DESIGN mention is line 1394. Existing ReadOnlyTools tests cover only forwarded client tools (write_file/read_file_chunk).

**Suggested fix.** Correct the name and decide on read_saga/web tools and the outbound tools; write the rule and list into DESIGN 10.2.1 and the API doc; add a test that every allowlisted name exists in ArcanumInternalToolServer.RegisteredToolNames plus the hub-native names.

#### RV-140 (Minor, usability, docs-mismatch, effort S): API.md documents reasoning-output resolution that no longer exists and omits the features.reasoningSummaries gate

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:7570` — related: `docs/Arcanum.API.md`, `docs/Compendium.README.md`, `src/RetroDownfall.Arcanum.Core/Configuration/ReasoningCapabilities.cs`

**Claim.** API.md:433 says an omitted reasoning_output resolves to 'full when SupportsFull, otherwise summary when SupportsSummary; AllowsClientOutput is required, and streaming also requires SupportsStreaming'. Those per-model facts were removed (ConfigurationValidator rejects them at startup; Compendium.README.md:119). The code resolves omitted -> Summary with no capability lookup and ignores its streaming parameter. A requested Summary/Full is silently None unless features.reasoningSummaries is true, which defaults to false (Compendium.README.md:177); the API doc never mentions this operator gate.

**Failure scenario.** A client sends reasoning_output:'full' to a reasoning-capable model, passes validation, and silently receives no reasoning; following API.md it cannot work out why.

**Evidence (probe).** Probe S7: ResolveClientReasoningOutput(null, capable, summariesOn, streaming) -> Summary; (Full, capable, summariesOff) -> None. Code: WizardIntelligenceProvider.cs:7570-7591 (the streaming parameter is unused), caller 1734-1738. Docs: Arcanum.API.md:433; ReasoningCapabilities.cs:57-70 still holds the dead properties; ConfigurationValidator.cs:37-38, 276-296; Compendium.README.md:119, 177.

**Suggested fix.** Rewrite the API.md paragraph (omitted -> summary; the operator gate; where reasoning facts live) and consider returning a warning when a requested output is suppressed.

#### RV-141 (Minor, usability, api-contract, effort S): Out-of-range sampling parameters and unknown response_format values are silently altered or ignored

`src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:8122` — related: `src/RetroDownfall.Arcanum.Core/Intelligence/PingRequestBoundsValidator.cs`, `src/RetroDownfall.Arcanum.Api/OpenAiChatCompletionRequestValidator.cs`, `docs/Arcanum.API.md`

**Claim.** ApplyInferenceParameters clamps temperature to [0,2], top_p to [0,1], penalties to [-2,2], ignores max_tokens <= 0 and unknown response_format strings, and turns response_format json_schema with no schema into plain JSON mode (which then skips schema validation). No bounds check exists in PingRequestBoundsValidator or OpenAiChatCompletionRequestValidator.

**Failure scenario.** A client sends temperature 7 or response_format json_schema without a schema and gets a normal 200 with different behaviour than requested; OpenAI answers 400 for these and the CLI contract validates 0..2.

**Evidence (probe).** Probe S7: requested temperature=7 top_p=3 presence=-9 max_tokens=-5 response_format=xml -> ChatOptions temperature=2 topP=1 presence=-2 maxOutput=(unset) responseFormat=null(ignored); response_format=json_schema with NO schema -> ChatResponseFormatJson. Docs: Command.Reference.md:430 (temperature 0 through 2); API.md:433 lists applied parameters without the clamp.

**Suggested fix.** Validate and return 400 naming the field, or document the clamp; reject a schema-less json_schema.

### U05: Arcanum.Api — Tower

#### RV-142 (Minor, performance, query-plan, effort S): Prompt list: the OR predicate defeats index order, and list routes read full rows (templates) to build summaries

`src/RetroDownfall.Arcanum.Api/Tower/PromptEndpoints.cs:44` — related: `src/RetroDownfall.Arcanum.Api/Tower/CampaignEndpoints.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/PromptRepository.cs`, `src/RetroDownfall.Arcanum.Api/Tower/PromptMapping.cs`

**Claim.** GET /api/prompts and GET /api/campaigns/{id}/prompts build PromptSummaryDto from PromptRepository.ListAsync (PromptRepository.cs:113-127), whose predicate is 'CampaignId = $c OR (CampaignId IS NULL AND $c IS NULL)'. SQLite plans it as MULTI-INDEX OR + USE TEMP B-TREE FOR ORDER BY, so every prompt in the scope (with its Template, ParameterSchema and DefaultParameters) is read and sorted before LIMIT 101, making the default 100-prompt page O(library size). With q or tag the handler forces limit 10,000 and filters in memory (PromptEndpoints.cs:44-83; CampaignEndpoints.cs:365-403 defaults to 10,000 even with no filter, and the Forge Atelier calls it with no limit).

**Failure scenario.** An operator with a few thousand prompts (large templates) sees every Atelier expansion and every filtered list read and discard tens of MiB and take 100-600 ms; allocations land on the LOH.

**Evidence (probe).** Probe P4 (U05GrimoireProbe.P4, 3,000 prompts x 8 KiB): EXPLAIN QUERY PLAN for the repository predicate = 'MULTI-INDEX OR ... USE TEMP B-TREE FOR ORDER BY'; for 'CampaignId IS $c' = 'SEARCH Prompts USING INDEX IX_Prompts_CampaignId_Name (CampaignId=?)' + 'USE TEMP B-TREE FOR LAST 2 TERMS OF ORDER BY'. Default page: 35-115 ms / 1.8 MiB (OR predicate) versus 0.2-2.0 ms (IS predicate, summary columns 0.2 ms). Filtered path: 100-577 ms and 52.8 MiB allocated per call.

**Suggested fix.** Use 'CampaignId IS $campaignId' in PromptRepository.ListAsync; give the list routes a summary-column projection; apply q/tag in SQL (LIKE on name/description, an indexed tag lookup) so paging and HasMore stay honest.

#### RV-143 (Minor, performance, unbounded-growth, effort S): DELETE /api/saga?confirm=true retains one result object per memory

`src/RetroDownfall.Arcanum.Api/Tower/SagaEndpoints.cs:462` — related: `src/RetroDownfall.Arcanum.Api/Security/CovenantSensitiveDeletion.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantSensitiveRetentionPurgeCoordinator.cs`

**Claim.** PurgeEveryLabeledSagaAsync pages the walk at 128 so it never holds an unbounded identity list, but results.AddRange(purged.Value.Results) (:462) keeps a CovenantSensitivePurgeResult for every memory, unlabelled ones included, only so MarkProtectedWhenPurged can later ask AllUnlabeled. The purger also issues one TryReadLabelAsync statement per target (CovenantSensitiveRetentionPurgeCoordinator.ResolveLabelsAsync).

**Failure scenario.** Clearing a Saga with a million memories holds about 92 MiB of result records (plus List-growth garbage) for the length of the request.

**Evidence (probe).** Probe P6 (U05MicroProbe.P6): 100,000 results retain 9.6 MiB (100 B each); 1,000,000 retain 92.4 MiB (97 B each).

**Suggested fix.** Keep a boolean for 'anything other than Unlabeled' and retain only Purged/Blocked results.

#### RV-144 (Minor, performance, full-scan, effort M): Session Divination ranks the whole installation, filters afterwards, and may scan twice

`src/RetroDownfall.Arcanum.Api/Tower/SessionDivinationEndpoints.cs:134` — related: `src/RetroDownfall.Arcanum.Infrastructure/Weave/DivinationService.cs`

**Claim.** POST /api/sessions/divine searches entry_embeddings with no filter (managed path; vec0 can never load, WeaveIndexAvailability) and applies the Campaign and status filters to the hits afterwards (JoinSessionMetadataAsync). When the first 4 x limit window leaves at most limit survivors and the ranking is not exhausted it runs a second complete scan at window 500 (:122-170). The filter is not pushed into the scan the way SearchCampaignScopedAsync does for Saga.

**Failure scenario.** With SessionSearch enabled and a Campaign filter that excludes most entries, one query costs two full scans of every embedding (about 4 KB read, decrypted and scored per row).

**Evidence (probe).** Probe P8 (U05GrimoireProbe.P8): DivinationService.SearchAsync('entry_embeddings_vec', ...) over 40,000 x 1024-dim embeddings (156 MiB): window 40: 1,733, 1,950, 1,950 ms; window 500: 2,041, 1,930, 1,888 ms - the cost is the scan, not the window. Reachable only with Features.Embeddings and Features.SessionSearch on; load average 56-106 during the run.

**Suggested fix.** Push the Campaign/status filter into the scan with an Entries -> Sessions join (as SearchManagedCampaignScopedAsync does for Saga), or confine the second pass to rows the first window could not reach.

#### RV-145 (Minor, performance, missing-index, effort M): GET /api/sessions/analytics scans the whole Entries table for the by-model counts

`src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs:98` — related: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/Entries.sql`

**Claim.** The route calls SessionRepository.GetAnalyticsAsync, whose ReadEntryCountsByModelAsync (SessionRepository.cs:1118-1130) runs 'SELECT ModelUsed, COUNT(*) FROM Entries WHERE ModelUsed <> '' GROUP BY ModelUsed'. ModelUsed has no index (Data/Schema/Tables/Entries.sql) and sits after Content in the record, so the whole table including content pages is read.

**Failure scenario.** 'arcanum session analytics' on a large Grimoire takes hundreds of ms to seconds, growing with total transcript bytes, while the other two statements it runs take a few ms.

**Evidence (probe).** Probe P5: EXPLAIN QUERY PLAN = 'SCAN Entries / USE TEMP B-TREE FOR GROUP BY / USE TEMP B-TREE FOR ORDER BY'; 232-356 ms at 60,000 entries (88 MiB), versus 3-9 ms for the sessions and role statements (covering indexes). Only the CLI calls it (ArcanumApiClient.cs:1172).

**Suggested fix.** Add a partial index on Entries(ModelUsed) WHERE ModelUsed <> '' in Entries.sql with its transition file, so the GROUP BY runs on the index.

#### RV-146 (Minor, reliability, data-integrity, effort M): Filtered prompt listings and Campaign export silently stop at 10,000 prompts

`src/RetroDownfall.Arcanum.Api/Tower/CampaignEndpoints.cs:938` — related: `src/RetroDownfall.Arcanum.Api/Tower/PromptEndpoints.cs`

**Claim.** Export reads promptRepo.ListAsync(id, ListQueryLimit(10_000)) and ignores promptPage.HasMore (937-941), although the same method names omitted spells 'so a partial bundle never reads as complete' (903-905). A q/tag filter on GET /api/prompts (PromptEndpoints.cs:44-50, 77-78) and GET /api/campaigns/{id}/prompts (365-375, 401-402) reads the first 10,000 rows, filters in memory, and answers hasMore:false.

**Failure scenario.** A Campaign with more than 10,000 prompts exports a bundle missing the remainder with no omission listed; a 'replace' import of it elsewhere deletes the target's prompts and writes only 10,000. A filtered search misses matches beyond row 10,000 while claiming completeness.

**Evidence (trace).** Trace (lines in claim; ArcanumSettingClamps.ListQueryLimit clamps to 10,000). Not executed (needs >10,000 prompts).

**Suggested fix.** Page the repository until HasMore is false for export (or refuse with a typed error); push q/tag filtering into SQL, or return hasMore:true when the 10,000-row read itself had more.

#### RV-147 (Minor, reliability, data-integrity, effort S): Memory search is case-insensitive for ASCII only

`src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs:1209`

**Claim.** The session, summary, attachment and workspace predicates use instr(lower(col), lower(@query)) (1209, 1272, 1335-1337, 1417-1418); SQLite's built-in lower() folds ASCII only, while the Lexicon arm uses .NET OrdinalIgnoreCase (1619-1624). API.md promises 'Case-insensitive persisted-memory inspection'.

**Failure scenario.** An entry 'Meet at the Café Étoile' is not found by 'CAFÉ' or 'étoile'; the operator concludes the memory does not exist.

**Evidence (probe).** Probe P9: 'café' -> 1, 'CAFÉ' -> 0, 'étoile' -> 0, 'ÉTOILE' -> 1, 'cafe' -> 0.

**Suggested fix.** Register an AOT-safe Unicode fold function on the connection (SqliteConnection.CreateFunction with ToLowerInvariant) and use it in the four predicates, or document ASCII-only folding in API.md.

#### RV-148 (Minor, reliability, error-message, effort S): Genuine Grimoire connection faults are reported as planned maintenance and logged only at Debug

`src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs:1752` — related: `src/RetroDownfall.Arcanum.Api/Tower/SessionDivinationEndpoints.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireOrdinaryConnectionFactory.cs`, `src/RetroDownfall.Arcanum.Api/Middleware/ArcanumExceptionHandler.cs`

**Claim.** OpenConnectionAsync (MemoryEndpoints.cs:1752-1755) and JoinSessionMetadataAsync (SessionDivinationEndpoints.cs:236-239) throw GrimoireMaintenanceUnavailableException for any AcquireScopedAsync failure. The factory deliberately separates MaintenanceUnavailable() from Refused(...) (Covenant.Unavailable: native runtime unavailable, connection could not be opened, no admitted provenance, non-canonical target; GrimoireOrdinaryConnectionFactory.cs:78-143, remark 496-504). ArcanumExceptionHandler.cs:61-80 answers the maintenance exception as a retryable 503 with a Debug log.

**Failure scenario.** A SQLCipher open failure (passphrase source or disk error) makes /api/memory/status|sources|search|explain and /api/sessions/divine tell the operator to wait for maintenance that is not happening, with no Error log; other routes surface the same fault as a logged 500 (GrimoireRepository.cs:1426-1429).

**Evidence (trace).** Trace through the factory, the two call sites and the exception handler (line numbers in claim); not executed because it needs a broken native open.

**Suggested fix.** Throw the maintenance exception only when acquired.Error.Code == GrimoireMaintenanceUnavailableException.Code; otherwise return the Result error through ArcanumErrorMapper or throw InvalidOperationException(acquired.Error.Message) so it is logged at Error.

#### RV-149 (Minor, reliability, cancellation, effort S): Provider connectivity probe has no deadline once response headers arrive

`src/RetroDownfall.Arcanum.Api/Tower/ProviderTestEndpoints.cs:144` — related: `src/RetroDownfall.TheForge.Ux/Services/Services/ModelService.cs`

**Claim.** The probe sets HttpClient.Timeout = 5 s (109-114) but uses HttpCompletionOption.ResponseHeadersRead (129-131); that timeout covers only SendAsync, and TryReadCappedStringAsync (144, 212-234) reads the body on the request token alone. An endpoint that sends headers and stalls holds the probe until the caller disconnects. Caller cancellation is also caught as TaskCanceledException ('timed out') or the generic Exception arm (Warning log).

**Failure scenario.** The Forge 'Test provider' against a wedged proxy or a server mid-crash spins until its 100-second headers deadline and reports a transport error; the server-side probe runs that long too.

**Evidence (probe).** Probe P6: loopback TcpListener answering 'HTTP/1.1 200 OK', Content-Length: 1000, then one body byte and silence. 'provider test outcome after 20.0s: client gave up (ProbeTimeout is 5s)'. The route is [ExcludeFromCodeCoverage] with no tests.

**Suggested fix.** Use one CancellationTokenSource(ProbeTimeout) linked to the request token for both GetAsync and the capped body read; report its expiry as the timeout result and let request cancellation propagate.

#### RV-150 (Minor, reliability, data-integrity, effort S): Session status filters are case-sensitive in SQL and unvalidated, returning silent empty pages

`src/RetroDownfall.Arcanum.Api/Tower/SessionDivinationEndpoints.cs:284` — related: `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs`, `src/RetroDownfall.Arcanum.Api/Tower/CampaignEndpoints.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs`

**Claim.** Divine allow-lists status OrdinalIgnoreCase (83-91) but then binds the caller's casing into `s."Status" = @status` (284-288); stored values are always lowercase. Its own comment says the allow-list exists to avoid 'silently matching zero rows'. GET /api/sessions and /api/campaigns/{id}/sessions pass any status string to SessionRepository (86, 126-129), where only 'all' is case-insensitive.

**Failure scenario.** `status=Archived` on divine or list returns an empty, successful answer; a typo such as 'archvied' on the list routes does the same.

**Evidence (probe).** Probe P7 (one archived session): GET /api/sessions?status=archived -> 1, Archived -> 0, ARCHIVED -> 0, bogus -> 0, all 200.

**Suggested fix.** Lowercase after the allow-list in divine; validate status on the two list routes against active|archived|all (400 otherwise) and lowercase it.

#### RV-151 (Minor, reliability, data-integrity, effort S): Default context-pin label is cut inside a surrogate pair

`src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs:1230`

**Claim.** The default label is target[..157] + "..." (1229-1231), which splits a surrogate pair; the lone high surrogate becomes U+FFFD on the UTF-8 round trip. Utf8Truncation.SafeCharSliceLength exists for exactly this and is used in SessionDivinationEndpoints.cs:314.

**Failure scenario.** A pin whose target has an emoji at UTF-16 position 157 is stored and shown with a replacement character in its label.

**Evidence (probe).** Probe P4 (156 ASCII chars + U+1F600 + 10 chars): stored label 'label length=160 last3=[FFFD,002E,002E,002E]'.

**Suggested fix.** target[..Utf8Truncation.SafeCharSliceLength(target, 157)] + "...".

#### RV-152 (Minor, reliability, data-integrity, effort S): POST /api/sessions/{id}/entries persists an undefined MessageRole

`src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs:1289` — related: `src/RetroDownfall.Arcanum.Core/Tower/SessionDtos.cs`, `src/RetroDownfall.Arcanum.Core/Storage/Entities/MessageRole.cs`

**Claim.** The append route stores Role = request.Role with no Enum.IsDefined check (1286-1292); MessageRole's JsonStringEnumConverter accepts integers by default, and Entries.Role has no CHECK constraint.

**Failure scenario.** {"role":7,"content":"x"} is stored; EntryDto then reports role "7", history assembly maps it to user (InferenceContextBuilder.cs:370-380), compaction counts it as user; a client that maps role strings strictly cannot render the transcript. (The usability report found the sibling: an omitted role silently stores system.)

**Evidence (probe).** Probe P5: 'POST entry role=7 -> 200 {"data":{...,"role":"7",...}}' and GET entries returns '"role":"7"'.

**Suggested fix.** Refuse a missing or undefined role with Validation.InvalidBody (make Role nullable in AppendEntryRequest and check Enum.IsDefined), or use a string-only enum converter as MemorySearchActionKind does.

#### RV-153 (Minor, reliability, race, effort S): Pinned-entry cap is check-then-act and a pin of a vanished entry reports success

`src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs:1818` — related: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs`

**Claim.** The pin route counts pins (1818) and then calls SetEntryPinnedAsync (1832) outside the per-session write lock, which GrimoireRepository.SetEntryPinnedAsync (805-824) takes only around its own UPDATE; the bool it returns is ignored here and in unpin (1917).

**Failure scenario.** Two concurrent pins at MaxPinnedEntries-1 both pass the count and both commit, exceeding the cap; an entry deleted between GetEntryByIdAsync and the UPDATE answers 200 true although nothing was pinned.

**Evidence (trace).** Trace of SessionEndpoints.cs:1800-1835 and GrimoireRepository.cs:799-825; not executed.

**Suggested fix.** One conditional UPDATE under the session lock that also checks the current pinned count, mapping 0 rows to Session.EntryNotFound or Session.TooManyPinned.

#### RV-154 (Minor, reliability, data-integrity, effort S): Manual compaction deletes transcript sized to the default model's provider, not the Session's model

`src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs:1870` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/ContextCompressionService.cs`

**Claim.** POST /api/sessions/{id}/compact irreversibly deletes the oldest non-pinned entries until the transcript fits a threshold of contextWindowLimit (API.md /compact). The limit comes from ProviderResolver.TryResolveProviderForModel(settings, settings.DefaultModel, ...) with an 8192 fallback (1866-1874), not from the model the Session used; API.md does not say which window governs.

**Failure scenario.** Default model is an 8192-context local model, the Session was conducted on a 128k-context provider; `arcanum session compact` deletes nearly the whole transcript instead of trimming to the Session model's window.

**Evidence (trace).** Trace: SessionEndpoints.cs:1866-1878 and ContextCompressionService.CompressSessionAsync (177-217) uses only the passed limit. Not executed.

**Suggested fix.** Resolve the window from the Session's most recent Entry.ModelUsed (falling back to the default model) or accept an explicit model on the request, and document the rule in API.md.

#### RV-155 (Minor, usability, api-contract, effort S): An unconvertible optional query value returns a bare 400 with no body, contradicting API.md section 8.4

`src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs:728` — related: `docs/Arcanum.API.md`, `src/RetroDownfall.Arcanum.Api/ApiRequestJson.cs`, `src/RetroDownfall.Arcanum.Api/Tower/SagaEndpoints.cs`, `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs`, `src/RetroDownfall.Arcanum.Api/Tower/CampaignEndpoints.cs`

**Claim.** API.md:364 says a query, route or header value the binder cannot convert is thrown (ThrowOnBadRequest pinned on) and answered 400 Validation.InvalidBody 'Request parameters could not be bound to this route.'. That holds for a missing required value, but for an unparsable optional value (int?, Guid?, DateTimeOffset?, enum?, bool?) the Request Delegate Generator emits 'wasParamCheckFailure = true;' without calling ParameterBindingFailed and then sets StatusCode 400 and returns, so ThrowOnBadRequest never fires; the status-code hook deliberately leaves bare 400s alone. About 25 Tower routes carry such parameters.

**Failure scenario.** A script or The Forge sends GET /api/saga?limit=abc, /api/sessions?role=bogus, /api/campaigns?type=bogus, /api/sessions?from=notadate or DELETE /api/saga?confirm=yes and gets 400 with content-length 0: no envelope, no code, no indication of which parameter failed.

**Evidence (probe).** Probe Probe_BindingFailureShapes: GET /api/saga?limit=abc, /api/lore?limit=abc, /api/campaigns?type=bogus => '400 ct= len=0 body='; GET /api/campaigns/by-path (required path missing) => 400 envelope 'Request parameters could not be bound to this route.'. Generated GeneratedRouteBuilderExtensions.g.cs (EmitCompilerGeneratedFiles dump) for each optional parameter: 'else { wasParamCheckFailure = true; }' ... 'if (wasParamCheckFailure) { httpContext.Response.StatusCode = 400; return; }' with no logOrThrowExceptionHelper call. ApiBootstrapper.cs:719-727 comment: the hook leaves bare 400s untouched because GET /api/presence returns one on purpose. ArcanumExceptionHandlerTests (~817-849) only feed a synthetic BadHttpRequestException into the handler, so a host-level bad optional value is untested.

**Suggested fix.** Extend the status-code hook to put the envelope on a bare 400 for bodiless methods (GET/DELETE) except endpoints that opt out (presence), naming the query keys the caller sent; or correct API.md:364. Add a host-level test with a bad optional value.

#### RV-156 (Minor, usability, error-message, effort S): A wrong value or type in a syntactically valid JSON body is reported as 'could not be parsed as valid JSON' with no field

`src/RetroDownfall.Arcanum.Api/ApiRequestJson.cs:277` — related: `src/RetroDownfall.Arcanum.Api/Middleware/ArcanumExceptionHandler.cs`, `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs`, `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs`

**Claim.** Every JsonException from deserialization (bad enum spelling, string where a number is expected, and so on) is mapped to the MalformedJsonMessage 'Request body could not be parsed as valid JSON.' with details:null, both for routes that read their own body (ApiRequestJson.ReadAsync) and for bound parameters (ArcanumExceptionHandler -> ResolveBodyFault). The JSON is valid and the exception's Path (for example $.role, no value) is available but dropped.

**Failure scenario.** An operator posts role 'Assistant ' or scope 'workspaces' or limit '50' and is sent to a JSON syntax checker; nothing names the field. This affects every bound body in the Tower unit (role, scope, limit, campaignId, enum fields).

**Evidence (probe).** Probe Probe_EntryRoleAndEnumMessages: {"role":"bogus","content":"x"}, {"role":"user","content":5} (entries) and {"query":"x","scope":"bogus"}, {"query":"x","limit":"abc"} (memory search) => all 400 Validation.InvalidBody 'Request body could not be parsed as valid JSON.' details:null. ApiRequestJson.cs:18 and ResolveBodyFault lines 276-278 ('failure?.InnerException is JsonException => MalformedJsonMessage').

**Suggested fix.** Put JsonException.Path into Error.Details and reword the message to 'Request body is not valid for this route (see details)', keeping the syntax wording for a true parse failure (distinguish by JsonException.LineNumber/Path).

#### RV-157 (Minor, usability, diagnostics, effort S): A provider-originated cancellation ends an NDJSON stream with 'See server logs for details' but writes no log line

`src/RetroDownfall.Arcanum.Api/Tower/InferenceExecuteWriter.cs:300` — related: `tests/RetroDownfall.Arcanum.Tests/Api/Tower/InferenceExecuteWriterTests.cs`, `docs/Arcanum.DESIGN.md`

**Claim.** The catch (OperationCanceledException) arm (lines 300-345) writes the generic error frame, whose text ends 'See server logs for details.', but contains no logger call; the sibling catch (Exception) arm (361-368) logs the exception type, responseStarted and trace. DESIGN.md:1261 says an OperationCanceledException observed while the caller token is not cancelled is a provider failure whose logs carry safe metadata and the exception type.

**Failure scenario.** A provider HTTP timeout surfacing as TaskCanceledException while the client is still connected yields the generic failure frame and nothing in the log or ring buffer, so the operator told to read the logs finds no record. The existing test pins the frame but not a log entry.

**Evidence (probe).** Probe U05WriterProbeTests (same harness as InferenceExecuteWriterTests): TaskCanceledException with RequestAborted not cancelled => frame {"type":"error","message":"Inference failed. Ensure the provider is running and reachable, then try again. See server logs for details.","data":"Hub.Error"} and logEntries=0; InvalidOperationException => same frame and logEntries=1. InferenceExecuteWriterTests.cs:344-359 asserts only the frame. Whether production providers surface a raw OCE here depends on Api/Intelligence, outside this unit.

**Suggested fix.** Log the exception type, responseStarted and Activity.Current?.Id ?? TraceIdentifier in the OCE arm before writing the frame (type only, as the design requires); add a log assertion to the OCE test.

#### RV-158 (Minor, usability, api-contract, effort S): POST /api/prompts without a template is a 500 Hub.Unhandled

`src/RetroDownfall.Arcanum.Api/Tower/PromptEndpoints.cs:143` — related: `src/RetroDownfall.Arcanum.Api/Tower/PromptMapping.cs`, `src/RetroDownfall.Arcanum.Core/Tower/PromptDtos.cs`

**Claim.** CreatePrompt validates only name and version (line 143) and copies request.Template (line 179). CreatePromptRequest.Template is a non-nullable positional parameter but System.Text.Json passes null for a missing or null value, and Prompts.Template is NOT NULL, so the insert throws an unclassified exception that surfaces as a 500 with an Error log. Import, clone and update all validate their inputs.

**Failure scenario.** An API client or script posts {"name":"p","version":"1"} and is told 'An internal error occurred.' (Hub.Unhandled) for what is a missing field.

**Evidence (probe).** Probe Probe_PromptCreate_NoTemplate: {"name":"p-no-template","version":"1"} => 500 Hub.Unhandled 'An internal error occurred.'; {"...","template":null} => 500; with "template":"hi" => 201. PromptImportHelper.Validate (PromptMapping.cs:99-103) already rejects a null template with Prompt.InvalidRequest. Schema/Tables/Prompts.sql: '"Template" TEXT NOT NULL'.

**Suggested fix.** Reject a null Template with Prompt.InvalidRequest (400) naming 'template' (reuse PromptImportHelper.Validate) and add the missing test.

#### RV-159 (Minor, usability, api-contract, effort S): GET /api/saga?sessionId=<unknown> answers 200 with an empty list while every memory route answers 404 Session.NotFound

`src/RetroDownfall.Arcanum.Api/Tower/SagaEndpoints.cs:84` — related: `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs`

**Claim.** The saga listing resolves the session scope with scopeResolver.ResolveForSessionAsync and filters by the session id without checking the session exists, so an unknown or mistyped session id is a successful empty page. memory status/sources/explain/search for an unknown session return 404 Session.NotFound.

**Failure scenario.** arcanum saga list --session <typo'd id> shows no memories and exits 0, indistinguishable from a session with no Saga memories.

**Evidence (probe).** Probe Probe_MissingSessionAndMisc: GET /api/memory/status/{missing}, /sources/{missing}, /explain/{missing} and POST /api/memory/search with sessionId => 404 Session.NotFound; GET /api/saga?sessionId={missing} => 200 {"data":[],...}. API.md:90 does not document an empty result for an unknown session.

**Suggested fix.** Check the session exists (404 Session.NotFound) when sessionId is supplied, as the memory routes do.

#### RV-160 (Minor, usability, api-contract, effort S): Status filters are case-sensitive: divine accepts 'Archived' then matches nothing, and GET /sessions silently returns an empty page for any non-exact status

`src/RetroDownfall.Arcanum.Api/Tower/SessionDivinationEndpoints.cs:84` — related: `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs`, `docs/Arcanum.DESIGN.md`

**Claim.** The divine route validates status with StringComparer.OrdinalIgnoreCase (line 84) but binds statusFilter.Trim() unchanged to 's."Status" = @status' (lines 284-288), a BINARY comparison, so 'Active', 'ARCHIVED' and similar pass validation and match zero rows, the exact outcome the allow-list comment says it prevents. GET /api/sessions (and campaigns/{id}/sessions) does not validate status at all.

**Failure scenario.** arcanum session divine q --status Archived (or an API client sending 'Archived') returns 200 with no hits although archived sessions match; GET /api/sessions?status=bogus returns 200 empty instead of 400, while PATCH /api/sessions/{id} validates and normalizes the same field.

**Evidence (probe).** Probe Probe_Divine_StatusCase (one active and one archived embedded session): status 'active' => 1 hit; 'Active' and 'ACTIVE' => 200 sessions=[]; 'archived' and ' archived ' => 1 hit; 'Archived' and 'ARCHIVED' => 200 sessions=[]. Probe_BindingFailureShapes: GET /api/sessions?status=Archived and ?status=bogus => 200 {"summaries":[]...}. SessionRepository.cs:86 'statusFilter = ... request.Status.Trim()' and the "Status" = condition (only 'all' is case-insensitive). No existing test sends a status of different case.

**Suggested fix.** Lower-case the validated status in divine; validate and normalize status in the list/campaign-sessions query (400 Session.InvalidStatus for an unknown value, keep 'all').

#### RV-161 (Minor, usability, api-contract, effort S): POST /api/sessions/{id}/entries without 'role' silently stores a system entry

`src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs:1286` — related: `src/RetroDownfall.Arcanum.Core/Tower/SessionDtos.cs`, `src/RetroDownfall.Arcanum.Core/Storage/Entities/MessageRole.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs`

**Claim.** AppendEntryRequest.Role is a non-nullable MessageRole whose first member (value 0) is System, so a body with no role deserializes to System; the handler validates only content (line 1277) and SessionRepository.AddEntryAsync applies no role rule, so the entry is stored and later replayed to the model as a system message.

**Failure scenario.** A client forgets 'role' on append-entry and a system instruction is injected into the transcript the next turn sends to the model; the first-user-message auto-title also never runs. Nothing tells the caller.

**Evidence (probe).** Probe Probe_EntryRoleAndEnumMessages: {"content":"hello"} => 200 {"role":"system","content":"hello"} and GET entries lists role system. Core/Tower/SessionDtos.cs:16 'AppendEntryRequest(MessageRole Role, string Content, string? ModelUsed = null)'; Core/Storage/Entities/MessageRole.cs lists System first. SessionRepository.AddEntryAsync (Infrastructure/Repositories/SessionRepository.cs:419-469) has no role check.

**Suggested fix.** Make Role nullable and answer 400 Validation.InvalidBody 'role is required (system, user, assistant or tool)' when absent.

#### RV-162 (Minor, usability, error-message, effort S): 'Memory management is disabled.' names no configuration key on four routes whose gate defaults to off

`src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs:1721` — related: `docs/Arcanum.Command.Reference.md`, `docs/Compendium.README.md`, `src/RetroDownfall.Arcanum.Cli/Services/CliFailureExit.cs`

**Claim.** Delete-entry, pin, compact and unpin refuse with Session.MemoryManagementDisabled and the bare text 'Memory management is disabled.' (lines 1721, 1796, 1852, 1899). The gate is Arcanum:Features:MemoryManagement (features.memoryManagement, default false), so every operator's first session delete-entry, pin-entry or compact hits it. Sibling messages in the same unit do name their keys.

**Failure scenario.** arcanum session compact prints only 'Memory management is disabled.'; CliFailureExit.Annotate adds nothing for non-connection errors and the Command Reference says only 'when memory management is enabled' (delete-entry does not mention the gate), so the operator has to search the docs for the key.

**Evidence (probe).** Probe Probe_MemoryManagementDisabledAndSagaConfirm: POST compact and DELETE entry => 400 {"code":"Session.MemoryManagementDisabled","message":"Memory management is disabled."}. Compendium.README.md:180 '| features.memoryManagement | bool, false | ...'. ErrorCodes.cs:296 documents the key. Compare SessionDivinationEndpoints.cs:73 and CampaignPathPolicy 'outside Arcanum:Security:CampaignRoots'.

**Suggested fix.** Message: 'Session memory management is disabled; set Arcanum:Features:MemoryManagement (features.memoryManagement) to true.' Mention the gate in the Command Reference rows for delete-entry, pin-entry, unpin-entry and compact.

### U06: Arcanum.Cli — CommandCenter (1)

#### RV-163 (Minor, reliability, crash-consistency, effort S): If Application.Init fails after touching the terminal, the app is never disposed and terminal modes are never restored

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterApp.cs:113`

**Claim.** CreateAndInit (lines 387-410) creates the IApplication and calls Init() inside a local. If Init throws after the driver switched to the alternate screen, raw mode or mouse modes, the outer 'app' stays null and terminalTouched stays false. The finally therefore neither disposes the driver nor calls RestoreTerminalModes.

**Failure scenario.** A partial Init failure leaves the shell on the alternate screen or with mouse reporting on, and the start-failure message is written into that broken state.

**Evidence (trace).** Trace only: CommandCenterApp.cs:113-114 (terminalTouched set only after CreateAndInit returns) and 143-167 (restore conditional on terminalTouched; app null).

**Suggested fix.** Create the app, assign it to the outer variable and set terminalTouched=true before calling Init(), so the finally disposes it and restores the terminal.

#### RV-164 (Minor, reliability, unbounded-work, effort S): Attachment drift monitor revalidates on every file event in the whole working tree; each call double-SHA-256s every tracked source

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterAttachmentDriftMonitor.cs:304` — related: `src/RetroDownfall.Arcanum.Api/Tower/SessionEndpoints.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs`

**Claim.** A recursive FileSystemWatcher over the working directory signals on every create, change, delete or rename. After a 150 ms debounce the pump GETs /api/sessions/{id}/attachments, which runs RevalidateBoundSourcesAsync: an open, a containment check and a double SHA-256 per distinct tracked file. This happens even when the session has no tracked attachments, and events are never filtered to the tracked paths.

**Failure scenario.** A build, git checkout, npm install, or a launch from ~ on macOS (where ~/Library churns) keeps the CLI polling the host about 4-6 times a second and the host re-hashing every attached workspace file for as long as the churn lasts.

**Evidence (probe).** Probe 4 (real monitor, fake host, temp workspace): 'idle baseline GETs=1; GETs during 4.7s of unrelated obj/ writes (+1s tail)=22; session attachments=0'. Server cost per call: SessionEndpoints.cs:243-288 → SessionAttachmentStore.Lifecycle.cs:494-560 (the comment there says 'double-SHA-256 pass').

**Suggested fix.** Skip the fetch when no row is a WorkspaceFile source. Ignore events whose path is not a tracked SourceRelativePath (keep overflow and error as a full revalidate). Add a minimum interval of about 1 s between fetches.

#### RV-165 (Minor, reliability, crash-consistency, effort S): A FileSystemWatcher failure at start-up ends the whole Command Center as CrashedAfterStart

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:254` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterAttachmentDriftMonitor.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterApp.cs`

**Claim.** attachmentDriftMonitor.Start runs unguarded inside the buildAndWire lambda. new FileSystemWatcher(...) or EnableRaisingEvents=true (CommandCenterAttachmentDriftMonitor.cs:79-109) can throw (macOS FSEventStreamStart failure, Windows directory-handle failure, network or FUSE mounts). The exception propagates to CommandCenterApp.RunAfterStart, which reports CrashedAfterStart, so an optional badge feature takes down the session.

**Failure scenario.** Running arcanum from a mount whose watcher fails to start prints 'Command Center stopped unexpectedly' and exits 1 right after start-up.

**Evidence (trace).** Trace only: CommandCenterHost.cs:254 (no try/catch) → CommandCenterApp.cs:175-188 (catch → CrashedAfterStart) → CommandCenterHost.cs:679-686 (prints RuntimeFailureMessage, returns 1).

**Suggested fix.** Wrap Start in a try/catch, log at Warning, and continue without the watcher. The post-turn and slash RequestRefresh calls still refresh the badges.

#### RV-166 (Minor, reliability, cancellation, effort S): Quitting with an unresponsive auto-launched host waits up to five minutes before the process exits

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:793` — related: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.cs`

**Claim.** StopHostIfWeStartedItAsync calls QuitServerAsync(CancellationToken.None) on the short-call client. Its only bound is the 5-minute response-headers deadline (ArcanumApiClient.DefaultRequestResponseHeadersTimeout). After Ctrl+Q the UI is gone but the process lingers. The SIGTERM/SIGHUP backstop does not apply to a normal quit.

**Failure scenario.** If the owned host is wedged (deadlocked or stopped), the operator quits and the arcanum process does not return to the shell for up to 5 minutes.

**Evidence (trace).** Trace only: CommandCenterHost.cs:784-810; ArcanumApiClient.cs:46 (5-minute headers timeout) and 242-372 (SendRequestAsync on RequestHttpClientName).

**Suggested fix.** Run the quit on a linked source with a short timeout (for example 5 s) and log if it expires.

#### RV-167 (Minor, reliability, race, effort S): Ctrl+PgUp/PgDn page loads bypass the action gate; overlapping loads corrupt SessionPageHistory

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:1085` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterState.cs`

**Claim.** The LoadOlder/LoadNewer session and transcript page actions (lines 1085-1105 and 1179-1199) run without RunGatedAsync. Each reads the cursor, awaits HTTP, then mutates shared state from pool threads, including the plain List SessionPageHistory (CommandCenterState.cs:256). Two overlapping loads start from the same cursor and both push history. When their continuations overlap, a List.Add can be lost. The loads also race the post-turn RefreshSessionsAsync, which clears the history.

**Failure scenario.** Holding Ctrl+PgDn (auto-repeat) issues two loads within one round trip. Only one page step is taken, but history holds [null,null]. After one Ctrl+PgUp the view is on the first page and still reports a newer page, so the next Ctrl+PgUp reloads page 1.

**Evidence (probe).** Probe 5 (real SessionWorkspaceService). With answers delivered in order: 'history=[null,null]', then 'after one newer: cursor=null hasNewer=True'. A first run with simultaneous answers gave 'history=[null]', a lost update from concurrent List.Add.

**Suggested fix.** Route the page loads through RunGatedAsync, or a dedicated non-reentrant gate that drops a request while one is in flight.

#### RV-168 (Minor, reliability, data-integrity, effort S): Resume from the Sessions pane keeps the previous session's attachment rows; the footer shows the wrong session's indexing status

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:1452` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterState.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs`

**Claim.** DoResumeCoreAsync never calls attachmentDriftMonitor.RequestRefresh() (the slash and turn paths do, at lines 2256 and 2306). ApplySessionMeta and ApplyTranscriptPage do not reset state.SessionAttachments. Session B's footer, badges and notice baseline keep session A's rows until a file event or a slash command arrives, which in a quiet workspace can be indefinitely.

**Failure scenario.** Session A has a Pending attachment. The operator resumes B from the sidebar. The footer still reads 'Indexing attachments: 1 pending' for B.

**Evidence (probe).** Probe 7 (real SessionWorkspaceService.ResumeSessionAsync): 'resume=Success session=bbbb… attachments=1 footer='Indexing attachments: 1 pending''.

**Suggested fix.** Reset SessionAttachments to [] whenever ApplySessionMeta changes SessionId, and call RequestRefresh() after a successful DoResumeCoreAsync (including the ResumeWithConfirm path).

#### RV-169 (Minor, reliability, api-contract, effort S): Model drop-down lists the same id once per provider, but selection stores only the id; the confirmation names a provider the host will not use

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:1732` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterModelPicker.cs`, `src/RetroDownfall.Arcanum.Core/Configuration/ProviderResolver.cs`

**Claim.** CommandCenterModelPicker.Build keeps 'llama3 (Local)' and 'llama3 (Remote)' as separate rows ('genuinely different routes'). ApplySelectedModel stores only selected.Model, and PingRequest carries no provider, so the host's ProviderResolver routes to the first provider in configured order. The transcript still says 'Model set to llama3 (Remote)', and both rows render with the active ●.

**Failure scenario.** The operator picks the Remote row to use a hosted endpoint. Turns keep going to the local provider while the confirmation says Remote.

**Evidence (probe).** Probe 6: 'confirmation='Model set to llama3 (Remote) for this session.' routed=Local rows=[● llama3  (Local) | ● llama3  (Remote)]'.

**Suggested fix.** Collapse duplicate ids into one row that lists their providers, or name the provider the host will actually route to in the confirmation. A real per-provider choice needs a provider-qualified session model and a provider field on PingRequest.

#### RV-170 (Minor, reliability, race, effort S): Chat turns bypass the action gate, so a message sent during a gated resume goes to the old session and sessionBound re-binds to it

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:2286` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterChatRunner.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs`

**Claim.** Gated session mutations (resume, new, fork via /resume, sidebar Enter) deny only while Generating. The chat path admits a turn with TryBeginTurn and never takes _actionGate. A turn started while a resume holds the gate builds its PingRequest with the old state.SessionId (CommandCenterChatRunner.cs:134). The resume then replaces the transcript, and the turn's sessionBound frame calls PersistBoundSessionAsync(old), which sets state.SessionId back to the old session and saves it as the last session (SessionWorkspaceService.cs:644-690).

**Failure scenario.** The operator sends '/resume <id>' and immediately pastes a message and presses Enter while the resume's 3 host calls and cli-context write are in flight. The message lands in session A; afterwards the header and every later turn target A while the transcript shows B.

**Evidence (trace).** Trace only (timing-dependent, no deterministic seam): CommandCenterHost.cs:2246 (slash path gated) versus 2286-2304 (chat path ungated); SessionWorkspaceService.ResumeSessionAsync awaits GetSession, GetSessionEntries and SaveSessionIdAsync before ApplySessionMeta; CommandCenterChatRunner.cs:322-341 persists every SessionBound.

**Suggested fix.** Admit chat through the same gate (_actionGate.Wait(0) with a 'busy' footer), or have gated mutations set a mutation-in-flight flag that TryBeginTurn refuses. Ignore a sessionBound for a session that is no longer state.SessionId.

#### RV-171 (Minor, reliability, race, effort S): EndTurn runs before TurnCts is cleared, so a turn admitted in that window has its CTS disposed and becomes uncancellable

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:2321` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterState.cs`

**Claim.** The finally calls state.EndTurn() first, then captured = state.TurnCts; state.TurnCts = null; captured.Dispose(). If the UI thread admits the next submit between EndTurn and the read (TryBeginTurn succeeds, state.TurnCts = turnCts2), the old finally captures turnCts2, clears the slot and disposes it.

**Failure scenario.** An Enter lands right as a turn finishes. The new turn runs with a disposed linked source, unregistered from the run token. Ctrl+C finds TurnCts == null (TryCancelTurn returns false), and shutdown cancellation does not reach the turn either.

**Evidence (trace).** Trace only (a window of a few instructions): CommandCenterHost.cs:2286-2301 (admission and publish) versus 2319-2330 (EndTurn before the slot is read and cleared).

**Suggested fix.** Clear the slot only if it still holds this turn's source (Interlocked.CompareExchange(ref _turnCts, null, turnCts)), dispose it, then call EndTurn() last.

#### RV-172 (Minor, reliability, ui-responsiveness, effort S): Turn completion moves focus to the composer whatever the operator is doing; next keys are swallowed (overlay) or typed into the composer (Sessions)

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs:2332` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterWindow.cs`

**Claim.** The finally of HandleSubmitAsync always runs window.FocusInput() and sets state.FocusRegion = Composer, regardless of state.Overlay or where the operator moved during the turn. With the palette, slash menu or model picker open, the overlay stays visible but TextView focus moves to the composer, and Input.KeyDown → TryHandleModalOverlayKey (lines 2447-2452) swallows every printable key. With the Sessions pane in use, j/k now type into the composer, and Enter sends them as a message.

**Failure scenario.** During a long turn the operator opens Ctrl+K and types a filter. The turn ends mid-typing, the following letters vanish and the filter stops updating. Or: browsing sessions with j/k as the turn ends puts 'jk' in the composer, and the next Enter sends it to the model.

**Evidence (probe).** Probe 3 (real host HandleSubmitAsync with a fake stream returning Result): 'after turn: overlay=CommandPalette focusRegion=Composer inputFocus=True filterFocus=False'; 'key 'a': handled=True/True filter='''.

**Suggested fix.** In the finally, return focus to the composer only when it was there at submit and no overlay is open (state.Overlay == None && FocusRegion == Composer). Otherwise only refresh.

#### RV-173 (Minor, reliability, docs-mismatch, effort S): UI update pump coalesces before a non-blocking hand-off, so refreshes arriving while the UI thread is busy are not folded

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterUiUpdatePump.cs:44` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs`

**Claim.** marshal is Terminal.Gui's Invoke, which queues and returns immediately, so the pump never waits for an apply to finish. It drains only what is in the channel at that instant (usually one item) and queues it. Every refresh produced while the UI thread is busy becomes its own full ApplyState on the UI queue. That contradicts the class comment (lines 7-13) about folding 'dozens of refreshes while the UI thread is still applying the first one'. The existing test only covers a burst written before the pump runs.

**Failure scenario.** During a resize, a large paste or a long re-wrap, a streamed answer's 20 flushes per second each queue a redundant RefreshLog apply, each re-copying the transcript lines, and the lag grows instead of collapsing.

**Evidence (probe).** Probe 8: '30 RefreshLog updates while the UI thread was busy -> 30 applies queued, 30 run' (non-draining Invoke queue, writes 5 ms apart). CommandCenterUiUpdatePumpRunTests.A_burst_of_queued_updates_is_applied_once writes the whole burst before the pump starts.

**Suggested fix.** Allow at most one outstanding apply per kind: set a flag when it is queued, clear it inside the queued work, and fold anything that arrives meanwhile. Alternatively, wait for completion before the next drain.

#### RV-174 (Minor, reliability, docs-mismatch, effort S): Overlay frame width is measured in UTF-16 units, clipping CJK and other wide text

`src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterWindow.cs:2004` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/OverlayLayout.cs`

**Claim.** ShowOverlay and ShowHumanPromptOverlay wrap to a width measured in cells, but ApplyAbsoluteLayoutCore re-sizes the pane from _overlayLines using line.Length. A row of 40 CJK characters (80 cells, length 40) gets the 60-cell default and its right part is cut off. This contradicts DESIGN §16.6, which says overlay wrap and width measure display cells.

**Failure scenario.** A Japanese ask_human question or session title shows only its first 58 cells; the rest is clipped.

**Evidence (probe).** Probe 2: 'question cells=80 overlay inner width=58', and the overlay rows contain the full 80-cell line (the wrap decided it fits on one row).

**Suggested fix.** Use TerminalCellMetrics.MeasureWidth(line) in the longestOverlay loop.

### U07: Arcanum.Cli — CommandCenter (2)

#### RV-175 (Minor, reliability, correctness, effort S): Tool-markup detection ignores the entry role, hiding user prompts on resume

`src/RetroDownfall.Arcanum.Cli/CommandCenter/PersistedToolInteraction.cs:12` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/TurnContextGuards.cs`

**Claim.** IsToolInteraction routes any entry with a ToolName, or whose trimmed content starts with '[ToolCall:'/'[ToolResult:' case-insensitively, to Incantations and out of the transcript, regardless of role. The host's definition (TurnContextGuards.cs:32-39) is role-bound and ordinal: ToolCall = assistant, ToolResult = system. User prompts starting with that text vanish from the resumed transcript, and model text starting with '[ToolCall: ...]' is shown as a tool call that never ran.

**Failure scenario.** Operator sends '[ToolResult: ...] why did this fail?' as a prompt; after /resume the prompt is missing (the answer appears with no question) and an 'unparseable' row appears in Incantations instead.

**Evidence (probe).** Probe P8: `role=user content='[ToolResult: this is my question]' IsToolInteraction=True`; `role=user content='[toolcall: pretend(1)]' IsToolInteraction=True`; caller SessionWorkspaceService.cs:721-727 skips such entries from the transcript.

**Suggested fix.** Mirror TurnContextGuards: ToolCall only for assistant entries (ToolName or ordinal prefix), ToolResult only for system entries with the ordinal prefix, plus the tool/function roles.

#### RV-176 (Minor, reliability, correctness, effort S): WrapLine never breaks at the column just past the window

`src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionLogBuffer.cs:534`

**Claim.** The whitespace search covers only i in (index, sliceEnd-1] and never line[sliceEnd]. A word ending exactly at the width is pushed to the next row; when a row is a single unbroken token exactly as wide as the pane, the next row starts with the space and the following word is hard-split.

**Failure scenario.** WrapLine("hello world again", 5) renders 'hello' / ' worl' / 'd' / 'again'; in the transcript this hits long tokens (hashes, URLs, paths) exactly as wide as the pane. Ordinary prose only wraps one word early (0 of 1,838 rows affected at 80 columns).

**Evidence (probe).** Probe P5: ['aaaa' | ' bbb' | 'b'], ['hello' | ' worl' | 'd' | 'again'], ['The' | 'quick' | 'brown' | 'fox jumps']; P5b frequency at 80/100 columns: 0 leading-space rows.

**Suggested fix.** If sliceEnd < line.Length and line[sliceEnd] is whitespace, emit line[index..sliceEnd] and skip the whitespace run.

#### RV-177 (Minor, reliability, correctness, effort M): Transcript paging and entry selection go stale after live turns

`src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs:370` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterState.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterWindow.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionLogBuffer.cs`

**Claim.** SessionEntryCount, LoadedTranscriptEntries and TranscriptEntryOffset are written only by resume/fork (336, 556, 732-734) and ClearSessionBinding. Turns sent in Command Center add entries at the head of the newest-first offset space without updating them, so the next older page is offset by the new entries and HasOlderTranscriptEntries uses the stale total, hiding the oldest entries; a session created in Command Center keeps SessionEntryCount=null and can never page back once the 1,000-entry log trims. Live entries have no SourceEntryId, so `/fork at` and `/fork alternative` on an answer produced in this sitting fail with 'Select a transcript entry/answer' until the session is resumed again.

**Failure scenario.** Resume a 350-entry session (page 0..199), send 30 turns (+60 entries), Ctrl+PgDn: offset 200 returns new-numbered 200..399 and 200+200 < 350 is false, so the last 10 entries are unreachable; `/fork alternative` on the latest answer is refused.

**Evidence (trace).** Trace, not executed: only writers are SessionWorkspaceService.cs:336/556/732-734 and CommandCenterState.ClearSessionBinding (grep); CommandCenterWindow.GetSelectedTranscriptEntryId returns SourceEntryId, which SessionLogBuffer.Append never sets; HasOlderTranscriptEntries at CommandCenterState.cs:296-298.

**Suggested fix.** After each turn, re-read the session entry count and the newest transcript page (or advance the count/offset by the entries the turn appended and record their persisted ids), so paging and selection stay aligned.

#### RV-178 (Minor, reliability, idempotency, effort S): Fork created on the host but not reloaded is reported as a plain failure; retry creates another branch

`src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs:515` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs`

**Claim.** After POST /fork succeeds, a failure of the detail/entries/attachments reload (505-524) or of the last-session save (543-554) returns Failed with only the reload error. The branch exists on the host, the session list is not refreshed, and the message neither names the branch nor says it was created, so the natural retry forks again.

**Failure scenario.** Host briefly busy right after the fork: operator sees 'host busy', runs /fork again, and later finds two identical 'Fork of ...' branches.

**Evidence (probe).** Probe P6 (fork POST 201, GET of the fork detail 503): `first /fork -> 'host busy'`, `second /fork -> 'host busy'`, `fork POSTs (branches created on host) = 2; message names fork id: False`.

**Suggested fix.** Once the fork id is known, report later failures as 'Branch {forkId:D} was created but could not be opened: ...; use /resume {forkId:D}' and refresh the session list.

#### RV-179 (Minor, reliability, correctness, effort S): /branch child on a New Session opens an unrelated root session

`src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:324`

**Claim.** `state.Sessions.FirstOrDefault(s => s.ForkedFromSessionId == state.SessionId)` compares two Guid? values; with no bound session both are null, so the newest non-branch session is treated as the child and resumed.

**Failure scenario.** After /clear, or at start-up with no last session, `/branch child` resumes the most recently updated root session as if it were a branch.

**Evidence (probe).** Probe P7: `/branch child with SessionId=null -> SessionId=11111111-... title=Parent`.

**Suggested fix.** Return 'Open a session first.' when state.SessionId is null, as /fork does.

#### RV-180 (Minor, reliability, platform, effort S): /pin file containment check is case- and alias-sensitive on case-insensitive volumes

`src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:824`

**Claim.** The candidate must StartsWith the working directory using StringComparison.Ordinal; Path.GetFullPath neither normalises case nor resolves symlinks. On Windows (a shipping RID) and default case-insensitive APFS, an absolute path typed with different case, or via /tmp rather than /private/tmp, is refused although File.Exists is true.

**Failure scenario.** Windows, cwd C:\Repo: `/pin file c:\repo\src\a.cs` -> 'File pins must name an existing file inside the workspace.'

**Evidence (trace).** Trace (not executed on Windows): lines 822-829.

**Suggested fix.** Compare with the platform path comparison (OrdinalIgnoreCase on Windows/macOS) on resolved paths, or reuse the shared workspace containment helper.

#### RV-181 (Minor, reliability, docs-mismatch, effort S): /config help and Command Reference promise the configuration file path; the handler never prints it

`src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:1156` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/SlashCommandRegistry.cs`, `docs/Arcanum.Command.Reference.md`

**Claim.** Registry text (SlashCommandRegistry.cs:37) and the Command Reference row say /config shows 'the effective configuration summary and its file path'; FormatConfig prints models, provider count, workspace and campaign and points at `arcanum config list`, but no path.

**Failure scenario.** An operator running /config to find which arcanum.json is in effect gets no path.

**Evidence (trace).** Read FormatConfig 1156-1175 against the registry and docs/Arcanum.Command.Reference.md /config row.

**Suggested fix.** Print the resolved configuration file path, or correct the registry and reference text.

#### RV-182 (Minor, reliability, data-integrity, effort S): /attach and /pin collapse runs of whitespace inside the path

`src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandParser.cs:92` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs`

**Claim.** The line is split with RemoveEmptyEntries|TrimEntries (81) and the remainder rejoined with single spaces (92-94 for /attach, 114 for /pin), so a path with consecutive spaces or a tab is altered before it is resolved.

**Failure scenario.** `/attach My  Notes.txt` reports not found at .../My Notes.txt, or stages a different file of that single-spaced name for the model; `/pin file docs/a  b.md` likewise pins the wrong path.

**Evidence (probe).** Probe P10: `/attach My  Notes.txt -> Kind=Attach Argument='My Notes.txt'`; `/pin file docs/a  b.md -> Kind=Pin Argument='file' Secondary='docs/a b.md'`.

**Suggested fix.** Take the raw substring after the head token (and after the kind token for /pin), trimming only the ends.

#### RV-183 (Minor, reliability, error-message, effort S): Bare /branch, /session or /ward suggests itself

`src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandParser.cs:342` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/SlashCommandRegistry.cs`

**Claim.** A registered head with missing or wrong sub-arguments falls to Unknown, and SlashCommandRegistry.Suggest's prefix match (115-128) returns the head itself.

**Failure scenario.** Typing `/branch` answers "Unknown command `/branch`. Did you mean `/branch`?"; same for /session and /ward.

**Evidence (probe).** Probe P11 output for /branch, /session, /ward.

**Suggested fix.** When the head resolves in the registry, return Denied with that descriptor's Usage instead of the did-you-mean path.

#### RV-184 (Minor, reliability, crash-consistency, effort S): Coalescer cancellation/dispose flushes throw ChannelClosedException after the UI channel completes

`src/RetroDownfall.Arcanum.Cli/CommandCenter/StreamingUiCoalescer.cs:229` — related: `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterHost.cs`, `src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterChatRunner.cs`

**Claim.** FlushCancelledAsync/FlushFinalAsync/DisposeAsync use WriteAsync on the assumption that the unbounded channel 'completes at once' (doc 99-106). On quit the host cancels the run token and immediately completes the channel (CommandCenterHost.cs:120-123, 653), so a turn streaming at a confirmed Ctrl+Q reaches FlushCancelledAsync after TryComplete and throws from the chat runner's cancel handler; HandleSubmitAsync's catch logs 'Command Center submit failed.' at Error and its own ui.WriteAsync throws again out of the fire-and-forget task (unobserved).

**Failure scenario.** Confirmed quit during a streaming answer writes an Error log entry with a ChannelClosedException stack on an ordinary exit.

**Evidence (probe).** Probe P9: `FlushCancelledAsync after TryComplete: System.Threading.Channels.ChannelClosedException: The channel has been closed.` Ordering traced in RunTerminalLoop (Cancel then cleanup -> TryComplete).

**Suggested fix.** Use _ui.TryWrite(...) in FlushCoreAsync (non-blocking on an unbounded channel; false means the UI is gone).

### U23: Arcanum.Infrastructure — Covenant (1)

#### RV-185 (Minor, performance, cache-invalidation, effort S): Idle maintenance passes bump the Covenant availability generation twice per pass; that counter is hashed into the DCI v2 prompt-cache scope marker at the start of the system prompt

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceCoordinators.cs:78` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantPersistedAvailabilityPublisher.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantAvailability.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantRuntimeGenerationProvider.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/PromptCacheDispatchValidator.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/SystemPromptBuilder.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantFeatureConfigurationPublisher.cs`

**Claim.** CovenantOwnerCleanupCoordinator (lines 78-80) and CovenantSearchOutboxCoordinator (151-153) republish availability after every committed batch even when it applied nothing. CovenantAvailability.Publish -> CovenantRuntimeGenerationProvider.PublishAvailability (line 225) increments Availability.Generation unconditionally, with no comparison against the current facts. PromptCacheDispatchValidator.cs:213 hashes captured.Availability.Generation into the DCI v2 scope marker that SystemPromptBuilder.cs:200 appends to the end of the preamble ('[arcanum-dci-scope-v2:<digest>]'), and the plan key 'arcanum-pc-v2-' + Digest(prefix, tools, marker) includes it. PromptCacheDispatchValidator.cs:306 also requires authority.Capture() == prepared.Authority (whole-record equality including the counter). CovenantFeatureConfigurationPublisher.Republish has the same shape for any arcanum.json change.

**Failure scenario.** With Features.Covenant on and healthy and Features.DynamicContextInjectionV2 on, on a turn whose Campaign has no admitted Covenant content (so the DCI v2 layout is used), the generation moves twice every five minutes with nothing changed. Each tick changes the first bytes of every DCI v2 prompt, so the provider's cached prefix is invalidated at least once per pass (one avoidable full-prefix miss per five minutes of active use), and a tick landing between PrepareAsync and a physical dispatch makes the dispatch return ReplanRequired.

**Evidence (probe).** Probe P2 (full test host with Covenant on, background pass removed so the test drives CovenantMaintenanceHostedService.RunOnceAsync): ten consecutive idle passes, e.g. 'P2 idle pass 1: 2.9 ms; Generation 8 -> 10 ... every other published fact unchanged=True', then 10->12, 12->14 ... 26->28, LastHealthTransition unchanged. Each pass 0.3-0.6 ms wall (p50 0.6 ms), so the pass is cheap and the churn is the cost. Code: CovenantRuntimeGenerationProvider.cs:223-226 'Generation = current.Availability.Generation + 1'; PromptCacheDispatchValidator.cs:210-213 and :306. DESIGN prompt-cache section says the marker is derived from the published availability snapshot; the DCI v2 spec lists state generations (dataset, canonical, deletion, Campaign availability), not a publication counter. Output: reports/C/U23-probe-output-baseline.txt.

**Suggested fix.** In CovenantAvailabilityRepublisher.RepublishAsync (or CovenantAvailability.Publish) skip the publication when the freshly read persisted facts equal the current snapshot (still publish when they differ, which preserves the stale-snapshot correction the comments rely on). Independently, hash the state generations (dataset generation, canonical sequence, deletion sequence, feature flag, canonical health) into the marker instead of the raw publication counter, and compare those in ValidateAsync instead of whole-record equality.

#### RV-186 (Minor, performance, backlog-drain-rate, effort S): Owner-deletion cleanup drains at most 64 events per five-minute pass, from one ordered queue shared by Sessions and Campaigns

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceHostedService.cs:127` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCleanupWorker.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantOwnerDeletionReader.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceCoordinators.cs`

**Claim.** Each pass calls RunBatchAsync(CovenantCleanupWorker.DefaultBatchSize = 64) exactly once and then sleeps Interval = 5 minutes (lines 60, 86, 127-135), whether or not the batch was full. The catch-up rate is therefore a fixed 64 events per 5 minutes (768 per hour, 18,432 per day) regardless of backlog. CovenantOwnerDeletionReader.ReadPendingAsync orders Sessions and Campaigns together by Sequence with a LIMIT, so a Campaign deletion queued behind N Session deletions waits N/64 passes; the query also aggregates the whole pending backlog (GROUP BY OwnerKindCode, OwnerId ... ORDER BY MAX(Sequence)) on every pass. The worker's own remark says this bound is 'the difference between catching up and never finishing'.

**Failure scenario.** An operator enables retention.activeSessions on a long-lived installation and the first daily sweep deletes 20,000 Sessions. Each deletion appends an owner_deletion_events row via trigger; the Covenant tier needs 313 passes (about 26 hours) to reach them, and a Campaign deleted after that sweep has its canonical rows, curation and search projection removed only after the same lag. In this release the Session half removes little (turn receipts have no producer yet), so the visible effect is delayed erasure of a deleted Campaign's protected rows rather than wasted CPU.

**Evidence (trace).** Derived from constants and a single call site, not executed: CovenantMaintenanceHostedService.cs:60 'Interval = TimeSpan.FromMinutes(5)', :127-135 one '.RunBatchAsync(CovenantCleanupWorker.DefaultBatchSize, ...)', :86 'Task.Delay(swept ? Interval : IdleInterval ...)'; CovenantCleanupWorker.cs:37 'DefaultBatchSize = 64'; CovenantOwnerDeletionReader.cs:80-95; the only production caller of the coordinators is this hosted service (grep of src). 20,000 / 64 = 313 passes x 5 min = 26.1 h.

**Suggested fix.** Re-run the cleanup (and outbox) sweep immediately, within a time budget such as 2 seconds, while the previous batch was full (CampaignsCleaned + SessionsCleaned == maxEvents, or RowsConsumed == maxRows), and keep the five-minute delay only for an idle pass. Optionally make the pending query keyed per owner kind with LIMIT so it does not aggregate the whole backlog.

#### RV-187 (Minor, performance, full-table-scan, effort M): Live entry erasure scans every version, receipt and provenance row in the installation three times while the Covenant scope is closed

`src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantEntryErasurePlan.cs:1080` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantEntryErasureService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantIdentitySql.cs`

**Claim.** EntryPredicate and the version/receipt/provenance/review predicates are built with CovenantIdentitySql.Keyed, i.e. lower(replace(col,'-','')) = @id, which cannot use a BINARY index, so each statement against covenant_versions, covenant_mutation_receipts, covenant_version_attachment_provenance, covenant_review_events and the decision receipts scans the whole table, not the entry's rows. CovenantEntryErasureService.ApplyInTransactionAsync runs the plan three times inside the BEGIN IMMEDIATE transaction held under the entry-erasure closure (MeasureAsync at line 710, RunAsync Delete at 723-725, ProveAbsentAsync at 770-772); for a Global entry or a reclaiming erase the closure covers the whole installation, so every Covenant turn is refused or drained for the duration. DESIGN 10.17 accepts the scan cost for the keyed purge (rows that can be another installation's) and says an exact comparison 'keeps its index' where the spelling is settled and the rows are this installation's own; a live erase reads only this installation's canonical family (written with ToString("D")), and its 'background path' rationale does not cover work done under a closure.

**Failure scenario.** On an installation with 20,000 versions, erasing a Global entry holds the installation closure for roughly 3 x 61 ms of plan scans plus the receipts table (which is empty in the probe bed and up to 16,640 rows per scope in practice), growing linearly with installation size (quotas allow 8,192 versions and 16,640 receipts per scope with an unbounded number of scopes).

**Evidence (probe).** Probe P3 (CovenantEntryErasurePlan.RunAsync Count mode on one Global entry, N other versions, scratch SQLCipher Grimoire): versions=1,001 -> p50 1.56 ms; 5,001 -> 12.16 ms; 20,001 -> 60.82 ms (min 50.4, max 208.8). EXPLAIN QUERY PLAN: 'SCAN covenant_versions USING COVERING INDEX ux_covenant_versions_head_candidate', 'SCAN covenant_mutation_receipts', 'SCAN covenant_version_attachment_provenance USING COVERING INDEX sqlite_autoindex_...', versus 'SEARCH covenant_versions USING COVERING INDEX idx_covenant_versions_entry_created (EntryId=?)' for the exact form. Output: reports/C/U23-probe-output-baseline.txt.

**Suggested fix.** In CovenantEntryErasureMode.Live use exact predicates (EntryId = $entry, VersionId IN (...)) that keep the existing indexes, retaining the normalised shape for Staged; or add expression indexes on lower(replace(EntryId,'-','')) / VersionId for the canonical tables as was done for artifact_sensitivity and entry_embeddings. Drop the redundant second Count pass (MeasureAsync) inside the closure if the effect digest can be recomputed from the Delete pass's tally.

#### RV-188 (Minor, reliability, cancellation, effort S): A refused, replayed or not-found Covenant erase still revokes every live memory-delegation grant

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantEntryErasureService.cs:351` — related: `src/RetroDownfall.Arcanum.Core/Intelligence/MemoryDelegation/MemoryDelegationCoordinator.cs`

**Claim.** ApplyAsync begins the memory-delegation mutation as its first statement. That is before the null/empty-token check (355), the parse (360), the receipt-first replay (435-440), the authority-epoch refusal (451) and the absent-entry branch AnswerAbsentAsync (458-466), which holds no erasure authority and deletes nothing. Every one of those exits revokes and cancels all live delegation grants without mutating any memory source.

**Failure scenario.** An operator or a client retry sends an erase that is malformed, already committed (receipt replay), names an entry that is already gone (404/410), or carries a stale authority epoch. Each such request cancels every concurrent memory-delegation grant and in-flight delegated child on the host, and those delegations fail and lose their results, although nothing was erased.

**Evidence (probe).** CovenantEntryErasureService.cs:351 `await using MemoryDelegationMutation? memoryMutation = await memoryDelegation.BeginOptionalAsync(cancellationToken)` is followed by `if (request is null || string.IsNullOrWhiteSpace(request.PreflightToken)) return InvalidBody;` (355-358). Probe Probe_an_invalid_covenant_erase_request_revokes_a_live_memory_delegation_grant (scratch worktree at ac0d2d8e): ApplyAsync(null, authority) returns Validation.InvalidBody and the live root's Revocation is cancelled. The probe passed, which confirms the defect.

**Suggested fix.** Take the barrier only on the branch that actually closes a scope and deletes: immediately before gate.AcquireEntryErasureAsync, after the probe read lease has been released. That preserves the barrier-before-gate ordering. Leave validation, receipt replay and AnswerAbsentAsync outside it.

#### RV-189 (Minor, reliability, idempotency, effort S): Every resumed family reinitialize re-runs the pre-drop protected-state erasure outside the phase guard (latent)

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantFamilyReinitializeCoordinator.cs:208` — related: `tests/RetroDownfall.Arcanum.Tests/Covenant/CovenantFamilyReinitializeCoordinatorTests.cs`

**Claim.** EraseProtectedStateAsync is called unconditionally between the guarded AdvanceAsync(AdmissionClosed) and AdvanceAsync(LocalArtifactsProcessed). A run resumed from FamilyDropped, DatabaseCompacted, AcceleratorInstalled, SidecarsVerified or ReopenedVerified therefore re-enumerates the old dataset generation's work and calls both erasure kernels again, after the family was dropped and the tiers reinstalled. That breaks the class's own rule that a recorded phase is never repeated.

**Failure scenario.** A reinitialize that crashed after the family drop is resumed. The erasure source enumerates the old generation again, against the dropped family, under a lease whose dataset is now the new generation. Either the work repeats against state it no longer describes, or the source or kernel fails. A failure makes RunAsync return KeepClosed on every retry, so the operation can never finish and admission stays closed. Latent: the coordinator and the erasure source have no production construction or implementation.

**Evidence (probe).** CovenantFamilyReinitializeCoordinator.cs:200-226: `state = await AdvanceAsync(... AdmissionClosed ...)`, then `Result erased = await EraseProtectedStateAsync(operation, state, authority.Value, cancellationToken)` with no `state.Phase` check, then `AdvanceAsync(... LocalArtifactsProcessed ...)`. Probe theory Probe_U23_a_resumed_run_past_the_drop_re_runs_the_pre_drop_erasure ran for FamilyDropped, SidecarsVerified and ReopenedVerified. All three passed: harness.Steps contains `erase-artifacts` and `erase-managed-files`, and Artifacts.Calls == 1. The existing A_resumed_run_never_repeats_a_step_its_checkpoint_already_records passes only because it asserts the absence of a single named step.

**Suggested fix.** Run the erasure only when `state.Phase < CovenantFamilyReinitializePhase.LocalArtifactsProcessed`. Extend the resume test to assert that neither kernel is called once the checkpoint is past LocalArtifactsProcessed.

#### RV-190 (Minor, reliability, crash-consistency, effort S): Family-reinitialize recovery reports a ReopenedVerified checkpoint as Completed although publication is still owed (latent)

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantFamilyReinitializeCoordinator.cs:577` — related: `tests/RetroDownfall.Arcanum.Tests/Operations/CovenantMaintenanceOperationTests.cs`, `src/RetroDownfall.Arcanum.Core/Operations/LongRunningOperationRecoveryRegistry.cs`

**Claim.** The coordinator checkpoints ReopenedVerified and only afterwards runs PublishCommittedAsync (committed key, issuer, availability, gate transition), ReopenDisclosureWriterAsync and the disposition. None of those is checkpointed, so a run resumed from ReopenedVerified still publishes. The registered CovenantFamilyReinitializeRecoveryHandler returns Completed() for exactly that checkpoint. A crash between the ReopenedVerified checkpoint and a successful publication would therefore be closed as finished, as would a KeepClosed after a failed publish or writer reopen. The existing test Reinitialize_recovery_completes_only_a_verified_reopen_and_closes_an_unstarted_row pins this behaviour.

**Failure scenario.** Once a transition implementation is wired, the process dies after VerifyReopenAsync's checkpoint and before PublishCommittedAsync completes. At startup the handler marks the operation Completed. The candidate key and new dataset generation are never published, and the KeepClosed gate owner is never resumed, so admission stays closed behind a database whose committed key was never published. Latent today: the coordinator is constructed nowhere and the transition has no implementation (DESIGN 10.17, deliberately absent). The handler is registered, though.

**Evidence (probe).** CovenantFamilyReinitializeCoordinator.cs:304-310 advance to ReopenedVerified; 329-357 publish, reopen the writer and close, with no checkpoint after. Handler at 576-579: `checkpoint.Value.Phase == CovenantFamilyReinitializePhase.ReopenedVerified ? LongRunningOperationRecoveryResult.Completed() : ...RequiresAttention(...)`. Probe Probe_U23_a_resumed_ReopenedVerified_checkpoint_still_owes_publication_that_recovery_reports_as_completed (scratch worktree) passed, confirming the inconsistency. It runs the coordinator from ReopenedVerified (harness.Steps contains `publish` and `reopen-writer`), then runs the handler on the same checkpoint (State == Completed).

**Suggested fix.** Record a durable terminal state after publication and writer reopen, either a new final phase or the long-running completion itself, and let the handler complete only on that. Treat ReopenedVerified as resume-required. Update the pinning test.

#### RV-191 (Minor, reliability, unbounded-growth, effort S): A stale index rebuild 'restarts' into an orphaned operation that nothing drives and whose identity is discarded (latent)

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantIndexRebuildCoordinator.cs:256` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceService.cs`

**Claim.** On a RestartRequired phase, AbandonStaleAsync abandons the current row and calls StartAsync, but discards the result (`_ = await StartAsync(ownerId, cancellationToken)`). The replacement operation's identity never reaches the caller, because AdvanceAsync returns the old progress with RestartRequired. The replacement never receives a checkpoint, and the next startup's recovery handler closes it as covenant.index_rebuild_identity_changed. The documented 'starts a new one' behaviour is a no-op that leaves a Running ledger row with a five-minute lease.

**Failure scenario.** Once a driver exists, a rebuild hits a dataset generation change. The driver sees RestartRequired and has no handle on the replacement, so the index stays unrebuilt. Meanwhile an orphan Running covenant-index-rebuild row appears in `arcanum operation list` until a restart abandons it. Latent: only the unregistered CovenantMaintenanceService starts rebuilds.

**Evidence (trace).** Proven by tracing the code; there is no production driver to execute. CovenantIndexRebuildCoordinator.cs:206-215: SaveCheckpointAsync routes RestartRequired to AbandonStaleAsync. 237-259: TryTransitionAsync(... Abandoned ...), then `_ = await StartAsync(ownerId, cancellationToken)` and `return Result.Success()`. AdvanceAsync (120-125) then returns `advanced`, the old progress. CovenantIndexRebuildRecoveryHandler (299-303) abandons any operation that has no checkpoint or is not Completed.

**Suggested fix.** Return the replacement operation to the caller (for example on the AdvanceAsync result) so the driver continues it. Alternatively, do not start one here and let the driver decide.

#### RV-192 (Minor, usability, error-message, effort M): Marker lifecycle refusals drop the Campaign id and closed blocker code they already hold and never log (restore cleanup and full-installation reset); latent until Campaign registration ships

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.cs:589` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.FullInstallationReset.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.RestoreInventory.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.RestartRootProof.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycleContracts.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs`

**Claim.** A blocked destination root is observed with a CampaignPathCleanupRootBlockerEvidence (Campaign id, closed code RootUnavailable/PhysicalIdentityMismatch/OwnershipMismatch, observation digest) that the contract remark says is 'what a blocked cleanup records and what an operator later reads'. ValidateSeed refuses the whole restore with the fixed string 'A restore cannot proceed while a destination Campaign root cannot be opened.' and discards all of it; BackupRestoreService.Issue forwards exactly that string. The restart-proof messages (RestartRootProof.cs:165) are justified by 'the operator who now has to look at the directory' yet name no Campaign. In CampaignPathMarkerLifecycle.FullInstallationReset.cs every one of roughly fifty refusal sites returns the same Inert() (2014-2022: Data.RecoveryRequired, 'The full-installation reset Campaign cleanup is not available.'), which HostToolsMarkerPairResetCoordinator collapses again to 'requires recovery'. The only Log call in the lifecycle files is a disposal-failure Warning with no identifiers (CampaignPathMarkerLifecycle.cs:1087).

**Failure scenario.** Once Campaign path registration exists, an operator who moved or deleted one project directory runs 'arcanum backup restore' (gate on) and is refused with one fixed sentence: no Campaign, no reason, no next step, nothing in the log. A full-installation reset that fails on a missing root key, an unopenable root, an unproved marker or a stale authority prints the same sentence for all of them. Not reachable today because campaign_path_identities has no production writer.

**Evidence (probe).** Probe P5 (blocked seed with closed code RootUnavailable): 'code=Covenant.ManualRecoveryRequired message=A restore cannot proceed while a destination Campaign root cannot be opened. containsCampaignId=False containsBlocker=False'. The existing test A_seed_whose_root_is_blocked_refuses_the_whole_preparation asserts only the code. 'rg campaign_path_identities' finds only reads and the restore-time DELETE (BackupCovenantRestoreReconciler.cs:631). CampaignPathMarkerLifecycleContracts.cs:94-101; RestoreInventory.cs:36-40,171-188; BackupRestoreService.cs:1962 and :2976.

**Suggested fix.** Put the Campaign id (a GUID, not a path) and the closed blocker code in the message, log one Warning per refusal with owner operation id, Campaign id and reason code, replace Inert() by a closed reason enum surfaced in the message, and document the operator remedy. The content-free rule covers paths and digests, not a GUID. Pin the message content in the existing test.

#### RV-193 (Minor, usability, error-message, effort S): Entry erase discards the commit-failure cause and logs nothing, leaving the operator with 'retry it' or 'restart the host' and no reason

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantEntryErasureService.cs:551` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureEvidence.cs`

**Claim.** The COMMIT failure is wrapped in UncertainCommitException(failure, rollbackFailed) (809), but the catch at 551-585 reads only RollbackFailed; the inner exception is never read or logged. ReReadReceiptAsync swallows its exception (621-624) and the closing lease.CompleteAsync result is discarded (601). The service has no ILogger by design ('Nothing here logs'), so the SQLite result code (which carries no content) appears nowhere.

**Failure scenario.** Disk full or an I/O error during an erase's COMMIT. If the receipt cannot be read back the operator gets Covenant.ManualRecoveryRequired with the Covenant scope closed until restart; if it can, Covenant.MaintenanceFailed 'commit failed and recorded nothing ... retry it'. The cause that decides whether a retry can ever succeed (SQLITE_FULL versus corruption) is gone, in the one state where a privacy erase has failed and the feature is offline.

**Evidence (probe).** Probe P6 (existing seams CommitForTesting and ReceiptReReadForTesting, commit throws SqliteException("database or disk is full", 13)): 'code=Covenant.ManualRecoveryRequired message=The erase's commit failed and its outcome could not be read back, so its Covenant scope stays closed until the host restarts. mentionsDiskFull=False'. CovenantEntryErasureService.cs:85-96 primary constructor has no logger; only 'uncertain.RollbackFailed' is referenced (575). The existing tests assert the code only.

**Suggested fix.** Log one Warning at the uncertain-commit catch with the mutation id, the disposition chosen and the SQLite result code and exception type (never the message), and log a failed CompleteAsync; state in the retry message whether the rollback succeeded.

#### RV-194 (Minor, usability, error-message, effort S): CovenantFamilyReinitializeCoordinator.RunAsync discards the reason every time it keeps admission closed (unwired class)

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantFamilyReinitializeCoordinator.cs:312` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceService.cs`

**Claim.** catch (CovenantReinitializeStepFailedException) never reads .Error; the failed erased (211), published (333) and writer (344) results are dropped as well. Every branch returns Success(KeepClosed), a disposition and not an error, so an operation that leaves the whole Covenant feature closed after the family may already be dropped records no cause anywhere (no log, no checkpoint field, no return value).

**Failure scenario.** Whoever wires the coordinator (it is constructed nowhere; ICovenantMaintenanceService is registered in no container) inherits a reinitialize that stops mid-way with admission closed and no way to learn which step or code failed. No operator can hit this in the current release.

**Evidence (trace).** CovenantFamilyReinitializeCoordinator.cs:208-218 (erase failure returns CloseAsync KeepClosed), 312-316, 329-348; CovenantPersistedAvailabilityPublisher.cs:47 states the class is 'unregistered in this release and constructed nowhere'.

**Suggested fix.** Carry the Error code into the returned result or the operation's terminal code and log it with the operation id and phase.

#### RV-195 (Minor, usability, cli-ergonomics, effort S): 'arcanum memory status' on a default (Covenant disabled) installation prints a 'Refused' census

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantManagementService.cs:510` — related: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs`, `src/RetroDownfall.Arcanum.Core/Covenant/CovenantReadContracts.cs`, `docs/Arcanum.Command.Reference.md`

**Claim.** StatusAsync initialises state to CovenantCensusReadState.Refused (510) and skips the census when the feature is off (516), so a disabled installation reports census Refused. The enum documents Refused as 'the installation read capability was declined'; nothing was asked. The CLI prints 'Covenant: disabled, available' then 'Covenant entries were not counted (Refused). This is not a report that you have none.' (MemoryCommands.cs:159-164). Covenant defaults to off, so every new installation shows it.

**Failure scenario.** A default-config user runs 'arcanum memory status' and reads a refusal that did not happen, and may look for a running exclusive operation. A script reading census=Refused cannot tell a disabled feature from a closing exclusive operation, the case the member exists to flag. Intended and pinned (CovenantStatusTests.cs:163, Command Reference 'never on an installation with the feature off'), but the stated reason is wrong.

**Evidence (trace).** CovenantManagementService.cs:506-541, 543; CovenantReadContracts.cs enum remark on CovenantCensusReadState; MemoryStatusCovenantCommandTests asserts 'Refused' appears.

**Suggested fix.** Add a NotAttempted (or Disabled) member and render 'Covenant is off, so entries were not counted', or have the CLI choose the wording from Enabled.

#### RV-196 (Minor, usability, docs-mismatch, effort S): covenant explain reports Admission=Admitted and zero tokens as if measured; DTO and DESIGN docs say the admission code runs

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantManagementService.cs:642` — related: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantReadContracts.cs`, `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`

**Claim.** ExplainAsync loads and links only. Explain() hard-codes ConfirmedTokens: 0 and ProposedTokens: 0 (642-643), CovenantAdmissionDecision.Admitted (648) and EstimatedTokens: 0 for every section (667). The inline comment (645-647) says 'leaving admission unclaimed is the honest shape', but Admitted is a claim and the enum has no NotEvaluated member. CovenantExplainDto's remark says it 'runs the same loader, linker, and admission code as a live turn', CovenantExplainSectionDto says default inspection reports 'byte and token counts', and DESIGN.md:2220 repeats 'live loader, linker, and admission code'.

**Failure scenario.** An HTTP or ArcanumApiClient caller asks why a Confirmed preference is not honoured, for an entry the live admission would refuse (RequiredNoFit), and is told Admitted with 0 tokens while the section reports non-zero rendered bytes. The operator-facing admission answer is in 'context inspect', which limits reach (no CLI verb, Forge or TUI caller), hence Minor.

**Evidence (probe).** Probe P2 with a Global Confirmed entry rendering 87 bytes: 'P2 admission=Admitted confirmedTokens=0 proposedTokens=0' and 'P2 section GlobalConfirmed items=1 renderedBytes=87 estimatedTokens=0'. CovenantReadContracts.cs remarks on CovenantExplainSectionDto and CovenantExplainDto; DESIGN.md:2220.

**Suggested fix.** Either run CovenantAdmissionPlanner against a stated default budget, or add a NotEvaluated admission value and make the token members nullable; correct the three doc sentences in either case.

### U24: Arcanum.Infrastructure — Covenant (2)

#### RV-197 (Minor, performance, lock-contention, effort S): ListAsync and PrepareAsync open BEGIN IMMEDIATE (the single writer lock) for reads, so a review page waits behind every writer, blocks every writer, and surfaces a long writer as a raw SqliteException

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:135` — related: `src/RetroDownfall.Arcanum.Api/Tower/MemoryReviewEndpoints.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteBusyRetry.cs`

**Claim.** Lines 134-136 (ListAsync) and 367-369 (PrepareAsync) call connection.BeginTransactionAsync(cancellationToken), which in Microsoft.Data.Sqlite 10.0.10 issues BEGIN IMMEDIATE. PrepareAsync never writes; ListAsync writes only the marker INSERT OR IGNORE the first time a queue is seen. The repo uses BeginTransaction(deferred: true) for true read snapshots elsewhere (CovenantEntryErasureService 243/987, SessionTurnClaimStore 71). Unlike ApplyAsync (MapApplyFailure -> retryable DatabaseBusy), List and Prepare have no busy mapping and MemoryReviewEndpoints.WithCovenantReadAsync has no catch.

**Failure scenario.** While any background writer (long rest, indexing, outbox, backfill batch) holds the write lock, the operator's review list or prepare call waits for it instead of being served from the WAL snapshot; if the writer outlasts the 5 s busy handler and the provider's 30 s command timeout the call throws SqliteException 'database is locked' and the endpoint returns an unhandled 500. Conversely every session-turn writer stalls for the whole page (with finding 1's scan inside the lock).

**Evidence (probe).** Console probe txmode: second writer blocked after BeginTransactionAsync(ct), BeginTransactionAsync(Serializable), BeginTransaction(deferred:false) and BeginTransaction(); not blocked after deferred:true. Through the real service (PROBE_list_and_prepare_hold_the_database_write_lock): a second connection's BEGIN IMMEDIATE inside the page transaction is blocked during ListAsync and during PrepareAsync. PROBE_list_waits_behind_another_writer_but_a_deferred_read_does_not (marker pre-created): another connection holds BEGIN IMMEDIATE 1,500 ms; a deferred snapshot read returns in 0.1 ms, ListAsync returns after 1,535 ms. PROBE_list_throws_instead_of_mapping_a_busy_database: 'THREW SqliteException: SQLite Error 5: database is locked.' Hold time on a small DB (Debug, loaded): 0.97 ms for a 50-row page with 50 source reads, 2.0 ms for a 50-decision prepare.

**Suggested fix.** PrepareAsync: connection.BeginTransaction(deferred: true). ListAsync: read in a deferred transaction; if the marker is missing, end the snapshot, create it in a short immediate transaction, then reopen deferred. Map busy/locked exceptions in List and Prepare to the existing DatabaseBusy (503) answer the way MapApplyFailure does.

#### RV-198 (Minor, performance, unbounded-growth, effort S): ListAsync persists a review-marker row for any campaign id a caller names; rows are keyed by user input and only a campaign-deletion event removes them

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:1186` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCleanupWorker.cs`, `src/RetroDownfall.Arcanum.Api/Tower/MemoryReviewEndpoints.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantOperationGate.cs`

**Claim.** ReadOrCreateMarkerAsync (1163-1212, called at 147) runs INSERT OR IGNORE INTO covenant_review_markers for whatever (generation, scope, campaignId, lane) the request names. Nothing validates the campaign exists: request.Validate() checks shape only, gate.AcquireReadAsync takes no campaign probe (only exclusive acquisitions call RequireLiveCampaignAsync), and the endpoint adds none. The only deleter is CovenantCleanupWorker.cs:232, run once per campaign-deletion event.

**Failure scenario.** A stale or buggy client (or a loop) listing review queues for unregistered or already-deleted campaign ids creates one persistent row per id and lane; a client that lists a campaign after its cleanup has run recreates an orphan that is never removed. The rows also lengthen the ReadMarker scan (finding 1) for every later review call.

**Evidence (probe).** PROBE_listing_an_unregistered_campaign_persists_a_marker_row: markers before=0, after 25 lists of 25 never-registered campaign ids=25. Endpoint trace: MemoryReviewEndpoints.WithCovenantReadAsync only validates the request shape and acquires the read lease; CovenantOperationGate.AcquireOrdinary does not consult ICovenantCampaignScopeProbe.

**Suggested fix.** Resolve the campaign through ICovenantCampaignScopeProbe and refuse unknown or deleted campaigns before creating a marker, or do not persist a marker on read (answer with a synthetic revision-1/reviewed-0 marker in List and Prepare and create it at apply).

#### RV-199 (Minor, performance, missing-index, effort S): Review-queue statements bind the campaign with IS $campaign, so the partial queue indexes are never used and every page, frontier read and apply scans the installation-wide review-event rowid window

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:1298` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables/covenant_review_events.sql`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables/covenant_review_markers.sql`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantStoreSql.cs`, `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantQueryPlanTests.cs`

**Claim.** ReadMarker (1231), ReadUpperFrontier (1265), ReadPage (1298), ReadEvent (1347), ComputeReviewedThrough (1859), AdvanceMarker (1909) and ReadCurationRevision (1800) all filter CampaignId IS $campaign with a bound parameter. SQLite cannot prove the partial predicates of idx_covenant_review_events_global_queue (WHERE CampaignId IS NULL) and idx_covenant_review_events_campaign_queue (WHERE CampaignId IS NOT NULL) from IS ?, so both indexes are dead and each statement walks the rowid range across every scope and lane. The repo's own CovenantStoreSql/CovenantQueryPlanTests already follow the 'only an equality on the Campaign can use a partial index' rule; the review service does not, and no test names either queue index.

**Failure scenario.** An operator lists the Proposed queue of a campaign (or any empty queue) on an installation with E review events across all campaigns: the page, the MAX(Sequence) frontier and every apply's ComputeReviewedThrough cost O(E) instead of O(page). Synthetic data on the real DDL, same statement text: at 90,000 events the first 50-row page takes 5.0 ms vs 0.36 ms with '=', the frontier of an empty queue 5.1 ms vs 0.03 ms, ComputeReviewedThrough 4.5 ms; at 360,000 events user CPU is 24-31 ms vs 1.0 ms for the page and 25 ms vs 0.07 ms for the empty frontier. The cost grows linearly with campaigns x events (up to 8,192 versions per scope) and is paid inside the write transaction of finding 2.

**Evidence (probe).** EXPLAIN QUERY PLAN on the shipped SQLCipher via probe PROBE_queue_statement_plans_on_the_shipped_sqlcipher (EventSelect read by reflection): shipped ReadPage = 'SEARCH event USING INTEGER PRIMARY KEY (rowid>? AND rowid<?)'; with '= $campaign' = 'SEARCH event USING INDEX idx_covenant_review_events_campaign_queue (CampaignId=? AND ScopeCode=? AND LaneCode=? AND Sequence>? AND Sequence<?)'; with literal IS NULL = 'USING INDEX idx_covenant_review_events_global_queue'; shipped ReadUpperFrontier = 'SEARCH covenant_review_events' (no index) vs index with '='; ComputeReviewedThrough = 'SEARCH event USING INTEGER PRIMARY KEY (rowid>?)'; ReadMarker = 'SCAN covenant_review_markers'. Timings: sqlite3 3.54 CLI over 300 campaigns x 50 entries x 6 and x 24 interleaved versions (probe-u24/gen.sql, time2.sql, time3.sql). 'rg idx_covenant_review_events tests' returns nothing, so any regression here passes every existing test.

**Suggested fix.** Build two statement variants per query (Global: literal 'CampaignId IS NULL'; Campaign: 'CampaignId = $campaign') as CovenantStoreSql does, for ReadMarker, ReadUpperFrontier, ReadPage, ReadEvent, ComputeReviewedThrough, AdvanceMarker and ReadCurationRevision, and add plan assertions next to CovenantQueryPlanTests that require idx_covenant_review_events_{global,campaign}_queue and ux_covenant_review_markers_*.

#### RV-200 (Minor, performance, polling-interval, effort S): The schema-transition driver sleeps 15 s after every pass regardless of pass cost, capping backfill throughput at 16 x MaxRowsPerBatch / 15 s (137-273 rows/s) while the Covenant capability stays unavailable

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionHostedService.cs:63` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionCoordinator.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaBackfillRunner.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/UtcInstantCanonicalizationBackfill.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs`

**Claim.** Interval = 15 s (line 37) is applied after every advancing pass (line 63) and a pass is limited to MaxBatchesPerPass = 16 batches (coordinator line 57). Declared batch bounds are 128-256 rows (UtcInstantCanonicalization 256, LongRestSourceIdentity 128, most others 200), so throughput has a hardware-independent ceiling of 273 / 213 / 137 rows/s. The Core v9 sweep walks Entries, session_attachment_chunks, saga_memories and about 60 other tables; the v7 sweep walks Entries at about 190 per batch.

**Failure scenario.** On an upgrade that crosses a sweep-bearing version, a Grimoire with 500,000 swept rows keeps the Covenant capability unavailable (TransitionIncomplete, 'a Core tier mid-run stands its dependents down') for about 31 minutes at 256 rows per batch or 61 minutes at 128, over 99% of it sleeping; 1.5 million rows (entries plus attachment chunks) is about 90 minutes. Data is intact, the operator has no knob, and it recurs for every release that adds a sweep.

**Evidence (probe).** PROBE_backfill_pass_duration_versus_the_15s_interval (real UtcInstantCanonicalizationBackfill, 16 batches in BEGIN IMMEDIATE transactions on SQLCipher over a 20,000-row table without triggers, so an optimistic batch cost, Debug build, machine load >100): one pass = 4096 rows in 94 ms, then 15 s sleep; duty cycle 0.6%; effective 271 rows/s vs 43,394 rows/s back to back. Batch bounds from Data/Schema/*Backfill.cs MaxRowsPerBatch; DESIGN section on the transition driver.

**Suggested fix.** After an advancing pass wait only a short yield (for example a small multiple of the pass duration, or 250-500 ms) and keep the 5-minute idle interval for 'nothing in flight'; optionally make the cadence configurable.

#### RV-201 (Minor, performance, polling-interval, effort S): A schema-transition pass refused admission, or one that failed, is scheduled like an idle pass (5 minutes)

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionHostedService.cs:78` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs`, `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaTransitionHostedServiceTests.cs`

**Claim.** RunOnceAsync returns false both when TryAcquireWorkLease is refused (lines 78-83) and when the coordinator reports a failure (lines 94-100); ExecuteAsync maps false to IdleInterval = 5 minutes (line 63) although a run may be pending.

**Failure scenario.** A pending sweep (capability fail-closed) whose pass meets a closed admission window (the gate is closed in production by exclusive Covenant operations such as erasure or reset) or a transient coordinator failure waits up to 5 further minutes after the window reopens instead of the 15 s used for advancing passes.

**Evidence (trace).** Trace only (not executed): hosted service lines 44-70 and 76-112; GrimoireSchemaTransitionHostedServiceTests.SchemaPassDenialCreatesNoScope shows the closed-gate case returns false. TryAcquireWorkLease returns false whenever the gate state is not Ordinary (GrimoireConnectionAdmissionGate.cs 322-329).

**Suggested fix.** Return a three-way outcome (idle, advanced, deferred) from RunOnceAsync and retry deferred and failed passes at Interval (or a short backoff) rather than IdleInterval.

#### RV-202 (Minor, reliability, blocking-io, effort M): Review list and prepare, both reads, take the database write lock, so any writer blocks them and a busy database surfaces as a 500

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:134` — related: `src/RetroDownfall.Arcanum.Api/Tower/MemoryReviewEndpoints.cs`

**Claim.** ListAsync (line 134) and PrepareAsync (line 367) call connection.BeginTransactionAsync(ct). That goes through SqliteConnection.BeginTransaction(Unspecified) and runs BEGIN IMMEDIATE, so both reads take the writer lock. ListAsync needs a write only for ReadOrCreateMarkerAsync's one-time INSERT OR IGNORE; PrepareAsync writes nothing. Neither method maps exceptions, and MemoryReviewEndpoints.WithCovenantReadAsync has no catch. Under WAL a deferred read is never blocked by a writer, but these reads queue behind every Grimoire writer and fail with the generic 500 after 30 s.

**Failure scenario.** `arcanum memory covenant review list` or a review prepare runs while a turn, a hosted producer, or maintenance holds the write lock. The read stalls behind it, and if the hold exceeds 30 s the operator gets 500 Hub.Unhandled for a read-only request. In turn, every concurrent writer is blocked for the duration of each list.

**Evidence (probe).** Probe P4 used a scratch copy of CovenantMemoryReviewServiceTests: seed a head; a second connection holds BEGIN IMMEDIATE; then ListAsync under a scoped read lease. Output: "PROBE4 list under a held writer: thrown=SqliteException sqlite=5".

**Suggested fix.** Read in a deferred transaction. Create the marker only when it is absent, in a separate short BEGIN IMMEDIATE under SqliteBusyRetry, before the read. Alternatively, treat an absent marker as (ReviewedThrough 0, Revision 1), which is what the insert creates, and create it in apply. Map busy to 503 Covenant.Unavailable as in F3.

#### RV-203 (Minor, reliability, error-message, effort S): Operator Covenant set, retire, correct and curate commits turn a busy database into a generic 500 after up to 30 s (a new angle on R-171)

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.cs:509` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.Curation.cs`, `src/RetroDownfall.Arcanum.Api/Tower/CovenantMutationEndpoints.cs`, `src/RetroDownfall.Arcanum.Api/Middleware/ArcanumExceptionHandler.cs`, `docs/Arcanum.API.md`

**Claim.** CommitAsync opens BEGIN IMMEDIATE through BeginTransactionAsync(Serializable) with no SqliteBusyRetry and no exception mapping, and CommitCurationAsync (Curation.cs:297) does the same. The endpoints have no catch either. R-171 fixed this class of fault only for the review apply, which retries for up to 2 min and answers 503 Covenant.Unavailable, "retry the same request", with nothing written. The four operator commit routes over the same database still let a SqliteException (code 5) escape to ArcanumExceptionHandler. That handler answers 500 Hub.Unhandled "An internal error occurred." and logs the exception object, including the driver text, at Error.

**Failure scenario.** A long Grimoire write holds the lock for more than Microsoft.Data.Sqlite's 30 s DefaultTimeout; SqliteBusyRetry's own remarks say exclusive-maintenance holds that long exist. An operator's `arcanum memory covenant set/retire/correct/pin/...` hangs for 30 s, then fails with an opaque internal error instead of a retryable 503. The log carries the exception text at Error rather than a content-free type and code.

**Evidence (probe).** Probe P3 used a scratch copy of CovenantMutationServiceTests: prepare a set; a second connection holds BEGIN IMMEDIATE; the service connection has DefaultTimeout=1 and busy_timeout=50; then SetAsync. Output: "PROBE3 elapsedMs=1157 thrown=SqliteException sqlite=5 answered=-". By contrast, CovenantMemoryReviewServiceTests.Apply_returns_a_stable_error_when_the_database_is_busy (R-171) asserts Covenant.Unavailable for the review apply. API §8 documents the 503 only for the review apply.

**Suggested fix.** Wrap the transaction body of CommitAsync and CommitCurationAsync in SqliteBusyRetry.ExecuteAsync; every attempt starts fresh, and the kernel already resolves a raced replay inside the transaction. Map an exhausted busy or GrimoireBusyTimeoutException to Covenant.Unavailable, and other storage faults to Covenant.WriteFailed, as CovenantMemoryReviewService.MapApplyFailure does, logging type and code only. Document the 503 on the four routes.

#### RV-204 (Minor, reliability, crash-consistency, effort S): A restart treats every ReopenPending schema-repair journal as durably mutated, so an interrupted no-op repair is kept closed or filed as Completed

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairStartupRecovery.cs:192` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantExclusiveDisposition.cs`, `docs/Arcanum.DESIGN.md`

**Claim.** durablyMutated = current.Phase is not Prepared. ReopenPending is reached either by the proven no-mutation edge (Prepared→ReopenPending, revision 1) or by the committed path (revision 3). A journal left at ReopenPending is exactly what DESIGN §10.17 says happens when the finalizer fails after the gate acted, and also what a process kill between the ReopenPending commit and the finalizer leaves. On restart that journal is always treated as mutated, so the disposition depends on canonical validity rather than on what happened. This contradicts DESIGN's statement that the next start resumes it.

**Failure scenario.** A RepairOrdinaryIndex with no missing index runs on a canonical family that is invalid for another reason, such as a missing trigger. The original process selects RollbackAndReopen, but the finalizer write fails or the process dies. On every later start the recovery picks KeepClosed (HealthPublished=false), the journal never terminates, and Covenant admission stays shut. With a valid canonical tier, the journal is instead filed Completed rather than Abandoned.

**Evidence (probe).** Probe P2 used a scratch copy with the real journal and triggers: commit Prepared, TryAdvanceAsync to ReopenPending, then RecoverBeforeReadinessAsync. Output: "PROBE2 canonicalValid=False outcome=KeptClosed terminalPhase=4 repairCalls=0" and "PROBE2 canonicalValid=True outcome=RecoveredReady terminalPhase=5 repairCalls=0" (5 is Completed; it should be 6, Abandoned). The issue is latent for the same reason as F1: no route writes journals today.

**Suggested fix.** The trigger enforces NEW.Revision = OLD.Revision + 1 from revision 0, so the path is recoverable from the row itself. Use durablyMutated = current.Phase switch { Prepared => false, ReopenPending => current.Revision != 1, _ => true }. Add tests for both ReopenPending origins.

#### RV-205 (Minor, reliability, crash-consistency, effort M): A failed schema repair leaves an active Prepared journal that closes Covenant admission on every later start, with no way out

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairStartupRecovery.cs:208` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMaintenanceService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairExecutor.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairJournal.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/covenant_schema_repair_intents_guard_update.sql`, `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantSchemaRepairTests.cs`

**Claim.** When RepairAsync fails, the in-request path (CovenantMaintenanceService.ExecuteRepairAsync:217-228) calls FinishAsync with RollbackAndReopen while the journal is still at Prepared. The gate reopens, but the finalizer then tries the edge Prepared→Abandoned (1→6), which the guard trigger forbids, so the journal stays active at Prepared. FinishAsync meanwhile reports Phase=ReopenPending. On every later start, CovenantSchemaRepairStartupRecovery adopts the owner and reruns the same failing repair. It answers Kept("repair") and keeps the installation closure: it never takes the proven no-mutation path, even though the catalog digest is unchanged. Separately, CovenantSchemaRepairExecutor.RepairAsync returns failure after DDL may already have committed: an installer result that is not healthy (line 187), or index N failing after indexes 1 to N-1 were created as autocommit statements (line 264). The in-request path then asserts DurablyMutated=false about a catalog that changed. The issue is latent: DESIGN §10.17 says ICovenantMaintenanceService is registered in no container, but the startup recovery that wedges is live.

**Failure scenario.** An operator runs a schema repair that cannot apply, for example InstallAbsentCanonicalFamily over a present family, or RepairExistingFamily over an absent one. That request reports ManualRecoveryRequired / ReopenPending and the process serves normally. After the next restart the adopted owner keeps the installation closed forever. Every ordinary lease (installation read, scoped read/write, turn, MCP, cleanup, accelerator) answers Covenant.Unavailable. Every other exclusive operation is refused too: another repair, a Covenant reset, a family reinitialize, and a restore. No route can resume the owner, because the in-request path always mints a new OperationId. Recovery needs a manual database edit or a factory reset.

**Evidence (probe).** Probe P1 used a scratch copy of CovenantSchemaRepairTests: CovenantMaintenanceService over the real journal and triggers, a stub executor with RepairFails=true, then two simulated restarts with a fresh gate. Output: "PROBE1 result.IsSuccess=True disposition=ManualRecoveryRequired reportedPhase=ReopenPending blocking=Covenant.MaintenanceFailed durablePhase=1"; "start#1 outcome=KeptClosed phase=1 read.ok=False read.code=Covenant.Unavailable otherRepair.ok=False otherRepair.code=Covenant.Unavailable reset.ok=False"; start#2 is identical. The trigger covenant_schema_repair_intents_guard_update allows only (1→{2,4}), (2→3), (3→4) and (4→{5,6}). The existing test Recovery_keeps_a_failed_repair_closed_and_leaves_its_journal_active pins the keep-closed half. No test drives RepairSchemaAsync with a failing repair.

**Suggested fix.** On a RepairAsync failure, reinspect first. If the catalog digest still equals InspectedCatalogDigest, take the no-mutation path in both the in-request and the startup code: Prepared→ReopenPending, then RollbackAndReopen, then the finalizer moves the journal to Abandoned. If the digest moved, keep it closed, but advance through CatalogCommitted so the next start does not answer catalog-moved. Have RepairAsync report whether it mutated alongside its error. Have FinishAsync report the durable phase rather than an inferred one. Update the pinned test.

#### RV-206 (Minor, usability, cli-ergonomics, effort S): Human-mode review list omits the proposed content (outside U24, in the Cli unit; confirm intent)

`src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.Review.cs:405` — related: `docs/Arcanum.Command.Reference.md`

**Claim.** WriteCovenantReviewPage prints key, origin, scope/lane, source, version and token but not item.CompiledContent, which CovenantReviewItemDto carries and the --json branch emits. memory covenant show and list are content-free by rule, so in human mode there is no CLI view of what a proposal says; the operator must discover that --json shows it. Saga and Lexicon human lists omit their text the same way, so this may be deliberate (terminal scrollback); if it is, the Command Reference should say that --json is the way to read the content under review.

**Failure scenario.** Operator runs memory covenant review list --lane proposed, sees keys such as response.style with opaque tokens and cannot decide Confirm versus Retire without re-running with --json.

**Evidence (probe).** MemoryCommands.Review.cs:405-421 (human) versus 130-148 (Json -> WriteJson(CovenantReviewPageDto)); the executed probe LIST output shows the DTO content ('- response.style: "Prefers concise answers."'). Command Reference review rows do not mention it.

**Suggested fix.** Print the content under the item in human mode (the response is already a protected read) or document that --json is the reading path.

#### RV-207 (Minor, usability, diagnostics, effort S): Review apply diagnostics drop the cause, 13 of 14 IntegrityFailure sites log nothing, and a retryable busy is logged at Error

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:499`

**Claim.** MapApplyFailure logs failure.GetType() only. For InvalidDataException the messages are app-authored constants ('Covenant canonical state is absent.', 'The Covenant review marker could not be created.', 'A Covenant head update did not produce its review event.') and are the only thing that says which state is damaged; they are dropped. Of the 14 places that return IntegrityFailure (245, 519, 1550, 1712, 2128-2278) only 519 logs. The ten in ReadReplayAsync return a 500 MemoryReview.IntegrityFailure with no log line and no request id, and the provenance-count check in ListAsync (242-246) fails the whole page of up to 50 items for one damaged version without saying which, so the lane cannot be reviewed. The busy case, which API 8.34 documents as a retryable 503, is logged at Error (503-506).

**Failure scenario.** Operator gets 500 MemoryReview.IntegrityFailure 'Durable Covenant review state failed its integrity contract.' on a replayed apply or a list page; the host log has either nothing (replay, list) or 'found damaged review state: System.IO.InvalidDataException.' with no hint which of three missing states; an on-call alert on Error-level lines fires for ordinary lock contention.

**Evidence (probe).** Executed probe: 'REPLAY OF A TAMPERED RECEIPT' -> FAILED code=MemoryReview.IntegrityFailure ... logEntriesAdded=0. 'COVENANT STATE ROW DELETED, THEN APPLY' -> only log line: '[Error] Covenant memory review apply found damaged review state: System.IO.InvalidDataException.' (exception detail not logged). Code: MapApplyFailure 499-528; IntegrityFailure return sites listed above.

**Suggested fix.** Log the app-authored message for InvalidDataException; add a fixed reason code per replay/list failure site plus RequestId (not content) at Warning; log the busy give-up at Warning.

#### RV-208 (Minor, usability, cli-ergonomics, effort S): Review confirmation screen identifies bulk targets by GUID only; the key is omitted although the server has it

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:967` — related: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.Review.cs`, `src/RetroDownfall.Arcanum.Core/Memory/MemoryReviewContracts.cs`

**Claim.** PlanItem builds (Sequence, Canonical(EntryId), Canonical(VersionId), IsCurrent, Origin, 'Turn:<guid>/Tool:<id>', 'Campaign:<guid>/<lane>') from a CovenantReviewEvent that carries Key, and the CLI (MemoryCommands.Review.cs:366-373) prints exactly those fields. The operator approving a bulk Retire/Correct/Pin sees only GUIDs and must trust that the opaque observation tokens in a hand-built decision file map to the keys they meant.

**Failure scenario.** Operator copies the wrong observation token into a decision file (tokens are 218-character base64 strings) and confirms 'Apply retire to 3 exact Covenant version(s)?' after a plan screen that shows 'event 12: 5b8c... @ 9f2e... (Campaign:.../Proposed; AgentProposed; Turn:.../Tool:...)'; nothing on the screen reveals that the wrong preference is about to be retired.

**Evidence (probe).** Executed probe 'PLAN ITEMS' prints per item subject=<guid> version=<guid> scope=Campaign:<guid>/Proposed origin=AgentProposed source=Turn:<guid>/Tool:call-1 and ':: contains key? False' for response.style, tone.formal and units.metric. Code: CovenantMemoryReviewService.cs:967-975; WriteReviewPlan in Cli MemoryCommands.Review.cs:366-373. The same keys are shown by review list (event numbers), so the information exists one screen earlier.

**Suggested fix.** Put the key into the plan item (the Source/Scope string or a trailing optional Label on the shared MemoryReviewBulkPlanItemDto) and print it in WriteReviewPlan.

#### RV-209 (Minor, usability, api-contract, effort S): Hash casing differs between the review list and every other Covenant read of the same DTO

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs:1133` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantManagementService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.cs`

**Claim.** CovenantHeadDto.AuthoredHash/RenderedHash/ProvenanceDigest and CovenantSourceDto.ContentHash are lower-case hex from /list, /detail, /sources and the mutation DTOs (Convert.ToHexStringLower in CovenantManagementService.cs:1091 and CovenantMutationService.cs:792) but upper-case from review list (Hex at line 1133 and the source projection at line 1419 use Convert.ToHexString). The parse side is case-insensitive, so correct --target-hash hides it, but a script comparing show --json renderedHash with review list --json current.renderedHash as strings sees different values. No test or document pins the case.

**Failure scenario.** Automation that matches a review item to a show/list entry by rendered hash string never matches.

**Evidence (probe).** Executed probe LIST output: 'headHashes: rendered=3CA948FFC0E18D4A23C141EA099249523BFFFE94996DF1F6B7309B19EB1F230B' (upper-case); CovenantManagementService.cs:719-751,769 use the lower-case helper for the same DTO types; grep of CovenantMemoryReviewServiceTests / MemoryReviewCommandTests finds no hash-case assertion.

**Suggested fix.** Use the lower-case helper in the review service (lines 1133, 1419) and pin it in CovenantMemoryReviewServiceTests; note the casing in API 8.28.

#### RV-210 (Minor, usability, error-message, effort S): Covenant.InvalidContent from the operator write and review paths names an internal parameter, not the offending character, and the refusal rule is undocumented

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMutationService.cs:731` — related: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantCompiler.cs`, `src/RetroDownfall.Arcanum.Core/Covenant/CovenantOperationalDefaultCatalog.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantMemoryReviewService.cs`, `docs/Arcanum.API.md`

**Claim.** TryCompile (and the two review-service catch blocks at CovenantMemoryReviewService.cs:954-961 and 1516-1523) return the compiler ArgumentException.Message unchanged. The BCL appends "(Parameter 'authoredContent')", which is not a wire field (the body fields are content / replacementContent), and the compiler (Core/Covenant/CovenantCompiler.cs:156-165) says only 'contains a Unicode 17 Format scalar' or 'a prohibited control scalar', with no code point or offset. Format scalars include U+200B, U+200D (emoji joiner), U+00AD, U+200E/F and U+FEFF, which arrive with web and PDF pastes and emoji sequences. API 8.28 'Frozen request bounds' lists only 1-2,048 UTF-8 bytes; no document says Format scalars, control characters or whitespace-only text are refused. The reserved typed defaults refuse any whitespace including the trailing newline that 'echo 0.7 |' adds with 'A Covenant operational default requires a finite bounded unsigned decimal scalar.', which does not mention whitespace; only a Command Reference example (printf '0.25' |, line 809) hints at it.

**Failure scenario.** Operator pastes a preference copied from a web page or PDF into a file and runs memory covenant set: 400 Covenant.InvalidContent 'Covenant authored content contains a Unicode 17 Format scalar. (Parameter 'authoredContent')'. Nothing says which character or where in up to 2 KB of invisible-character text, nor that such characters are refused at all.

**Evidence (probe).** Console probe over the built Core DLL (scratchpad/review/probe-u24-usability/compile-probe.txt): ZWJ family emoji, zero-width space, soft hyphen, BOM and LRM each -> 'ArgumentException: Covenant authored content contains a Unicode 17 Format scalar. (Parameter 'authoredContent')'; NUL -> '...prohibited control scalar. (Parameter 'authoredContent')'; whitespace-only -> '...cannot be empty or whitespace-only. (Parameter 'authoredContent')'; 'Response Style' key -> '(Parameter 'value')'; typed default '0.7\n' -> 'requires a finite bounded unsigned decimal scalar. (Parameter 'content')'. Through the real service (executed probe, PrepareSetAsync): zero-width space and soft hyphen -> code=Covenant.InvalidContent with the same text; 3,000 bytes -> the clear 'must be between 1 and 2048 UTF-8 bytes' from wire validation.

**Suggested fix.** Map the compiler failure to a typed reason without the BCL suffix; name the wire field, the code point (U+200B) and the 1-based scalar offset (neither is content a log would mind); add the rule (Format scalars, control characters, whitespace-only, typed-default whitespace) to API 8.28 and the set/correct Command Reference rows.

#### RV-211 (Minor, usability, docs-mismatch, effort S): Gate class remarks say acquisition touches no database; the Campaign-exclusive paths read it and can throw

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantOperationGate.cs:14` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantCampaignScopeProbe.cs`

**Claim.** The remarks say 'Acquiring and revalidating touch memory only: no secret store, no database, no logging of a Campaign ID or any Covenant content.' AcquireCampaignExclusiveAsync (214-245), AcquireProtectedTransferAsync with a Campaign scope (247-280) and AcquireEntryErasureAsync for a Campaign entry (282-326) call RequireLiveCampaignAsync (553-581), which opens a DI scope and runs two SELECTs (Campaigns, owner_deletion_events) and throws GrimoireMaintenanceUnavailableException when admission is closed (CovenantCampaignScopeProbe.cs:101) instead of returning a Result. The probe's own remarks call this 'the cold exclusive-acquisition path'.

**Failure scenario.** The next engineer calls a Campaign-bound exclusive acquisition while holding a Grimoire connection or transaction, or without a handler for GrimoireMaintenanceUnavailableException, because the gate is documented as memory-only.

**Evidence (trace).** CovenantOperationGate.cs:14-17 vs 553-581; CovenantCampaignScopeProbe.cs:46,101,109. Trace.

**Suggested fix.** Restate the remark: ordinary leases are memory only; Campaign-bound exclusive acquisitions do one live-Campaign read and can throw the maintenance exception.

#### RV-212 (Minor, usability, error-message, effort S): The gate's refusal says 'closing' for a closure that may be kept closed indefinitely, and never names the operation

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantOperationGate.cs:633`

**Claim.** AcquireOrdinary answers every blocked lease with Covenant.Unavailable 'A Covenant operation is closing this scope, so no new lease may be taken over it.' A closure whose exclusive lease completed with KeepClosed (CompleteExclusive 1222-1244) stays installed for the life of the process (and is re-adopted at restart), so the operator sees 'closing' for a state that needs recovery. The closure knows Owner.Operation and whether LiveRegistration/AcquisitionInProgress is set, so it could distinguish 'an erasure is in progress, retry in a moment' from 'a reset was kept closed pending recovery'. ResumeOrAcquireInstallationAsync ('Another Covenant recovery owner already holds this scope closed.', 786-789) and the 'Another Covenant operation already owns this scope.' texts (703-706) have the same gap.

**Failure scenario.** After a restore or reset is kept closed, every memory covenant list/review/set answers 503 with the same transient-sounding sentence for hours; the operator retries instead of running the recovery.

**Evidence (trace).** Code lines 633-638, 703-706, 786-789, 965-967, 1222-1244; the gate logs nothing by design (class remarks), so the message is the only carrier. Trace, not executed.

**Suggested fix.** Include the operation name and in-progress versus kept-closed in the message (operation names are not secrets); keep the log silent.

#### RV-213 (Minor, usability, diagnostics, effort S): Startup refusal 'See logs for recovery steps' has no log behind it when the repair journal cannot be read (dormant today)

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairJournal.cs:105` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairStartupRecovery.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairExecutor.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`

**Claim.** TryReadActiveAsync turns any exception, two live intents and any malformed row into the same ReadFailure() 'could not be read safely' with no log; PrepareBeforeEffectsAsync forwards it, and GrimoireDatabaseBootstrapper discards the Error and throws GrimoireDatabaseUnavailableException 'Protected Covenant startup recovery could not prove one durable owner. See logs for recovery steps.' (1227-1230, 1340-1343); no Log call lies between. The host does not start and no log line carries the reason. The InvalidOperationException arm of PreparationFailure (CovenantSchemaRepairStartupRecovery.cs:130-133) also drops its message although the doc comment at 408-413 says the operator needs it. Kept() warnings (397-406) name the stage and code but not the intent's operation id, action or phase, and say the journal 'stays active for the next start' even for deterministic stages (catalog-moved, canonical-invalid, evidence-selected-keep-closed) whose next start repeats; with ICovenantMaintenanceService unregistered there is no operator verb to clear it. CovenantSchemaRepairExecutor.RepairAsync returns raw SqliteException.Message (195, 265) where Kept says driver messages are not logged. Dormant: the only creator of intents (CovenantMaintenanceService.CommitPreparedAsync) is registered nowhere, so no production database holds a repair journal.

**Failure scenario.** Once the maintenance port is wired, a damaged or duplicated repair journal stops the host at startup with a message pointing to logs that say nothing about it.

**Evidence (trace).** Code lines cited; grep shows CommitPreparedAsync has a single caller (CovenantMaintenanceService.cs:123) and ICovenantMaintenanceService is registered nowhere; DESIGN 10.17 and API 8.28 state the same absence. Trace, not executed.

**Suggested fix.** Log the Error (code and stage) before throwing in the bootstrapper; include operation id/action/phase in Kept and say whether a restart can change the outcome; keep driver messages out of returned errors.

#### RV-214 (Minor, usability, docs-mismatch, effort S): Documentation attached to the wrong member in CovenantTurnHeadProbe

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantTurnHeadProbe.cs:118`

**Claim.** Two stacked summary/remarks blocks precede ProbeScopeAsync (118-133). The first ('Measures the turn's own Campaign Section') describes ProbeSectionAsync, which has no documentation, so the scope-quota probe is documented as a Section probe and the Section probe not at all.

**Failure scenario.** An engineer reading ProbeScopeAsync is told it measures one Section and may exclude the wrong keys or skip the scope-wide ceiling.

**Evidence (probe).** CovenantTurnHeadProbe.cs:118-157 (ProbeScopeAsync calls store.ReadQuotaSnapshotAsync; ProbeSectionAsync calls store.ReadSectionOccupancyAsync).

**Suggested fix.** Move the first block onto ProbeSectionAsync.

#### RV-215 (Minor, usability, docs-mismatch, effort S): GrimoireSchemaTransitionPassOutcome.Advanced does not mean what its remarks say and can spin a stuck run every 15 seconds while logging 'advanced'

`src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionCoordinator.cs:116` — related: `src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionHostedService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaEvolutionPlanner.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaBackfillRunner.cs`

**Claim.** The remarks (12-24) say Advanced means 'there was work and some of it was done'. The coordinator sets it whenever a run exists with a null BackfillName (line 120) before any work, and whenever the runner counts a batch it then rolled back (GrimoireSchemaBackfillRunner returns StepComplete: false with BatchesRun >= 1 when finalization is unhealthy). A non-Core run with a null BackfillName that the build cannot resume (TransitionUnresumable: GrimoireSchemaEvolutionPlanner.Resume refuses a journal whose target version or fingerprint differs from this build's head, and GrimoireSchemaInstaller.Refuse returns a failed result rather than throwing for Covenant tiers) therefore reports Advanced = true on every pass, so the hosted service waits 15 s instead of 5 min, re-runs the installer for all tiers each time, republishes availability, and logs 'advanced 1 run(s) through 0 batch(es) and 0 row(s)'.

**Failure scenario.** After a downgrade or a second upgrade over an unfinished Covenant-tier run, the host logs a false 'advanced' line and rewrites the head DDL and availability every 15 seconds indefinitely, while the tier stays unavailable.

**Evidence (trace).** GrimoireSchemaTransitionCoordinator.cs:116-124,143,169-170; GrimoireSchemaEvolutionPlanner.cs:142-163; GrimoireSchemaInstaller.cs:718-724 (Refuse); GrimoireSchemaBackfillRunner.cs unhealthy-finalization arm. Trace, not executed (the existing harness is Core-tier only, where Refuse throws).

**Suggested fix.** Set Advanced only from progress that moved a cursor or finished a step, or from a converge result that changed a tier's health or version; do not count rolled-back batches.

#### RV-216 (Minor, usability, error-message, effort S): propose_covenant refuses uncompilable input with the wrong code and a sentence the model cannot act on (outside U24; found while tracing TryCompile)

`src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.CovenantTools.cs:104` — related: `src/RetroDownfall.Arcanum.Infrastructure/Mcp/InternalTools/ArcanumInternalToolServer.CovenantSchemas.cs`, `src/RetroDownfall.Arcanum.Core/Covenant/CovenantCompiler.cs`

**Claim.** Any ArgumentException from the compiler (bad key grammar, over 2,048 bytes, whitespace-only, Format scalar) becomes Covenant.InvalidScope 'propose_covenant could not compile that key and content under the Covenant text policy.' The code names a different problem (scope), the text does not say which of key or content failed or what the policy is, the tool schema (ArcanumInternalToolServer.CovenantSchemas.cs:38-49) gives the model neither the key grammar ([a-z0-9][a-z0-9._-]{0,127}) nor the size cap, and the log records only the exception type name (ArgumentException).

**Failure scenario.** The model proposes key 'Response Style' or a 3 KB paragraph; it gets InvalidScope with a fixed sentence, cannot tell whether to change the key, shorten the content or strip characters, and either retries blindly or drops the preference; the operator sees only 'rejected content that failed compilation: ArgumentException' in the log.

**Evidence (probe).** Code lines 104-119 (catch (ArgumentException ex) -> CovenantFailure(ErrorCodes.Covenant.InvalidScope, fixed sentence), log reason = ex.GetType().Name). The probe in compile-probe.txt shows the distinct causes the single sentence collapses: key grammar, 2048-byte cap, Format scalar, whitespace-only. Schema descriptions: key 'Short stable identity ... such as response.style or build.commands'; content 'The preference itself, in one plain sentence ...' (no limits). Not executed through the tool server.

**Suggested fix.** Return Covenant.InvalidKey / Covenant.InvalidContent with a fixed reason per cause (the reasons are not content), state the key grammar and byte cap in the schema descriptions, and log the reason category rather than the exception type.

### U25: Arcanum.Infrastructure — Data (1)

#### RV-217 (Minor, performance, missing-index, effort S): Subject-scoped Annals erasure appends OR NOT EXISTS orphan clauses that force full scans of long_rest_receipt_inputs and long_rest_suppressions

`src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsErasurePlan.cs:106` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsClaimWriter.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/long_rest_receipt_inputs.sql`

**Claim.** Build() gives long_rest_suppressions (101) and long_rest_receipt_inputs (106) the predicate 'ReceiptId IN (receiptScope) OR NOT EXISTS (SELECT 1 FROM long_rest_receipts ...)' for every plan, including the single-subject ForSubjectQuery. An OR with a non-indexable branch makes SQLite SCAN the whole table (long_rest_receipt_inputs rows carry SnapshotJson up to 1 MiB). The statements run for every erased Saga memory (SagaMemoryStore.cs:827), every Lexicon delete (LexiconService.cs:452) and, through SagaLongRestPruneSteps (DataRetentionService.Pruning.cs:2229), as count + delete (+ planning preview) per retention candidate (6018).

**Failure scenario.** Retention sweep over K Saga memories on an installation with Long Rest history (80k receipt inputs): each candidate pays ~2-3 scans of ~25 ms CPU in its own write transaction, growing linearly with receipt count; K=1000 is ~1 minute of scanning. Single explicit memory delete pays one ~25 ms scan.

**Evidence (probe).** EXPLAIN QUERY PLAN: 'SCAN long_rest_receipt_inputs' with the receipt scope as LIST SUBQUERY. Probe P4 (SQLCipher copy of the tables, 20k receipts/80k inputs/138 MB): receipt_inputs with clause 20.2-21.6 ms CPU vs 0.1-0.3 ms without; suppressions 4.2-9.2 ms vs 0.1-0.2 ms; cache_size 256 MB made no difference (18 ms).

**Suggested fix.** Emit the orphan clauses only in the store-wide ForStore plan (FKs with ON DELETE CASCADE are enforced and read back on every connection) or run the orphan sweep once per retention pass rather than per subject; keep the subject-scoped statements index-driven.

#### RV-218 (Minor, performance, error-handling-cost, effort S): After the alert threshold every turn attempts a duplicate BudgetAlerts insert, throws, and logs a warning; the cheap pre-check is unused

`src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetAlertRepository.cs:19` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/BudgetMonitor.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/BudgetAlerts.sql`

**Claim.** BudgetMonitor.CheckAsync (run at every turn start) calls TryDispatchAlertAsync -> RecordAlertAsync on every turn once spend is at or above the alert threshold (and again at the 100% limit). The unique index (Threshold, date(AlertedAt)) rejects the insert; the catch at line 56 logs LogWarning each time. HasAlertedTodayAsync (line 66) has no production caller.

**Failure scenario.** From the moment spend crosses 80% until UTC midnight, each turn takes a write-lock attempt, raises a SqliteException, and writes a Warning log line ('already recorded today; skipping duplicate'), flooding the log and adding an avoidable write-lock acquisition (which can contend/retry under load) to every turn.

**Evidence (probe).** Probe P9 (SQLCipher): duplicate RecordAlertAsync 0.047 ms CPU per call, 7 first-chance exceptions per call, 1 Warning per call, vs 0.007 ms for the unused HasAlertedTodayAsync.

**Suggested fix.** Call HasAlertedTodayAsync first (or remember the last-alerted UTC day per threshold in the singleton BudgetMonitor) and only attempt the insert when it returns false.

#### RV-219 (Minor, performance, allocation, effort M): Committed-spend sum re-reads and BigInteger-adds every bill completed today on each reserve/adjust, inside the write transaction

`src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs:733` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/ExactUsdText.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/BillableOperations.sql`

**Claim.** SumCommittedAsync (699-739) streams every BillableOperations row completed today and folds it with ExactUsdText.CheckedAdd (ExactUsdText.cs:51-89), which allocates two int[4], two BigIntegers, two BigInteger.Pow(10,k) and a divide loop per row. It runs within BEGIN IMMEDIATE in ReserveAsync/AdjustAsync and in Recheck/GetToday*. Work per call is O(bills today), so a day's total work is quadratic in provider calls, contradicting the class summary 'tiny BEGIN IMMEDIATE transactions'.

**Failure scenario.** Agent-heavy or embedding-heavy day with 10k-30k billable operations: every reserve/raise holds the write lock for 12-33 ms of pure summing and allocates ~4-12 MiB of garbage per pass; all other writers (entry appends, checkpoints) queue behind it.

**Evidence (probe).** Probe P7: 1.2-1.4 us and 421 B/row for Parse+CheckedAdd (12 MiB garbage per 30k rows) vs 0.04-0.2 us for plain decimal += with equal totals. Probe P1 GetTodayCommitted CPU: 5.35 ms at 2k bills, 12.0 ms at 10k, 33.2 ms at 30k. Plan is SEARCH ... IX_BillableOperations_CompletedAt (not covering, so one table lookup per row).

**Suggested fix.** Accumulate once into a scaled Int128 (fall back to BigInteger only on overflow), add a covering index (CompletedAt, ActualCostUsd), or keep a per-UTC-day running total updated in the same transaction that inserts the BillableOperation.

#### RV-220 (Minor, performance, complexity, effort S): Sensitivity-label keyset pagination uses ($after IS NULL OR LabelId > $after), so every page rescans the index from the start

`src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.cs:340` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureInventorySource.cs`

**Claim.** ReadPageWithinAsync filters with a parameter-guarded OR, which SQLite cannot use as a range constraint; the plan is a full COVERING INDEX SCAN with a residual filter, so page k reads k pages of entries and an inventory walk is O(n^2 / pageSize). Used by the erasure inventory over the whole artifact_sensitivity table.

**Failure scenario.** Covenant erasure inventory on an installation with ~500k labels walks ~2,000 pages each rescanning up to 500k index entries (~100x the linear cost; roughly 17 s of CPU vs 0.25 s, extrapolated from 50k) inside the enrolled maintenance snapshot.

**Evidence (trace).** EXPLAIN QUERY PLAN: 'SCAN artifact_sensitivity USING COVERING INDEX sqlite_autoindex_artifact_sensitivity_1'. Full walk of 50,000 labels at 256 per page (196 pages, plain sqlite3): OR form 169 ms CPU vs 25 ms for WHERE LabelId > ? bound to '' for the first page. The same shape in CampaignRollupStore.Manifests.cs:158 is bounded by one rollup's sources.

**Suggested fix.** Bind '' as the first cursor and use plain WHERE LabelId > $after ORDER BY LabelId LIMIT $limit so the primary-key index is range-sought.

#### RV-221 (Minor, performance, complexity, effort M): Every tainted-artifact write re-reads all of the Session's sensitivity labels to recompute the merged provenance digest

`src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.cs:663` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/artifact_sensitivity.sql`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.LexiconReplacement.cs`

**Claim.** MergedProvenanceDigestAsync (651-685) runs SELECT DISTINCT ProvenanceModeCode, ExactGenerationIds, GenerationBloom FROM artifact_sensitivity WHERE SessionId=$sessionId from AdvanceProjectionAsync (624) and ReplaceLexiconWithinAsync (.LexiconReplacement.cs:126). idx_artifact_sensitivity_session is on SessionId alone, so every label is fetched with a table lookup for two blobs and de-duplicated in a temp B-tree; the comment claiming the indexed lookup keeps this bounded is wrong: DISTINCT bounds the merge, not the read. It runs inside the writer's transaction.

**Failure scenario.** A Covenant Session with ~10k labelled artifacts pays 6.6 ms CPU inside the write lock per additional labelled write (37 ms at 50k); cumulative cost per Session is O(n^2).

**Evidence (probe).** Plan: 'SEARCH artifact_sensitivity USING INDEX idx_artifact_sensitivity_session (SessionId=?)' + 'USE TEMP B-TREE FOR DISTINCT'. Probe P3 (SQLCipher, all labels one generation): 0.043 ms at 100 labels, 0.095 ms at 1,000, 6.58 ms at 10,000, 37.3 ms at 50,000.

**Suggested fix.** Store the merged provenance bytes (<=128 B) on session_sensitivity_state and fold the new label into it, or at minimum add a covering index (SessionId, ProvenanceModeCode, ExactGenerationIds, GenerationBloom).

#### RV-222 (Minor, performance, n-plus-one, effort M): Validating a bound Campaign rollup re-verifies every source with ~5 queries each, twice per provider dispatch

`src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignRollupStore.Manifests.cs:16` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignRollupStore.Read.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs`

**Claim.** ReadArtifactAsync for a rollup (Read.cs:196-239) calls VerifySourcesAsync, which for each of N sources issues ReadMetadataAsync, ReadStateAsync, ReadCanonicalCampaignAsync (3-way join) and VerifyArtifactLabelAsync (presence probe + point read), unprepared commands every time. ValidateAsync and ReadCurrentAsync both take this path, and WizardIntelligenceProvider.AcknowledgeCovenantDispatchAsync (WizardIntelligenceProvider.cs:7737 and 7810) calls campaign.ValidateAsync before and after the dispatch acknowledgement, i.e. twice per provider round for a turn carrying a rollup.

**Failure scenario.** Campaign with 600 contributing Sessions and a 4-round turn: ~8 validations x ~22 ms = ~175 ms of query work added per turn, scaling linearly with Campaign size.

**Evidence (probe).** Probe P5 (SQLCipher, unlabelled sources): ValidateAsync 3.7 ms CPU at 100 sources, 11.3 ms at 300 (~37 us per source); ReadCurrentAsync 3.5 / 10.5 ms.

**Suggested fix.** Validate the rollup row, SourceCount and manifest digest and bind sources by a per-source SourceGeneration/revision comparison in one set-based query instead of per-source round trips.

#### RV-223 (Minor, reliability, data-integrity, effort S): Attachment consultation window compares with millisecond julianday() against 100 ns watermarks

`src/RetroDownfall.Arcanum.Infrastructure/Data/AttachmentMemoryProvenanceStore.cs:106` — related: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/Loremaster.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/EntryTemporalQueries.cs`

**Claim.** ListConsultationsAsync filters `julianday(e.CreatedAt) > julianday(@afterExclusive) AND julianday(e.CreatedAt) <= julianday(@throughInclusive)`. SQLite holds a parsed date as integer milliseconds, so instants less than 1 ms apart compare equal. The only caller (Loremaster.cs:372-386) passes the Logger watermark and the batch's last CreatedAt. Both come from the batch EntryTemporalQueries selects, and that comparison uses fixed-width 7-digit text (100 ns). An entry within the same millisecond as a batch boundary is therefore in one window for entries and in another for consultations.

**Failure scenario.** The previous Logger batch ends at an entry stamped T, and the next turn's entries are stamped T+300us (rapid automated turns, for example A2A or scripted ask loops). The next batch contains that assistant entry, but ListConsultationsAsync(T, end) returns none of its attachment consultations, so that summary drops its consulted references. The previous batch's window (..,T] did include them, even though their entry was not in that batch.

**Evidence (probe).** `sqlite3 :memory: "select julianday('2026-10-24T12:00:00.0004000Z') > julianday('2026-10-24T12:00:00.0001000Z'), '...0004000Z' > '...0001000Z'"` prints 0|1. Probe AttachmentMemoryProvenanceStoreU25ProbeTests.U25Probe_consultation_window_loses_sub_millisecond_entries failed as predicted (`Assert.Single() Failure: The collection was empty`). Output: `PROBE consultations in (watermark, +1s]: 0` / `PROBE consultations in (-1s, watermark]: 1`. The existing AttachmentMemoryProvenanceStoreTests uses a +/-1 s window and cannot see this.

**Suggested fix.** Compare the canonical fixed-width text directly (`e."CreatedAt" > @afterExclusive AND e."CreatedAt" <= @throughInclusive`), as EntryTemporalQueries does. A better fix is to key the window on Entry Sequence. Keep the probe as a regression test.

#### RV-224 (Minor, reliability, docs-mismatch, effort S): DESIGN says the erasure coordinator checkpoints ReopenedVerified; it never does, and the resume arm for it is dead

`src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs:1889` — related: `docs/Arcanum.DESIGN.md`

**Claim.** DESIGN §10.20.4 states 'The coordinator checkpoints ReopenedVerified' and that 'ReopenedVerified uses ResumeOrAcquireExclusiveAsync only to close the narrow post-disposition/checkpoint-finalization crash gap'. In the code ReopenedVerified is only an in-memory assignment (line 2567); no journal or row write records it. Both checkpoint projections (FromMutationCheckpoint and FromFactoryResetCheckpoint, through Owned at line 409) always yield CovenantResetPhaseMachine.First, so the `or CovenantResetPhase.ReopenedVerified` arm at lines 1889-1890, and its comment, can never be reached.

**Failure scenario.** A maintainer or operator relying on DESIGN expects a post-proof crash to leave a durable ReopenedVerified that recovery resumes from. In fact the journal's last completed phase is SidecarsVerified, and recovery repeats VerifyReopenAsync before publishing. The behaviour is safe; the documented recovery model and the dead arm are wrong, which misleads future changes to this phase machine.

**Evidence (trace).** Traced, no execution needed. `rg ReopenedVerified src` finds only lines 1886-1890 and 2551-2567 in the coordinator. The phase is never passed to phases.BeginPhaseAsync or CompletePhaseAsync, and nothing records it otherwise. Owned() at line 409 always projects CovenantResetPhaseMachine.First.

**Suggested fix.** Correct the two DESIGN §10.20.4 sentences to say the proof is repeated on resume and is not checkpointed. Remove the unreachable ReopenedVerified arm in RunAsync, or say in its comment that it is never taken.

#### RV-225 (Minor, reliability, data-integrity, effort S): Campaign-summary retention plans protected summaries that apply can never delete (permanent PlanChanged conflict)

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.CampaignSummaries.cs:44` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs`, `docs/Compendium.README.md`

**Claim.** The planner selects every aged rollup or contribution artifact using only `julianday(CreatedAtUtc) <= julianday(@cutoff)`. Apply, at line 158, deletes only rows with `SensitivityCode = 0`. A protected summary (SensitivityCode = 1) is therefore planned as a deletion, but apply returns CandidateDeleteResult.Empty for it. Pruning.cs:3523-3553 then turns it into a preserved candidate with a Data.PlanChanged conflict and pins earliestSkippedIndex. Every later sweep plans the same row again.

**Failure scenario.** Covenant, campaign rollups and retention.campaignSummaries are on, and one contribution older than the cutoff was built from protected input. From then on every `data prune` preview reports a CampaignSummaries row to delete. Every apply and every automatic sweep deletes 0 rows and reports `Data.PlanChanged: The retention candidate changed or became protected after planning`, although nothing changed. The prune checkpoint cursor cannot advance past that candidate, so a crash later in the sweep replays every later candidate. Compendium.README does not say protected summaries are kept.

**Evidence (probe).** Probe DataRetentionServiceTests.U25Probe_protected_campaign_summary_is_planned_but_never_deleted (scratch worktree at ac0d2d8e) seeds one aged contribution with SensitivityCode=1, then plans, applies and plans again. It passed, which confirms the defect. Output: `PROBE plan candidates: campaign-contribution:06164EB3-...; rows=1` / `PROBE apply rowsDeleted=0; conflicts=Data.PlanChanged:The retention candidate changed or became protected after planning; it was preserved.` The row survives apply and the second plan selects it again. The existing DataRetentionServiceTests.CampaignSummaries tests seed only SensitivityCode 0, so they all pass despite this.

**Suggested fix.** Add `SensitivityCode = 0` to the planner predicate at line 45 so planning and apply agree. Protected summaries could instead be reported as retained-protected status, not as candidates. Document in Compendium.README/DESIGN that protected summaries leave only through the sensitivity purge or reset. Keep the probe as a regression test.

#### RV-226 (Minor, usability, cli-ergonomics, effort S): The help example for data retention set does not work

`src/RetroDownfall.Arcanum.Cli/Infrastructure/Surface/CliSurfaceExamples.cs:310` — related: `tests/RetroDownfall.Arcanum.Tests/Cli/CliHelpExampleTests.cs`, `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Data.cs`

**Claim.** Outside the unit, found while following the retention-set messages. arcanum data retention set --help prints 'Examples: arcanum data retention set --sessions 90'. The command takes two positional arguments (<class> <days|disabled>) and has no --sessions option. CliHelpExampleTests only asserts that examples are printed, never that they parse into a handler.

**Failure scenario.** An operator copies the only example in the help and gets exit 2 and a message that lists no valid classes.

**Evidence (probe).** Probe PROBE_retention_set_example_runs through CliTestHarness: 'PROBE-RUN exit=2 ... err=[<data-class> must name a supported retention data class.]'; the same probe prints the --help output ending with the broken example. The parse reports 0 errors because --sessions is consumed as the class argument.

**Suggested fix.** Change the example to 'arcanum data retention set archived-sessions 90' and add a test that parses every registered example and checks it reaches a handler.

#### RV-227 (Minor, usability, diagnostics, effort S): Connection failure is logged as one fixed line with no exception type or SQLite result code, and EF's own event is suppressed

`src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContextOptionsConfigurator.cs:50` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantConnectionEnrolmentInterceptor.cs`, `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionInterceptorTests.cs`

**Claim.** ConfigureServingEnrolment ignores RelationalEventId.ConnectionError. The replacement signal (Covenant/CovenantConnectionEnrolmentInterceptor.cs:205-217) is 'Error: An ordinary Grimoire connection failed to open safely.' with no exception, and GrimoireConnectionAdmissionInterceptorTests.cs:1092-1094 pins that text and Assert.Null(entry.Exception). A wrong key, a locked file, a missing file and a full disk all log identically. CovenantLabeledArtifactGuard.cs:308-312 shows the accepted content-free alternative: SqliteErrorCode and SqliteExtendedErrorCode.

**Failure scenario.** After a credential-store change the Grimoire stops opening for ordinary requests; the only log line cannot distinguish SQLITE_NOTADB from SQLITE_BUSY or SQLITE_FULL.

**Evidence (trace).** Read of the configurator comment (41-44), the interceptor LogFailure and the pinning test. Not executed.

**Suggested fix.** Log eventData.Exception.GetType().Name and, for SqliteException, the primary and extended result codes; update the pinning test.

#### RV-228 (Minor, usability, docs-mismatch, effort S): Stale and stranded documentation comments

`src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs:12` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/UploadedFileRepository.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/SanctumBreachRepository.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyStore.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/UnseenServantWatermarkStore.cs`

**Claim.** The class summary says the table is 'created by the embedded InitialCreate.sql schema baseline under Data/Schema/Tables/'. No such file exists (the object is Data/Schema/Tables/Batches.sql); AGENTS.md says to edit the object's own file. The same sentence is in UploadedFileRepository.cs:14, SanctumBreachRepository.cs:17, IdempotencyStore.cs:12 and UnseenServantWatermarkStore.cs:12. In CovenantErasureCoordinator.cs the summary at 3065-3074 ('Performs one phase's step exactly once and records it') belongs to AdvanceAsync but sits above the _reconciler field, and the summary at 4269-4276 documents the progress type with no member beneath it.

**Failure scenario.** The next engineer looks for InitialCreate.sql to change a Batches column, and IDE hover on _reconciler shows the wrong text.

**Evidence (trace).** find Data/Schema -iname '*InitialCreate*' returns nothing; Tables/Batches.sql exists; read of the two comment blocks.

**Suggested fix.** Reword the five comments to name Data/Schema/Tables/<Object>.sql; move or delete the two stranded summaries.

#### RV-229 (Minor, usability, error-message, effort S): One bad row aborts every blob-encryption command and the error names no row

`src/RetroDownfall.Arcanum.Infrastructure/Data/BlobEncryptionMetadataStore.cs:170` — related: `src/RetroDownfall.Arcanum.Infrastructure/Storage/BlobEncryptionLifecycleService.cs`, `src/RetroDownfall.Arcanum.Cli/Commands/DataEncryptionCommands.cs`

**Claim.** ListAsync throws on the first attachment row whose path escapes the root (InvalidDataException 'Session attachment metadata escapes the attachment root.', 170-181) or whose UploadedFiles Id is not a Guid (Guid.Parse at 152, no row context). status, migrate, verify and rotate-key all list first; DataEncryptionCommands has no catch, so CliFailureMapper prints 'An unexpected CLI error occurred.' (CliContracts.cs:857-866) with exit 1. The row-vanished message at 76-77 has no id either.

**Failure scenario.** A tampered or damaged catalog row makes arcanum data encryption status fail with a generic line; the operator cannot learn which row, and cannot run migrate or verify for the healthy rows.

**Evidence (trace).** Trace through BlobEncryptionLifecycleService.GetStatusAsync, DataEncryptionCommands.Status and CliFailureMapper.Map. Not executed; needs a corrupted row.

**Suggested fix.** Count the offending row as invalid and carry its id in the exception or log, as GetStatusAsync already does for a missing file.

#### RV-230 (Minor, usability, log-level, effort S): Every turn after the alert threshold logs a Warning for an expected duplicate budget alert; HasAlertedTodayAsync has no caller

`src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetAlertRepository.cs:58` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/BudgetMonitor.cs`, `docs/Arcanum.DESIGN.md`

**Claim.** BudgetMonitor.CheckAsync calls TryDispatchAlertAsync on every check at or above a threshold, which calls RecordAlertAsync unconditionally (BudgetMonitor.cs:133). After the first insert each later call runs a write transaction that fails on the unique index and logs Warning 'Budget alert for threshold {Threshold} already recorded today; skipping duplicate.' (pinned by BudgetAlertRepositoryTests.cs:118-135). The monitor's own comment and Debug line describe this as a rare concurrent-turn race. DESIGN 5922 says HasAlertedTodayAsync 'is retained as a cheap pre-check'; rg finds no production caller.

**Failure scenario.** From the first turn that crosses 80% until UTC midnight each turn adds one Warning and one failed write; at 100% each refused request adds one. Operators scanning warnings see 'skipping duplicate' noise on a healthy system.

**Evidence (probe).** Probe PROBE_every_check_above_threshold_attempts_the_alert_insert: five CheckAsync calls at 80% of the limit with a recording repository that behaves like the unique index gave 'PROBE-ALERT checks=5 RecordAlertAsync calls=5 (accepted=1) HasAlertedTodayAsync calls=0 dispatched=1'. Four refused calls are four Warnings with the real repository.

**Suggested fix.** Pre-check HasAlertedTodayAsync in TryDispatchAlertAsync and keep the constraint as the race guard, or log the duplicate at Debug. Correct DESIGN 5922.

#### RV-231 (Minor, usability, error-message, effort S): Budget refusals read as arithmetic errors and omit the request, the UTC day and the governing setting

`src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs:145` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/BudgetMonitor.cs`, `tests/RetroDownfall.Arcanum.Tests/Data/BudgetReservationServiceTests.cs`

**Claim.** ReserveAsync (145) and AdjustAsync (283) report committed+reserved before the request, so the message can say the limit would be exceeded while the shown figure is below it; DailyLimitRecheckFailure (396) reports a total that includes the delegated amount under the same wording. Two-decimal formatting makes limit and figure equal when they are not. None of the messages (nor BudgetMonitor.cs:72) says the day is a UTC day, when it resets, or that Arcanum:Cost:Budget:DailyLimitUsd is the setting to change. DESIGN 1707 and CHAT-LOOP 33 say the error reports the usage and how to change or resume the policy.

**Failure scenario.** An operator in UTC-5 is refused at 16:00 local with 'limit $5.00 would be exceeded (committed+reserved: $4.99)'; it looks like a bug and nothing says the budget resets at 19:00 local.

**Evidence (probe).** Probe PROBE_budget_message_shapes: limit 5 / committed 4.99 / request 0.12 gives 'Daily budget limit of $5.00 USD would be exceeded (committed+reserved: $4.99 USD).'; limit 4.9899 / request 0.0001 gives 'Daily budget limit of $4.99 USD would be exceeded (committed+reserved: $4.99 USD).' The existing test BudgetReservationServiceTests.cs:278 pins the equal-numbers sentence.

**Suggested fix.** One shared formatter: 'Daily budget of $X (UTC day YYYY-MM-DD) would be exceeded: $A committed + $B reserved + $C requested. Resets at 00:00 UTC; raise Arcanum:Cost:Budget:DailyLimitUsd or wait.' Show 4 decimals when a figure is under one cent.

#### RV-232 (Minor, usability, diagnostics, effort S): Coordinator error lines omit operation id and phase, and the pre-erasure rollback line drops the reason

`src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs:2919` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`

**Claim.** The eight LogError calls in CloseAsync (3605, 3615, 3658, 3668, 3704, 3721, 3739, 3769), which are the lines that say 'admission stays closed', and the warnings at 1296 and 2919 do not log operation.Id or checkpoint.Phase although both are in scope; they log only an error code, which is nearly always Covenant.MaintenanceFailed because the failing helper returned MaintenanceFailure(). The RollbackAndReopen branch at 2919 logs only the code, while the KeepClosed branch six lines below logs phase, operation id, code and message. CovenantErasureCompletion has no message field, so the sentence that explains a pre-erasure refusal reaches neither the log nor the caller.

**Failure scenario.** Data.Conflict from CovenantErasureInventorySource.cs:141 ('An active inference run still protects its inputs and accounting chain after maintenance drain.') or a ManualArtifactErasureRequired preflight refuses the reset; admission reopens and the log records only the bare code. With several operations in the ledger the operator cannot link the line to arcanum operation show <id>.

**Evidence (probe).** Probe PROBE_preflight_refusal_reason_is_dropped_from_the_log_and_the_completion (preflight stub returns Covenant.IntegrityFailure / 'The erasure inventory could not be enumerated.'): 'PROBE3 success=True disposition=RollbackAndReopen blocking=Covenant.IntegrityFailure', 'PROBE3 log line count=1', 'Warning: A Covenant erasure aborted before any artifact was touched with Covenant.IntegrityFailure; admission reopens.' The message appears nowhere. DataRetentionService.cs:2020-2026 then maps the code to a fixed sentence.

**Suggested fix.** Add {OperationId}, {ResetPhase} and, for the rollback line, {ErrorMessage} to those templates. The messages are fixed strings, so this stays inside the content-free policy.

#### RV-233 (Minor, usability, cli-ergonomics, effort S): data retention set: refusals name neither the value nor the valid classes, one rule is shared by several class names, and the accounting floor silently replaces the request

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionPolicyStore.cs:177` — related: `src/RetroDownfall.Arcanum.Cli/Commands/DataRetentionCommands.cs`, `docs/Arcanum.Command.Reference.md`, `docs/Compendium.README.md`

**Claim.** 'The retention data class is not recognized.' (177), 'The retention data class has no configurable rule.' (204, 312) and the CLI's '<data-class> must name a supported retention data class.' do not echo the input or list valid classes (the sibling --scope message does). Command Reference 1225 says 'Set one named typed rule', but batch-input-files/output/error and uploaded-files all write the single UploadedFiles rule (241-253), attachment-* write Attachments, and inference-runs|billable-operations|budget-reservations|cost-adjustments write Accounting (292-297); the prompt asks only 'Update the batch-input-files retention rule?'. Normalize (334-340) raises Accounting days to accountingMinimumDays before saving, so the stored value is the floor, while Compendium 307 calls it the 'requested age threshold'.

**Failure scenario.** An operator runs retention set batch-input-files 2; the next sweep applies a 2-day rule to every uploaded file and batch output. Or runs retention set accounting 30 and the file says 365 with no explanation.

**Evidence (probe).** Probe ProbeU25RetentionPolicy against the real DataRetentionPolicyStore and ConfigurationWriter in an isolated home: 'unknown class => Data.InvalidRequest [The retention data class is not recognized.]'; 'tapestry => [The retention data class has no configurable rule.]'; 'batch-input-files 2 => UploadedFiles=True/2 CompletedBatches=True/30'; 'accounting 30 => Accounting=True/365 AccountingMinimumDays=365'; persisted arcanum.json fragment '"accounting": { "enabled": true, "days": 365 }'.

**Suggested fix.** List valid class names in both messages; have the prompt and response name the rule that changes and the classes it covers; say when a floor applied (or refuse). Fix Command Reference 1225 wording.

#### RV-234 (Minor, usability, diagnostics, effort S): data status and the factory path drop the cause when the Covenant inventory cannot be measured

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.CovenantInventory.cs:352` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryActivation.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureExposureReader.cs`

**Claim.** TryAcquireCovenantInstallationReadAsync returns null on any gate failure (87); InventoryCovenantAsync returns null when the lease no longer revalidates (209) or the disclosure read fails (246); ReadNonrevocableDisclosureExposureAsync discards exposure.Error (352-356), the specific 'The nonrevocable Covenant disclosure exposure could not be folded safely.' DataRetentionService.cs:496-510 then omits the Covenant row. A closed gate after a stopped erasure, when the row is most needed, looks identical to a Covenant-off install. On the apply path the same null becomes the generic IntegrityFailure/MaintenanceFailed sentence of FactoryActivation.cs:102-107.

**Failure scenario.** After a stopped erasure the operator runs arcanum data status to check the Covenant: no row and no hint. A damaged external_disclosure_state table gives the generic message on apply and nothing in the log.

**Evidence (trace).** Trace through the five sites above and the reader's MalformedExposure error (CovenantDisclosureExposureReader.cs:31-34, 195-199). Not executed.

**Suggested fix.** Return a typed 'unmeasured: <code>' and print it in data status; log the discarded Error.Code at the discard site.

#### RV-235 (Minor, usability, diagnostics, effort S): Factory-erasure error logs carry no operation id

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryActivation.cs:336`

**Claim.** The warnings at 336-338 and 464-466 ('Healthy-catalog factory erasure lost its exact durable owner; recovery must reconcile it.') and the error at 537-539 ('...failed unexpectedly after durable operation admission.') have no {OperationId} although operation is non-null at each. Elsewhere the same service logs the id (DataRetentionService.cs:7466, FactoryActivation.cs:690,702). For an unnamed apply the caller never learns the id, so the log is the only place to find it.

**Failure scenario.** A factory erasure loses its owner; the operator sees a Warning with no id and cannot run arcanum operation show for it among several rows.

**Evidence (trace).** Read of the three sites; the surrounding try block uses operation.Id at 253-256.

**Suggested fix.** Add operation.Id to the three templates.

### U26: Arcanum.Infrastructure — Data (2)

#### RV-236 (Minor, performance, unbounded-growth, effort S): Prune plan and blocker lists are unbounded: one blocker and one diagnostics query per protected entry or aged run

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:299` — related: `docs/Arcanum.API.md`

**Claim.** BuildUnifiedPrunePlanCoreAsync sets limit = int.MaxValue and passes it as diagnosticLimit to the protection-diagnostics methods, which add a DataRetentionBlocker per protected entry/attachment/embedding and run a 3-way UNION conflict query per entry (AddBoundedSessionConflictDiagnosticsAsync, not cached per session); the accounting 'blocked' query adds one blocker per aged run in a retained session.

**Failure scenario.** An operator holds one session (ProtectedSessionIds) with 100k aged entries, or simply keeps sessions (the default) while accounting retention is on: the dry-run response carries one blocker per entry/run (~17 MB for 100k), issues 100k extra queries, and builds, hashes (ComputePlanId) and serialises the list on every preview.

**Evidence (trace).** Protected_session_plan_scaling (results-6-batch3.txt): 4,000 aged entries in one held session -> 4,000 blockers, ~671 KB plan content, 4,009 statements, 215 ms CPU; accounting_blockers_retained_sessions: 4,000 aged runs in one retained session -> 4,000 blockers, ~816 KB.

**Suggested fix.** Cap detailed blockers per class (for example 100 plus a count) or emit one blocker per held session/session-with-runs, and ask the conflict question once per session.

#### RV-237 (Minor, performance, n-plus-one, effort M): Statement fan-out per retention candidate: repeated sqlite_master probes, duplicated checks and identical per-session reads

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:1553` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaVectorMirror.cs`

**Claim.** Each candidate re-runs TableExistsAsync (inside every CountTableAsync, ReadSessionConflictsAsync and SagaVectorMirror.ClassifyAsync) for tables whose existence cannot change during a plan, repeats the context-pin count (1553) and ReadSessionConflictsAsync (1577) that the candidate SQL already excluded, and re-reads identical per-session conflict data for every entry of the same session; Microsoft.Data.Sqlite re-prepares every freshly built statement.

**Failure scenario.** Planning and applying thousands of candidates issues hundreds of thousands of statements (2,000 session candidates = 246,020 statements, 4.2 s CPU), adding seconds of pure overhead on top of the scan costs and extending the time the retention lease and single-flight slot are held.

**Evidence (probe).** Profile hook counts (results-1/2/5): entry-embedding plan 11 statements per candidate (88,010 for 8,000; 5 are sqlite_master probes), entry plan 15 (22,659 for 1,500), entry apply 83 (12,399 for 150), session plan 123 (246,020 for 2,000 sessions, process-cpu 409/839/1,915/4,196 ms at 250/500/1,000/2,000). ~12 us per statement.

**Suggested fix.** Memoise table existence for the lifetime of a plan/apply pass, cache per-session conflict reads in a dictionary, drop the duplicated checks that the candidate SQL already enforces, and batch per-candidate derived counts with GROUP BY or json_each parameters.

#### RV-238 (Minor, performance, blocking-io, effort S): Workspace reset scans tapestry_nodes about 18 times (no scope index) and recomputes the full fingerprint for the post-commit reconcile

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.WorkspaceReset.cs:246` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/tapestry_nodes.sql`

**Claim.** tapestry_nodes has no index on (ScopeKind, ScopeId), so each of the five scoped counts/fingerprints per snapshot and the two scoped DELETEs scan the table; ReadWorkspaceResetSnapshotAsync runs three times (plan, in the writer transaction, post-commit) and the post-commit call needs only TotalOwnedRows yet streams every chunk id, embedding id, node id and node-embedding id through SHA-256.

**Failure scenario.** Resetting a workspace whose Tapestry holds 200k nodes (summary text per node) scans ~25 MB-GB of node pages roughly 18 times (about 10-15 s at 58 ms per 20k-node scan), part of it while the writer transaction is held.

**Evidence (trace).** EXPLAIN (explain.txt): 'SELECT COUNT(*) FROM tapestry_nodes WHERE ScopeKind=.. AND ScopeId=..' -> SCAN tapestry_nodes; node-embedding join -> SCAN node; 'DELETE FROM tapestry_node_embeddings WHERE NodeId IN (SELECT NodeId FROM tapestry_nodes WHERE ScopeKind..' -> SCAN tapestry_nodes. Tapestry_scope_scan (results-5-batch2.txt): one scoped count costs 14 ms at 5,000 nodes and 58 ms at 20,000 nodes (1.5 KB content per node). Table indexes in Schema/Tables/tapestry_nodes.sql: (GenerationId,Layer), ParentScopeKey, ParentNodeId, (GenerationId,ChildMembershipHash).

**Suggested fix.** Add CREATE INDEX idx_tapestry_nodes_scope ON tapestry_nodes(ScopeKind, ScopeId); make the post-commit reconcile a counts-only read that skips AppendWorkspaceResetFingerprintAsync.

#### RV-239 (Minor, reliability, crash-consistency, effort S): Factory reset does not sweep prune quarantine directories (.arcanum-retention-*) under the logs root, yet reconciles as clean

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs:1271`

**Claim.** `CaptureFactoryLogFiles` (1252-1282) and `EnumerateFactoryLogFiles` (1351-1380) inventory only top-level dated files and `.arcanum-cleanup-*` directories. The prune's `DeleteLogCandidate` quarantines into `OperationQuarantineDirectoryPrefix(operationId)` = `.arcanum-retention-{op:N}-`. Such a directory survives whenever its pending journal is never processed. This happens when the request path terminalizes a candidate failure as Failed (the `ApplyStartedOrdinaryAsync` generic catch, for example after `TryRestoreQuarantined` fails in `DeleteLogCandidate`), or when the row is stranded corrupt. The audit or guardrail log inside is invisible to the audit readers and survives a factory reset, whose reconciliation still reports success. The attachments and files roots are not affected, because their tree walk captures every entry.

**Failure scenario.** A request-driven prune fails after quarantining a dated audit log (restore failure), leaving the row Failed and the log in <logs>/.arcanum-retention-<op>-x/. A later factory reset reports Completed, but the audit log remains on disk.

**Evidence (probe).** Probe U26Probe_FactoryResetLeavesAPruneQuarantinedLogUnderTheLogsRoot: created <logs>/.arcanum-retention-<guid>-probe/audit-20000101.jsonl plus a top-level audit-20000102.jsonl, then ran the V0 factory reset. Output: `factory plan files=1`; `factory completed=1 attention=0 datedExists=False strandedExists=True`. Code: FactoryReset.cs:1271 and :1368 enumerate only ".arcanum-cleanup-*"; DataRetentionService.cs:7173 defines the retention prefix; DataRetentionService.cs:1740-1766 marks unexpected request-path failures Failed, which the reconciler never adopts.

**Suggested fix.** Have factory log capture and count also walk `.arcanum-retention-*` directories, ideally from one shared list of quarantine prefixes. Leave request-path candidate failures that carry a pending journal as ReconciliationRequired with the retention code, as the hosted path does, so the journal is recovered.

#### RV-240 (Minor, reliability, race, effort S): In production the frozen prune authority is never found, so apply recomputes cutoffs and fails the whole prune on one Session status change

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:3349`

**Claim.** `BuildUnifiedPrunePlanCoreAsync` registers `FrozenPruneAuthority` in a ConditionalWeakTable keyed by the plan instance; its own comment warns that a later `with` loses it. With a Covenant gate, which production always composes, `PlanAdmissionAsync` returns `BindCovenantErasurePlanIdentity` (DataRetentionService.CovenantInventory.cs:279, `plan with { Covenant = inventory }`). The instance that `ApplyOrdinaryAsync` and `StartHostedPruneAsync` pass in is therefore never registered. Apply falls back to `BuildCandidateCutoffsAsync(..., SnapshotRetention(CurrentRetention))`, which re-reads each `session:` candidate's status. It throws InvalidOperationException ("has no enabled age boundary") if a Session was deleted, or archived while ArchivedSessions is disabled, between planning and apply. Tests miss this because DataRetentionServiceTests.CreateService never passes a covenantGate.

**Failure scenario.** A prune with ActiveSessions enabled is applied while the user archives one of the candidate Sessions (ArchivedSessions disabled). The whole request fails with "requires operator review" before any candidate runs; a hosted sweep fails that pass.

**Evidence (probe).** Probe U26Probe_GatedPrunePlanIsNotTheInstanceItsFrozenAuthorityWasRegisteredFor (added to a scratch copy of CovenantRetentionTests, using reflection over `_frozenPruneAuthorities.TryGetValue`): `gatedHasCovenant=True gatedRegistered=False ungatedRegistered=True`. Trace: DataRetentionService.cs:1368 ApplyOrdinaryAsync calls PlanAsync, which returns the wrapped admission plan (560-575, 607-627); Pruning.cs:3346-3357 falls back to BuildCandidateCutoffsAsync; 3024-3027 throws on a null boundary; 3169-3187 shows that session status other than active/archived yields null.

**Suggested fix.** Key the frozen authority by PlanId rather than by instance, or re-register it after BindCovenantErasurePlanIdentity. In the fallback, treat a candidate that lost its boundary as preserved rather than throwing. Add a gated-composition test.

#### RV-241 (Minor, reliability, platform, effort S): The prune log glob matches non-dated names that apply can never delete, and any checkpoint holding one is unrecoverable

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:6380`

**Claim.** `AddLogCandidates` calls `Directory.EnumerateFiles(_logsRoot, "audit-????????.jsonl", SearchOption.TopDirectoryOnly)`. The SearchOption overload uses MatchType.Win32 on every OS, where `?` is DOS_QM (zero or one character). Factory reset uses `new EnumerationOptions()` (MatchType.Simple, exactly one character). Files such as audit-old.jsonl, audit-1.jsonl or audit-2026.jsonl become plan candidates and are counted in files and bytes. Apply always refuses them (`IsDatedLogFile`), so they are preserved with Data.PlanChanged on every prune and pin `earliestSkippedIndex`. `IsValidCheckpointCandidate` rejects them on parse, so an interrupted prune whose plan contains one strands with checkpoint_corrupt, just like the 10,000-candidate finding.

**Failure scenario.** An operator or tool leaves audit-old.jsonl (or audit-bak.jsonl) in the Grimoire directory. Every prune advertises deleting it, never does, and reports a PlanChanged conflict. If such a prune is interrupted, recovery marks it checkpoint_corrupt and all retention operations are refused until manual repair.

**Evidence (probe).** A .NET 10 file-based probe on macOS (scratchpad/u26probe/glob.cs) found that SearchOption.TopDirectoryOnly matched audit-.jsonl, audit-1.jsonl, audit-2026.jsonl, audit-20261001.jsonl, audit-old.jsonl and audit-snapshot.jsonl. EnumerationOptions matched only audit-20261001.jsonl and audit-snapshot.jsonl. Probe U26Probe_NonDatedAuditFileMatchesThePrunePatternAndPoisonsTheCheckpoint: `candidates=[audit-log:audit-old.jsonl] files=1`; `apply success=True files=0 conflicts=[Data.PlanChanged:audit-log:audit-old.jsonl] strayExists=True`; replaying its checkpoint gave `state=ReconciliationRequired code=operation.checkpoint_corrupt`.

**Suggested fix.** Enumerate with `new EnumerationOptions { MatchType = MatchType.Simple, ... }` and filter each name through IsDatedLogFile (or IsValidCheckpointCandidate) at plan time, as daemon candidates already are at line 2987.

#### RV-242 (Minor, usability, error-message, effort S): Expired-lease idempotency claims are blocked forever with the code Data.IdempotencyLeaseActive and a 'reconciliation' no route performs

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:2404` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs`, `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs`

**Claim.** For `IdempotencyClaims` in state Claimed or Running that are past the retention cutoff, the planner reports a blocker and a conflict with code `Data.IdempotencyLeaseActive`, even when the lease expired long ago. The message is 'An unfinished idempotency claim requires reconciliation before pruning.' Nothing reconciles such a row: IdempotencyClaimStore reclaims it only when a client retries the same key, and DeleteExpiredAsync deletes only Completed, Failed and Abandoned claims. So the claim is never pruned and is re-listed on every dry-run.

**Failure scenario.** A host is killed mid-request. The row stays Running with an expired lease and the client never retries that key. After the 7-day IdempotencyClaims window, every `data prune --dry-run` lists 'Blocked IdempotencyClaims <id>: Data.IdempotencyLeaseActive - An unfinished idempotency claim requires reconciliation before pruning.' and 'Conflict <id>: Data.IdempotencyLeaseActive'. The code says a lease is active, and there is no command to reconcile. Factory reset (FactoryReset.cs:937) treats expired claims as not conflicting, and InstallationResetService.RemedyFor says to wait for the lease to expire, so the surfaces disagree.

**Evidence (probe).** Pruning.cs:2370-2413 and :2454-2473 (both the blocked scan and the Claimed/Running rows of the terminal scan). The message branch on leaseExpiresAt at :2404-2406 and :2463-2465 only changes the text, not the code or the outcome. IdempotencyClaimStore.cs:96-100 and :310-345 (reclaim only on the same-key acquire, `LeaseExpiresAt < now`); :371-394 (DeleteExpiredAsync excludes Claimed and Running). Trace only. No probe was run for this finding.

**Suggested fix.** Treat a Claimed or Running claim whose lease expired and whose UpdatedAt is older than the cutoff as prunable, which is equivalent to the reclaim path already in the store. Otherwise use a distinct code such as `Data.IdempotencyClaimStale` with a message that says what the operator can do.

#### RV-243 (Minor, usability, cli-ergonomics, effort M): Default configuration yields one 'Blocked' plan entry per historical inference run, and held or pinned entries one per entry, in an unbounded, uncapped plan

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:2716` — related: `src/RetroDownfall.Arcanum.Cli/Commands/DataRetentionCommands.cs`, `docs/Arcanum.DESIGN.md`

**Claim.** With the shipped defaults (Accounting enabled at 365 days, session and Entry rules disabled), AddAccountingCandidatesAsync adds a `Data.SessionAccountingAuthority` blocker for every inference run older than the cutoff whose session still exists. AddEntryProtectionDiagnosticsAsync and AddEntryEmbeddingProtectionDiagnosticsAsync add one blocker per pinned or held entry (`Data.PinnedEntry`, `Data.SessionHold`, `Data.ContextPin`). All of them run with limit == int.MaxValue, nothing is aggregated or capped, and the blocker's ResourceId is the entry or run id, not the session that holds it. The CLI prints every blocker on `--dry-run` and prints them again in the `--apply` result (Cli/Commands/DataRetentionCommands.cs:440-444 and :474-478). These blockers are informational: a prune applies regardless of them.

**Failure scenario.** After a year on defaults, `arcanum data prune --dry-run` prints one 'Blocked InferenceRuns <id>: Data.SessionAccountingAuthority - Accounting for a retained session remains available to explain its cost.' line per old run, and the real candidate totals scroll away. An operator who protects a long conversation via `retention.protectedSessionIds` gets one 'Data.SessionHold' line per old entry of that session and never sees the session id. `--json` returns a plan of hundreds of KB, growing with history.

**Evidence (probe).** Probe in a scratch worktree. Default ArcanumSettings (Accounting.Enabled=True Days=365; Active and Archived sessions disabled) with 2,000 completed runs older than a year in a retained session: blockers=2000, candidates=0, plan JSON 428,460 bytes, all with the single code Data.SessionAccountingAuthority. A protected session with 3,000 old entries and the Entries rule enabled: blockers=3000, plan JSON 513,460 bytes; with 500 entries, 85,960 bytes. A first blocker looked like `ResourceId = 003a8a28-..., ReasonCode = Data.SessionHold, Message = An operator-retained session protects its entries.`, so the session is not named. Doc side: DESIGN.md:53 says 'Plans report every discovered item', so the fix is presentation, not semantics. Machine-facing detail stays reachable via --json, but the human CLI has no cap.

**Suggested fix.** Keep per-item blockers for actionable cases. For informational retention relationships (SessionAccountingAuthority, SessionHold, PinnedEntry), emit a bounded sample (for example 20) plus a per-reason-code, per-session count in the plan (a new summary field), and name the holding session id as the ResourceId or in the message. Update API.md 8.20 and the Command Reference for the summary, and have the CLI print the counts and 'N more'.

#### RV-244 (Minor, usability, error-message, effort S): Reconciliation and recovery failures name no candidate, table or file, and the recovery arms log nothing

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:3519` — related: `src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationReconciler.cs`, `src/RetroDownfall.Arcanum.Core/Operations/LongRunningOperationContracts.cs`

**Claim.** (a) When post-delete reconciliation finds retained data for a prune candidate, ApplyUnifiedPruneCoreAsync throws `IOException("Post-delete reconciliation found retained owned data for the current candidate.")` without the candidate id, which is in scope. (b) ReconcileFactoryResetAsync (FactoryReset.cs:805-857) ANDs about 60 table, directory and log checks into one bool, so `Factory reset retained owned data after its ordinary cleanup.` and `Factory reset committed, but post-commit reconciliation found retained owned data.` (:497) cannot say which one failed. (c) RecoverPrunePendingJournalAsync (Pruning.cs:3946-4010) and its caller (:3850) return `RequiresAttention(Data.ReconciliationFailed)` from five different checks with no log line, and the reconciler logs nothing when a handler returns RequiresAttention, only a metric. (d) RecoverFactoryResetAsync swallows RetentionConflictException (:636) and a non-empty plan.Conflicts (:617) as a bare `Failed(Data.Conflict)`, and uses `Covenant.MaintenanceFailed` for a lease-less v0 row (:597), with no log.

**Failure scenario.** A prune of 40,000 candidates fails on one of them. The operator sees 'The retention operation failed; its durable history requires operator review.' and a log line with the operation and plan ids but no candidate, so they cannot find the row. After an interrupted file-bearing prune, `arcanum operation show` reports `Data.ReconciliationFailed` and neither the CLI nor the log can say whether the quarantine, the original file's metadata or the journal target mismatched.

**Evidence (probe).** Probe in a scratch worktree: a SanctumBreaches AFTER DELETE trigger that re-inserts the row, then ApplyAsync(prune). Result `Data.ReconciliationFailed: The retention operation failed; its durable history requires operator review.` The only log was `Error: Data-retention operation c599d942-... failed while applying plan E01E7AFF... | IOException: Post-delete reconciliation found retained owned data for the current candidate.` The candidate `sanctum:5a3d5eff-...` appears nowhere. Reconciler logging: Operations/LongRunningOperationReconciler.cs:649,677,708,718 are the only log calls; `default: RecordOutcome(leased.Kind, "attention")` at :632 logs nothing. LongRunningOperationRecoveryResult carries only a code (Core/Operations/LongRunningOperationContracts.cs:331). No test references these messages.

**Suggested fix.** Include the candidate in the (a) message. Make ReconcileFactoryResetAsync return the first failing target (table name, or root role) and use it in the thrown message and the log. Log at Warning, with the operation id, the specific check that returned RequiresAttention or Failed in RecoverPrunePendingJournalAsync, RecoverPruneCoreAsync and RecoverFactoryResetAsync. Use a Data.* code instead of Covenant.MaintenanceFailed at :597.

#### RV-245 (Minor, usability, error-message, effort M): Planned labelled artifacts are applied as 'Data.PlanChanged: changed or became protected after planning' although the plan did not change

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:3545` — related: `docs/Arcanum.API.md`, `tests/RetroDownfall.Arcanum.Tests/Data/CovenantLabeledRetentionRouteTests.cs`

**Claim.** The prune planners (AddEntryCandidatesAsync, AddSagaCandidatesAsync, AddLexiconCandidatesAsync and the Session path) never consult Covenant sensitivity labels. The dry-run counts a labelled Entry, Saga memory or Lexicon entry as a deletable candidate with no blocker. At apply, DeleteEntryCandidateAsync, DeleteSagaCandidateAsync and DeleteLexiconCandidateAsync return Empty for a labelled artifact, and ApplyUnifiedPruneCoreAsync reports it as a conflict with code `Data.PlanChanged` and the message 'The retention candidate changed or became protected after planning; it was preserved.' The conflict's ResourceId is the prefixed candidate string ('entry:<guid>'), while plan conflicts and blockers use a bare GUID.

**Failure scenario.** An operator with a labelled assistant Entry older than the Entries rule runs `data prune --dry-run`, which shows rows=1, blockers=0, conflicts=0. `--apply` with that exact planId returns success with rowsDeleted=0 and the conflict 'Data.PlanChanged'. API.md documents Data.PlanChanged as a 409 'request a new dry-run' signal, so automation loops or the operator re-previews repeatedly. Every later sweep repeats the same preserved candidate.

**Evidence (probe).** Probe in a scratch worktree (a copy of CovenantLabeledRetentionRouteTests.A_labeled_entry_survives_the_retention_prune_route that also records the plan). PLAN: items=[Entries rows:1 derivedRecords:1], blockers:[], conflicts:[], candidateIds:["entry:f25fb376-..."]. APPLY with planId 0DF0A23C...CAE5FC5 (the same id): `rowsDeleted:0, reconciled:true, conflicts:[{code:"Data.PlanChanged", resourceId:"entry:f25fb376-...", message:"The retention candidate changed or became protected after planning; it was preserved."}]`. The existing test asserts only that the row survives, not what the operator is told. Pruning.cs:3545-3553 (the preserved conflict), :5414 (Entry label guard), :5983 and :6150 (Saga and Lexicon guards). API.md:767 documents only the 'becomes protected after planning' case.

**Suggested fix.** Let the prune planner read the label ledger and report labelled candidates as blockers with a distinct code (for example `Data.CovenantLabeled`) so the dry-run total excludes them. In the apply path, give a candidate preserved because of a label a code of its own rather than Data.PlanChanged. Use the same ResourceId shape as plan conflicts. Document it in API.md 8.20.

### U27: Arcanum.Infrastructure — Data (3)

#### RV-246 (Minor, reliability, api-contract, effort S): DELETE of an in-flight (not Bound) attachment answers 404 Data.NotFound instead of its Data.AttachmentInFlight blocker

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:1396` — related: `src/RetroDownfall.Arcanum.Api/Data/DataRetentionEndpoints.cs`

**Claim.** BuildDeleteAttachmentPlanAsync (lines 2518-2533) returns an empty-items plan with the Data.AttachmentInFlight blocker for any attachment whose State is not 'Bound'. ApplyOrdinaryAsync checks 'DeleteSession/DeleteAttachment with Items.Length == 0 -> Data.NotFound' (1396-1404) before it checks blockers (1406-1413), so the blocker is never reported on apply and an existing attachment is reported as missing.

**Failure scenario.** A turn is in flight, or a crash left an attachment Pending until attachment GC reclaims it. DELETE /api/data/attachments/{id} (or arcanum data delete-attachment) returns 404 'The requested data source was not found.' while the row and the bytes are still there. A client or script that reads 404 on DELETE as 'already gone' believes the attachment was removed.

**Evidence (probe).** Probe ReviewProbe_in_flight_attachment_delete_reports_not_found (executed in a scratch worktree at ac0d2d8e): seeded a Bound attachment, set State='Pending', planned and applied. Output: 'REVIEWPROBE inflight: planBlockers=[Data.AttachmentInFlight] planItems=0 apply=Data.NotFound|The requested data source was not found. rowsLeft=1 fileExists=True'. No existing retention test applies a delete to a non-Bound attachment (grep for InFlight / 'Pending' in the DataRetention suites finds none), so this regression passes every existing test.

**Suggested fix.** Check blockers and conflicts before the not-found test, or only return NotFound when Items.Length == 0 && Blockers.Length == 0 && Conflicts.Length == 0. Add a regression test that deletes a Pending attachment and expects 409 Data.Blocked.

#### RV-247 (Minor, reliability, docs-mismatch, effort S): A Campaign-targeted reset of a store with no owner is refused as 409 Data.Blocked, not the documented Data.InvalidRequest, after the CLI has already asked for confirmation

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:1406` — related: `src/RetroDownfall.Arcanum.Cli/Commands/DataRetentionCommands.cs`, `docs/Arcanum.Command.Reference.md`, `src/RetroDownfall.Arcanum.Api/Data/DataRetentionEndpoints.cs`

**Claim.** BuildResetMemoryPlanAsync (2684-2695) records the refusal as a blocker whose ReasonCode is Data.InvalidRequest. ApplyOrdinaryAsync (1406-1413) turns every blocker into Data.Blocked, which maps to HTTP 409. The BuildPlanAsync default arm (718-724) is re-coded the same way. docs/Arcanum.Command.Reference.md:1230 says --campaign with any scope other than saga, lexicon or campaign-summary 'is refused with Data.InvalidRequest'. The CLI (DataRetentionCommands.ResetMemory 216-241) does not inspect the preview's blockers, so it prompts for confirmation of a reset that can never run.

**Failure scenario.** arcanum data memory reset --scope entry --campaign <id>: the preview succeeds, the operator is asked 'Reset the entry memory scope for campaign <id>?', confirms, and then gets Data.Blocked (409, conflict exit code) instead of the documented invalid-input refusal.

**Evidence (probe).** Probe ReviewProbe_untargetable_campaign_reset_is_refused_as_blocked (executed): 'REVIEWPROBE targeted: planBlockers=[Data.InvalidRequest] apply=Data.Blocked|Only Saga and Lexicon memories record an owning Campaign, so only those two can be reset for one Campaign. ...'. CLI flow traced in DataRetentionCommands.cs 216-241.

**Suggested fix.** When the first blocker's ReasonCode is Data.InvalidRequest, return that code (400). Better, reject the targeted request before planning (endpoint or PlanAdmissionAsync), and have the CLI refuse locally, or at least refuse when the preview carries blockers, before it prompts.

#### RV-248 (Minor, reliability, crash-consistency, effort M): A failed in-process restore after a rolled-back delete marks the operation Failed and strands the attachment bytes in quarantine

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:3260` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Security/IdentityOwnedFileSystemCleanup.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs`

**Claim.** Before the delete runs, PrepareMutationJournalAsync saves an ARCAMUT2 v2 journal naming every attachment file, and RecoverMutationAsync (7189-7328) can restore exactly those quarantines while the target still exists. When the transaction rolls back after quarantine (commit failure, or cancellation at commit) and the in-process restore is refused, RestoreQuarantinedFactoryFiles (FactoryReset.cs 1590-1601) throws an IOException. TryRestoreQuarantined refuses when the original path is occupied, on IO or access errors, or when the quarantine directory cannot be removed. That IOException replaces the original exception, and the catch-all (1740-1766) marks the row Failed. Recovery never selects Failed rows, so the journal is never used. The same applies to DeleteAttachmentAsync (3538-3553). A cancellation that triggered the rollback also loses its lease-surrender path.

**Failure scenario.** The session and attachment rows survive the rollback, but the attachment file now sits under '.arcanum-retention-<op>-<hex>/'. Every later read of that attachment fails, and nothing durable moves the file back.

**Evidence (probe).** Probe ReviewProbe_failed_restore_after_rollback_strands_quarantined_attachment (executed): a DEFERRABLE INITIALLY DEFERRED foreign key to Sessions made COMMIT fail after the quarantine, and the FileHandleIdentityInterop no-follow metadata seam reported the original path occupied once the file had left it. Output: 'REVIEWPROBE restore: apply=Data.ReconciliationFailed|The retention operation failed; its durable history requires operator review. op=5:Data.ReconciliationFailed:v2 originalExists=False quarantined=[<id>.bin] sessionRows=1 attachmentRows=1'.

**Suggested fix.** In ApplyStartedOrdinaryAsync, once a v2 journal has been saved, settle any unexpected failure as ReconciliationRequired with RetentionRecoveryTerminalCode, the pattern SettleUnexpectedHostedPruneFailureAsync already uses, so RecoverMutationAsync restores or finalizes from the journal. In the rollback catch, keep the original exception and attach the restore failure to it instead of replacing it.

#### RV-249 (Minor, reliability, blocking-io, effort M): Retention mutations block a thread ~31 s on a held write lock, ignore cancellation, then report a no-op as 'operator review' and terminalize the operation

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:4808` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteBusyRetry.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/CampaignRepository.cs`

**Claim.** BeginMutationTransactionAsync calls the synchronous SqliteConnection.BeginTransaction(deferred: false). BEGIN IMMEDIATE runs inside Microsoft.Data.Sqlite's own busy loop bounded by DefaultTimeout (30 s, as SqliteBusyRetry.cs 23-35 documents) on the calling thread, without the caller's token, and nothing in this service retries SQLITE_BUSY. The resulting SqliteException falls into ApplyStartedOrdinaryAsync's catch-all (1740-1766), which marks the operation Failed with Data.ReconciliationFailed and returns HTTP 500 'The retention operation failed; its durable history requires operator review.' Nothing was mutated. DeleteSessionAsync (3079), DeleteAttachmentAsync (3449), ApplyMemoryResetAsync (3632) and the WorkspaceReset/CampaignSummaries/Pruning partials all use the same helper.

**Failure scenario.** While some other writer holds the Grimoire write lock (a long transaction elsewhere, an external sqlite3 or backup, a leaked transaction), an operator deletes a session or resets a memory store. The request hangs about 31 s even after the client cancels, ties up a thread-pool thread, then tells the operator to review durable history for an operation that changed nothing. The failure bookkeeping then waits on the same lock, so the call can take over a minute.

**Evidence (probe).** Probe ReviewProbe_delete_session_busy_begin_blocks_and_reports_operator_review (executed): took BEGIN IMMEDIATE on a second connection from the session-gate hook, cancelled the caller's token at 2 s and released the lock at 40 s. Output: 'errorLoggedAfterLock=00:00:31.39 exception=Microsoft.Data.Sqlite.SqliteException:SQLite Error 5: 'database is locked'. elapsed=00:01:04.84 result=Data.ReconciliationFailed|The retention operation failed; its durable history requires operator review. ops=[5:Data.ReconciliationFailed] sessionsLeft=1' (state 5 = Failed).

**Suggested fix.** Open mutation transactions through SqliteBusyRetry (or a SqliteBusyRetry.Begin cursor), with a lowered per-attempt DefaultTimeout/busy_timeout as CampaignRepository.AddAsync does, and check the token between attempts. Map a final busy failure to a retryable Data.Conflict rather than ReconciliationFailed/'operator review'.

#### RV-250 (Minor, reliability, race, effort S): GET /api/data/status throws FileNotFoundException when a managed file disappears during tree measurement

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:7954` — related: `src/RetroDownfall.Arcanum.Api/Data/DataRetentionEndpoints.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs`

**Claim.** MeasureOwnedTree (7954-7971) and CountFiles (7973-7989) first enumerate paths into FileInfo objects and only afterwards read file.Length. A file removed in between throws FileNotFoundException, and neither GetStatusAsync (callers at 197, 246, 5818-5824) nor the status endpoint catches it. The attachments tree routinely holds transient files: promotion staging '<dest>.tmp', EncryptedBlobStore '.<name>.tmp.<guid>', and the attachment-GC probe '.sweep-clock.tmp.<guid>' (SessionAttachmentStore.cs 2149-2154, 2216, 2333).

**Failure scenario.** Running arcanum data status (or any GET /api/data/status client) while an attachment is being stored or attachment GC runs returns HTTP 500 instead of the status report.

**Evidence (probe).** Probe ReviewProbe_status_throws_when_a_managed_file_vanishes_during_measurement (executed): 2,000 files plus a loop writing and deleting '.staged-N.tmp' in the attachments root. Output: 'REVIEWPROBE status: System.IO.FileNotFoundException after 5 calls in 00:00:00.079: Could not find file '.../attachments/<dir>/.staged-6.tmp'.'

**Suggested fix.** Take the length from the enumeration itself (DirectoryInfo.EnumerateFiles yields FileInfo with cached attributes, or a FileSystemEnumerable that projects entry.Length), and skip any entry that has disappeared (FileNotFoundException/DirectoryNotFoundException).

#### RV-251 (Minor, reliability, race, effort M): A promoted initiating request that survives a proven stage-one abort is missing from the next closure's census

`src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs:738` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/CovenantErasureCoordinator.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantConnectionDrain.cs`, `docs/Arcanum.DESIGN.md`

**Claim.** BeginOrResumeExclusive promotes the initiating request by removing it from _requestLeases (419-428). AbortClosingAsync (738-821) reopens admission but never returns a still-live promoted request to the census, and ReleaseRequest (1088-1099) skips removal for promoted leases. After a RollbackAndReopen abort (CovenantErasureCoordinator.cs 3748-3779, e.g. after Grimoire.WorkDrainTimeout) the initiating request keeps running (FailCovenantResetAsync writes its operation row, then the response is written), but a later owner's stage-one drain does not wait for it. DESIGN line 2799 says a surviving old lease remains in the next closure's census; this one does not.

**Failure scenario.** A Covenant reset times out in stage one and proves an abort; while the initiating request is still unwinding, a background maintenance owner begins closing. Its DrainRequestAndWorkAsync succeeds immediately and CloseConnectionAdmissionAsync issues a closed lease; CovenantConnectionDrain.DrainAsync then closes every enrolled handle, including the connection the initiating request is still using for its terminal ledger write, which fails and leaves the row for later reconciliation. The window is only the request's tail, so this is rare.

**Evidence (probe).** Probe ReviewProbe_promoted_request_surviving_abort_is_missing_from_next_census (executed): promoted request R under owner 65, timed out stage one on a blocking work lease, proved the abort, then Begin(owner 66) without disposing R: DrainRequestAndWorkAsync returned Success and CloseConnectionAdmissionAsync returned a closed lease (test passed). Control ReviewProbe_control_unpromoted_survivor_is_waited_for passed: the same survivor, unpromoted, keeps stage one waiting. The existing test Promoted_request_cannot_replay_after_abort_or_remove_a_new_request_from_the_next_census disposes the promoted lease before it asserts the drain is blocked, so it passes with the gap present.

**Suggested fix.** In AbortClosingAsync, if _closure.InitiatingRequest is still unreleased, un-promote it (IsPromoted=false, Lifetime.PromotedConnection=null), re-arm its terminal signal and add it back to _requestLeases (or keep a 'promoted survivors' set that stage one also awaits). Turn the probe into a regression test.

#### RV-252 (Minor, usability, diagnostics, effort S): Retention failure results and the hosted-sweep warning carry no operation id

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:1765` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionSweepHostedService.cs`, `src/RetroDownfall.Arcanum.Core/DataLifecycle/DataRetentionContracts.cs`

**Claim.** ApplyStartedOrdinaryAsync logs OperationId/PlanId (1744-1746) but returns Data.ReconciliationFailed (HTTP 500) with 'The retention operation failed; its durable history requires operator review.' The row was already marked terminal Failed, so there is nothing to reconcile and no operation to review is named; OperationId exists only on DataRetentionApplyResult success. In the hosted path the settle helpers return HostedPruneError(...) on the same code for 'could not claim', 'could not verify its durable owner' and 'cannot resume its exact durable lease' (945-994), and the scheduler logs only 'Automatic retention sweep did not complete: {ErrorCode} {ErrorMessage}' (DataRetentionSweepHostedService.cs:154-158) while its success line carries the id. data status and doctor expose no last-automatic-sweep outcome.

**Failure scenario.** An operator who enabled retention.automaticSweepsEnabled sees 'Data.ReconciliationFailed Automatic retention could not verify its durable owner.' in the log and cannot tell which row to inspect with 'arcanum operation show'; an HTTP caller gets a 500 telling them to review history without an id.

**Evidence (trace).** DataRetentionService.cs:1740-1766; 945-994 and 1354-1358 (HostedPruneError uses ErrorCodes.Data.ReconciliationFailed for all); DataRetentionSweepHostedService.cs:154-158 versus 161-165; DataRetentionStatus record (Core/DataLifecycle/DataRetentionContracts.cs:256) has no sweep field.

**Suggested fix.** Put the operation id in the error message and in the sweep warning, and use a distinct code for 'could not claim/verify' failures that leave nothing to reconcile.

#### RV-253 (Minor, usability, docs-mismatch, effort S): Plan blocker and conflict reason codes are scattered string literals absent from ErrorCodes and from every document

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:2463` — related: `src/RetroDownfall.Arcanum.Core/Primitives/ErrorCodes.cs`, `docs/Arcanum.API.md`

**Claim.** Wire-visible reasonCode/code values Data.PinnedEntry (2463), Data.SessionHold (2475, 2545), Data.AttachmentInFlight (2527), Data.ContextPin (4858), Data.PinnedAttachment (4901), Data.DaemonExecutionActive (5158), plus Data.BatchInProgress, Data.IdempotencyLeaseActive, Data.BatchReference and Data.SessionAccountingAuthority in the Pruning/FactoryReset partials are literals in five files, not in ErrorCodes.cs, and appear zero times in API.md, the Command Reference and DESIGN. API.md:765 says blockers carry a reasonCode but never lists the values.

**Failure scenario.** A client or the Forge branches on a blocker's reasonCode, or an operator searches the docs for 'Data.ContextPin'; there is nothing to find, and a rename of any literal silently breaks consumers.

**Evidence (trace).** grep of docs/Arcanum.API.md, Command.Reference.md, DESIGN.md for each code: 0 hits for PinnedEntry, SessionHold, ContextPin, AttachmentInFlight, PinnedAttachment; rg over src shows the literals only in DataRetentionService*.cs, InstallationResetService.cs and two store files.

**Suggested fix.** Add constants to ErrorCodes.Data, use them at all sites, and add a reason-code table (code, meaning, operator action) to API.md.

#### RV-254 (Minor, usability, docs-mismatch, effort S): reset-memory --campaign refusal names the wrong stores and is Data.Blocked after the confirmation prompt, not Data.InvalidRequest as documented

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:2692` — related: `src/RetroDownfall.Arcanum.Cli/Commands/DataRetentionCommands.cs`, `docs/Arcanum.Command.Reference.md`

**Claim.** The message says 'Only Saga and Lexicon memories record an owning Campaign, so only those two can be reset for one Campaign', but CampaignTargetedResetIsSupported (2740) also admits CampaignSummary, and the Command Reference (1230) and CLI help (DataRetentionCommands.cs:185-188) list three. The Command Reference says the flag is 'refused with Data.InvalidRequest'; the code returns an HTTP 200 plan with a blocker (code InvalidRequest) and ApplyAsync converts it to Data.Blocked (409, line 1412). The CLI never prints preview.Blockers (DataRetentionCommands.cs:218-244) so the refusal arrives after the confirmation prompt.

**Failure scenario.** 'arcanum data reset-memory --scope entry --campaign <guid>' asks 'Reset the entry memory scope for campaign ...?', the operator confirms (or passes --yes), and gets Data.Blocked: 'only those two can be reset', which wrongly implies campaign-summary cannot be targeted either.

**Evidence (probe).** PROBE (executed, U27Probe_ResetMemory_WithCampaign_PlanAndApply): scope=Entry and Workspace: 'blockers=1 first=Data.InvalidRequest | Only Saga and Lexicon memories record ...' and 'apply success=False code=Data.Blocked message=Only Saga and Lexicon ...'; scope=CampaignSummary: 'blockers=0' and 'apply success=True'. Command.Reference.md:1230 'is refused with `Data.InvalidRequest`'.

**Suggested fix.** Build the message from the supported set (Saga, Lexicon, CampaignSummary), return Data.InvalidRequest (400) for this case in ApplyOrdinaryAsync, and make the CLI stop on a non-empty plan.Blockers before prompting.

#### RV-255 (Minor, usability, error-message, effort S): Delete-session/attachment errors say 'request a new dry-run' but those commands have no dry-run; 'Data was pruned' is used for every operation

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:3107` — related: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Data.cs`, `src/RetroDownfall.Arcanum.Api/Data/DataRetentionEndpoints.cs`

**Claim.** DeleteSessionAsync, DeleteAttachmentAsync and RevalidateAttachmentDeletionBoundaryAsync throw 'Session/Attachment data changed after preview; request a new dry-run before retrying.' (3107, 3138, 3477, 3498, 4536) and ApplyOrdinaryAsync says 'request a new dry-run before applying' for any operation (1381). 'arcanum data delete-session' and 'delete-attachment' have no --dry-run and the API has only DELETE /data/sessions/{id} and /data/attachments/{id} with ExpectedPlanId always null, so the advice cannot be followed. Separately, the finalization failure reads 'Data was pruned, but the durable operation could not be finalized; retry is safe.' (1193, 1617) for delete-session, delete-attachment, reset-memory and reset-workspace as well.

**Failure scenario.** A new entry is written to a session while 'arcanum data delete-session <id>' is applying; the command fails with a 409 telling the operator to request a dry-run that does not exist, instead of 'run the command again'.

**Evidence (trace).** CliCommandTree.Data.cs:162-205 defines delete-session/delete-attachment with only the id argument; DataRetentionEndpoints.cs:181,199 build the apply request with no ExpectedPlanId; the strings at the cited lines.

**Suggested fix.** Select the advice by request.Operation (dry-run only for prune, reset-memory, reset-workspace, factory reset; otherwise 'run the command again') and use 'The data change was applied' instead of 'Data was pruned'.

#### RV-256 (Minor, usability, error-message, effort S): Global-reset conflict messages give no remedy, and the documented remedy needs a started host while factory-reset needs a stopped one

`src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:5017` — related: `docs/Arcanum.API.md`, `src/RetroDownfall.Arcanum.Cli/Commands/DataRetentionCommands.cs`

**Claim.** GlobalOperationConflict and the sibling messages (5017-5033, 5112, 5160) say only 'An active durable operation conflicts with this global reset.' or 'A durable operation is waiting for an operator and conflicts with this global reset.' The remedy exists only in API.md:773: Data.ActiveOperation clears by starting the host once; Data.OperationNeedsAttention needs 'arcanum operation cancel <id>' or 'retry' against a started host. 'arcanum data factory-reset' plans locally with the host stopped (Command Reference 1215, 1231), so the real sequence is start the host, cancel/retry, stop it, plan again. The CLI prints only '{Code} - {Message}'.

**Failure scenario.** A parked Long Rest sweep or interrupted retention row makes 'arcanum data factory-reset --global --dry-run' print 'Conflict <id>: Data.OperationNeedsAttention - A durable operation is waiting for an operator...'. The operator cannot run 'arcanum operation cancel' because the host is stopped, and nothing says to start it first.

**Evidence (trace).** DataRetentionService.cs:5016-5033; API.md:773; Command.Reference.md:1215,1231; DataRetentionCommands.cs:447-451 renders conflicts as '{code} - {message}'; no Cli/Api source mentions OperationNeedsAttention.

**Suggested fix.** Put the remedy in the message text per code ('start the host once to settle it' / 'start the host, run arcanum operation cancel <id>, stop it, and plan again').

#### RV-257 (Minor, usability, diagnostics, effort S): Admission gate timeouts name nothing about what held the database; three gate codes are undocumented, unmapped and answer 500

`src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs:569` — related: `src/RetroDownfall.Arcanum.Api/Primitives/ArcanumErrorMapper.cs`, `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`, `docs/Arcanum.API.md`

**Claim.** The gate has no logger. The stage-one timeout error says only 'Ordinary Grimoire request or background work did not drain before maintenance closing timed out.' although the gate holds the exact lease sets with GrimoireWorkKind/GrimoireRequestKind. The window is a fixed 5 s (ProductionOpeningAttemptTimeout); production composition (ServiceCollectionExtensions.cs:2102) never passes another value, so the class comment about a separate 'knob of its own to turn' (44-49) has no operator-reachable knob. Grimoire.OpeningTimeout (17-24, message 686-687), Grimoire.AdmissionLifecycleConflict and Grimoire.StaleOpenGeneration are private literals absent from ErrorCodes, ArcanumErrorMapper (default arm 500) and API.md (only MaintenanceUnavailable and WorkDrainTimeout are listed). OpeningTimeout fires after stage two has already moved the gate to Closed.

**Failure scenario.** A Covenant reset or schema transition times out while an Apprentice run or batch holds a work lease. The operator cannot learn which lease kind, count or window; the log is silent. If a physical open stalls past 5 s the client gets a 500 'Grimoire.OpeningTimeout' with no documented meaning while every other request answers 503 'temporarily unavailable' until the owner is retried or the process restarts.

**Evidence (probe).** GrimoireConnectionAdmissionGate.cs:569-573, 649-672, 684-687; ArcanumErrorMapper.cs:224-228 lists only MaintenanceUnavailable/WorkDrainTimeout under Grimoire; grep finds the other three codes only in the gate. PROBE (executed, U27Probe_GateDrainTimeout_NamesNothingAboutWhatHeldTheDatabase) with a BatchProcessing work lease and a finite request lease open: 'U27PROBE drain failure code=Grimoire.WorkDrainTimeout message=Ordinary Grimoire request or background work did not drain before maintenance closing timed out.'

**Suggested fix.** Add outstanding lease kinds and counts (and the window) to the timeout message and log a Warning in the gate; move the three codes into ErrorCodes.Grimoire, map the timeouts to 503, and document the retry-through-the-same-owner path in API.md.

### U28: Arcanum.Infrastructure — Data (4)

#### RV-258 (Minor, reliability, unbounded-growth, effort S): Idempotency claims stranded in Running by a crash are never swept, contrary to DESIGN

`src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs:371` — related: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/UnseenServantService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs`, `docs/Arcanum.DESIGN.md`

**Claim.** DeleteExpiredAsync deletes only Completed/Failed/Abandoned rows. A claim left Running (or Claimed) by a request whose process died before CompleteAsync/MarkAbandonedAsync is never marked abandoned or deleted by anything. DESIGN 10.8.1 (line 1801) says idempotency claims recover through the Unseen Servant's expiry sweep, and 11.17 (line 3926) says claim rows older than the TTL are swept; the code does neither for such a claim. The retention planner reports each stranded claim on every prune as a Data.IdempotencyLeaseActive blocker that 'requires reconciliation before pruning' (DataRetentionService.Pruning.cs:2370-2412), and nothing ever performs that reconciliation.

**Failure scenario.** The host is SIGKILLed, loses power or runs out of memory during an Idempotency-Key request. Its claim stays Running with an expired lease indefinitely. Every retention prune preview and run lists it as a blocker. The row clears only if a client happens to replay the same key.

**Evidence (probe).** Probe P2 (passes): TryAcquireAsync with CreatedAt 30 days ago and lease expiry 30 days ago + 5 min; DeleteExpiredAsync(DateTimeOffset.UtcNow) returns 0; TryGetAsync still returns the row with State Running. UnseenServantService.CleanupExpiredIdempotencyKeysAsync calls only this DeleteExpiredAsync.

**Suggested fix.** In DeleteExpiredAsync, also delete (or first mark Abandoned) Running/Claimed rows whose LeaseExpiresAt < @olderThan, i.e. leases expired longer ago than the TTL. Add a test for the stranded case.

#### RV-259 (Minor, reliability, test-hides-defect, effort S): Fork copy-time blob identity is never reused in production; tests only exercise the path production cannot take

`src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs:334` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs`, `tests/RetroDownfall.Arcanum.Tests/Data/SessionAttachmentStoreTests.cs`

**Claim.** CopyBytesForForkAsync records the identity it captured for each copy in a ConditionalWeakTable<SessionAttachmentForkCopyPlan, ForkBlobAuthority> (SessionAttachmentStore.cs:66-67). That table keys by object reference. SessionRepository.ForkAsync (Repositories/SessionRepository.cs:551-566 and 617-635) builds a new list of plan records for the insert pass, so TryGetValue never hits in production. As a result, every forked blob is decrypted and SHA-256-verified a second time and its identity is recaptured inside the ambient fork transaction, which already holds the Grimoire write lock (the session and entry rows are written before the attachment pass). A copy replaced between the two passes is recaptured and published instead of refused.

**Failure scenario.** Forking a session with large attachments (up to the configured per-session budget, 256 MiB by default and 10 GiB at the clamp) holds the write lock for a second full decrypt and hash of every blob, so concurrent writers wait. A copy swapped between the passes is published rather than refused with 'missing or replaced bytes'. Existing tests (SessionAttachmentStoreTests.cs:2171/2178, 2253/2271) pass the same plan instance to both calls, so they pin the retained path and would still pass if the retained path were deleted outright.

**Evidence (probe).** Probes P4 and P5 both pass. Copy with plan A, then replace the copy with a new inode holding the same ciphertext. InsertForkRowsInAmbientTransactionAsync with an equal-but-distinct plan B (production's shape) succeeds and publishes the row. With the same instance A it throws IOException 'Attachment persistence refused to publish metadata for missing or replaced bytes.'

**Suggested fix.** Key the retained authority by NewAttachmentId in a per-fork dictionary, or have ForkAsync keep and reuse the copy-pass plans. Add a test that uses distinct plan instances.

#### RV-260 (Minor, reliability, crash-consistency, effort S): Missing-file sweep permanently deletes a live attachment row when containment or existence cannot be proven

`src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs:695` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Security/WorkspacePathPolicy.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Hosting/SessionAttachmentPendingGcHostedService.cs`

**Claim.** SweepMissingFileRowsAsync deletes the row when ResolveUnderRoot throws (logged as 'escaping RelativePath') or when File.Exists is false. ResolveUnderRoot (SessionAttachmentStore.cs:2381) throws whenever WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck returns false. That check returns false on a real escape, but also on any IOException or UnauthorizedAccessException from File.GetAttributes (WorkspacePathPolicy.cs:713-725), and File.Exists is false on any stat error. The deletion is permanent, and the next reconcile (every host start) sees the intact ciphertext as unreferenced and unlinks it. The orphan sweep refuses to unlink on filesystem errors, but the row sweep that feeds it has no such rule. Effect is large, trigger is rare.

**Failure scenario.** During a startup reconcile, a transient lstat/stat error hits an attachment path (EIO, a network home directory blip), or a below-root directory is temporarily reached through a link. The row is deleted. Once the condition clears, the next host start's orphan sweep deletes the blob, and the attachment is lost even though nothing was ever missing.

**Evidence (probe).** Probe P3b (passes): persist an attachment; move its logical-key directory outside the root and symlink it back; ReconcileAsync completes without error and deletes the row; restore the directory exactly; ListBoundAsync is empty while the blob still exists; a second ReconcileAsync deletes the blob. Probe P3a, a chmod 000 session directory, does NOT reach the delete: DeleteStalePendingAsync runs the orphan sweep first, and its Directory.EnumerateFiles throws UnauthorizedAccessException (SessionAttachmentStore.cs:2126), which aborts the reconcile. So the branch is reached by transient stat errors or below-root links, not by a plain permission flip.

**Suggested fix.** Make the sweep tri-state. Delete only on a definite ENOENT/ENOTDIR for the leaf after a containment walk that completed. When containment or existence cannot be established, skip the row and log a warning. Have WorkspacePathPolicy report 'could not classify' separately from 'escapes'.

#### RV-261 (Minor, usability, docs-mismatch, effort S): Docs name an arcanum operations verb that does not exist and show a typed-looking operation list --json | jq example

`docs/Arcanum.DESIGN.md:1093` — related: `src/RetroDownfall.Arcanum.Infrastructure/A2A/A2ASendingRecoveryHandlers.cs`, `src/RetroDownfall.Arcanum.Infrastructure/A2A/ArcanumA2ATaskStore.cs`, `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Operations.cs`

**Claim.** DESIGN.md:1093 says the durable-operation surfaces are 'arcanum operations' and 'GET /api/operations'; the same words appear in source comments A2ASendingRecoveryHandlers.cs:17 and ArcanumA2ATaskStore.cs:236. The command is 'operation' (CliCommandTree.Operations.cs:12, no alias; DESIGN.md:1842 and the Command Reference both say operation). DESIGN.md:397 uses 'arcanum operation list --json | jq' as its example of a pipe, but operation is in the Command Reference text-envelope family (pinned by CliJsonEnvelopeContractTests), so --json yields { "output": "<table text>", "exitCode": 0 }, not rows jq can address.

**Failure scenario.** An operator or script author types arcanum operations and gets an unknown-command error, or pipes operation list --json into jq expecting an array and gets one text string (with the wrapped IDs of the table).

**Evidence (trace).** rg 'arcanum operations' returns DESIGN.md:1093 and the two source comments; CliCommandTree.Operations.cs defines new Command("operation", ...) and no alias exists (rg Aliases shows none for it); CliJsonEnvelopeContractTests.cs:50 lists operation among text-envelope families.

**Suggested fix.** Correct the verb at the three places; replace the jq example with a verb that has a typed payload.

#### RV-262 (Minor, usability, api-contract, effort M): operation list silently shows only the newest 100 operations; the store silently clamps limit and the CLI cannot page

`src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs:550` — related: `src/RetroDownfall.Arcanum.Api/Operations/LongRunningOperationEndpoints.cs`, `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.cs`, `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Operations.cs`, `docs/Arcanum.API.md`

**Claim.** ListAsync does Math.Clamp(query.Limit, 1, 500) and Math.Max(0, Offset). GET /api/operations defaults limit to 100 and returns a bare array (no hasMore, total or cursor). limit=0 or negative returns one row, limit=1000 returns 500, silently. Arcanum.API.md:254 says 'optional kind, state, limit, and offset' with no default, maximum or clamp. The CLI (ArcanumApiClient.GetOperationsAsync, CliCommandTree.Operations.cs:16-19) never sends a limit, has no --limit/--offset and prints no notice. This contradicts Command Reference line 72 ('a local page ... bound must expose or automatically follow its continuation') and the rule MemoryEndpoints states ('Refused rather than clamped ... a full page is never mistaken for a complete one'). Terminal history is retained 90 days (DESIGN.md:699).

**Failure scenario.** 150 operations exist; arcanum operation list shows the newest 100 with no hint; the operator cannot find a Failed operation from last week unless they know to add --state or --kind.

**Evidence (probe).** Probe P2 (real store, 520 operations): default 100; Limit=0 -> 1; Limit=-5 -> 1; Limit=1000 -> 500; Limit=100 Offset=500 -> 20. Endpoint LongRunningOperationEndpoints.cs:36-40.

**Suggested fix.** Return hasMore (read limit+1) or a cursor; document default/maximum in API.md; refuse out-of-range limit/offset with 400 as memory search does; give the CLI --limit/--offset and a stderr continuation notice (as session list does) or follow pages.

#### RV-263 (Minor, usability, error-message, effort S): MemoryErasureGuard.KeyUnavailableError omits the remedy its sibling KeyLostError carries

`src/RetroDownfall.Arcanum.Infrastructure/Data/MemoryErasureGuard.cs:110` — related: `src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureProtocol.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Hosting/SagaExtractionService.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs`

**Claim.** KeyLostError ends 'Run arcanum memory erasure status'; KeyUnavailableError is 'The erasure key could not be read, so automatic writes to this store are withheld until it can be.' with no pointer. These strings are the final text for automatic writers: the Lexicon scribe and agent Covenant proposals return it to the model (LexiconService.cs:285-288, CovenantAgentErasureGate.cs:67), and the Saga extraction deferral warning (SagaExtractionService.cs:756-758) carries neither code nor remedy. The unlock-the-credential-store guidance exists only in status output (Command Reference line 728). The startup warning (MemoryErasureKeyWarmup.cs:71-73) does name the command, so log and error disagree on what to do.

**Failure scenario.** The OS credential store is locked after a restart; Lexicon writes and Saga extraction are withheld; the model and the Warning log say the key 'could not be read' and neither tells the operator where to look or that unlocking the credential store clears it.

**Evidence (trace).** MemoryErasureGuard.cs:106-112 versus :106-108 text; Command.Reference.md:719, 728, 730 describe the remedy living in status/reset-key output; MemoryErasureProtocol.cs:54-56 is the erase-flow copy whose text the CLI replaces.

**Suggested fix.** Append 'Run arcanum memory erasure status.' to KeyUnavailableError (the erase-flow copy can stay because the CLI substitutes its own explanation) and add the error code and the command to the extraction deferral warning.

#### RV-264 (Minor, usability, docs-mismatch, effort S): Saga memory text search is ASCII-only case-insensitive but documented as 'Case-insensitive'

`src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs:642` — related: `docs/Arcanum.API.md`, `src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs`

**Claim.** BuildListCommand filters with m."Content" LIKE @query ESCAPE '\'. SQLite LIKE folds case for ASCII only (the hermetic SQLCipher build has no ICU), so non-ASCII text is matched case-sensitively. Arcanum.API.md:98 describes POST /api/memory/search as 'Case-insensitive persisted-memory inspection'; no doc mentions the ASCII limit. The same query feeds saga list --query and memory search (scope saga).

**Failure scenario.** A memory 'Eric aime les eclairs' (accented letters) is stored; searching the accented-lowercase 'eric' or the accented-uppercase 'ECLAIRS' returns nothing, with no hint that case mattered.

**Evidence (probe).** Probe P3 (real SagaMemoryStore.ListAsync): 'ECLAIR' finds the ASCII row (1); 'eclairs' with accents, exact case, finds 1; accented-uppercase 'ECLAIRS' finds 0; accented-lowercase 'eric' finds 0. sqlite3 :memory: "select 'e-acute-clair' like '%E-ACUTE-CLAIR%'" -> 0.

**Suggested fix.** State 'ASCII case-insensitive' in API.md:98 and the CLI help, or fold on a normalized column / registered Unicode collation / the FTS index the Archive search already uses.

#### RV-265 (Minor, usability, error-message, effort S): Attachment source revalidation turns any non-IO exception into a persisted CorruptMetadata with a generic reason and no log

`src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs:481` — related: `src/RetroDownfall.Arcanum.Infrastructure/Security/AttachmentSourceResolver.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CampaignBackedWorkspaceRegistry.cs`

**Claim.** RevalidateAttachmentSourcesAsync (476-488, startup reconcile) uses catch (Exception) (which also catches OperationCanceledException) and RevalidateBoundSourcesAsync (535-559, driven by the debounced workspace watcher) does the same for everything but cancellation. AttachmentSourceResolver.RevalidateAsync already maps missing/inaccessible/unsafe/changed cases to precise statuses, so what reaches the catch is mainly a failure reading the Campaign-backed (Grimoire) workspace registry. The store records CorruptMetadata / 'Source metadata could not be safely revalidated.', writes it to the row via UpdateSourceAsync although the stored metadata is fine, and logs nothing. The status shows in attachment listings and refresh_session_file refusals with no log line tying it to a cause.

**Failure scenario.** A transient registry read failure during a watcher-driven revalidate marks a healthy workspace-file attachment corrupt until the next successful pass; the operator has nothing to search the log for. In the reconcile variant a shutdown cancellation is converted to the same status.

**Evidence (probe).** Probe P4 (real SessionAttachmentStore, resolver throwing InvalidOperationException, raw Bound WorkspaceFile row): returned and persisted 'Status=CorruptMetadata Reason=Source metadata could not be safely revalidated.', log entries: 0. CampaignBackedWorkspaceRegistry reads campaigns via ICampaignRepository.

**Suggested fix.** Log the exception at Warning with the attachment id; leave the persisted row unchanged for exceptions that are not about its metadata; rethrow OperationCanceledException in the reconcile variant as its sibling does.

### U29: Arcanum.Infrastructure — Data (5)

#### RV-266 (Minor, reliability, api-contract, effort S): A stored logical key with leading whitespace cannot be looked up by its own key

`src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:779` — related: `src/RetroDownfall.Arcanum.Core/Storage/SessionAttachmentPathSanitizer.cs`, `src/RetroDownfall.Arcanum.Api/Intelligence/SessionAttachmentToolInjection.cs`

**Claim.** GetByLogicalAsync re-sanitizes its argument, but the sanitizer is not idempotent. It trims whitespace first and strips leading dots afterwards, and its trailing trim covers only '.' and ASCII space. So '. notes' is stored as ' notes', and 'x .' as 'x '. Sanitizing the stored key again yields 'notes', which matches nothing. SessionAttachmentToolInjection also Trim()s before the lookup.

**Failure scenario.** An attachment named '. notes' (or a name ending in a no-break space and a dot) is listed in the attachment index as ' notes'. read_session_file, per-turn tool injection and logical-name pins all report it as not found.

**Evidence (probe).** Probe U29Probe_logical_key_with_leading_whitespace_is_unreachable_by_its_stored_key passed. Output: "stored key=' notes' index key=' notes' byStoredKey=False byTrimmed=False". Sanitizer: Core SessionAttachmentPathSanitizer.cs:62 `input.Trim()`, :99 `TrimStart('.')`, :106 `TrimEnd('.', ' ')`.

**Suggested fix.** Make SessionAttachmentPathSanitizer idempotent by repeating the whitespace trim after the dot trims until the value is stable. Add a property test asserting Sanitize(Sanitize(x)) == Sanitize(x).

#### RV-267 (Minor, reliability, race, effort S): Orphan sweep unlinks a new attachment whose bytes landed before the sweep but whose row has not committed

`src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:2137` — related: `docs/Arcanum.DESIGN.md`, `src/RetroDownfall.Arcanum.Infrastructure/Storage/EncryptedBlobStore.cs`

**Claim.** The sweep spares only files modified at or after its filesystem-clock probe, plus promotion copies registered in PromotionsInFlight. PersistNewCoreAsync publishes its blob and then waits in InsertRowAsync for BEGIN IMMEDIATE. That wait can last busy_timeout 5 s, then Microsoft.Data.Sqlite's 30 s loop, then SqliteBusyRetry's 2 min. The blob is never registered as in flight, so a sweep running during the wait unlinks it. Commit-time revalidation then refuses the insert, and the upload fails with IOException. An EncryptedBlobStore staging temporary is exposed in the same way during VerifyEnvelopeAsync. A narrower interleaving was traced but not executed: the sweep's re-check SELECT runs before the writer commits and its unlink runs after the writer's ValidateOwnedBlob. That commits a row for missing bytes while the upload reports success. DESIGN (line 1436) and the remark at :2085-2096 claim an attachment write in flight is spared because its ciphertext lands before its row. That is true only when the ciphertext lands after the snapshot.

**Failure scenario.** At host start, which DESIGN says overlaps request serving, the startup reconcile's orphan sweep runs while an upload's row insert waits behind another writer's transaction. The upload fails with a 500 ('Attachment persistence refused to publish metadata for missing or replaced bytes') and the operator must retry. The traced interleaving instead loses the bytes of a reported-successful upload.

**Evidence (probe).** Probe U29Probe_orphan_sweep_unlinks_a_new_attachment_waiting_for_the_writer_lock passed. A second connection holds BEGIN IMMEDIATE; a real PersistNewAsync on its own DbContext publishes its blob and blocks; DeleteStalePendingAsync (which runs the orphan sweep) runs on another scope. Output: 'blob exists after sweep: False', then after the lock was released 'persist outcome: IOException: Attachment persistence refused to publish metadata for missing or replaced bytes.' Code: PersistNewCoreAsync :461-535 has no in-flight registration; only PromotePendingAsync :614 calls PromotionsInFlight.TryAdd. The TOCTOU variant is a code trace of :2168-2173 against InsertRowAsync :1602-1675 and was not executed, because no seam exists between the re-check and the unlink.

**Suggested fix.** Register every publish destination (and its staging directory or prefix) in the in-flight set from before CreateWriterAsync until the row commits or the write is abandoned. Rename PromotionsInFlight to reflect its wider role, and register fork copies the same way. Correct the DESIGN sweep paragraph. Add the probe as a regression test.

#### RV-268 (Minor, reliability, crash-consistency, effort S): Promotion deletes the fsynced pending blob after publishing an unsynced copy

`src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:2351` — related: `src/RetroDownfall.Arcanum.Infrastructure/Storage/EncryptedBlobStore.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Storage/DurableDirectoryFlush.cs`

**Claim.** AtomicCopyFileAsync copies with CopyToAsync and then FlushAsync, which does not flush to disk. It replaces the destination non-atomically (File.Delete then File.Move) and flushes neither the file nor its directory. PromotePendingAsync then commits the Bound row and deletes the original pending blob (:725-728), which EncryptedBlobStore had durably flushed (Flush(flushToDisk: true) at EncryptedBlobStore.cs:1141). On APFS a rename can become durable before the renamed file's data. After the WAL commit is checkpointed, a power loss leaves the row pointing at a truncated ciphertext with no surviving copy. Fork copies share the helper.

**Failure scenario.** A first turn with attachments creates a session and promotes its pending attachments. A checkpoint follows, then a power loss or kernel panic hits before the copy's dirty pages reach disk. After reboot the attachment fails authentication on every read, and its bytes cannot be recovered.

**Evidence (trace).** Code trace; power loss cannot be executed in a test. AtomicCopyFileAsync :2340-2363 contains only `await destStream.FlushAsync(cancellationToken)` and has no Flush(true), WriteThrough or DurableDirectoryFlush. Compare EncryptedBlobStore.CompleteAsync :1140-1141 and AtomicFile.cs :147/:207/:315, which use WriteThrough and a directory barrier. The original is deleted unconditionally after commit (:725-728).

**Suggested fix.** Call destStream.Flush(flushToDisk: true) before closing the copy, use File.Move(temp, destination, overwrite: false), and call DurableDirectoryFlush on the destination directory before the row commit.

#### RV-269 (Minor, reliability, crash-consistency, effort S): An uploaded-file delete interrupted after quarantine strands the file permanently

`src/RetroDownfall.Arcanum.Infrastructure/Data/UploadedFileRepository.cs:324` — related: `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`, `src/RetroDownfall.Arcanum.Api/OpenAiV1FilesEndpoints.cs`

**Claim.** TryDeleteUnreferencedOnceAsync moves the bytes into .arcanum-file-delete-{id:N}-* before the metadata delete commits. If the process dies in that window, the transaction rolls back: the row stays live, {files}/{id:N} is missing, and the bytes sit in quarantine. Nothing in src restores or reclaims such a quarantine; the prefix appears only in this file. Every later delete returns RecoveryRequired as its first check, so the file can never be deleted or read. A failed post-commit TryDeleteQuarantined gives the same permanent state: the row is gone, deletes answer RecoveryRequired instead of NotFound, and the bytes are never reclaimed. DESIGN and API document the 500 but give no recovery path.

**Failure scenario.** The host is killed while DELETE /v1/files/{id} is in flight. From then on every delete of that id returns 500 file_delete_recovery_required, its content is unreadable, and a batch that names it fails for a missing input. No API, CLI command or startup step clears the state.

**Evidence (probe).** Probe U29Probe_uploaded_file_delete_interrupted_after_quarantine_is_stranded passed. It recreates the post-kill state (row live, bytes moved into a .arcanum-file-delete-{id:N}-killed/{id:N} quarantine). Output: 'delete1=RecoveryRequired delete2=RecoveryRequired rowLive=True bytesAtPath=False'. `rg 'arcanum-file-delete' src` matches only UploadedFileRepository.cs, so no recovery exists. The existing test TryDeleteUnreferencedAsync_WhenByteFinalizationFails_RequiresRecoveryAndKeepsRecognizableQuarantine pins the permanent RecoveryRequired outcome.

**Suggested fix.** Add an idempotent recovery, either a startup step or the first step of TryDeleteUnreferencedAsync. For each recognizable quarantine it should restore the bytes, identity-checked, when the row is live, and finish the unlink when the row is gone. Document the recovery in the DESIGN /v1/files section.

### U30: Arcanum.Infrastructure — Data/Covenant (1)

#### RV-270 (Minor, usability, diagnostics, effort S): Connection-level refusals name no holder and the open-failure log carries no cause

`src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantConnectionDrain.cs:226` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantConnectionEnrolmentInterceptor.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCanonicalErasureTransaction.cs`, `tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionInterceptorTests.cs`

**Claim.** CovenantConnectionDrain.DrainAsync refuses with 'A Covenant connection handle did not close, so no exclusive maintenance connection may be opened.' although the type's own remarks (lines 13-23) exist because an exclusive open 'fails on a lock whose holder the caller cannot name'; Register takes no label, so the refusal names neither the enrolling component nor a count. CovenantConnectionEnrolmentInterceptor.LogFailure logs 'An ordinary Grimoire connection failed to open safely.' at Error with no exception type or SQLite code, so a wrong key, a permission error, a full disk and a pragma read-back failure are one line. CovenantCanonicalErasureTransaction.Failure(GrimoireBusyTimeoutException) says 'another handle held the database for N attempt(s) over T' with no next step even though the drain already proved this process's handles closed (so the holder is another process).

**Failure scenario.** An erasure is refused because a handle will not close, or the erase times out on a busy file held by a database browser or a second arcanum process. The operator sees the sentence, no component, no process hint, and no cause in the log.

**Evidence (probe).** CovenantConnectionDrain.cs:206-211 and :226-232; CovenantConnectionEnrolmentInterceptor.cs:205-218; CovenantCanonicalErasureTransaction.cs:754-760. The omission in LogFailure is pinned by tests/RetroDownfall.Arcanum.Tests/Data/GrimoireConnectionAdmissionInterceptorTests.cs:1087-1096 (Assert.Null(entry.Exception)); exception type and SqliteErrorCode carry no key material. Established by reading; no probe run.

**Suggested fix.** Add an enrolment label to ICovenantConnectionDrain.Register and list labels and counts in the refusal; log exception type name and SqliteErrorCode in LogFailure (update the pinning test); reword the busy-timeout message to name 'another process' and say to close it and retry.

#### RV-271 (Minor, usability, error-message, effort S): Transient SQLite faults are reported as integrity failures at five sites, against the kernel's own classification and the API doc

`src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureExposureReader.cs:191` — related: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignRollupStore.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantErasureInventorySource.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantProtectedArtifactErasureKernel.cs`

**Claim.** CovenantProtectedArtifactErasureKernel.StorageBlocker and API.md:81 classify busy, locked, no-memory, read-only, interrupt, I/O, full-disk and cannot-open as environmental (Covenant.Unavailable: 'says nothing about the stored data, and can clear on its own'). Five sites in this unit collapse every SqliteException to an integrity-style code: CovenantDisclosureExposureReader (Covenant.IntegrityFailure 'could not be folded safely'), ArtifactSensitivityLedger.WriteWithinAsync (IntegrityFailure 'could not be persisted (N)'), CovenantErasureInventorySource bare catches, the startup adopter catch-all, and CampaignRollupStore.WithinAsync (Grimoire.WriteFailed 'Campaign summary storage could not complete (N)', even for write:false read entry points).

**Failure scenario.** A reset preview or retention exposure read meets a busy database: the operator is told the disclosure exposure 'could not be folded safely' (an integrity claim) with no retry hint. A full disk (SQLITE_FULL 13) while a turn's assistant-entry label is persisted yields 'IntegrityFailure ... could not be persisted (13)'.

**Evidence (probe).** Probe P5: a second connection (1 s timeout) reading while another holds BEGIN EXCLUSIVE gets code=Covenant.IntegrityFailure message='The nonrevocable Covenant disclosure exposure could not be folded safely.'; the identical read succeeds once the lock is released ('P5 same read after the lock is released ok=True'). CovenantDisclosureExposureReader.cs:191-197; ArtifactSensitivityLedger.cs:190-197; CampaignRollupStore.cs:302-307; CovenantProtectedArtifactErasureKernel.cs:450-472 (StorageBlocker); API.md:81.

**Suggested fix.** One shared SQLite fault classifier (the kernel's StorageBlocker rule) used by all five sites: environmental codes map to Covenant.Unavailable with 'retry' wording, others stay integrity; give CampaignRollupStore read entry points a read code instead of Grimoire.WriteFailed.

#### RV-272 (Minor, usability, diagnostics, effort S): Disclosure writer closes for good after one failed open, says 'maintenance', and never logs why

`src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureWriter.cs:117` — related: `src/RetroDownfall.Arcanum.Api/Intelligence/CovenantDispatchGate.cs`, `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`

**Claim.** AcknowledgeAsync calls FailClosed on any OpenVerifiedAsync failure except Grimoire.MaintenanceUnavailable (a thrown exception, a failed OpenFreshAsync, a dataset mismatch, a StaleSnapshot). FailClosed sets _accepting=false/_pendingClose=true and only ReopenAsync clears it; ReopenAsync is called only by the erasure coordinator and the bootstrapper's authenticated-transition restore. Every later call returns Covenant.LifecycleConflict 'The Covenant disclosure writer is closed for maintenance.' although no maintenance is running. The class has no logger and CovenantDispatchGate returns the error without logging, so the first failure's cause is lost, and 409 invites a client retry loop that cannot succeed until restart.

**Failure scenario.** A transient storage fault (busy/IO) while the writer lazily opens its warm connection. The first provider dispatch with Covenant-derived content fails with the fault; every later dispatch in every Session fails with 409 'closed for maintenance' until the host restarts or a reset/erasure runs, and nothing in the log says why.

**Evidence (probe).** CovenantDisclosureWriter.cs:110-129 (FailClosed on non-maintenance open failure), :466-470 (catch Exception -> MaintenanceFailure), :573-594 (FailClosed), :616-619 (message). Probe P2 using the existing WriterHarness with a one-shot RefuseNextOpen: first call fails with the refusal's own code; second call (healthy open available, opened=0) fails with LifecycleConflict 'closed for maintenance'; after republishing a healthy availability the third call fails the same way; ReopenAsync then lets the fourth succeed. Existing test An_ordinary_open_failure_keeps_the_writer_closed_without_a_lease pins fail-closed for the first call only. CovenantDispatchGate.cs:600-606 returns acknowledged.Error with no log. 'rg ReopenAsync src' shows only CovenantErasureCoordinator and GrimoireDatabaseBootstrapper:553.

**Suggested fix.** Log the first failure at Warning with its code and exception type when FailClosed fires; give the closed-after-failure state its own message ('closed after an earlier failure to open its connection; restart the host'); optionally allow one bounded re-attempt of OpenVerifiedAsync when the failure was environmental.


## Remediation record

Remediation is in progress on a local integration branch. The original findings above remain unchanged. Entries below record reviewed completed findings; unlisted findings remain open. Full integrated qualification and external delivery have not occurred.

| Finding | Outcome | Evidence |
| --- | --- | --- |
| RV-001 | fixed `70a7fe48b3000923dc5995baebd850fc08282ea8` | `DataRetentionServiceTests.LargeCheckpoints.cs` reproduces legacy recovery above 10,000 candidates and interrupted large-plan recovery through the real reconciler, with frozen selection, journal, corruption and single-flight assertions. Initial RED and deliberate writer mutation failed; restored focused verification passed 1,187 tests. Independent spec and code-quality review approved. |
| RV-067 | fixed `70a7fe48b3000923dc5995baebd850fc08282ea8` | The same writer regression uses a 7,404,120-byte frozen set, fails the old 5 MiB checkpoint writer, and recovers the complete selection with a 144-byte cursor. Snapshot writes are transactional and bounded in chunks; there is no total-plan item cap. |

The snapshot mechanism also addresses the shared implementation behind RV-064. Its distinct journal-bearing scenario evidence remains subject to its owning task review. RV-069's operator-documentation audit is completed below. Payload-size measurements are not end-to-end throughput benchmarks.

### Reviewed local integration checkpoint — 2026-10-25

The following task commits were independently reviewed and merged locally through `51d4032b6e06659ec7cb257edd3f83d9f26ced6a`. The combined candidate built with zero warnings/errors and passed 3,554 affected tests and contract guards. Eight tests skipped: six Windows-only tests on macOS and two opt-in Long Rest scale probes. An earlier overbroad run selected hosted producer analysis and was cancelled; it is not passing evidence. Complete hosted analysis, all full suites, exact final-candidate CI and Native AOT/live-provider qualification remain outstanding.

| Finding | Outcome | Evidence |
| --- | --- | --- |
| RV-002 | fixed `48f60043` | Real legacy attachment filenames ending in `.tmp` survive reconciliation. Three behavioral RED cases, restored store suite and deliberate mutation establish the redundant early-delete defect. |
| RV-003 | fixed `1c4a5338`, caller correction `f71214f5` | Opaque identity-based paths, create-new publication, live physical-identity claims and length/digest verification preserve compatible legacy snapshots while refusing substitution. Real 129-file repository fork tests cover second-page collisions, late transaction failure, replacement preservation and successful ownership release. Independent review found and then verified the caller correction. Full-consumption stream validation does not promise withholding every earlier byte before final integrity is known. |
| RV-004 | fixed `b19581e7` | Oversized native rollup entries have bounded representations bound to original digest/length/provenance; preparation does not consume them, and publication rederives the bound content. Real preparation/publication failures and mutations are covered. |
| RV-008 | resolved `b19581e7` | Operational rollup failure defers maintenance after mandatory authority, claim and source fences, allowing the user turn to proceed. The suggested blanket relaxation for slash-prefixed text was rejected because it also admits absolute host paths; conservative privacy remains. No stale summary fallback was added. |
| RV-009 | fixed `8e8e0b37` | Maintenance provider factories apply the existing output-token wire dialect per call and reject invalid dialects before credential access. Real wire tests cover all three factories and stable selected-provider snapshots. |
| RV-010 | fixed `bfc38f15` | Per-source pin failures are contained with content-free diagnostics; caller cancellation propagates. Enumeration failure retains its separate boundary. Behavioral RED and deliberate mutation are covered. |
| RV-014 | fixed `bf18b3f7` | Real fresh/evolved SQLCipher query-plan tests use the Status index for the original OR query. Exact managed decimal membership remains, including prior-period uncertain-paid reservations. Wrong-column index mutation fails. |
| RV-041 | fixed `9dab9da0` | Quitting preserves the active hard-modal human prompt and typed answer. Interaction RED, restored tests and mutation verified. |
| RV-042 | fixed `a2130fc5` | Older history uses the existing timestamp-plus-ID cursor, requests one lookahead row and preserves stable ordering when dates tie. Paging and continuation regression tests pass. RV-177's separate live-entry identity/count cases remain open. |
| RV-043 | fixed `8e91f904`, comment correction `e2beb1e2` | Full human questions scroll and reflow; paging keys retain answer focus and ephemeral status text remains accessible. Interaction tests and mutation verify the behavior. |
| RV-055 | fixed `bf18b3f7` | Shares RV-014's indexed budget reservation query. Core22 evolution preserves the exact Core21 fingerprint; a 2,048-settled-row fixture verifies indexed access and an exact 28-place decimal total. |
| RV-069 | fixed `70a7fe48`, docs `50a8944a` | Frozen selection and compact durable cursor semantics replace obsolete sweep-item-cap guidance in Engineering, Debugging and Command Reference. Original selection, cutoff and recovery authority remain. |
| RV-077 | fixed `b19581e7` | Fair maintenance discovery retains continuation only for successful queue admissions, including more than 100 deferred items, and alternates lanes. Independent review corrections prevent queue overflow from starving later Campaigns and distinguish stale frozen admission from genuine capacity exhaustion. |
| RV-149 | fixed `9392aa28` | One linked five-second HTTP deadline covers headers and capped body reads; caller cancellation propagates. Both actual loopback regressions fail on old production and deliberate restoration, then pass; focused provider/docs checks total 138. |
| RV-211 | corrected `50a8944a` | Gate documentation describes the actual Campaign-exclusive database probe. Existing contract/documentation guards pass; no runtime behavior changed. |
| RV-214 | corrected `50a8944a` | Section/probe documentation now belongs to the correct method and describes the captured scope. Existing guards pass; no runtime behavior changed. |
| RV-228 | corrected `50a8944a` | Five schema comments identify per-object SQL ownership. Erasure documentation describes idempotent replay without an exactly-once claim; obsolete detached commentary removed. |
| RV-261 | corrected `50a8944a` | CLI documentation uses the actual singular `operation` verb and typed budget JSON output. Existing documentation/CLI checks pass. |
| RV-267 | fixed `1c4a5338`, caller ownership `f71214f5` | Upload publication is registered before writing through row commit or abandonment. Session-gated orphan deletion protects unpublished fork copies through the transaction, including a fork starting at the destructive boundary. Actual upload/fork race RED cases and independent mutations verify protection. |

This first checkpoint covers 21 findings. Later reviewed checkpoints follow; shared files, similar wording or partial mechanism overlap alone do not close a finding.

### Retention and web integration checkpoint — 2026-10-25

Retention commit `4ea9fdd9` was independently approved and integrated at `e489e278`. That merged candidate passed 1,497 affected retention/schema/repository/fork/contract tests with no skips and built with zero warnings/errors. Its initial multi-node build ended with MSBuild child-process failures; the successful single-node retry is the build evidence. Web commits `e24c20d5` and `8af97e3f`, with the separately verified whitespace-only prerequisite `9f151a87`, were independently approved and integrated at `f7eef34f`. Web task qualification passed 679 tests with no skips and a zero-warning build; the reviewer independently reran 52 focused tests successfully. The web merge does not turn the prior retention test run into evidence for the later combined tree.

| Finding | Outcome | Evidence |
| --- | --- | --- |
| RV-061 | fixed `4ea9fdd9` | Conflict membership is materialized instead of repeatedly correlated. Real executed-query plans and bounded SQLite instruction counts detect the original work amplification; controlled old-code mutation fails. |
| RV-062 | fixed `4ea9fdd9` | Grouped accounting preserves the original ledger membership. Real work-budget tests cover own runs and unrelated outstanding reservations; independent review compared old/new SQL across 40 varied datasets with equal results. |
| RV-063 | fixed `4ea9fdd9` | Embedding candidate membership uses a HashSet; an allocation regression detects the original repeated membership work. |
| RV-064 | verified `4ea9fdd9`, mechanism `70a7fe48` | A persisted-write ledger covers 1,001 real file deletions and 2,003 checkpoint updates, including exactly 1,001 pending journals. Fixed payload totals are 599,528 cursor bytes plus 100,220 snapshot bytes; the full-inline mutation writes 201,051,756 bytes and fails the bound. This is a bounded byte measurement, not a throughput extrapolation. |
| RV-065 | fixed `4ea9fdd9` | Core23 normalized identity and accounting indexes are verified on real query plans and fresh/evolved schemas. Six UploadedFiles cases preserve dashed/compact lower/upper/mixed-case identities. Historical Core22 SQL and fingerprints remain unchanged. |
| RV-066 | fixed `4ea9fdd9` | Factory reset inventories, removes and reconciles the complete Campaign rollup/contribution/checkpoint closure under existing narrow authorization. Real fixtures verify artifact and derived-row counts while preserving Campaign registrations. |
| RV-015 | fixed `e24c20d5` | Research summaries occupy at most one third of their retained and synthesis budgets, newest first, leaving room for fetched source content. Real workflow regression, framing/ceiling checks and deliberate mutation verify the contract. |
| RV-016 | fixed `e24c20d5` | Search, browse and research preserve completed paid results when attachment persistence fails, with existing safe error shapes. Real endpoint regressions cover failure modes and caller cancellation; cancellation still propagates and does not produce a terminal result. |
| RV-017 | fixed `e24c20d5` | Provider-local numeric markers are stripped from summary text. Global citations refer only to emitted source blocks; deduplicated, denied and omitted-source cases plus publication/retrieval/event dates are covered. |
| RV-018 | fixed `8af97e3f` | A random per-turn HMAC key lets protected tool arguments/results participate in equality comparisons without retaining plaintext or emitting stable content fingerprints. Normal calls and synthetic denials retain true-repeat/oscillation detection, changing progress, per-turn separation and audit redaction. |

Reviewed completed findings now total **31 of 272**; **241 remain open**. Final integrated qualification remains outstanding. A separate connection-acquisition inventory guard correctly identified two uncatalogued retention snapshot helper opens; that integration repair is tracked and is not waived by the passing task suites above.

### Covenant and session-fork integration checkpoint — 2026-10-25

Independently approved commits `1bad138c` (Covenant fragment budgeting), `6bb4966c` (Command Center session forks), and `6b93f460` (Covenant maintenance) were merged locally at `7b7bb952`, `e923f872`, and `4f6947b8`, respectively. Task qualification recorded 662 tests with no skips, 1,310 tests with four Windows-only skips, and 100 tests with no skips, each with a zero-warning build. These overlapping task suites are not a combined final-candidate run. The broader maintenance guard run recorded 566 passes and two failures for the separate retention acquisition-inventory gap; that run is not green and the repair remains required.

| Finding | Outcome | Evidence |
| --- | --- | --- |
| RV-024 | fixed `1bad138c` | Fragment measurement no longer charges request tool schemas already included in the baseline. Real composed Wizard requests with 25/40 added tools reproduce withheld Confirmed entries; a tight-window control retains genuine withholding. Initial and deliberate mutation failures restore to 662 passing tests. Framing, defaults, disclosure and the final context gate remain. |
| RV-044 | fixed `6bb4966c` | Staged attachment references are cleared when their Session binding changes. Real Command Center state and interaction regressions preserve the intended generation behavior. |
| RV-045 | fixed `6bb4966c` | Central generation guards prevent Session mutation commands from changing active generation state. Interaction tests and deliberate mutations verify the boundary. |
| RV-046 | fixed `6bb4966c` | Additive exclusive `BeforeEntryId` fork cutoff supports regeneration while retaining inclusive `UpToEntryId` and rejecting contradictory selectors. Actual Wizard regeneration tests cover first-entry and prior-history cases with one user entry and one answer, preserving the source. A necessary atomic fork prerequisite copies validated immutable Campaign bindings under the narrow binding-write authority, including GlobalOnly, Campaign and LegacyUnresolved; missing/corrupt bindings refuse and rollback remains atomic. |
| RV-047 | fixed `6bb4966c` | A source-bound pending fork intent and typed host preview preserve the exact selected entry and attachment set through confirmation; fingerprint revalidation refuses stale confirmation. Real interaction/endpoint tests and mutations cover the selection. |
| RV-048 | fixed `6b93f460` | An oversized first outbox sequence drains whole, up to the existing 65,536-row ceiling. A successor sentinel distinguishes a complete ceiling-sized sequence from an over-ceiling sequence, which advances nothing. Real six-Campaign cleanup, later work, rollback and hosted warning regressions pass. |
| RV-049 | fixed `6b93f460` | An admitted idle probe preserves live memory delegation without taking a mutation barrier. Its read lease ends before the pending/full-sweep path takes the barrier, cleanup lease and transaction. Real live-grant, revocation/drain, missing-cursor and cancellation-boundary coverage passes. |

Reviewed and locally integrated findings now total **38 of 272**; **234 remain open**. All three Critical findings are integrated. Final combined qualification and delivery remain outstanding.

### Scope eligibility, workspace identity and CODEX checkpoint — 2026-10-25

The independently approved retention acquisition-inventory correction `f5be6bc8` was integrated at `f3ef84e8`, and the fork-preview deadline-inventory correction `7b72b782` at `6529ba42`. Both preserve their closed scanners; the first adds exactly the two previously missing snapshot opens and records the observed count 535, while the second classifies the existing short-call preview method. They repair integration omissions and do not add finding closures. The acquisition catalog's existing raw-consumer route label remains; the traced physical open is through the scoped DbContext and serving EF enrolment interceptor, not a direct factory call.

| Finding | Outcome | Evidence |
| --- | --- | --- |
| RV-023 | fixed `51858aad` | Real buffered/streaming turns skip extraction when their eligible Lexicon tiers are empty, including unrelated-Campaign-only storage. The admitted, schema-aware probe preserves active Campaign/Global retrieval, retired exclusions, pre-curation schema reads, cancellation and unavailable-storage distinction. Populated scopes still extract and inject context; existing paid-work/accounting assertions remain with eligible fixtures. Deliberate guard/scope/retirement mutations fail 4/2/2 cases. Restored affected verification passes 1,290 tests; later inventory/new-control verification passes 172 (overlapping), without skips. The exact new acquisition increases the observed closed count to 536. No new parallel database work or wall-clock speed claim. |
| RV-035 | fixed `6ad9af83` | Secure CODEX read status reaches the API: over-cap, invalid UTF-8 and unreadable regular files fail with typed errors rather than editable empty success. Absent and valid-empty files remain supported. Original bytes are preserved. Forge revokes edit/save permission on failed initial load or reload; successful reload restores it. Real client HTTP tests detect forbidden PUTs under mutation. |
| RV-038 | fixed `6ad9af83` | Shares the CODEX refusal implementation and evidence, with explicit contained-alias refusal and existing outside-root containment guards. Alias write refusal was already present and remains; it is not claimed as a newly demonstrated overwrite defect. Final task checks pass 70 Arcanum tests with one Windows-only skip, 217 guards and 19 Forge tests; both builds have zero warnings/errors. API helper and Forge HTTP coverage are distinguished in the review. |
| RV-037 | fixed `a019d36a` | The actual ID returned by the workspaces API resolves to its canonical stored identity in workspace/all memory search. GUID forms and case, trimmed IDs, names and unrelated-root exclusion are exercised through real endpoints. Unknown supplied selectors return typed 404 before shared result/byte budgets can hide them. Unfiltered and irrelevant-selector behavior remains. |
| RV-039 | fixed `a019d36a` | Shares RV-037's normalization and unknown-selector fix and documents the CLI selector contract. Initial and original-resolver mutation runs fail six cases with three controls passing; restored affected classes and the complete timestamp caller guard pass 172 tests, without skips. The exact GUID-only Format caller is added to the closed census without weakening it. |

These fixes were integrated at `043728e1` (workspace), `3dc5c6ac` (CODEX), and `cfa715e1` (Lexicon). The combined `cfa715e1` candidate passed **2,506 affected Arcanum tests**, with five Windows-only skips and no failures, and **19 affected Forge tests** with no skips. Both builds completed with zero warnings/errors. The first Forge build lacked this worktree's assets file; a successful locked restore preceded its successful build and tests. That setup failure is not behavioral RED. This combined run includes the repaired acquisition/deadline inventories and timestamp caller guard, closing the three previously recorded integration guard failures. It is not full-suite, hosted-analysis, CI, AOT or live-provider qualification.

Reviewed and locally integrated findings now total **43 of 272 (15.8%)**; **229 remain open**: **42 Important and 187 Minor**. All three Critical findings are integrated. Full final qualification and external delivery remain outstanding.

### Campaign and Lore integration checkpoint — 2026-10-25

The following changes were independently approved and integrated locally. This brings the reviewed, integrated count to **45 of 272 findings** (all 3 Critical, 36 Important, 6 Minor), with **227 open** (40 Important, 187 Minor). Task-specific verification below is complete; combined qualification of this newer tree and all final delivery gates remain outstanding.

| Finding | Outcome | Evidence |
| --- | --- | --- |
| RV-034 | fixed `bc9a8e8eb080e1f1b4dad9d4d292c47d93c22be5` | Four field-owned Campaign write ports preserve concurrent header, Sanctum, import-settings and workspace changes through parameterized admitted SQL and the existing transaction. Unique-name races and deleted Campaign/Prompt writes return typed errors. Ten repository and four actual HTTP RED cases, 21 deliberate mutation failures, then 86 affected and 424 guard passes with no skips and a zero-warning build. Caller timestamps remain authoritative, including out-of-order completion. |
| RV-036 | fixed `30265b8e43332b54c99b411b1682839a57f0b191` | Additive query-based Lore GET/DELETE routes preserve accepted keys containing slashes, dot segments and other reserved characters; CLI and Forge encode the query, while legacy routes remain. Actual client-to-host/database GET and DELETE RED, full production reversion RED, then 339 Arcanum and 98 Forge passes, zero skips/warnings. The test-only authentication adapter verifies routing and data behavior, not real-socket presence/authentication. The exact route inventory also records the previously approved fork-preview route without weakening enumeration. |

### Budget and recovery integration checkpoint — 2026-10-25

These independently reviewed changes bring the integrated count to **47 of 272 findings**: all **3 Critical**, **38 Important** and **6 Minor**; **225 remain open** (38 Important, 187 Minor). Combined candidate `cd4dfc1d981264be772ffce4bbb178c922c4a2ab` built with zero warnings/errors and passed **2,280 Arcanum tests** with one Windows-only filesystem-jail skip, plus **110 Forge tests** with no skips. This bounded combined run covers the latest Campaign, Lore, budget and cancellation work and affected architecture/SQL/authority/documentation guards; it is not all-suite, full hosted-source, CI, Native AOT or live-provider qualification.

| Finding | Outcome | Evidence |
| --- | --- | --- |
| RV-006 | fixed `22193050789fe656ea174f7e74a5a26cf28f0f1b` | Recorded local committed-plus-outstanding and delegated spend remains visible when enforcement is off or has no positive limit. Explicit `enforced` distinguishes active policy from `enabled`; configuration, doctor, health, CLI and Treasury expose the missing-limit remedy without changing zero-limit compatibility or accounting policy. Per-surface behavioral RED and deliberate mutations (12 Arcanum, 2 Forge failures) precede exact restoration; 510 Arcanum and 12 Forge affected checks passed, zero skips/warnings. |
| RV-073 | fixed `6beaff9e`, review correction `9c72127388343b89b4b01829d960e4f5e665b2a0` | Lease takeover preserves durable cancellation. Blob/prune recovery observes it between safe units, completes required quarantine compensation and retains committed effects and handler failure authority. Independent review found cancellation racing a checkpoint save; four deterministic boundary regressions and three ownership/checkpoint/expiry conflict controls verify the narrow repair without changing store CAS guards. Restored 549 affected, 456 guard and 15 hosted-boundary checks passed; mutation reproduced all four races. |

### Provider diagnostics checkpoint — 2026-10-25

RV-040 is fixed in independently approved commit `6ece737dc8272b23ab18a286cb577982a76ac3c4` and integrated locally. Typed, sanitized diagnostics distinguish invalid URLs, DNS, blocked addresses, transport/TLS, HTTP authentication and malformed model-list responses; Forge retains the failed-envelope reason. Warning logs contain the configured host and fixed reason rather than raw URLs, credentials, resolved addresses or exception detail. Existing URL-guard contracts, pinned connections, the five-second shared headers/body deadline, caller cancellation and the response cap remain intact. Fourteen API and one Forge behavioral RED cases recur under mutation; restored verification passed 345 API/security/documentation and 106 Forge tests with no skips and a zero-warning solution build. The review audited the logs and historical source binding.

The count is now **48 of 272 integrated** (3 Critical, 39 Important, 6 Minor), with **224 open** (37 Important, 187 Minor). The last combined verification above belongs to the earlier `cd4dfc1d` candidate; combined verification including this provider change and all final delivery gates remains outstanding.


### Spell catalog checkpoint — 2026-10-25

Independently approved commit `acb0ab1f4ada617df406cb08d229c18d19d612a4` was integrated locally at `4f286f5ae9958818905066ef4f3e7016f33429bc`. Task verification passed 349 tests with one conditional builtin-clone skip and a zero-warning solution build. The independent reviewer checked the exact source/diff, initial and mutation failures, restored test logs and historical source binding.

| Finding | Outcome | Evidence |
| --- | --- | --- |
| RV-025 | fixed `acb0ab1f` | Existing metadata cache now uses a 300-second production default; a human-paced six-second repeat avoids metadata opens. Explicit zero remains uncached. Cold misses still traverse the allowed tree; external discovery changes have a documented five-minute freshness window. |
| RV-028 | fixed `acb0ab1f` | Campaign import takes one workspace lock and one fresh collision inventory, tracks successful published names and invalidates even after partial failure. Repeated parse/open work and duplicate-name publication failures have behavioral regressions. |
| RV-029 | fixed `acb0ab1f` | Export takes one metadata inventory and securely hydrates each selected spell directly. Existing containment, current-file identity, omission reporting and byte limits remain. Mutation repeats eleven sentinel opens against a maximum of four. |
| RV-031 | fixed `acb0ab1f` | Spell search consumes the shared production TTL while retaining existing Campaign/source filtering before traversal. Selected-Campaign coverage includes nineteen excluded roots. No credit is claimed for introducing already-existing filters. |
| RV-032 | fixed `acb0ab1f` | Repository/routing lookups reuse discovery metadata; first-party writers invalidate before releasing their workspace lock. Current file identity/content remains execution authority. Retained execute preflight preserves error semantics. |

Implementation also exposed equivalent-path and parent/nested-workspace invalidation gaps. Canonical intersecting-root generations and unique tokens across the bounded 64-root registry repair them; delayed-flight, eviction and unrelated-root controls preserve cache isolation. These additional repairs do not add review finding closures. The full production revert failed eight strengthened cases; generation/overlap mutations failed four cases while the unrelated-root control passed.

The independently reviewed Core23 integration assertion repair `d459d8ef`, merged at `4c095882`, changes only two current-head version expectations from 20 to 23; all historical fingerprints, data and catalog-equivalence guards remain. It repairs drift introduced by this remediation's schema transitions and closes no additional finding.

The count is now **53 of 272 integrated (19.5%)**: 3 Critical, 44 Important and 6 Minor. **219 remain open**: 32 Important and 187 Minor. The last combined verification belongs to `cd4dfc1d` (47 findings); combined verification of the newer candidate and all final delivery gates remain outstanding.


### Operation listing checkpoint — 2026-10-25

RV-074 is fixed by independently approved commit `c836932a8fc566d4f272670fd350d88897c80cb4`, integrated at `f419240e44be1834611b94cf8bb50b02ce9ded9d`. The real CLI preserves complete operation IDs, states and attempt counts at 80/120 columns in normal, plain and JSON modes. It omits Summary and then Kind with stderr notices, retaining full details at wide widths and the existing JSON text envelope. Reusing the established listing component also fixes a separately reproduced terminal-control rendering defect in operation summaries.

Initial behavioral RED and a deliberate production revert each failed seven cases with six controls passing. Exact restored verification passed 314 related CLI/documentation tests without skips; the build and required style checks passed with zero warnings/errors. Independent review checked all source/log hashes and reran 21 focused tests successfully. Pagination remains a separate open finding.

The reviewed, locally integrated count is **54 of 272 (19.9%)**: 3 Critical, 45 Important, 6 Minor. **218 remain open**: 31 Important and 187 Minor. The latest bounded combined evidence still belongs to `cd4dfc1d`; newer combined qualification and final CI/AOT/live delivery gates are pending.


### Batch and erasure-guidance checkpoint — 2026-10-25

Independently reviewed batch commit `0cacf6b6` plus review repair `61b2425d3e0710d87a04cbf9693a214cad477753`, and erasure-guidance commit `300a0558f2bdd9325f4295240ab8b3c90f859b77`, are integrated locally in candidate `bb5857b62dda19759c35ba2fc9c8917cd87d584a`.

| Finding | Outcome | Evidence |
| --- | --- | --- |
| RV-005 | fixed `61b2425d` | Missing, unreadable and undecryptable batch input reaches failure bookkeeping and a published diagnostic. Reset preserves those distinctions. Original and mutation failures precede restored tests. |
| RV-080 | fixed `61b2425d` | Unexpected faults after a fully started page expose an honest continuation boundary when EOF is unknown. Known EOF does not invent a request. Independent review repaired occupied-note collisions on resumed paid checkpoints; diagnostic slots remain separate from input boundaries and guidance prohibits replay of saved results, including later gaps. Refused diagnostic insertion no longer silently succeeds. |
| RV-081 | fixed `61b2425d` | Per-batch input I/O/access/decryption faults no longer abort startup reconciliation of other batches. Cancellation and accounting/storage authority failures remain outside the containment catch. |
| RV-087 | fixed `61b2425d` | Missing input produces an inspectable error artifact and content-free batch/input identification in logs, without provider dispatch or overwriting paid checkpoint authority. |
| RV-088 | fixed `61b2425d` | Operator cancellation uses cancellation-specific text while preserving provider-charge uncertainty. Independent review exposed current-page sealing before cancellation inspection; deterministic authenticated-open cancellation now verifies that path and retains genuine host-interruption text in the control. |
| RV-058 | fixed `300a0558` | Diagnosis selects erasure guidance from each operation's recorded kind, checkpoint version and terminal code before grouping. Ordinary and erasure classes with equal kind/error keep separate counts and instructions; no operation IDs or checkpoint contents enter public health detail. Existing recovery authority is unchanged. |

Batch restored verification passed 227 tests without skips and a zero-warning build. The subsequent 83-documentation-test run overlaps those 227 and is not an additional unique count. Round-1 mutation reproduced four failures with two controls. Erasure-guidance RED/mutation each reproduced eight failures with ten controls, restored190 checks passed, and independent review reran29 tests successfully.

The prior54-finding combined candidate `8dab3558` completed its Arcanum run with **2,841 passes, 2 failures and 1 Windows-only skip**, after a zero-warning build. Both failures correctly detect the spell import helper's five-parameter signature against its stale four-parameter acquisition catalog entry. This is a remediation integration omission being repaired without weakening the scanner; **that run is not green**, and its Forge stage did not run.

The reviewed, locally integrated count is **60 of272 (22.1%)**:3 Critical,47 Important,10 Minor. **212 remain open**:29 Important,183 Minor. The catalog correction, newer combined qualification and full final CI/AOT/live gates remain outstanding.


### Context, budget and integration-catalog checkpoint — 2026-10-25

Independently approved context-error commit `78c773f6` and its reviewed Unicode repair `8e825457a7bfc710e4431193dd95668025973def` are integrated. The exact acquisition catalog correction `729567d59128fda5b355dbcf25080fc0904e2ba5` is also independently approved and integrated; candidate `87aca19bab06c5df2f4063106f6e2162e8327ef9` contains both.

| Finding | Outcome | Evidence |
| --- | --- | --- |
| RV-013 | fixed `8e825457` | OpenAI buffered/streamed errors distinguish true overflow (`invalid_request_error/context_length_exceeded`) from daily-budget refusal (`insufficient_quota`). Arbitrary error text remains sanitized; typed internal overflow metadata carries only bounded configured identifiers and numeric facts and is ignored by native serialization. |
| RV-020 | fixed `8e825457` | Native numeric overflow returns400 with provider/model, counts, actual window and compaction/model/configuration remedies. Internal stale/cache/payload admissions have distinct errors; Covenant capacity409 and genuine rate/budget429 remain. |
| RV-027 | fixed `8e825457` | Real Wizard buffered and streaming attachment-overflow paths preserve typed safe details through projections and an existing error wrapper. Stale admission cannot masquerade as deterministic context overflow; Saga retention and zero-provider-send controls preserve retry/authority boundaries. |
| RV-089 | fixed `8e825457` | All four budget refusal paths name the configuration key and00:00UTC accounting boundary without promising readmission while prior-period outstanding/uncertain-paid reservations still count. Dated accounting tests verify the boundary. |

Independent review exposed supplementary Unicode Format scalars bypassing char-level sanitization. Rune-based repair preserves valid supplementary characters, malformed-surrogate normalization and64/128-byte bounds; fourteen explicit-output regressions include decoded OpenAI JSON. Initial and mutation runs each reproduced four failures with ten controls. Final restored1295 tests passed with zero skips and a zero-warning build; the reviewer repeated its actual-Core-DLL probe and93 focused tests successfully.

The acquisition correction changes only two stale enclosing-member strings from ImportCreateStagedAsync(4) to(5). Filesystem call arity3, exact negative database proof, scanner and closed count536 are unchanged. Existing guard RED, old-identity mutation RED, restored88 guard passes and zero-warning build support it. The earlier failed combined run remains recorded; a successful newer combined run is still required.

The reviewed, locally integrated count is **64 of272 (23.5%)**:3 Critical,50 Important,11 Minor. **208 remain open**:26 Important,182 Minor. Full final qualification and delivery remain outstanding.

### Campaign closure cost checkpoint — 2026-10-25

Independently approved RV-057 candidate `5e53796c4325b2fe96ed6358aba548029f453fa7` is integrated in `85331593d609d9ed13eeaf91d14115744b5af33e`.

| Finding | Outcome | Evidence |
| --- | --- | --- |
| RV-057 | fixed `fc2dd15d`, reviewed prerequisite merge `5e53796c` | Complete Campaign closure now hashes legacy-framed rows incrementally with bounded Unicode encoding and adds exact keys directly to the planner-owned set. Redundant snapshot key strings are removed after tracing all in-memory and persistence consumers. Core24 adds all four indexes required by the full closure predicate, with frozen Core23 identity and atomic rollback/retry. No row cap, historical provenance loss or authority relaxation. |

Initial behavioral RED exposed scan plans and 48,640,720 allocated bytes; the corrected 20,004-row fixture allocates 10,259,120 bytes. Deliberate regression reproduced four failures with five compatibility controls, followed by exact restored source checks. Affected verification passed 482 tests, catalog/package verification passed 150, and post-prerequisite verification passed 88; these filters overlap. The independent reviewer rebuilt and passed 52 focused checks, verified all reported log hashes, complete closure framing, snapshot consumers, index coverage, schema transitions and catalog shape. Builds have zero warnings/errors.

The reviewed, locally integrated count is **65 of 272 (23.9%)**: 3 Critical, 51 Important, 11 Minor. **207 remain open**: 25 Important and 182 Minor. The prior combined run remains failed as recorded; a newer combined run and all final qualification/delivery gates are still required. RV-056 manifest storage remains separately open.

### Connection and owner-recovery diagnostics checkpoint — 2026-10-25

Independently approved RV075 `b5aa51409ba278f88fc358204b8f6d1dc7d73954` and RV076 `4ea1dc02e3db05f2e978139ad80a0e50635bfe77` are integrated in runtime candidate `cd02c2223bb24066e9222d084ae07f1c7e8a7a65`.

| Finding | Outcome | Evidence |
| --- | --- | --- |
| RV-075 | fixed `b5aa5140` | Closed typed metadata preserves useful SQLite/runtime failure details without exception content. Genuine faults propagate through nine inspected consumers and real API/health requests without being called maintenance. Real started/unstarted API and v1 pipeline tests preserve response/cache behavior. Separate cancellation defects in health and native initialization are repaired. Worker480 passes/1 Windows-only skip; independent reviewer259 passes/1 Windows-only skip; filters overlap. Mutation16 regressions, exact restored source hashes and zero-warning build verified. |
| RV-076 | fixed `4ea1dc02` | Retry/cancel explain authenticated ownership and conditional same-operation recovery while preserving CAS and terminal outcomes. Manual reconcile adds a separate nullable Int64 count using one parameterized aggregate; null is unmeasured, read errors are visible. Generic execution discovery still excludes owner-bound rows before pagination. Worker and independent reviewer each passed280 tests without skips; mutation12 regressions, exact restoration and zero-warning build verified. Acquisition inventory now537. |

The reviewed, locally integrated count is **67 of272 (24.6%)**:3 Critical,53 Important,11 Minor. **205 remain open**:23 Important,182 Minor.

The prior65-finding candidate46535159 built with zero warnings and completed combined Arcanum verification with4641 passes,1 failure and14 skips. The failure was missing encryption-key state during the temporary test-host startup for LexiconInspectionQueryBudgetTests; an isolated rerun passed5 checks, so investigation remains open. That combined run is not green and Forge was not reached. Its logs and manifest remain preserved. Full final qualification and delivery are outstanding.

The user additionally requires full native FTS5 and real RAPTOR. These feature expansions are tracked separately from the original register; FTS5 will also resolve RV072 after implementation and independent review. No closure credit is taken for pending feature work.

### Erasure diagnostics and test-host isolation checkpoint — 2026-10-25

Independently approved RV059/RV060/RV070 and their additional privacy repairs are integrated through `1d8be9e3fb2208c85363c8d52afa5342af73cfb3` in local runtime candidate `727ea0e61c11da85075668ff5e8b317af5d4e57e`.

| Finding | Outcome | Evidence |
| --- | --- | --- |
| RV-059 | fixed | Erasure failure diagnostics preserve operation identity, fixed phase/reason and exception type without private exception content; fixed writer restart and park causes are observable. |
| RV-060 | fixed | Direct reset failures distinguish confirmed local/canonical effects and recovery state. Only verified rollback/reopen allows a no-erasure claim; uncertain outcomes remain explicit. |
| RV-070 | fixed | Real ordinary-work drain timeout in reset and factory reset preserves rows, records terminal failure, reopens admission and explains retry after work is idle. |

The original candidate passed690 affected tests; its independent review ran216 tests. Four additional direct-reset exception-log hazards were then repaired before integration, with5 actual fault REDs,5 mutation failures, byte-exact restoration,393 affected passes/no skips, a zero-warning solution build and scoped independent approval. These counts overlap and are not a unique-test total.

Separately, a delayed test-host startup defect was deterministically reproduced: constructing another factory changed process-global test paths before the earlier factory composed its host. The fixture now reapplies its own captured isolated profile before composition. Candidate `b9bf0dcd279ab5623dcf4998098e1579ed9c76a6` passed73 affected tests and45 independent-review tests with no skips; production encryption gates remain unchanged. This additional defect earns no original-register credit. A new combined run is still required; the earlier failed combined run is not retroactively green.

The independently reviewed, locally integrated count is **70 of272 (25.7%)**:3 Critical,56 Important,11 Minor. **202 remain open**:20 Important,182 Minor. Full native FTS5 remains in implementation/review. Real RAPTOR has an approved12stage plan with numerical fixture implementation started; no production RAPTOR completion is claimed. Final qualification and external delivery remain outstanding.

### Combined verification of the 70-finding candidate — 2026-10-25

Exact candidate `349d7e960c591fc63929e04b3d6d0c24dada9b8f` passed the combined affected verification: **5,153 Arcanum tests and116 Forge tests**, both builds with **zero warnings/errors**. Arcanum skipped5 tests:2 Windows-only filesystem checks,2 opt-in Long Rest scale probes,1 opt-in published native/model qualification. Forge skipped none. The repaired delayed test-host path and the reviewed erasure, connection and owner-recovery changes are included. The command, exact tree, terminal exit codes and log hashes are recorded in `/private/tmp/arcanum-integrated-wave6-manifest.json`; historical failed attempts remain preserved.

This is combined affected verification, not the final full-suite/CI/AOT/live qualification. The integrated finding count remains70/272; pending transcript, FTS5 and RAPTOR work earns no additional completion credit here.

### Native search, Covenant and transcript checkpoint — 2026-10-25

The independently reviewed, locally integrated total is **76 of272 (27.9%)**:3 Critical,59 Important,14 Minor. **196 remain open**:17 Important,179 Minor. Runtime candidate `cb2c4790e91cf8fc645e61cd5881c95d49db5175` includes these additions:

| Findings | Reviewed outcome | Evidence |
| --- | --- | --- |
| RV-072 | Full native FTS5 grammar in archive/session search, bound intact with typed and content-free validation propagated through API/MCP/CLI. |3c9cab59;640 affected passes, then197 and227 scoped passes after independently reviewed parser-stack and leading-star repairs; no skips, zero-warning builds, exact mutation/restoration evidence. Counts overlap. |
| RV-050, RV-051 | Visible Covenant refusal/absence/feature diagnostics and truthful partial eligibility evaluation without protected metadata hydration. |b864bf89;146 affected Arcanum and198 full Compendium passes, zero-warning solution build, behavioral and mutationRED, independent approval. |
| RV-175, RV-176, RV-184 | Persisted tool markers respect roles; wrapping preserves cell boundaries and whole decorated whitespace graphemes; closed UI channels preserve final state without shutdown exceptions. |d9265cc0;26 original/mutation failures,987 restored passes/4Windows skips; review found and repaired Unicode case with6RED/1control,101 scoped passes and3000 independent generated checks. |

Two additions earn no original-register credit. The exact missing Lexicon projection/caller/snapshot catalog entries are repaired in revieweda5b34dec, with2RED/78controls and141 restored passes; enumeration remains closed. Real RAPTOR stage1 of12 is integrated through65eb378a: a pinned reproducible numerical reference environment, independent fixtures, algorithm fingerprint and seed identities. The existing production builder has not yet switched; neighbor-graph implementation is stage2. Full RAPTOR remains unfinished.

An extra RAPTOR full-project test attempt was cancelled and is not qualified. It exposed the now-repaired Lexicon catalog entries, an owned documentation guard failure repaired before stage1 approval, and an extraction contention fixture timeout now under deterministic diagnosis. No failed or cancelled attempt is treated as green.

The latest passing combined verification still binds to the prior70-finding candidate349d7e96:5153 Arcanum passes/5 disclosed skips and116 Forge passes, zero-warning builds. These newer integrations require their next combined run. Full final qualification and external delivery remain outstanding.

### Combined verification of the 76-finding checkpoint — 2026-10-25

Candidate `896cb98d4eecd9e623470eced5c5f923cd22b207` passed the locked solution restore, a solution build with zero warnings and errors, 2,976 affected Arcanum tests (four Windows-only skips), all 198 Compendium tests, and 79 affected Forge tests. The RAPTOR foundation passed both house blankline checks and scoped whitespace verification; the worktree remained clean. Exact commands, log hashes and candidate tree are in `/private/tmp/arcanum-integrated-wave7b-manifest.json`. These are scoped integration checks; full final qualification remains outstanding.

The first wave7 attempt stopped before tests because three projects had no restored assets in this worktree. Its failed setup log is retained separately; locked restore resolved that prerequisite without package or lock-file changes.

### Patch diagnosis and RAPTOR neighbor checkpoint — 2026-10-25

The independently reviewed and integrated total is **77 of 272 (28.3%)**: 3 Critical, 60 Important, and 14 Minor. **195 remain open**: 16 Important and 179 Minor. RV-019 is fixed in `ad6a8294`: patch preflight retains parser diagnoses and repair guidance, preserves refusal in both Sanctum modes, and avoids an invented policy breach. The actual baseline and mutation each fail 11 behavioral cases with one policy control; the restored affected suite passes 731 tests with one Windows-only skip. Independent review approved the change and freshly passed 59 focused cases. Build and scoped style checks passed. Integrated runtime candidate: `58a1b68036c22d967e2d9541513de56dda673892`.

Real RAPTOR has completed **2 of 12 stages**, separately from the original finding count. The neighbor graph uses bounded exact cosine search for small inputs and actual RP-tree-initialized NN-descent for larger inputs. Independent review approved `ab055c4b`; a one-phrase documentation correction in `b28e57f0` was verified before integration. Its 383 focused checks passed. Independent exact-neighbor recall is at least 0.984375 on the final representative fixtures, including 1.0 on the previously failing 4,096-point duplicate-heavy corpus. These measurements are not a universal accuracy guarantee. The production builder remains unchanged until later stages; UMAP implementation is now in progress.

The independently reviewed extraction clock repair `65a919c9` is also integrated. It fixes fixture ordering and supplies a System-default shared monotonic clock without weakening source analysis. The recent FTS5/RAPTOR source-catalog omissions are repaired by the exact tracked-file closure. Two pre-existing attachment stream-disposal effect-frontier diagnostics were reproduced on the prior code and remain a required, separately assigned repair. Neither gate failure was waived.

Combined wave8 verification passed **945 tests with no skips** and a zero-warning build at `8bb82926`, including the repaired catalog and integrated numerical/clock behavior. This run precedes the RV-019 merge; its exact commands and log hashes are in `/private/tmp/arcanum-integrated-wave8-manifest.json`. Earlier wave7b evidence remains bound to its broader preceding candidate. Full final qualification and external delivery remain outstanding.

### Shared provenance and actual UMAP checkpoint — 2026-10-25

The independently reviewed and integrated total is **78 of 272 (28.7%)**: 3 Critical, 61 Important and 14 Minor. **194 remain open**: 15 Important and 179 Minor. RV-056 is fixed by `1a06604f` and integrated in `d880bac5813d7c87bef0ec55ace4e2aa3a9c354e`. Campaign rollups now share immutable provenance segments instead of copying prior history into each summary. A 600-Session probe retains 600 shared source rows versus 180,300 previously, while authoritative validation remains proportional to history. Legacy data and the frozen Core24 fingerprint are preserved; the current schema is Core25. Independent review found and then approved the repair for joint retention planning (9 physical rows planned/deleted, formerly 5 planned) and restored current-format closure coverage. The repaired candidate passed 586 tests and meaningful mutation controls.

Real RAPTOR has completed **3 of 12 stages**, separately from original review findings. Actual smooth-kNN calibration, sparse fuzzy union and Euclidean UMAP optimization in `81c31079` passed independent review with no findings. Its 412 scoped tests, six deliberate mutations, independent numerical fixtures and sparse-allocation controls passed. Full-covariance GMM is now in progress. Production Tapestry has not switched; recursive composition, DAG persistence, retrieval, lifecycle and final qualification remain.

The separately discovered attachment cleanup analyzer defect is fixed and independently approved in `8bdb9419`. Explicit framework async disposal now follows the real inner stream's provenance and lifetime without adding runtime write authority. The bounded production attachment root reports zero diagnostics. Full global capsule reconciliation is still pending; the known 99-to-98 root-row change is not hidden by a partial manifest rewrite. Whole-file formatter debt of 286 unchanged baseline diagnostics is disclosed, not claimed green.

Combined wave9 verification at exact `d880bac5` passed **1,202 test executions representing 1,196 distinct cases, with no failures or skips**, and a zero-warning build. It covers Campaign/schema/retention, RAPTOR numerical/catalog/EF, cleanup authority and the real attachment root. The full tracked input union is 2,439 entries / 801 SQL. The committed tree exactly matches the frozen tested tree; commands and hashes are in `/private/tmp/arcanum-integrated-wave9-manifest.json`. PII guardrail and research evidence/completion candidates remain under implementation or review and are not counted closed here. Full final qualification and external delivery remain outstanding.

### PII guardrails and actual Gaussian mixture checkpoint — 2026-10-25

The independently reviewed and integrated total is **79 of 272 (29.0%)**: 3 Critical, 62 Important and 14 Minor. **193 remain open**: 14 Important and 179 Minor. RV-011 is fixed by `259f5b3e` and integrated in `ebcc9e93`. Input PII checks follow the actual user turn, cover text content parts, use bounded card/phone/SSN rules, and preserve category-specific safe refusals through native and OpenAI-compatible responses. Both review rounds approved the result; the sole follow-up supplied the missing explicit formatter evidence without changing source bytes. The candidate passed 870 affected Arcanum and 33 Compendium tests; 42 final wire checks overlap that evidence.

Real RAPTOR has completed **4 of 12 stages**. Full-covariance Gaussian mixture EM, normalized soft posteriors, exclusive BIC selection and bounded blockwise fitting in `12d4f940` passed independent review. Supplied numerical evidence includes independent correlated-Gaussian and EM oracles, five killed behavioral mutations with exact restoration, and 390 affected passing tests. No hard-clustering fallback replaces failed fits. The production builder has not switched; global/local composition, DAG storage, recursive summaries, retrieval, lifecycle and final qualification remain.

Combined integration `afa9ff47746e0ef025d9ceb7888250f25d33b86a`, tree `49d6f59aafec225ef31e69dd2195116faa04cc25`, passed a normal build with zero warnings/errors and **613 distinct tests with no failures or skips**: 450 numerical/Tapestry/catalog/EF cases and 163 guardrail/configuration/wire cases. The full input catalog contains 2,441 entries / 801 SQL. The tested tree and committed tree are identical; reviewed implementation blobs are preserved. Commands and hashes are recorded in `/private/tmp/arcanum-integrated-wave10-manifest.json`.

The research evidence/completion candidate remains open: independent review approved RV-022's behavior but found an RV-021 hostname diagnostic crash on validly parsed malformed IDNs, plus a raw scoped-IPv6 diagnostic privacy edge. A separate implementer is repairing those boundaries with mapped-endpoint regressions. Campaign and Long Rest maintenance diagnostics are also in progress. No additional closure credit or full final qualification is claimed for those pending changes.

### Research evidence and completion checkpoint — 2026-10-25

The independently reviewed and integrated total is **81 of 272 (29.8%)**: 3 Critical, 64 Important and 14 Minor. **191 remain open**: 12 Important and 179 Minor. RV-021 and RV-022 are fixed by `3759e47c` and integrated in `be54928238eea987141b696f99934937b67a9f3f`. Research cites only source bodies actually retained for synthesis, reports missing evidence, exposes length-cutoff warnings and the actual successful lease model when known, and documents the scope of cost accounting. An independent review caught a malformed-IDN hostname crash and raw scoped-IPv6 diagnostic privacy edge; a separate implementer repaired those with mapped-endpoint tests. The scoped re-review approved all findings with no new breakage. Its mutation failed six cases while the valid-IDN control passed; exact restoration and 188 affected passing tests support the repair.

Combined wave11 at tree `aa0c9bb3ee50232c189c2f6cf614ea3d09df34ad` passed a zero-warning build and **743 distinct tests with no failures or skips**: research 188, actual fallback/TurnEngine/projection 96, PII/native/v1/configuration/public wire 406, and catalog/EF/CLI boundaries 53. The tested and committed trees match. The 2,441-entry / 801-SQL catalog is unchanged. Commands, exact test identities and hashes are in `/private/tmp/arcanum-integrated-wave11-manifest.json`.

Campaign deferral diagnostics (`a7ce733f`) are in independent review after 415 affected passing tests and three killed mutations; they are not counted here. Long Rest diagnostic remediation and production streaming progress are underway. Actual RAPTOR remains at four reviewed/integrated stages out of twelve, with global/local soft composition and finite recursive planning in progress. Full final qualification and external delivery remain outstanding.

### Campaign deferral and Long Rest diagnostics checkpoint — 2026-10-25

The independently reviewed and integrated total is **83 of 272 (30.5%)**: 3 Critical, 66 Important and 14 Minor. **189 remain open**: 10 Important and 179 Minor. RV-007 is fixed by `a7ce733f` and integrated in `64c23457e960d92a6572fd5737912d53102e5e7f`; RV-012 is fixed by `84c711b6` and integrated in `85e1344db90e4e36c9324e0c86c2dff31b882fb7`. Both have independent approval with no open findings.

Campaign maintenance deferrals now retain a safe, actionable reason without losing contribution work or changing admission authority. Combined wave12 passed a zero-warning build and **449 distinct affected tests with no failures or skips**. The reviewed branch's 415 passing cases and three killed mutations are supporting, overlapping evidence.

Long Rest failures use structured HTTP status and fixed remedies, safe provider/model/operation correlation, an explicit credential-unavailable HTTP 409 / NeedsOperator outcome, mandatory diagnostic loggers, and visible settlement/release/heartbeat failures while preserving compensation, cancellation and paid-call recovery. Combined wave13 at exact tree `b001452973ce45de0a47bfca678cdf94cfde777d` passed a zero-warning build and **1,492 affected test executions representing 1,491 distinct cases, with no failures or skips**: Long Rest 1,014, current Campaign/doctor/configuration/API construction 444, and catalog/EF 34. All 12 existing owned-transport guard mutation controls passed. The committed tree equals the frozen tested tree; commands, TRX identities and hashes are in `/private/tmp/arcanum-integrated-wave13-manifest.json`.

The separate global nullable-default inventory remains **non-green**: one failing test reports four numerical observer defaults and the Covenant feature-publisher logger, all in source files byte-identical to the premerge controller. RAPTOR stage 5 fix round 1 is active; the separate mandatory publisher-logger repair has fix-round-1 approval after isolating its production-composition test home and awaits integration. These additions earn no original-finding credit. Actual RAPTOR remains four of twelve reviewed/integrated stages, with no production builder switch. The source catalog remains 2,441 entries / 801 SQL. Production streaming progress, full final qualification and external delivery remain outstanding.

### Soft composition and complete nullable-guard checkpoint — 2026-10-25

Actual RAPTOR has **5 of 12 stages independently reviewed and integrated**. Approved stage5 `cda8a370` is integrated in `a8b4cb3466fee54d2aa0792697a0aea72905c2d1`, with global/local soft memberships and a pure bounded request planner. Fix-round-1 review approved preservation of fitting and oversized proper siblings beside a full-size partitioned cluster, exact original-vector subset coverage, and deduplicated observer inventory entries. Production Tapestry has not switched.

Additional required Covenant publisher logging is integrated in `5a2cc2e`, including the independently repaired isolated composition fixture. Combined wave15 at exact tree `aeb77784363a6740024b4cf7ed1917796b438cfd` passed a zero-warning build and **568 distinct affected tests with no failures or skips**:428 Tapestry/planner/catalog and140 nullable/EF/acquisition cases. The complete nullable-default inventory and stale-entry guard now pass without exclusions. Earlier inventory failures remain historical failures of their earlier trees. The independently regenerated full catalog contains 2,445 entries / 801 SQL; commands, identities and hashes are in `/private/tmp/arcanum-integrated-wave15-manifest.json`.

Stage6 worker `raptor_dag_schema` is executing on `codex/raptor-dag-schema` from `a8b4cb34`, with **Core26 reserved** and **Core25/RV056 frozen as its predecessor**. Approved compatibility rulings permit whole-generation cleanup of legacy corrupt derived data and retain owner-first reset behavior. Stage6 receives no completion credit yet. Later production integration must still treat an Incomplete/WorkLimit plan as refusal, preserve the prior valid generation and back off before provider effects.

The original register remains **83 integrated: 3 Critical, 66 Important, 14 Minor; 189 open: 10 Important, 179 Minor**. RV033 is executing a no-schema, snapshot-safe Saga ranking evidence cache on `codex/review-saga-ranking-evidence` from `85e1344d`; its worker reports 40 TDD GREEN cases, with no independent approval or integration. RV026 remains an uncommitted root implementation with 42 focused GREEN cases and killed mutation groups 3 + 1 + 1 + 8; its held-test strengthening final rerun is pending. Neither earns finding credit. Full final qualification and external delivery remain outstanding.


### 2026-10-26 checkpoint — 85 original findings integrated

The independently reviewed and integrated total is **85 of 272 (31.3%)**: 3 Critical, 68 Important and 14 Minor. **187 remain open**: 8 Important and 179 Minor. All original Critical findings are integrated; the remaining Minor findings remain in scope. No main merge, push or external delivery has occurred.

RV-026 is integrated in `b45e433fc608c1c5f04f33ba6ade7354c0fdd2cb` after independent fix-round-1 approval. Actual public-reader consumption and successful native stream flush govern protected-status acknowledgement across both transport queues. Abandonment, cancellation and disconnected replay drains cannot falsely acknowledge delivery. The frozen committed tree `55d808c150369df1357078f87fcd06cac34842bf` passed a zero-warning build and **804 distinct affected tests, no failures or skips**. Both five-second held delivery barriers passed; commands and hashes are in `/private/tmp/arcanum-integrated-wave16-manifest.json`.

RV-033 is integrated in `a038a3cd5334904adbc8e8e472f3eeafd337b910` after independent approval with no findings. Immutable Saga ranking evidence is reused only for a validated SQLite snapshot and a valid time interval. The frozen committed tree `87a0f123f64a66df262550f5e1b83fc07db9a797` passed a zero-warning build and **259 distinct affected tests, no failures or skips**, including all three real WAL race boundaries, pooled native-handle reuse, rollback/cancellation and temporal validity. Commands and hashes are in `/private/tmp/arcanum-integrated-wave17-manifest.json`. Earlier cancelled broad runs are not counted as successful qualification; four unchanged old schema-version assertions are being corrected in RAPTOR stage6.

Full native FTS5 remains integrated. Actual RAPTOR remains **5 of 12 stages independently reviewed and integrated**. Stage6 candidate `f6b57fac` is in independent review, with atomic Core26 DAG evolution, immutable publication guards, legacy compatibility and owner-first cleanup. Its supplied evidence includes 1,601 runtime/schema passes plus the separately completed mandatory catalog closure: 12 exact manifest and59 controlled qualification cases. These remain candidate evidence, not integration credit. Production Tapestry has not switched.

RV-053 preflight candidate `16d96218` is in independent review after295 affected passes and deliberate mutation proof; RV-052 token lifetime and recovery guidance is under implementation after genuine SQLCipher and CLI RED tests. RV-054 and related RV-200/RV-201/RV-215 schema progress/scheduling findings are being inspected together. The current integrated catalog remains2,445 entries/801SQL; pending candidates require a union when integrated. Full final suites, hosted discovery/capsules, coverage, Native AOT/shipping/native provenance and live qualification remain outstanding.


### 2026-10-26 checkpoint — 87 original findings and six RAPTOR stages integrated

The independently reviewed and integrated original total is **87 of 272 (32.0%)**: 3 Critical, 70 Important and14 Minor. **185 remain open**: 6 Important and179 Minor. All original Critical findings are integrated. The remaining Important findings are RV-030, RV-054, RV-068, RV-071, RV-078 and RV-079; every remaining Minor finding remains in scope. Main and the original Claude review branch remain unchanged.

RV-053 is integrated in `86fa30d5f0b045a77a465d88ef3e8373f2992898`, after independent review caught and the implementer repaired a mixed-snapshot preflight race. Effect, correction target, lifecycle, scope quota and Section occupancy now share one deferred SQLite snapshot; real WAL writers can still commit concurrently and commit-time authority/capacity checks remain intact. The exact frozen tree `1453e65ff6daef20500150e80c65a9ed22db498f` passed **380 distinct tests, zero failures/skips**, and a zero-warning build. Evidence is in `/private/tmp/arcanum-integrated-wave18-manifest.json`.

Actual RAPTOR is now **6 of12 stages independently reviewed and integrated**. Stage6 is integrated in `dddfbfd6a0bc499b738b31c845f87eb15a9f703a`, tree `20d0f9f9cb052bbc33132599b7e3478d68a33a98`. Re-review approved guards against scope-unique REPLACE deleting a published generation, rejection of dangling legacy-parent deletion, and atomic cleanup of readable optional vector mirrors. Valid graph/mirror bytes survive; unsupported vec0 mirrors remain unopened and unreachable through managed-only retrieval. Fresh/evolved parity, all23 interruption phases, frozen Core24/25 fingerprints and older-reader refusal are covered. The combined current schema/runtime/preflight/nullable/catalog checks and controlled consumers passed **2,027 distinct tests, zero failures/skips**, with a zero-warning build. The four formerly stale schema assertions are corrected and explicitly passed here; their earlier failures remain historical evidence, not retroactively green. Evidence is in `/private/tmp/arcanum-integrated-wave19-manifest.json`.

RV-052 is integrated in `c46b014393c240c110c4d858e032ccff21d0572d`, tree `7197983d4a3f99707a5a021af466171c99b1fbfc`. Review refusals now identify safe decision ordinals/authenticated events and refresh guidance; page and complete-operation deadlines are truthful without weakening lifetime, process-key, authority or marker fences. The earlier-expiring second-observation regression and diagnosed switch indentation were both completed and independently approved. The exact integrated tree passed **365 distinct tests, zero failures/skips**, a zero-warning build, both house modes and explicit Roslyn verification for every owned C# file. Evidence is in `/private/tmp/arcanum-integrated-wave20-manifest.json`.

Full native FTS5 remains integrated. Production Tapestry still uses its existing builder: stage7 revision-safe source invalidation and graph persistence is executing separately on `codex/raptor-source-revisions`, with Core27 reserved and Core26 frozen at `9B9BE8D38636742B481D8C4A35BC1C84262AC4DA99373652B22FBB44517462A9`. Recursive RAPTOR construction, DAG retrieval and final lifecycle/qualification remain. The exact integrated input catalog is2,484 entries/839 SQL with its sole optional epoch and digest `3bead246273cbd663503032e878b553837782032c783528e61820ebe72be918b`.

RV-054/RV-200/RV-201/RV-215 schema progress, scheduling and read-only diagnostics are in final focused qualification, with no finding credit yet. RV-079 startup-owner refusal diagnostics are under TDD implementation. Full final suites, hosted/global capsule reconciliation, coverage, Native AOT/shipping/native provenance and live qualification remain outstanding. No external delivery or installation mutation has occurred.


### 2026-10-26 checkpoint — 88 original findings integrated

The independently reviewed and integrated total is **88 of 272 (32.4%)**: 3 Critical, 71 Important and 14 Minor. **184 remain**: 5 Important and 179 Minor. Remaining Important findings are RV-030, RV-054, RV-068, RV-071 and RV-078; all remaining Minor findings remain in scope. Main and the original Claude review branch remain unchanged.

RV-079 is integrated in `160415e45ba5ef3d24378b1e3a413f9e360168b5`, exact tested tree `a83f36f100ce14d4fa918cea3579b52c3713f85f`. Startup recovery now retains safe closed refusal reasons, validated nonempty operation identity and bounded storage diagnostics, with one useful bootstrap warning and offline guidance. Independent review found that WrongOperation was unreachable through the existing strict decoder; the fix preserves that closed cause through the same validation and source-generated decode without relaxing ownership proof or accepting new payloads. Both stored launch kinds and both mismatch forms are covered. Scoped re-review approved the fix. The combined tree passed **410 distinct tests, zero failures/skips**, and a zero-warning solution build. All ten C# blobs match the reviewed candidate exactly. Default house and Roslyn verification remain valid for those identical bytes; three existing files retain explicitly mapped compact-delimiter formatting debt with no new violations, so compact formatting is not claimed clean. Commands, hashes and terminal evidence are in `/private/tmp/arcanum-integrated-wave21-manifest.json`.

The RV-054/RV-200/RV-201/RV-215 candidate is frozen at `a70fb346d4016acbb20960026261da74e5847f65` after **517 fresh affected passes**, zero failures/skips, a zero-warning build and clean all-owned formatting. Its convergence amendment adds safe durable failure recording and conditional journal identity/revision writes. Independent review then found an existing installer warning still attaching raw exceptions on the optional-tier convergence path. A separate implementer is fixing that end-to-end diagnostic gap in round1; these four findings receive no integration credit yet.

RV-071 retention recovery diagnostics is under TDD implementation after genuine refusal/quarantine/reconciler regressions; it is not yet reviewed. Actual RAPTOR remains **6 of12 stages integrated**. Stage7 source revisions, atomic graph checkpoints and query embedding provenance are in progress, including model/width refusal and source edit/revert protection. Production still uses the existing spherical builder until the planned recursive RAPTOR stage. Full native FTS5 remains integrated; the separate remaining RV-030 persisted-memory substring search performance work has been investigated and will follow the frozen Core27 schema. Final full suites, hosted discovery/capsules, coverage, Native AOT/shipping/native provenance and live qualification remain outstanding.


### 2026-10-26 checkpoint — 93 original findings integrated

**93 of 272 findings (34.2%) are independently reviewed and integrated:** 3 Critical, 73 Important and17 Minor. **179 remain:** 3 Important and176 Minor. Remaining Important findings are RV-030, RV-068 and RV-078. All remaining Minor findings stay in scope. Main and the original Claude review branch remain unchanged.

RV-071 retention recovery diagnostics is integrated in `df3ba915e1820509e3cff35401786342d40e6266`, tested tree `291819e417eb68f135067e31034676c1eb32afb6`. Real SQLCipher checkpoints, filesystem quarantines and reconciliation tests establish safe closed causes without raw exception/payload logging, with existing cancellation, mutation authority and parked-state behavior preserved. Independent review found no issue. The combined change passed **594 distinct tests, zero failures/skips**, and a zero-warning solution build. Both C# files match the reviewed candidate exactly; the documentation conflict was resolved by retaining the newer Covenant startup remedy alongside the new ordinary-retention remedy. Evidence: `/private/tmp/arcanum-integrated-wave22-manifest.json`.

RV-054, RV-200, RV-201 and RV-215 are integrated in `9daa2f772811dfe7271b740fa39cdb597f32fd9c`, tested tree `632663e86a0563a7ef50aff1952392b8ec65a4c1`. Schema convergence distinguishes committed progress, pending work and deferred failure; durable failures are recorded only against the matching journal identity/revision. Memory status refuses to present an unmeasured census as an empty corpus, and offline doctor reports pending transition facts with useful guidance. The review found raw installer exceptions still entering convergence logs; separate fix-round implementation covers optional-tier DDL plus adjacent Lexicon and embedding probes with real SQLCipher canary tests. Scoped re-review approved the fix with no remaining findings.

The combined schema tree passed **1,285 distinct tests, zero failures/skips**: 1,103 schema/status/doctor and guard cases,59 controlled catalog-consumer cases, and123 current Tapestry/Long Rest compatibility cases. Uniqueness is measured by stable TRX test IDs; four long theory display names are truncated duplicates. Normal solution build reports zero warnings/errors; both house formatting modes, Roslyn verification and diff checks pass. All C# source/test files match the reviewed task except the installer retaining the previously approved Core26 optional-mirror guard and the benchmark manifest's recomputed union digest. The complete input catalog has2,486 entries,839 SQL entries andone optional input; digest `4f8ee18bed8d7a7f40043e3e61a860f29d60057fefa376a23e0425d06e724e61`. Evidence: `/private/tmp/arcanum-integrated-wave23-manifest.json`.

RV-068 factory-reset inventory diagnosis and RV-078 Covenant erasure inventory diagnosis are under TDD implementation. RV-078 regressions also exposed cleanup overriding caller cancellation and malformed numeric provenance/sensitivity data being accepted; those fixes require review before credit. Root has begun RV-091/RV-093/RV-095/RV-096 request-validation regressions.

Actual RAPTOR remains **6 of12 stages integrated**. Stage7 owns Core27 source revisions, atomic graph publication and actual query embedding provenance; it is also checking same-transaction physical graph cleanup in current session/attachment erasure owners. Those changes are not yet independently reviewed or integrated. Production still uses the existing spherical builder until the planned recursive RAPTOR stage. Full native FTS5 is integrated; RV-030 persisted-memory substring search performance is separate and awaits the frozen Core27 baseline. Final whole-suite, hosted/capsule, coverage, Native AOT/shipping/native provenance and live qualification remain outstanding.


### 2026-10-26 checkpoint — 94 original findings integrated

**94 of 272 findings (34.6%) are independently reviewed and integrated:** 3 Critical,74 Important and17 Minor. **178 remain:** 2 Important (RV-030 and RV-078) and176 Minor. All findings and concretely discovered adjacent defects remain in scope.

RV-068 factory-reset inventory diagnosis is integrated in `b4c7f7015e793fb98fece0fe421e0da73d643e1e`, exact tested tree `bf464ee6f2020e4e8dc12aada0ccfc3770dae590`. Managed-tree inventory refusals carry a closed root role/cause and a bounded, escaped relative entry only when containment is established. Public responses use `Data.ManagedTreeRefused`; warnings carry safe operation identity and no raw paths/exceptions. Existing stopped-host recovery and reconciliation rules continue to treat uncertain effects as requiring recovery. Independent review approved the source. A historical test-results overwrite warning is retained in the evidence; this integration uses unique result paths and produced clean output.

The combined tree passed **1,121 distinct tests, zero failures/skips** (1,044 affected cases and77 catalog/EF/nullable guards), a zero-warning normal solution build, both house formatting modes, explicit Roslyn verification and diff checks. Twelve of13C# files match the reviewed candidate byte for byte; the remaining ErrorCodes file also retains the already integrated schema diagnostics. The source catalog is unchanged at2,486 entries/839SQL/one optional input. Evidence: `/private/tmp/arcanum-integrated-wave24-manifest.json`.

The five-finding request-validation candidate (RV-084/091/093/095/096) passed1,027tests, but review identified missing/blank OpenAI image URLs being dropped by the mapper before validation. That concrete gap will be fixed and independently re-reviewed before any of those findings receives integrated credit. RV-078 re-review preparation also found malformed revisions on three adjacent ownership readers could permit protected-file deletion; real SQLCipher/filesystem tests reproduce those defects and implementation is underway.

Full native FTS5 remains integrated; persisted-memory substring performance RV-030 is a separate open finding. Actual RAPTOR remains **6 of12 stages integrated**. Stage7 review found repeated full-scope snapshot scanning/sorting; shipping SQLCipher regressions now reproduce it for all three source kinds, and indexed continuation is being implemented. Remaining RAPTOR builder/retrieval/config/lifecycle stages and final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and live qualification remain outstanding. Main and the original Claude review branch remain unchanged.


### 2026-10-26 checkpoint — RAPTOR stage 7 integrated

Original findings remain **94 of272 integrated (34.6%)**, with178 remaining. Eight more original findings have independently approved candidates awaiting combined integration: RV-078,084,091,093,095,096,103 and104. Ten further attachment/turn-lifecycle findings are in TDD implementation.

Actual RAPTOR is **7 of12 stages integrated**. Commit `099f50f4362cc6afeef070e8cfb62dce643b1602`, exact tested tree `08c8adf585c0d7943d117cb15084b4aa92d15aa4`, integrates Core27 source revisions, atomic checked graph publication, actual query embedding provenance, physical graph erasure under current session/attachment owners and indexed source snapshot continuation. Independent review found quadratic snapshot paging; the fix has shipping-SQLCipher query-plan/VM regressions across all three source kinds. The production recursive RAPTOR builder is now executing as stage8; production remains spherical until that integration. DAG retrieval, invocation/lifecycle and final qualification follow.

The combined tree passed **3,234 distinct tests, zero failures/skips**, plus a normal zero-warning solution build using the updated .NET installation. The prior run lost its runtime during the user SDK update and never produced its required result file; it is explicitly invalidated. A fresh probe reproduced three genuine catalog/factory-census integration failures, repaired with exact inventory547 and independent physical revision counts, before the full passing rerun. Existing house-formatting debt in two files and120 Roslyn diagnostics on unchanged baseline lines were verified as inherited; aggregate formatting is not called clean. Evidence: `/private/tmp/arcanum-integrated-wave25r1-qualification.json` and its commit receipt.

The exact source catalog is2,518 entries/866SQL/one optional input, digest `08a98a8b356c4922cdfcb53daa8c4192d0d24abd9b385ba6fe4b7bca62c5b118`. Core24/25/26 historical fingerprints remain frozen. Full native FTS5 is integrated; RV-030 remains open. Its isolated native spike supports capped trigram candidates plus ordered continuation and exposed tied-key rescans, native/managed sort differences and an INSERT OR IGNORE trigger hazard. Production tests, migration/erasure coverage and review still remain. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and live qualification are pending; no main merge or push.


### 2026-10-26 checkpoint — 95 original findings integrated

**95 of 272 findings (34.9%) are independently reviewed and integrated:** 3 Critical, 75 Important and17 Minor. **177 remain:** RV-030 is the last Important finding; all176 remaining Minor findings stay in scope. Twelve original findings have approved candidates awaiting integration: RV-084/091/093/095/096/098/103/104/106/112/115/131. RV-131 was independently confirmed as the same provider-guidance defect covered by RV-096, including its distinct429/503 scenario; neither receives credit until its candidate integrates.

RV-078 is integrated in `645fd06ee1294153d75d7e2c3518e11e1d844f3f`, exact tested tree `fb0e59755f28eba5f67c5e40617a41a1c8296fa2`. Inventory refusals preserve validated artifact identity and closed safe causes, distinguish numeric SQLite storage categories, and exclude private payloads/paths/exception text. The reviewed fix also refuses malformed raw SQLite ownership revisions before a protected-file effect on request, Campaign-root and resumed-work paths. Authority, durable disposition and recovery rules remain intact.

The combined source passed **1,855 distinct tests, zero failures/skips**, a zero-warning normal build, both house formatting modes, explicit Roslyn verification and diff checks. All production C# matches approved `301dddc5` byte for byte. Seven integrated maintenance tests reproduced old warning expectations; only their exact diagnostic assertions changed, retaining independent operation/phase, row/file, journal, gate and recovery checks and adding private fault-text exclusion. All72 maintenance-admission cases now pass. A temporary-directory cleanup warning during the failed probe did not recur in the passing terminal run. Evidence: `/private/tmp/arcanum-integrated-wave26-qualification.json` and commit receipt.

Actual RAPTOR remains **7 of12 stages integrated**, with the production builder under TDD. Full native FTS5 grammar is integrated. RV-030 now has four real encrypted-HTTP continuation failures against unchanged production and is implementing the measured native search design under its exclusive Core28 reservation. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and live qualification remain pending. Main and the original Claude branch remain preserved.


### 2026-10-26 checkpoint — 101 original findings integrated

**101 of 272 findings (37.1%) are independently reviewed and integrated:** 3 Critical,75 Important and23 Minor. **171 remain:** 1 Important (RV-030) and170 Minor. Twelve further original findings have approved candidates awaiting integration: RV-082/083/085/086/094/098/099/103/104/106/112/115. Five web-workflow findings and RV-030 are executing; actual RAPTOR remains7 of12 stages integrated with its production builder in progress.

RV-084/091/093/095/096 and the independently verified duplicate RV-131 are integrated in `4f06f20b2dffacccf61700b8fb4190bc51740dab`, exact tested tree `6e4c1c850ebcce866c6879ed83351e9451c7fa83`. Explicit client-tool intent is validated with native field indices; empty reset scope is refused; typed maintenance/manual-erasure/server failures retain their proper status; native and raw OpenAI image shape is checked before mapping can drop parts; provider failure guidance matches its known status category. RV-131's distinct429/503 scenario is covered by the same status-specific tests and separately reviewed evidence mapping.

The combined tree passed **1,291 distinct tests, zero failures/skips**, a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Every changed C# file matches independently approved `f78d78d5` exactly. Current72-case maintenance behavior and the raw live/batch image cases remain in the qualified union. Evidence: `/private/tmp/arcanum-integrated-wave27-qualification.json`, commit receipt and `/private/tmp/arcanum-rv131-review.md`.

Full native FTS5 grammar stays integrated. Core27 is frozen; RV-030 owns Core28. The next integrations are explicit OpenAI parameter handling, attachment diagnostics and turn-commit outcomes. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and live qualification remain outstanding. No main merge or push.


### 2026-10-26 checkpoint — 103 original findings integrated

**103 of 272 findings (37.9%) are independently reviewed and integrated:** 3 Critical,75 Important and25 Minor. **169 remain:** RV-030 and168 Minor findings. Ten more original findings are approved awaiting integration: RV-082/083/085/086/094/098/099/106/112/115. Six original findings are implementing in the separate search and web-workflow branches. Actual RAPTOR remains7 of12 integrated, with production builder stage8 executing.

RV-103/104 are integrated in `03a3ec0eeec0cf3e73ac877e95932b452521d148`, exact tested tree `686dd56280c830443f85e4b25b44c4ae288ce2df`. Unsupported log-probability requests are explicitly refused before buffered, streaming or batch inference; omitted/false remains valid. Explicit embedding dimensions must match the configured width, and the actual returned vector width is checked before serialization. No vector slicing was introduced. Models documentation now describes the actual flat wire fields.

The combined candidate passed **442 distinct tests, zero failures/skips**, normal zero-warning build, both house formatting checks, explicit Roslyn verification and diff checks. The raw validator retains the separately approved pre-mapping image validation; all other C# files match the approved OpenAI candidate. The documentation conflict retained both contracts. Source paths/catalog are unchanged, so the prior controlled catalog evidence was retained without a redundant rerun. Evidence: `/private/tmp/arcanum-integrated-wave28-qualification.json` and commit receipt.

Full native FTS5 remains integrated; the separate literal-memory performance/continuation implementation owns Core28. RAPTOR retrieval will respect trustworthy source/event recency for semantic ties, without treating summary build time as fact recency or altering the numerical kernel's stable-ID ordering. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and live qualification remain pending; main and the original review branch are preserved.


### 2026-10-26 checkpoint — 107 original findings integrated

**107 of 272 findings (39.3%) are independently reviewed and integrated:** 3 Critical, 75 Important and 29 Minor. **165 remain:** 1 Important and 164 Minor. 6 further original findings have approved candidates awaiting integration: RV-082, RV-083, RV-085, RV-086, RV-094, RV-099.

RV-098/106/112/115 are integrated. Attachment validation separates declared caller refusals from safe server failures; extraction markers preserve actionable closed causes; refresh integrity failures are distinguished from storage limits; selectors, tool names and truncation guidance identify a useful next step. The separately reproduced unadvertised-tool defect is repaired: a guessed attachment tool name cannot initiate lookup, refresh or content injection.

The integration preserves the previously qualified actual-embedding provenance guard in Wizard; every other changed C# file matches the approved attachment candidate. Complete attachment/storage/tool/Wizard classes, projection controls and current native/OpenAI request-validation and batch cases passed together. The independent review approved the candidate and verified its seven causal mutations and all source/evidence seals.

The exact runtime commit is `1365e859978a889fe13db99caec091db7eccc30c`, tested tree `cec97e6570f7a5a1f0f90a3390c9c07dff9d7b10`. **1394 distinct tests passed with zero failures/skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave29-qualification.json` and commit receipt. Original source paths/catalog are unchanged; prior controlled catalog qualification remains applicable.

Actual RAPTOR remains **7 of 12 stages integrated**, with the production builder executing. Full native FTS5 grammar is integrated; RV-030 literal-memory search/continuation owns Core28 and remains open. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and live qualification remain pending. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 114 original findings integrated

**114 of 272 findings (41.9%) are independently reviewed and integrated:** 3 Critical, 75 Important and 36 Minor. **158 remain:** 1 Important and 157 Minor. 5 further original findings have approved candidates awaiting integration: RV-108, RV-109, RV-114, RV-119, RV-120.

This wave integrates six turn-handling findings, RV-082/083/085/086/094/099, from independently reviewed ba05693eb48365c8149800d81525d6add6ac855e. Eventual Campaign preparation cleanup retains real diagnostics after scope disposal; both production scope factories explicitly receive the Wizard logger, including NotEligible paths that can later own preparation. Unsupported provider-call inputs are refused deterministically. Saved authoritative turn receipts remain successful when later settlement or protected notifications fail, and exact-key replay reconciles without a second provider call. Diagnostic fields stay safe and bounded. The full strict nullable-constructor guard is unchanged.

The earlier wave30 attempt is preserved as failed evidence:1199passed/1failed, with no completion credit. Fix1 reproduced the guard failure, corrected the dependency rather than exempting it, and was independently re-reviewed. Fresh wave30r1 passes1262distincttests, including the full strict guard and all five real-health-tracker protected-status cases. Integration changed only the approved two Wizard factory arguments relative to the prior controller, preserving later attachment error propagation and Stage7 query-embedding provenance; exact diff proof is retained.

RV-128 also closes through the already integrated RV-026 stream-progress/delivery-acknowledgement fix, after an independent mapping review against the exact original finding and immutable source/evidence. This is reused implementation credit, not a new runtime fix. Five current case IDs passed again in this wave; native flush ownership, plain-consumer acknowledgement, abandonment and replay protections remain covered by the preserved approved evidence. RV-129 receives no credit.

The newly required terminal boundary is explicit: finish all remediation and realRAPTOR, qualify the exact final candidate through full CI, then pause and report before any local install or live Ollama/embedding tests. After user go-ahead, use a fresh isolated Grimoire, qwen3.8:27b for vision, Flint:40b for Perplexity/PDF/tool workflows, and embedding qualification last. No installation or live-provider run has begun.

The exact runtime commit is `33732fec285420921ad1679d04ba0a729271e23a`, tested tree `76db36a544a7035dba386c4f9a9ae47bbf508a6b`. **1262 distinct tests passed with zero failures/skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave30r1-qualification.json` and commit receipt. Original source paths/catalog are unchanged; prior controlled catalog qualification remains applicable.

Actual RAPTOR remains **7 of 12 stages integrated**, with the production builder executing. Full native FTS5 grammar is integrated; RV-030 literal-memory search/continuation owns Core28 and remains open. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and live qualification remain pending. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 119 original findings integrated

**119 of 272 findings (43.8%) are independently reviewed and integrated:** 3 Critical, 75 Important and 41 Minor. **153 remain:** 1 Important and 152 Minor. 0 further original findings have approved candidates awaiting integration: none.

This wave integrates reviewed web candidate b4db48204c78cbbf429493074df80170269493f7 for RV-108/109/114/119/120. RV-108 uses the existing RV-012 starvation repair with the exact15-pass/42-source scenario and killed starvation mutant; RV-109 adds baseline-correct cap coverage plus a killed cap-removal mutant. Neither adds a summary-saturation or50-source discovery stop.

Follow-up queries reserve the entire supported integer pass representation; numeric exhaustion synthesizes already paid work instead of wrapping. Indexed, value-free validation preserves domain restrictions and fixes the adjacent omitted numeric defaults through source-generated constructors. Research frames and safe logs share a request trace, retain only reported usage, distinguish unknown cost and describe partial-result disposition. CLI normal/plain diagnostics show exact known spend or unknown status and the trace, while preserving JSON and old-server behavior.

All changed C# bytes match the approved candidate exactly. Fresh combined qualification passes1056distinctcases, including all complete candidate classes and the current full Wizard class, with0skips and a normal0warning build. Original eleven causal mutation/restoration receipts and independent review are preserved. This is local integration evidence; full final CI remains required. The explicit user pause before any install or liveOllama/embedding test remains in effect.

The exact runtime commit is `24dd4a704139d695c93408fd7d37ac65f6b89499`, tested tree `f05aa7c5bcef1822577ecd8786b1d4372dc56339`. **1056 distinct tests passed with zero failures/skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave31-qualification.json` and commit receipt. Original source paths/catalog are unchanged; prior controlled catalog qualification remains applicable.

Actual RAPTOR remains **7 of 12 stages integrated**, with the production builder executing. Full native FTS5 grammar is integrated; RV-030 literal-memory search/continuation owns Core28 and remains open. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and live qualification remain pending. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 120 original findings integrated

**120 of 272 findings (44.1%) are independently reviewed and integrated:** 3 Critical, 75 Important and 42 Minor. **152 remain:** 1 Important and 151 Minor. 0 further original findings have approved candidates awaiting integration: none.

RV-092 now reports explicit, truthful compaction outcomes through the API, CLI and Forge: compacted, not needed, too few messages, only pinned messages remain, or stopped. Short contexts retain measured token counts; protected stop codes and pinned-message behavior are preserved. Older hosts that omit the new optional outcome retain compatible count-only presentation. The independently reviewed candidate is `7e7ab5d052c71e385d23b245d4191cdf675ea1e9`; review evidence is `/private/tmp/arcanum-compaction-review.md`. All integrated C# files exactly match the approved candidate.

RAPTOR production builder Task8 has separately passed independent review at `1c7b4b0d` (1,823 passing tests and one existing Windows-only skip); it is not included in this integration count. The user-required full CI gate and subsequent explicit pause before installation and live Ollama/embedding testing remain mandatory.

The exact runtime commit is `6892ac82f3a6f4bc990697b724533f8b72f056cc`, tested tree `07037abc7c150c4234cac5d2be68537a609113a8`. **754 distinct tests passed with zero failures/skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave32-qualification.json` and commit receipt. Original source paths/catalog are unchanged; prior controlled catalog qualification remains applicable.

Actual RAPTOR remains **7 of 12 stages integrated**, with the production builder independently approved and awaiting integration. Full native FTS5 grammar is integrated; RV-030 literal-memory search/continuation owns Core28 and remains open. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and live qualification remain pending. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — real RAPTOR production builder integrated

Original findings remain **120/272 integrated (44.1%)**, with **152 open**: one Important and151Minor. RV100 is independently approved and queued; RV030, RV090, RV101 and RV107 remain active.

Actual RAPTOR is now **8/12 stages independently reviewed and integrated**. Approved builder1c7b4b0d installs actual recursive UMAP/GMM planning, overlapping DAG checkpoint edges, original-dimensional provider-produced summary vectors, ordered full-input reuse and source/model validation before direct and physical retry sends. WorkLimit preserves the prior generation and records backoff rather than publishing an incomplete layer. Workspace/model labels and provider alias provenance are corrected with focused regressions. Task9 typed collapsed/traversal retrieval, Task10 invocation/capsule integration, Task11 lifecycle completion and Task12 final qualification remain.

The first combined run caught one adjacent compaction regression: an orphaned internal always-true EnableContextCompression member. Independently approved correction1eb0d7a removes that dead member/constant and corrects its nearby token-measurement documentation, without weakening the strict source guard or changing public configuration. The failed run is retained and is not qualification evidence.

Fresh combined runtime `599578691e0a75428de4be04e348f51002ee85a1`, tested tree `e7f7ed00ca1a13c9526b33c0e455fbc53cf39b7c`, passed **3482 distinct tests with zero failures and one unchanged Windows-only filesystem skip**:3341affected,59controlledcatalog,82Forge. Normal solution build has zero warnings; all owned house/compact/Roslyn and diff checks pass. Only expected combined C# differences remain: Wizard preserves newer cleanup/attachment fixes, and RuntimeDefaults combines provider alias normalization with the reviewed dead-member removal. Catalog2522paths/866SQL and548admitted acquisitions are exact. Evidence `/private/tmp/arcanum-integrated-wave33r1-qualification.json`, commit receipt and immutable source hashes.

Full native FTS5 grammar remains integrated; RV030 literal-memory search/Core28 is still open. Full final suites, coverage, hosted/capsule, native shipping and full CI remain outstanding. After final CI passes, stop and wait for the user's go-ahead before installation or any live Ollama/embedding checks. Main and original Claude review refs are preserved.


### 2026-10-26 checkpoint — 121 original findings integrated

**121 of 272 findings (44.5%) are independently reviewed and integrated:** 3 Critical, 75 Important and 43 Minor. **151 remain:** 1 Important and 150 Minor. 0 further original findings have approved candidates awaiting integration: none.

RV-100 is integrated from independently approved candidate `b0d987c0d07a0a7e643542ef6d511762647df988`. Configuration validates both topic regex arrays at exact indexes even when guardrails are disabled. Allowed-topic misses now explain the missing match; malformed legacy runtime patterns retain skip behavior with one private-safe warning per field/index and settings snapshot. Parallel input/output scans share that warning boundary, and replacement snapshots can warn again. Adjacent regex exception logging is repaired for topics and PII so exception objects cannot retain private input or patterns; timeout refusal and audit behavior remain intact.

The review verified initial behavioral RED, five causal mutations with exact restoration and 1,696 candidate tests. Fresh combined integration passed 1,701 tests on the current RAPTOR tree. Two owned test files differ from the approved bytes only by blank lines between newly added statements; `git diff --ignore-blank-lines` is empty, and all checks ran after that spacing change. Production C# exactly matches the approved candidate. Review: `/private/tmp/arcanum-guardrail-topic-review.md`.

The exact runtime commit is `5cf77069cf8f2cd3c8d12618f0f8a49a134ac5c8`, tested tree `1f57554bdde3736ed5ab4ecdcfb28a7ee21ec285`. **1701 distinct tests passed with zero failures/skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave34-qualification.json` and commit receipt. Original source paths/catalog are unchanged; prior controlled catalog qualification remains applicable.

Actual RAPTOR remains **8 of 12 stages integrated**, with typed DAG retrieval Task9 implementing. Full native FTS5 grammar is integrated; RV-030 literal-memory search/continuation owns Core28 and remains open. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and live qualification remain pending. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 122 original findings integrated

**122 of 272 findings (44.9%) are independently reviewed and integrated:** 3 Critical, 75 Important and 44 Minor. **150 remain:** 1 Important and 149 Minor. 0 further original findings have approved candidates awaiting integration: none.

RV-107 is integrated from independently approved candidate `45b6279e64c82240b0e3f53bcc08bef8530fe079`. Input guardrail rejection records now retain the requested Session id and prospective canonical model from pure configuration resolution, including default-model requests. Unresolved model settings preserve the original guardrail refusal and requested value. No provider acquisition, session lookup/creation or authority is introduced; output audit context remains the actual attempt context. API and DESIGN explicitly distinguish preflight metadata from a completed provider call.

The review verified six genuine RED cases with four controls, ten focused GREEN cases, the killed original-context mutation and exact restoration, and 1,706 candidate passes. Fresh combined integration passed 1,711 tests. The sole C# difference from approved source is the previously integrated RAPTOR model-safe scope formatter in Wizard, preserved and recorded in `/private/tmp/arcanum-integrated-wave35-preserved-wizard.diff`. Review: `/private/tmp/arcanum-guardrail-audit-review.md`.

The exact runtime commit is `93adf675510ff5eb20428ca1c4d05cb9d5317910`, tested tree `3eded15beef124753e9aa448eee6083e6c3d9fb6`. **1711 distinct tests passed with zero failures/skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave35-qualification.json` and commit receipt. Original source paths/catalog are unchanged; prior controlled catalog qualification remains applicable.

Actual RAPTOR remains **8 of 12 stages integrated**, with typed DAG retrieval Task9 implementing. Full native FTS5 grammar is integrated; RV-030 literal-memory search/continuation owns Core28 and remains open. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and live qualification remain pending. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 124 original findings integrated

**124 of 272 findings (45.6%) are independently reviewed and integrated:** 3 Critical, 75 Important and 46 Minor. **148 remain:** 1 Important and 147 Minor. 0 further original findings have approved candidates awaiting integration: none.

RV-090 now reports the actual protected maintenance operation ID and a closed, safe recovery reason, with concrete operation-inspection and recovery guidance while preserving the existing refusal and authority behavior. RV-101 provides useful bounded caller-input diagnostics only when authenticated caller provenance, the original feature, current authority and the local diagnostic reason remain valid; arbitrary lower-layer failures and semantic authority refusals retain constant protected responses. No global error mapping or public payload changes were introduced. Both candidates passed independent review, and every integrated C# file is byte-identical to the approved candidate.

The exact runtime commit is `1974e38ea1bda37839321b55e90c756f702a0f45`, tested tree `f05e5843e66456f7b2bc834f36f24b28a0c3bdff`. **2204 distinct tests passed with zero failures/skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave36-qualification.json` and commit receipt. Original source paths/catalog are unchanged; prior controlled catalog qualification remains applicable.

Actual RAPTOR remains **8 of 12 stages integrated**, with typed DAG retrieval Task9 implementing. Full native FTS5 grammar is integrated; RV-030 literal-memory search/continuation owns Core28 and remains open. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and live qualification remain pending. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 125 original findings integrated

**125 of 272 findings (46.0%) are independently reviewed and integrated:** 3 Critical, 76 Important and 46 Minor. **147 remain:** 0 Important and 147 Minor. 1 further original findings have approved candidates awaiting integration: RV-113.

RV-030 is integrated after independent review of candidate `6dd9fd1e78c1f0db800b12864308c865df90ccd8`. Native trigram-assisted literal search now supports bounded scan pages and opaque continuation across the four content corpora, while Saga and Lexicon retain their owning-store authority checks. Core28 adds synchronized search projections, resumable backfill, secure token deletion and encrypted old-archive restore coverage. Tests cover literal/native-folding parity, sparse candidate completeness, pagination and lookahead, write conflicts, restore and query-plan work. Documented limits remain: no cross-request snapshot or strict native CPU/I/O bound, and one oversized row may exceed a byte budget.

The combined source preserves the newer CompactionOutcome JSON registration and reconciles the exact source catalog; all other incoming C# matches the approved candidate. The last original Important finding is now closed.

The exact runtime commit is `e7741c46bcb25d70cd48fea591461c31b37cb210`, tested tree `f215a22bb754b6abd57dea0f64a55bbe0e04bcad`. **6993 distinct tests passed with zero failures and 3 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave37-qualification.json` and commit receipt. The reconciled catalog contains 2571 paths / 911 SQL files / 548 acquisition sites, checked against this combined tree.

Actual RAPTOR remains **8 of 12 stages integrated**, with typed DAG retrieval Task9 under independent review. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 126 original findings integrated

**126 of 272 findings (46.3%) are independently reviewed and integrated:** 3 Critical, 76 Important and 47 Minor. **146 remain:** 0 Important and 146 Minor. 0 further original findings have approved candidates awaiting integration: none.

RV-113 and the adjacent BeginAsync failure-preservation defect are integrated after independent review of `887527b77de06d604fd43950932dca10a95cb2b5`. Reservation disposition and run completion are attempted independently, successful effects remain idempotent, and retries preserve the first completion state. A reservation failure no longer prevents a run from becoming terminal. Single original exceptions retain identity; simultaneous failures are preserved with the correct caller-cancellation semantics. Weave completion diagnostics use safe run/reservation identifiers and exception type while retaining vector results and scope disposal. Existing retention still protects unresolved reservations.

All incoming C# matches the independently approved candidate. The combined checks include complete owning accounting and retention classes plus Wizard, TurnEngine and production RAPTOR builder consumers on the integrated Core28 schema. RV-118 provider-specific embedding diagnostics remain separate pending work.

The exact runtime commit is `4743b7445cef4c6d8ed6e9cdb6d4467ca35b3dee`, tested tree `bdee8397c0bb7e1700501fa8575410eaae967bc3`. **1454 distinct tests passed with zero failures and 0 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave38-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR remains **8 of 12 stages integrated**, with typed DAG retrieval Task9 under independent review. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — offline text and vision qualification roles

The independently reviewed harness candidate `c2e935e3d14b8fbb5cc047944d90fa1374df97d6` is integrated as `25a76e4a77bc7619b3921b3041b6ee0787aa0262`, tested tree `b0641719557905c332e25ce35513a2eda6562758`. It preserves legacy `--model`, adds independent `--text-model` and `--vision-model` overrides, validates role configuration and adds passive persisted reply-model verification to the future opted-in run. The combined tree passed **449 offline tests with no failures/skips**, a normal zero-warning build, both house modes, Roslyn, shellcheck, Bash syntax and diff checks. No product source or source-catalog entries changed. Exact evidence: `/private/tmp/arcanum-integrated-wave39-qualification.json`.

This additional work earns no original finding credit: **126/272 remain integrated, all3 Critical/all76 Important/47 Minor;146 Minor remain**. PDF qualification preparation and embedding coverage assessment are still pending. No live provider, local installation or model operation occurred. The required full final CI gate and subsequent pause before installing or running live Ollama/embedding remain in force.


### 2026-10-26 checkpoint — 127 original findings integrated

**127 of 272 findings (46.7%) are independently reviewed and integrated:** 3 Critical, 76 Important and 48 Minor. **145 remain:** 0 Important and 145 Minor. 0 further original findings have approved candidates awaiting integration: none.

RV-105 is integrated after independent review of `681701e38f3f1f5a992fa9716f94b24785b4bc0f`. Semantic spell-routing degradation now emits an actionable Warning once per closed reason and shared monitored configuration epoch, including across concurrent request scopes. The captured epoch survives in-flight configuration replacement. Cache diagnostics retain closed reasons and safe exception types without raw exceptions or arbitrary error codes. Existing routing, cache policy and billing invocation counts remain unchanged; RV-127 is not included.

All incoming C# matches the approved candidate. Combined checks include complete changed router/cache and Wizard consumers, actual DI/constructor/source guards, and the newer accounting controls.

The exact runtime commit is `5cdb4870dd3ca019589843a0e20c262b86537538`, tested tree `f7fc3d50b7ce3eaa5b84110b19710fb06caace9f`. **910 distinct tests passed with zero failures and 0 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave40-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR remains **8 of 12 stages integrated**, with typed DAG retrieval Task9 under independent review. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 127 original findings integrated

**127 of 272 findings (46.7%) are independently reviewed and integrated:** 3 Critical, 76 Important and 48 Minor. **145 remain:** 0 Important and 145 Minor. 1 further original findings have approved candidates awaiting integration: RV-174.

Real RAPTOR Task9 typed graph retrieval is independently approved and integrated from `918d006feb0a29e5ffa3e1ae308c52a23d73a99d`, following the original review and its two corrected P2 findings. Collapsed mode retains complete-generation scoring; traversal retains resumable scope frontiers and fills available global budgets independently of scope order. Selected-frontier SQL binds bounded ID pages instead of scanning unrelated rows. Independent evidence records 140 native VM steps for one selected ID and 88 for empty selection at 300/600/1200 generation sizes; these are measured native instruction counts, not a disk-I/O benchmark. Incremental selection retains scalar decisions and accepted payloads, with prefix-change and currentness regressions, avoiding repeated hydration of unchanged accepted slices. Stage9 adds no original-finding count: 127/272 remains.

Task10 invocation/authority integration, Task11 lifecycle ownership and Task12 full/scale/end-to-end qualification remain. A broader, separate embedding qualification exposed an earlier recursive-weaver duplicate batch-cardinality predicate that conflicts with the existing `IWeaveService` contract and unchanged consumer guard. That known adjacent repair is tracked separately and is not claimed fixed or globally green by this scoped integration. Task10 implementation waits for its coherent repaired base. No installation or live provider/embedding action occurred.

The exact runtime commit is `113436060f2423ffd6ca4da192459014b84fcdbd`, tested tree `52e21fe4b04dac311808e343bc34903eb1a0acbe`. **7087 distinct tests passed with zero failures and 3 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave41-qualification.json` and commit receipt. The reconciled catalog contains 2575 paths / 911 SQL files / 551 acquisition sites, checked against this combined tree.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation and authority integration awaiting the coherent repaired embedding-service base. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 128 original findings integrated

**128 of 272 findings (47.1%) are independently reviewed and integrated:** 3 Critical, 76 Important and 49 Minor. **144 remain:** 0 Important and 144 Minor. 0 further original findings have approved candidates awaiting integration: none.

RV-174 and its confirmed adjacent resize defect are independently approved and integrated from `34e9e82b7b479388529df9f11491ede7c74e3a1b`. Generic overlays size frames in display cells and retain complete sanitized prose for narrowing/widening reflow on both help lists and label bodies. Existing preview limits remain; picker rows stay one-to-one with actions and discard stale prose on mode changes. Actual in-memory view-tree regressions, complete owning classes, and independent width/resize/picker-state mutations verify the repair. The separate compound-grapheme/sanitizer defect discovered during these tests remains in its own reviewed-remediation workstream, with no completion credit from this finding.

The exact runtime commit is `bb708b930cc66c188af38a2b89e9b63bc8e5bb27`, tested tree `d45202eed31cac17525df68b494a224af3431775`. **253 distinct tests passed with zero failures and 0 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave42-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation and authority integration awaiting the coherent repaired embedding-service base. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 128 original findings integrated

**128 of 272 findings (47.1%) are independently reviewed and integrated:** 3 Critical, 76 Important and 49 Minor. **144 remain:** 0 Important and 144 Minor. 1 further original findings have approved candidates awaiting integration: RV-118.

The offline Long Rest model-role correction is independently approved and integrated from `69b8dab17cb85b1540d7d0e66e42da7610f44be6`. Equal Flint fast/default tags share a single text-only model declaration; actual recorded requests are graded by purpose, model and credential. Distinct-default isolation and positive summary/adjudication/embedding evidence requirements remain. Fake-loopback behavioral tests and four causal mutations establish the harness correction. This adds no original-finding count and proves no live model capability, vector/calibration result, PDF acceptance or installation. PDF-specific multi-turn qualification remains a subsequent required harness addition; dedicated embedding qualification remains the last live test after the user-required pause/go-ahead.

The exact runtime commit is `268368398a5c2dbd07fc9f8615fd3bdb8e912a0b`, tested tree `2c715b70ddffb41603dd85e85ec7f6eccbfd8f73`. **409 distinct tests passed with zero failures and 0 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave43-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation and authority integration awaiting the coherent repaired embedding-service base. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 129 original findings integrated

**129 of 272 findings (47.4%) are independently reviewed and integrated:** 3 Critical, 76 Important and 50 Minor. **143 remain:** 0 Important and 143 Minor. 1 further original findings have approved candidates awaiting integration: RV-163.

RV-118 is independently approved and integrated from `e85911d75d1cfc2a62009b31c3e3e0894d7f57a4`. Embedding failures use safe fixed HTTP-status guidance and diagnostics from the actual current lease. A confirmed adjacent hot-reload accounting defect is repaired: the initial identity/pricing/reservation stays fixed, known provider/model drift refuses before another paid send, and earlier actual spend remains accounted. Same-name endpoint/key rotation remains supported; unknown legacy provenance is never portrayed as verified. No new retry or cache policy is introduced.

The broader candidate gate also exposed an earlier Tapestry duplicate cardinality check. The repair removes only that duplicate operand, preserves identity/quarantine, and proves real short provider responses produce no summaries. Independent review verified the ten causal mutants and exact restoration, and preserved the original failed candidate's evidence. Combined integration preserves Task9's three delegating test-store methods. Task10 may now build on the coherent embedding-service and DAG-retrieval base; original finding credit increases only for RV-118.

The exact runtime commit is `6d3d4cc52acee09d90edfc8840430ca9802c4650`, tested tree `e20f10ee7772f9962518cec2066755d6cd00997d`. **1233 distinct tests passed with zero failures and 1 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave44r1-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation and authority integration ready on the coherent repaired service base. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 130 original findings integrated

**130 of 272 findings (47.8%) are independently reviewed and integrated:** 3 Critical, 76 Important and 51 Minor. **142 remain:** 0 Important and 142 Minor. 0 further original findings have approved candidates awaiting integration: none.

RV-163 is independently approved and integrated from `c26eef52a3a7c7499674867001e97bf182a08c84`. Command Center owns the application and records potential terminal mutation before initialization, so partial initialization failures dispose the application and restore modes. Disposal failure does not skip restoration, and a factory failure before terminal initialization avoids unnecessary restoration. Existing startup/post-start exit codes, production DI and initialization AOT boundary are preserved. Behavioral RED, seven new lifecycle scenarios and three causal mutants accompany the repair; no real terminal initialization or operator installation occurred during its offline qualification.

The exact runtime commit is `6297de947cdabde0a693d2531baf6b126085eeeb`, tested tree `6f5e1235bafa4524d479369cac04cff2eec6be7f`. **222 distinct tests passed with zero failures and 0 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave45-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation and authority integration ready on the coherent repaired service base. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 130 original findings integrated

**130 of 272 findings (47.8%) are independently reviewed and integrated:** 3 Critical, 76 Important and 51 Minor. **142 remain:** 0 Important and 142 Minor. 0 further original findings have approved candidates awaiting integration: none.

The independently reviewed adjacent terminal-grapheme repair `a3f77bd620f4174bf2f313657ece4bdeacbbcfb5` is integrated. Shared widths follow the pinned Terminal.Gui 2.4.17 scalar measurement capped per complete grapheme. Sanitizer tab columns measure final visible graphemes after filtering controls/escapes; the original compound-emoji overlay regression is retained. The obsolete broad width table and variation-selector emoji heuristic are removed. Two old expected widths were corrected only after independent renderer/output-buffer evidence; the standalone-zero-width buffer cursor quirk is explicitly documented rather than claimed fixed. Four causal mutants and all nine path restorations were independently verified. This is an additional defect repair and adds no original-finding count; physical terminal/font behavior and full native/live qualification remain separate acceptance work.

The exact runtime commit is `9e0313423489e170984110e883b540bc6aed7db6`, tested tree `3f84a138b2b962c3fbc2de3ed24984001568cb32`. **522 distinct tests passed with zero failures and 0 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave46-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation and authority integration executing on the coherent repaired service base. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 131 original findings integrated

**131 of 272 findings (48.2%) are independently reviewed and integrated:** 3 Critical, 76 Important and 52 Minor. **141 remain:** 0 Important and 141 Minor. 0 further original findings have approved candidates awaiting integration: none.

RV-173 is independently approved and integrated from `d64bbb15f5d176a0784db28e5ad6048d2a7439ac`. The UI pump now folds separately arriving refreshes until the queued UI callback claims them. Mixed refresh kinds widen, focus actions retain ordered boundaries, an active apply permits one following refresh, and a failed hand-off releases its slot. An older callback cannot clear a newer pending run. Behavioral RED reproduced thirty queued callbacks, five causal mutations were killed, and existing delayed apply-error and cancellation controls remain. The owning documentation combines the exact reviewed insertion with the already integrated startup-cleanup paragraph; every prior document byte is preserved. An inefficient first comparison was stopped before writing or starting a gate, then replaced by an exact linear prefix/suffix extraction. No real terminal or live provider action occurred.

The exact runtime commit is `9931324e35e8c80b43b79f99f5565000b7d1ffe2`, tested tree `3c85d144831d92f90cf84f2d948bea06872b13a4`. **199 distinct tests passed with zero failures and 0 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave47-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation and authority integration executing on the coherent repaired service base. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 131 original findings integrated

**131 of 272 findings (48.2%) are independently reviewed and integrated:** 3 Critical, 76 Important and 52 Minor. **141 remain:** 0 Important and 141 Minor. 0 further original findings have approved candidates awaiting integration: none.

The requested offline PDF qualification harness is independently reviewed and integrated from `0b7b6855c68dd2910984f205549feaa43c4d7d7b`. Its explicit `--pdf` mode runs after the existing main-Session assertions, creates a separate PDF Session in the same isolated profile, and proves canonical upload/export, encrypted storage, arithmetic from fresh PDF-only facts, a second pin-only extraction, text-model/credential provenance and exact-source durable embedding publication. Baseline mode and original receipts remain intact; the approved PDF-only 45-minute hang ceiling retains a 30-minute baseline. The final required live invocation must include PDF mode and still wait for the user's pause/go-ahead. Dedicated embedding qualification remains separate and last. Twelve offline causal mutants were caught; no original finding count or live acceptance is added by this harness work.

The exact runtime commit is `65515e7459c13ee3c032e8837b7df0afc63f36d6`, tested tree `ad7df7d590855fdd264986cf45c2be2f6bfc0923`. **753 distinct tests passed with zero failures and 0 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave48-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation and authority integration executing on the coherent repaired service base. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 132 original findings integrated

**132 of 272 findings (48.5%) are independently reviewed and integrated:** 3 Critical, 76 Important and 53 Minor. **140 remain:** 0 Important and 140 Minor. 0 further original findings have approved candidates awaiting integration: none.

RV-166 is independently reviewed and integrated from `c94b5c57529840be07d75ffc8b04d48f1d625cea`. Command Center now bounds the entire owned-host quit operation to a fresh five-second deadline covering credential preparation, response headers and response body. An already-cancelled invocation still gets a fresh cleanup token; ownership policy and exit code remain intact, the timer is disposed and a fixed safe warning reports timeout. Behavioral RED proved all three stalled phases, four causal mutants were caught, and the public cancelled-startup control preserves exit130. Existing hosts are never quit. No install or live-provider action was run.

The exact runtime commit is `df061509251e21ba90ea03c4c612910e01ce2ecf`, tested tree `0d41fe090be913dfc9a5622f1307b1aa10b79cc1`. **250 distinct tests passed with zero failures and 0 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave49-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation and authority integration executing on the coherent repaired service base. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 136 original findings integrated

**136 of 272 findings (50.0%) are independently reviewed and integrated:** 3 Critical, 76 Important and 57 Minor. **136 remain:** 0 Important and 136 Minor. 2 further original findings have approved candidates awaiting integration: RV-164, RV-165.

RV-167, RV-170, RV-171 and RV-172 are independently reviewed and integrated from `f56bae1cac3dbc098787fd104008f39a33e93087`. Session/transcript paging and post-turn refresh now share the existing session-action gate. Chat admission briefly holds that gate while claiming the turn and publishing its cancellation source; busy admission preserves the composer and staged attachments. Cleanup disposes only its owned source before reopening turn admission, and completion respects current pane/overlay focus. Corrected actual-host RED reproduced11 failures with2controls; the earlier shared-gate fixture that resolved separate transient Hosts is explicitly invalidated. Six exact-restored causal mutants were caught. All72 complete affected classes passed on the combined tree. The separately reproduced closed-UI shutdown defect is still being qualified and receives no completion credit here. RV164/165 monitor candidates have independent approval but await exact observer-inventory closure before their integration gate can pass.

The exact runtime commit is `596f813ebedf090a1cf05d44e2e1faa239c9c8c0`, tested tree `76c6f554d7c5368bca50a31b5c60928173717849`. **1081 distinct tests passed with zero failures and 4 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave50-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation and authority integration executing on the coherent repaired service base. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 136 original findings integrated

**136 of 272 findings (50.0%) are independently reviewed and integrated:** 3 Critical, 76 Important and 57 Minor. **136 remain:** 0 Important and 136 Minor. 2 further original findings have approved candidates awaiting integration: RV-164, RV-165.

Reviewed adjacent Command Center shutdown repair integrated. Host completion and duplicate-submit notifications tolerate a closed UI channel, ordinary caller cancellation avoids a spurious error log, and turn/CTS ownership cleanup remains intact. Three genuine RED cases, three causal mutants and independent source/evidence review precede this integration. This is an adjacent defect, so the original count stays136/272.

The exact runtime commit is `6c746aff5f64dc3f370e2999a438138d9682be4a`, tested tree `fb8bf92e6d8c60d8826359d5b704b7b7c871950f`. **1084 distinct tests passed with zero failures and 4 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave51-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation/authority under implementation, Task11 graph lifecycle repair executing, and Task12 final qualification pending. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 136 original findings integrated

**136 of 272 findings (50.0%) are independently reviewed and integrated:** 3 Critical, 76 Important and 57 Minor. **136 remain:** 0 Important and 136 Minor. 2 further original findings have approved candidates awaiting integration: RV-164, RV-165.

Integrated the independently reviewed baseline observer inventory closure: two existing framework wrappers are now pinned to their exact construction sites, and the existing encrypted writer pin names its actual private core. All21 reviewed types and all construction sites match; no runtime/scanner/synthetic control was weakened. This adjacent baseline defect does not add original-finding credit. The monitor candidate retains its historical baseline failure until combined requalification.

The exact runtime commit is `6eaa08c46fb17491c099ecc1ce5186b3a5f4421d`, tested tree `1c25bfc95ba6eefee082f7cf0b412d3adba19d8f`. **190 distinct tests passed with zero failures and 0 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave52-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation/authority under implementation, Task11 graph lifecycle repair executing, and Task12 final qualification pending. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 138 original findings integrated

**138 of 272 findings (50.7%) are independently reviewed and integrated:** 3 Critical, 76 Important and 59 Minor. **134 remain:** 0 Important and 134 Minor. 0 further original findings have approved candidates awaiting integration: none.

RV-164/RV-165 are integrated: optional filesystem-watcher failure preserves explicit/pending refresh, failures produce safe guidance, irrelevant file churn is filtered, refresh reasons coalesce without losing intent, and sustained activity is paced from completed API operations. Pending indexing survives transient GET failures; cancellation and partial watcher disposal remain bounded. Independent review accepted the runtime candidate while retaining its historical baseline inventory failure. The separately reviewed observer closure is now present, and this combined run passes the full strict census plus all complete monitor/Command Center/architecture owner classes.

The exact runtime commit is `88413f77b2aad2bd043d470e6940b1e4ee654238`, tested tree `a9c285bc64a7bda823c1a418fb99bc6dab533f3f`. **1300 distinct tests passed with zero failures and 4 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave53-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation/authority under implementation, Task11 graph lifecycle repair executing, and Task12 final qualification pending. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 139 original findings integrated

**139 of 272 findings (51.1%) are independently reviewed and integrated:** 3 Critical, 76 Important and 60 Minor. **133 remain:** 0 Important and 133 Minor. 0 further original findings have approved candidates awaiting integration: none.

RV-169 integrated: shared model IDs now produce one selectable row with all distinct listing providers, one active marker and preserved provider-name filtering. Selection commits only the model ID. Listed/hidden model confirmations no longer promise a provider route that the request cannot select; two actual hidden-Familiar projection discrepancies were reproduced and repaired in the shared slash confirmation as an adjacent defect. Acceptance and real host routing remain unchanged. Four genuine RED cases, four causal mutants and independent review precede the combined qualification.

The exact runtime commit is `f4a24069a375eb57e28dcd66a136f4873d85ab91`, tested tree `d0cf349278d7c74a1cb95afef20a2cd414d8702f`. **1304 distinct tests passed with zero failures and 4 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave54-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation/authority under implementation, Task11 graph lifecycle repair executing, and Task12 final qualification pending. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.


### 2026-10-26 checkpoint — 140 original findings integrated

**140 of 272 findings (51.5%) are independently reviewed and integrated:** 3 Critical, 76 Important and 61 Minor. **132 remain:** 0 Important and 132 Minor. 0 further original findings have approved candidates awaiting integration: none.

RV-168 now binds attachment snapshots to the exact Session visit, clears prior projections on binding changes and schedules an immediate refresh after successful picker resume. The independently reproduced A-to-B-to-A delayed-response defect is closed by the same versioned binding and synchronized adoption. Failed resume preserves the current projection. The first combined wave55 exposed an existing test that treated a200ms negative observation window as proof of response completion; its failed1310-pass/1-fail/4-skip artifacts remain unchanged. An independently reviewed test-only repair holds and releases that response deterministically, preserves exact retry count/spacing/no-spin assertions and still kills the existing pending-retry mutant. The fresh combined wave55r2 is green.

The exact runtime commit is `a4b4cf4287c587aafe6a762ac5e84f4ed6e6dbb5`, tested tree `e645b6a688c068e280d8d72009a804fb69c3c2b9`. **1311 distinct tests passed with zero failures and 4 explicitly recorded Windows-only skips**, plus a normal zero-warning build, both house formatting modes, explicit Roslyn verification and diff checks. Evidence: `/private/tmp/arcanum-integrated-wave55r2-qualification.json` and commit receipt. No production paths were added or removed by this integration; the existing exact source catalog is preserved.

Actual RAPTOR has **9 of 12 stages integrated**, with Task10 invocation candidate under review with held-store canonical currentness gap being reproduced; Task11 lifecycle independently approved awaiting integration; Task12 final gates pending. Full native FTS5 grammar is integrated. RV-030 literal-memory search and all-scope continuation are integrated with Core28. Final whole-suite, coverage, hosted/capsule, Native AOT/shipping/native provenance and full CI qualification remain pending. After those pass, the user-required pause precedes installation and any live Ollama or embedding qualification. Main and the original Claude review branch are preserved.
