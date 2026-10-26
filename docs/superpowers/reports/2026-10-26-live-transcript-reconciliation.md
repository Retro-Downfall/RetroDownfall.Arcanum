# RV177 live transcript reconciliation — implementation report

Base: `ea58a88899de6c164cbf6134698c47cd85d93c7d`. Branch: `codex/review-live-transcript-reconciliation`. This report describes a review candidate; integration, full campaign CI, Windows runtime and live provider qualification are not claimed. Exact committed SHA/tree and all source/evidence hashes are supplied by the sealed package manifest, outside this self-referential tracked report.

## Finding and bounded implementation

The remaining RV177 defect was real: live User/Assistant rows had no persisted SourceEntryId, current count/page continuation stayed stale, and the existing fork consumers could not select the new answer. RV042 already repaired the dated offset/total-count paging claim with keyset paging. That old issue is a regression control, not new implementation credit.

ChatRunner now returns an internal completion receipt containing successful durable Result evidence, the accepted Session visit and exact local provisional row IDs. Its general failure arm marks failure before deciding eligibility. The Host keeps the turn admitted through an action-gated read of existing authenticated Session detail and a bounded newest 201-entry page. Adoption checks the versioned Session visit (including A→B→A), Session IDs, nonempty unique row IDs and cancellation before committing state. Only this turn's provisional User/Assistant rows are replaced by authoritative DTO rows; no text, role or guessed-position identity correlation occurs. Existing durable log anchors and local diagnostics/reasoning survive; metadata/count and 200-row lookahead continuation advance.

Historical or scrolled views remain anchored, with explicit newest-page navigation available. The Host rechecks current view intent at admission, including scrolling while HTTP is pending; Window changes only its existing follow-tail flag to volatile for that scalar cross-thread read. Completion preserves the operator's current focus and does not force a tail jump. The automatic catalog refresh preserves the existing turn warning/error; a local binding refusal remains a refusal. Cancelled/error/missing-Result paths keep provisional output and manual newest-page or /resume guidance. Cancellation during post-Result snapshot reads also keeps that guidance. No polling, uncancelled recovery, extra inference, endpoint, schema, authority or background mechanism was added.

The integrated RV178 ForkSessionAsync region remains byte-identical: SHA-256 `a8fcb5828067eceb5307591c00ce37cff423d73d2a89d507fc8047f8163ee36e`. New tests exercise the existing /fork at and /fork alternative delivery using real rendered SourceEntryId selection and assert exact cutoff IDs and one fork request, with no second inference. Existing fork-success/recovery tests remain complete controls.

## Separately confirmed adjacent repairs and limits

The first expanded implementation admitted an obsolete pre-turn viewport decision: an operator scrolling during the held snapshot lost the selected page. A separate behavioral RED established that race before the current-view admission fix.

Automatic reuse of explicit-resume's legacy tool loader could settle an unrelated live pending invocation, either on history replay or even with a verified result call ID. Two separate behavioral RED cases established those mistakes. Automatic reconciliation now preserves existing live records and Ward/error outcomes, merges only nonempty verified persisted call IDs, and applies a verified bracket result to its own call ID. Unidentified legacy rows never correlate by name/text/position and remain owned by explicit resume/history loading.

This does not repair or claim complete storage call identity. The documented normal TryAppendToolInteractionAsync/AppendToolInteractionAsync path intentionally accepts no call-id argument and writes bracket call/result pairs. Mandatory receipt calls retain ToolCallId on call rows, while their result rows omit it. Those representations and the original explicit resume/history semantics are unchanged. The new automatic path makes no identity assurance for unidentified rows. No broader API/schema/storage change was made.

## Behavioral TDD and terminal evidence

All tests use the actual retained Host → ChatRunner → ArcanumApiClient route with offline NDJSON/authenticated-client fixtures. Cancellation deliberately leaves server cleanup outstanding; no snapshot or invented ID is adopted. Held entries responses exercise A→B and A→B→A. Wrong-Session/refused snapshots, accepted/refused binding, wrapped viewport/focus, Incantation/Ward/error preservation and existing fork consumers have explicit controls.

| Stage | Terminal result |
| --- | --- |
| Initial existing production RED | 6 failed / 0 passed / 6 total; `primary-red/primary-red.trx` |
| First implementation GREEN | 0 failed / 8 passed / 8 total; `first-green/first-green.trx` |
| Additional scrolled-view RED | 1 failed / 12 passed / 13 total; `more-red/more-red.trx` |
| Expanded GREEN | 0 failed / 13 passed / 13 total; `expanded-green/expanded-green.trx` |
| Post-Result cancellation RED | 1 failed / 15 passed / 16 total; `snapshot-cancel-red/snapshot-cancel-red.trx` |
| Owning GREEN | 0 failed / 16 passed / 16 total; `all-own-green/all-own-green.trx` |
| Tool identity and late-scroll RED | 3 failed / 16 passed / 19 total; `adjacent-red/adjacent-red.trx` |
| Repaired owning GREEN | 0 failed / 19 passed / 19 total; `adjacent-green/adjacent-green.trx` |

