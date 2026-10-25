# U05 (Arcanum.Api / Tower) - PERFORMANCE review

Tree: `.claude/worktrees/codebase-review`, branch `claude/codebase-review-2026-10` at `ac0d2d8e` (read-only). All probes ran in a scratch worktree
(`scratchpad/review/wt/U05-perf`, removed afterwards). Probe sources are preserved in
`scratchpad/review/reports/A/U05-probes/` (4 files: `U05SpellWalkProbe.cs`, `U05GrimoireProbe.cs`, `U05MicroProbe.cs`, `U05RankingEvidenceProbe.cs`).

## Scope and method

* `scratchpad/review/units.json` did not exist when I looked (the `review/` directory held only other reviewers' files), so I took U05 to be every file under
  `src/RetroDownfall.Arcanum.Api/Tower/`: 28 `.cs` files, 12,873 lines, which matches the size in the brief exactly.
* I read all 28 files in full (listed under Coverage), plus the owning DESIGN sections (§9, §16, §19) and the Infrastructure code the Tower handlers call
  (SpellRepository, SpellScanner, SpellSearchService, PromptRepository, SessionRepository, SagaMemoryStore, DivinationService, the purge coordinator, the ranking
  evidence reader) so that every cost could be traced to where it is actually incurred.
* **Environment caveat.** Every timing below was taken while the machine ran other reviewers' builds (load average 56-106 on the whole run; `uptime` quoted in
  my notes). Absolute milliseconds are therefore inflated and noisy, so each number is given as the range of the samples I took (two independent runs where
  the probe was cheap enough). Ratios, query plans and allocation counts are not load-sensitive and are what the findings rest on.
* The published AOT binary (`scratchpad/review/publish/...`) did not exist, so I measured through the test project's built assemblies, which hold the same
  Core/Infrastructure/Api code. Nothing touched the operator's `~/.config/arcanum`: the spell probes set `DOTNET_ENVIRONMENT=Testing` and
  `ARCANUM_TEST_HOME=<temp>`, the Grimoire probes use `GrimoireFixture` throwaway copies, and the temp trees were deleted afterwards.

## Probes

| Id | What | Result |
|---|---|---|
| P1 | `SpellRepository.GetAsync` (the lookup every spell endpoint runs) over synthetic trees and over this repository | 100 dirs/1k files: 6-62 ms; 1,000 dirs/20k files: 56-314 ms; 5,000 dirs/100k files: 539-1,113 ms; this repo (about 390 dirs, 5.9k files): 17-29 ms. The same lookup through `ScanMetadataAsync(ttl: 5)` after the first call: 0.0-0.1 ms. |
| P2 | Campaign export loop (`ListAsync` then `ExportAsync` per spell) and import loop (`ImportAsync` per spell), 25 / 40 spells in a 1,000-dir / 20k-file workspace | Export: `ListAsync` 73-85 ms, then 25 x `ExportAsync` 2,653-3,088 ms (106-124 ms each). Import: 40 imports 11.7-15.3 s (median about 290-370 ms each) against a 38-40 ms median for the same import into an empty workspace; one `ScanAsync` of that tree alone is 95-385 ms. |
| P3 | `SpellRepository.SearchAsync` with 1 / 5 / 10 / 20 registered campaigns, 500 dirs / 5k files each | 1: 24-250 ms; 5: 204-1,339 ms; 10: 340-2,497 ms; 20: 990-3,591 ms. |
| P4 | Prompt list: 3,000 prompts x 8 KiB templates; the repository's exact SQL, `EXPLAIN QUERY PLAN`, and the endpoint's filter path | See F7. |
| P5 | Memory search / status / analytics statements, verbatim from `MemoryEndpoints` and `SessionRepository`, over 1,500 sessions / 60,000 entries x 1.5 KiB (SQLCipher db 281 MiB incl. FTS) | See F5, F10. |
| P6 | `PurgeEveryLabeledSagaAsync` accumulation | 97-100 B per memory (9.6 MiB at 100k, 92.4 MiB at 1M). |
| P7 | `InferenceExecuteWriter.WithHeartbeats` over 200,000 async-completing events | +302 ns/event, 176 B/event over a bare `await foreach`. Sound (see below). |
| P8 | `DivinationService.SearchAsync` (the call `/sessions/divine` makes) over 40,000 x 1024-dim embeddings (156 MiB) | 1,733-2,041 ms per scan (window 40 and window 500 cost the same). |
| P9 | Saga reads with Long Rest on: `SagaRankingEvidenceReader.ReadAsync`, `SagaRankingReader.CreateAsync`, `SagaMemoryStore.ListAsync(limit 10)`, `ReadRankingStatusAsync`, with 10 / 50 / 150 retained evaluations | 10: 2.5-9.7 ms; 50: 12.9-20.7 ms; 150: 40.7-47.8 ms, for every one of the four calls (about 0.29 ms per retained evaluation per call). |

