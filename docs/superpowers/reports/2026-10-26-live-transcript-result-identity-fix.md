# RV177 fix round 1: verified structured tool-result identity

Base/sealed original candidate: `06b97a798b55ef667bd2b963bbeae5ec14882d79`. Branch: `codex/review-live-transcript-result-identity-fix`. This is a narrow review correction, not independent integration credit. The original report/package and original branch remain preserved; exact descendant SHA/tree are bound by the new immutable manifest.

## Confirmed review defect

The reviewer identified a supported path that still delegated to the explicit-resume helper: chronological persisted call A, call B with the same ToolName, then a plain structured result for A carrying ToolCallId=A. UpsertCall(A) does not reorder records, and CompleteLatestPending(toolName) consequently settled B. JSON-valued tool results were incorrectly treated as call arguments by the same legacy helper.

Four real retained Host/NDJSON cases produced genuine RED on the exact sealed production: plain `tool`, plain `function`, JSON-valued `tool`, and explicit tool-error result payloads. All four asserted A's result/error outcome and B still pending; the prior 19 cases passed. Terminal RED: 4 failed / 19 passed / 23 total, no compile/error/timeout substitution.

## Narrow correction

The automatic verified-ID path now classifies explicit bracket results and structured tool/function-role rows as results, including JSON-valued payloads, and upserts result/error against their own verified call ID. Explicit bracket calls and assistant tool-call rows remain calls. It no longer invokes the legacy pending-by-name helper. Existing live-call records remain untouched, and rows without verified call IDs remain excluded as before. No correlation by name, payload text, JSON shape or entry position supplies identity.

The original explicit resume/history helper and the integrated ForkSessionAsync region remain byte-identical. Their hashes and assertions are in final-receipt.json. No public API, schema, wire DTO, authority, provider traffic, compaction/budget code or other worktree was changed. Owning DESIGN §16.6 now states the supported result representations and exact-ID rule.

## TDD, causal mutation and final qualification

- RED: 4 failed / 19 passed / 23 total in `red/fix1-red.trx` on sealed production.
- GREEN: 23/23 in `green/fix1-green.trx`.
- Meaningful mutation: restore the entire production file to the exact `06b97a79` Git blob. All four new cases fail again with ordinary assertions; 19 controls pass. The corrected production and test files are then restored byte-for-byte and their hashes checked. Mutation source files, command, terminal TRX and restoration receipt are retained.
- Original RV177 ten mutant receipts remain in the original sealed package; unaffected mutants were not repeated. This additional mutation directly binds the review correction.
- Fresh exact-source full qualification: **1,144 passed, 0 failed, four named Windows-only skips, 77 complete classes / 1,148 cases**. All Command Center classes plus strict constructor, CLI Infrastructure, client context mutation authority and API credential guards were selected. No partial, cancelled or missing-TRX run is credited.
- Normal full solution build: 0 warnings / 0 errors. Owned house, compact-delimiter, Roslyn whitespace verification and git diff --check all exit 0. Exact command arrays, cwd/timestamps, logs, class census, skip names, terminal TRX and all six contract-source hashes are retained. All six source hashes match the final causal/restored baseline and committed bytes.

Final commands:

```text
dotnet build RetroDownfall.Arcanum.slnx --no-restore -m:1 -nodeReuse:false
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-build -m:1 -nodeReuse:false --filter "FullyQualifiedName~RetroDownfall.Arcanum.Tests.Cli.CommandCenter.|FullyQualifiedName~NullableInterfaceConstructorDefaultTests.|FullyQualifiedName~CliInfrastructureBoundaryTests.|FullyQualifiedName~CliContextMutationBoundaryTests.|FullyQualifiedName~ArcanumApiCredentialLeaseTests." --logger "trx;LogFileName=final-complete.trx" --results-directory /private/tmp/arcanum-rv177-fix1-evidence/final-complete
```

Final TRX SHA-256: `c47ba56dbf13bc038efdec40602bb93d368f8e0165d0df235ce83371aa367482`.

Named skips (macOS host; no Windows runtime claim):

- `RetroDownfall.Arcanum.Tests.Cli.CommandCenter.CommandCenterTurnAttachmentBuilderTests.Windows_a_reparse_point_that_names_no_other_location_stages`
- `RetroDownfall.Arcanum.Tests.Cli.CommandCenter.CommandCenterTurnAttachmentBuilderTests.Windows_an_ordinary_file_stages_by_at_token_and_by_attach`
- `RetroDownfall.Arcanum.Tests.Cli.CommandCenter.CommandCenterTurnAttachmentBuilderTests.Windows_the_open_reads_an_ordinary_file_and_refuses_a_directory`
- `RetroDownfall.Arcanum.Tests.Cli.CommandCenter.CommandCenterTurnAttachmentBuilderTests.Windows_a_symbolic_link_to_a_regular_file_stages`

No live Ollama/embedding/provider discovery, install/config/service action, network qualification, Native AOT shipping publish or full campaign CI was performed. Original RV177 evidence stays immutable and no original-finding closure/integration is claimed before independent review.