Two initial test fixture compilation failures (missing namespaces and incorrect envelope helper) and the added UI fixture's nullable selection compilation failure are excluded from behavioral RED; their logs are retained. No timeout, missing TRX, cancelled or partial run is credited as green.

Initial seven causal mutants were killed before the three adjacent regressions were added. Their exact five-file baseline source, hashes, terminal TRXs and per-mutant exact restoration receipts are preserved. The initial source files were reconstructed from immutable base Git blobs plus recorded mutant diffs, reversed only at each recorded mutation site, and verified against their original hashes; the 16-case test source was reconstructed by removing exactly the three later regression methods and matched its original hash. The final three mutants bind to the final six C# source files (including Window); each restored those exact files. Causal-source differences are explicit rather than claiming all ten mutants used the same bytes.

| Mutant | Failed assertions |
| --- | --- |
| 01-durable-result | 3 |
| 02-visit-revision | 1 |
| 03-local-diagnostics | 1 |
| 04-binding-refusal | 1 |
| 05-anchored-view | 2 |
| 06-persisted-selection | 4 |
| 07-warning-preservation | 3 |
| 08-unverified-tool-replay | 2 |
| 09-current-viewport | 2 |
| 10-verified-tool-result | 1 |

All ten have ordinary assertion failures, no compiler/error/timeout substitution and exact source restoration. The earlier seven protect unchanged receipt/admission/diagnostic/selection contracts; the final three directly prove the changed tool/current-viewport paths.

## Final exact-source qualification

Final terminal TRX: **1140 passed, 0 failed, 4 named Windows-only skips, 77 complete classes / 1144 total cases**. SHA-256 `a1b4eee926b080a79df6b5b3e7e9e71f0cad38358ee3ecec0d8d47d09f3ba2c4`. All Command Center classes are selected because Host/stream/log/workspace orchestration and selection cross those retained paths. Additional complete guards cover strict nullable-interface constructor defaults, CLI Infrastructure boundaries, client context mutation authority and API credential leases. The class-by-class census and exact source hashes are in `final-receipt.json`.

```text
dotnet build RetroDownfall.Arcanum.slnx --no-restore -m:1 -nodeReuse:false
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-build -m:1 -nodeReuse:false --filter "FullyQualifiedName~RetroDownfall.Arcanum.Tests.Cli.CommandCenter.|FullyQualifiedName~NullableInterfaceConstructorDefaultTests.|FullyQualifiedName~CliInfrastructureBoundaryTests.|FullyQualifiedName~CliContextMutationBoundaryTests.|FullyQualifiedName~ArcanumApiCredentialLeaseTests." --logger "trx;LogFileName=final-complete.trx" --results-directory /private/tmp/arcanum-rv177-evidence/final-complete
```

Normal full solution build: 0 warnings, 0 errors. Owned house check, compact-delimiter check, Roslyn whitespace verification and git diff --check all return 0. Their exact argv/cwd/timestamps, terminal logs and exit receipts are retained. Final qualification verified all six source hashes equal the final causal baseline and preserved the integrated fork region exactly. Report/docs are the only subsequent non-code additions before commit.

Named skips (macOS host; no Windows runtime claim):

- `RetroDownfall.Arcanum.Tests.Cli.CommandCenter.CommandCenterTurnAttachmentBuilderTests.Windows_a_reparse_point_that_names_no_other_location_stages`
- `RetroDownfall.Arcanum.Tests.Cli.CommandCenter.CommandCenterTurnAttachmentBuilderTests.Windows_the_open_reads_an_ordinary_file_and_refuses_a_directory`
- `RetroDownfall.Arcanum.Tests.Cli.CommandCenter.CommandCenterTurnAttachmentBuilderTests.Windows_an_ordinary_file_stages_by_at_token_and_by_attach`
- `RetroDownfall.Arcanum.Tests.Cli.CommandCenter.CommandCenterTurnAttachmentBuilderTests.Windows_a_symbolic_link_to_a_regular_file_stages`

No live Ollama/embedding/provider discovery, installation, configuration/service/operator action, network qualification, Native AOT shipping publish or full campaign CI was performed. The existing PDF and Task11 branches are preserved. No main, controller, source ledger, unowned worktree, budget-monitor file or fork implementation was modified.