## Findings

### F1 (Important, M) - every spell lookup walks the whole workspace tree, uncached
`SpellExecutionEndpoints.cs:70-72` (`Spell_Execute`), `:193-195` (`execute-stream`), `:295-297` (`versions`), and `SpellAuthoringEndpoints.cs:88`
(validate), `:123` (export) all call `ISpellRepository.GetAsync`. `SpellRepository.GetAsync` (`Infrastructure/Intelligence/Spells/SpellRepository.cs:99-132`)
calls `SpellScanner.ScanMetadataAsync(workingDirectory, ct, max)` with the default `metadataScanCacheTtlSeconds = 0`
(`Infrastructure/Workspaces/SpellScanner.cs:331-335`, cache read at `:368`, cache write at `:404`, both guarded by `ttlSeconds > 0`), so nothing is cached, and
the walk (`EnumerateSpellFiles`, BFS, two directory enumerations plus a `ResolveLinkTarget` per directory, only `.`-prefixed and
`node_modules/bin/obj/out/dist` pruned) visits the global spells folder and the entire Campaign root on a thread-pool thread (`Task.Run`) every time. The
inference turn that follows reads the same data through the 5-second TTL cache (`WizardIntelligenceProvider.cs:5378-5382`), so the handler's pre-flight
`GetAsync` (used only for the 404 check unless a `version` is given) is the one uncached walk per request.
Cost (P1): 56-314 ms on a 20k-file Campaign, 0.5-1.1 s on a 100k-file one, against 0.0 ms through the cache the turn already uses. `GET .../versions` also
fully parses every `SPELL.v*.md` sequentially to read one description each (`SpellExecutionEndpoints.cs:342-358`, `:720-748`; not measured).
Fix: pass `ArcanumRuntimeDefaults.Spells.MetadataScanCacheTtlSeconds` from `GetAsync`/`ListAsync`/`SpellSearchService`, and add an invalidation (a
generation counter keyed by workspace root, bumped by the repository's own writers: create, update, delete, clone, import, version create/update/activate) so a
write is never shadowed for the TTL.

### F2 (Important, S) - Campaign export repeats the full walk once per spell
`CampaignEndpoints.cs:899-935`: `ListAsync` (one walk), then for each non-built-in spell `spellRepo.ExportAsync(summary.Name, campaign.Path)` (`:914-916`),
whose first line is `GetAsync` (`SpellRepository.cs:505-507`) = F1's walk again. P2: 25 spells in a 20k-file Campaign: 73-85 ms for the list, then 2,653-3,088 ms
for the exports (about 106-124 ms each, 35-40x the list). Cost is `spells x tree`.
Fix: scan the metadata once and export from it (an `ExportAllAsync(workingDirectory)` or an `ExportAsync` overload taking the already-found `SpellMetadata`).

### F3 (Important, M) - Campaign import re-parses every spell, once per spell, under the workspace lock
`CampaignEndpoints.cs:679-691` calls `spellRepo.ImportAsync` per bundle spell; `ImportCreateStagedAsync` (`SpellRepository.cs:1493`, scan at `:1535`) takes the
workspace write lock and then runs `SpellScanner.ScanAsync`, a full parse of every `SPELL.md` under the global folder and the Campaign tree, only to ask
`FindByName(allSpells, trimmedName)`. P2: 40 imports into a 20k-file Campaign took 11.7-15.3 s (about 290-370 ms median each) against a 38-40 ms median into an
empty workspace; the scan alone is 95-385 ms. Parsing grows with the number of spells already present, so the loop is also quadratic in bundle size. Every other
spell writer in that workspace is blocked behind the lock for the scan.
Fix: decide name collisions from `ScanMetadataAsync` names (no body parse), and give the endpoint a batch entry point that scans once under one lock and tracks
the names it adds.

### F4 (Important, S) - `GET /api/spells/search` scans every registered Campaign on every call
`SpellAuthoringEndpoints.cs:48-57` loads up to 10,000 full `Campaign` rows (Settings and Sanctum JSON included) and hands them to `SpellRepository.SearchAsync`,
which hands them to `SpellSearchService.SearchAsync` (`Infrastructure/Intelligence/Spells/SpellSearchService.cs:55-110`): one uncached `ScanSummariesAsync` per
Campaign (each walk also re-walks the global folder, because `ScanMetadataAsync` merges global and local) even when `campaignId`/`source` narrow the answer to
one. P3: 20 Campaigns of 5k files each: 0.99-3.6 s per request, linear in Campaigns (about 50-170 ms each). With zero Campaigns registered
`SpellRepository.SearchAsync` (`:421-441`) runs the 10,000-row campaign query a second time.
Fix: select only `Id`/`Path`; skip the scan of any Campaign a `campaignId`/`source` filter excludes; use the cached scan from F1.

### F5 (Important, M) - `POST /api/memory/search` is a full-corpus scan with a per-row `lower()` copy; the FTS index is not used
`MemoryEndpoints.cs:1204-1213` (`instr(lower(e.Content), lower(@query)) > 0 ... ORDER BY CreatedAt DESC, Sequence DESC LIMIT n`, no `SessionId` predicate for the
default whole-installation search), `:1265-1276` (session summaries), `:1330-1342` (attachment chunks, three `instr(lower())` terms) and `:1413-1423`
(workspace chunks). Every row of the table is read, decrypted, lower-cased and searched whatever the query and limit, and `scope=all` does this five times in one
request. P5 (60,000 entries x 1.5 KiB = 88 MiB): 285-900 ms per search whether the term matches nothing or everything (it cannot stop early because of the
`ORDER BY`), against 0.0-1.4 ms for an `Entries_fts MATCH` and 0.1-0.7 ms scoped to one session. The cost is linear in corpus bytes (about 5-6 s at 1M such
entries), and workspace chunks, which index a whole repository, are the largest corpus.
Fix: add an FTS5 trigram table (substring semantics, case-insensitive) for entries, attachment chunks and workspace chunks, or at least bound the scan (newest N
rows or a time budget) and report `hasMore`; at minimum take the existing `Entries_fts` for the single-token case.

### F6 (Important when `Features.LongRest` is on, M) - Saga ranking evidence is recomputed from scratch on every Saga read, and on every recall
Reached from Tower at `MemoryEndpoints.cs:251-252` (`ReadRankingStatusAsync`), `:449-454` (`ExplainRankingAsync`), `:1473-1481` (`ListCurationRowsAsync`),
`SagaEndpoints.cs:90-92` (`ListAsync`) and `:166-187` (`/saga/divine`), and by every turn's Saga recall (`WizardIntelligenceProvider.cs:5963-5975` ->
`DivinationService.cs:277`, `:346`, `:398`). Each calls
`SagaRankingReader.CreateAsync` (`Infrastructure/Data/LongRest/SagaRankingReader.cs:21-29`), which calls `SagaRankingEvidenceReader.ReadAsync`: up to
`MemoryEvaluationLimits.RetainedRegistrations` (1,024) evaluation roots, and for each one a full `MemoryEvaluationEvidenceStore.ReadAsync`, three statements of
`CurrentCleanSourceAsync` (one re-hashes the memory text) and `SuccessfulUsesAsync`. The result is a pure function of immutable reports, the dataset
generation and `asOf`, yet nothing is kept between calls. P9: 0.29 ms per retained evaluation per call (2.5-9.7 ms at 10 evaluations, 40.7-47.8 ms at 150),
and the page size does not matter (`ListAsync(limit 10)` is as slow as the status call); at the 1,024 cap that is about 300 ms on every Saga list, status,
explain, search and every Saga-recalling turn.
Fix: cache the evidence dictionary per (dataset generation, change stamp of `memory_evaluation*`/`saga_committed_uses`, `asOf` rounded to the next instant any
retained report's validity edge can cross) and drop it when a report or a committed use lands.

### F7 (Minor, S) - prompt list: the repository's `OR` predicate defeats the index order, and the endpoint reads full rows to build summaries
`PromptRepository.ListAsync` (`Infrastructure/Repositories/PromptRepository.cs:113-127`) filters with
`CampaignId = $c OR (CampaignId IS NULL AND $c IS NULL)`. P4 shows the plan is `MULTI-INDEX OR ... USE TEMP B-TREE FOR ORDER BY` (every prompt in the scope is read
and sorted before `LIMIT 101`); with `CampaignId IS $c` the plan is `SEARCH ... USING INDEX IX_Prompts_CampaignId_Name` + `USE TEMP B-TREE FOR LAST 2 TERMS OF
ORDER BY` and the page stops early. 3,000 prompts x 8 KiB: default 100-prompt page 35-115 ms / 1.8 MiB (OR) against 0.2-2.0 ms (IS). On top of that
`PromptEndpoints.cs:44-83` and `CampaignEndpoints.cs:365-403` (the Forge's Atelier calls this with no limit, which defaults to 10,000) read `Template`,
`ParameterSchema` and `DefaultParameters` for every row to build `PromptSummaryDto`, and with `q`/`tag` they load up to 10,000 rows and filter in memory: 100-577 ms
and 52.8 MiB allocated per call (LOH churn) for 3,000 prompts. Cost scales with library size, not page size.
Fix: `CampaignId IS $c` in the repository, a summary-column projection for the list routes, and `LIKE` on name/description (or an indexed tag table) in SQL.

### F8 (Minor, M) - `/api/sessions/divine` ranks the whole installation, filters afterwards, and may do it twice
`SessionDivinationEndpoints.cs:122-170`: the Campaign and status filters live on `Sessions`, so the search ranks every `entry_embeddings` row (managed path;
vec0 can never load, `WeaveIndexAvailability`) and joins afterwards. When the first window leaves at most `limit` survivors and the ranking is not exhausted it runs a
second complete scan at window 500. P8: one scan over 40,000 x 1024-dim embeddings is 1.7-2.0 s (about 4.1 KB read per row), so a Campaign-filtered query is up to
twice that. Only reachable with `Features.Embeddings` and `Features.SessionSearch` on.
Fix: push the filter into the scan with an `Entries -> Sessions` join the way `SearchCampaignScopedAsync` does for Saga, or run the second pass only over the
rows the first window could not reach.

### F9 (Minor, S) - `DELETE /api/saga?confirm=true` keeps one result object per memory
`SagaEndpoints.cs:417-464`: the walk is paged at 128 so it "never holds a list the operator never bounded", but `results.AddRange(purged.Value.Results)` (`:462`)
retains a `CovenantSensitivePurgeResult` for every memory, including the unlabelled ones, only so `MarkProtectedWhenPurged` can ask `AllUnlabeled` at the end.
P6: 97-100 B per memory (9.6 MiB at 100k, 92.4 MiB at 1M, plus list-growth garbage). The purger also reads each label with its own statement
(`CovenantSensitiveRetentionPurgeCoordinator.ResolveLabelsAsync`, one `TryReadLabelAsync` per target).
Fix: keep a boolean for "anything not Unlabeled" and keep only Purged/Blocked results.

### F10 (Minor, M) - `GET /api/sessions/analytics` scans the whole Entries table for the by-model counts
`SessionEndpoints.cs:96-107` -> `SessionRepository.cs:1118-1130`: `SELECT ModelUsed, COUNT(*) FROM Entries WHERE ModelUsed <> '' GROUP BY ModelUsed`. `ModelUsed` has no
index (`Data/Schema/Tables/Entries.sql`), and the column follows `Content` in the record, so the plan is `SCAN Entries` + two temp B-trees: 232-356 ms at 60,000 entries
(the other two statements are 3-9 ms on covering indexes). CLI-only today (`ArcanumApiClient.cs:1172`), so rare.
Fix: a partial covering index on `Entries(ModelUsed) WHERE ModelUsed <> ''` in `Entries.sql` plus the usual transition file.

## Checked and found sound

* **Token streaming (`InferenceExecuteWriter`).** `WithHeartbeats` starts one timer per idle interval, not per token; P7: +302 ns and 176 B per event over 200k
  async events. The writer reuses one `ArrayBufferWriter` and one `Utf8JsonWriter` for the whole stream; the per-token `FlushAsync` is the required write.
* **Session SSE (`SessionEntrySseStreamWriter`, `StreamSession`).** One buffer per connection, released above 64 KiB; the live buffer is bounded (256 default,
  drop-oldest, logged once per episode); replay is capped at 500 (10,000 max); `SessionEventHub` drops its hub with the last subscriber.
* **`IOptionsSnapshot<ArcanumSettings>`** (used per request in `InferenceExecuteWriter`, `PromptEndpoints`, `CampaignEndpoints`): `CopySettings` copies 14 references, so a
  snapshot is cheap. `ResolveEventBus()`/`ResolveEmbeddings()` allocate a few small records per request, not per token.
* **Per-turn Campaign resolution (`CanonicalCampaignContextResolver`)**: one binding read, one `IN` query over the bounded ancestor-identity list against a unique index, one
  generation read; no path-prefix scan. `CampaignWorkspaceFill` only reads the Campaign when the request carries no directory.
* **Session entry paging** uses sequence keysets (`SessionRepository.GetEntriesAsync`), the session list pages by `UpdatedAt` cursor, and `GET .../entries` clamps to 1,000.
* **Session attachment revalidation** (`GET .../attachments`) memoizes per distinct source snapshot and writes back only a real change.
* **Codex endpoints, provider probe, attachment upload**: all byte-capped (codex cap, 4 MiB probe cap with `ResponseHeadersRead`, attachment read limit); the upload's extra
  `MemoryStream`+`ToArray` copy is bounded by the attach-size clamp and not worth a finding.
* **`memory status` counts**: eight statements, 13-30 ms at 60,000 entries, on covering indexes; linear but small.
* **Divination "second pass"** is limited to one extra scan (documented in the code comment), not the eight it replaced.
* Nothing in Tower holds a static cache, a user-keyed dictionary or a timer; no `.Result`/`.Wait()`/`Thread.Sleep`; no per-request regex construction.

## Coverage

Read in full: `CampaignEndpoints.cs`, `CampaignPathPolicy.cs`, `CanonicalCampaignContextResolver.cs`, `CanonicalCampaignRootEnricher.cs`, `CanonicalPingResolution.cs`,
`CodexEndpoints.cs`, `CovenantInspectionEndpoints.cs`, `CovenantMutationEndpoints.cs`, `ExecuteWorkspaceResolver.cs`, `InferenceExecuteWriter.cs`, `LexiconCurationEndpoints.cs`,
`LongRestEndpoints.cs`, `LoreEndpoints.cs`, `MemoryCampaignScopeReport.cs`, `MemoryEndpoints.cs`, `MemoryErasureEndpoints.cs`, `MemoryReviewEndpoints.cs`, `PromptEndpoints.cs`,
`PromptMapping.cs`, `ProviderTestEndpoints.cs`, `SagaCurationEndpoints.cs`, `SagaEndpoints.cs`, `SessionDivinationEndpoints.cs`, `SessionEndpoints.cs`, `SessionEntrySseStreamWriter.cs`,
`SessionMapping.cs`, `SpellAuthoringEndpoints.cs`, `SpellExecutionEndpoints.cs` (28 of 28). Traced into, not reviewed as units: `SpellRepository`, `SpellScanner`, `SpellSearchService`,
`PromptRepository`, `SessionRepository`, `SagaMemoryStore.{cs,Ranking}`, `SagaRankingReader`, `SagaRankingEvidenceReader`, `DivinationService`,
`CovenantSensitiveRetentionPurgeCoordinator`, `CampaignPathIdentityReader`, `SessionEventHub`, `ConfigurationBootstrapper.CopySettings`, `SessionAttachmentStore.RevalidateBoundSourcesAsync`.

Not measured (stated plainly): F1's per-version parse in `GET .../versions`; the heavy per-row path of `ReadRankingStatusAsync` (my probe only has the evidence part, the per-row
C# ranking evaluation over a large Saga is additional); workspace-chunk search was extrapolated from the entries scan (same SQL shape). All timings were taken under a
machine load of 56-106; expect the absolute figures to drop on an idle machine but the ratios and plans to hold.
