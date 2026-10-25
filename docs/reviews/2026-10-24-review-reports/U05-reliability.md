# U05 (Arcanum.Api / Tower) -- RELIABILITY lens

Tree: `.claude/worktrees/codebase-review` at `ac0d2d8e`. Unit: every file under `src/RetroDownfall.Arcanum.Api/Tower/` (28 files, 12,873 lines; `units.json` was not present in the scratchpad, so the unit was taken as the Tower directory, which matches the stated line count exactly).

Probes ran in a scratch worktree (`wt/U05-reliability`, since removed) against the in-process `ArcanumWebApplicationFactory` host (Testing environment, real SQLCipher Grimoire). The probe source is saved beside this report as `U05-reliability-probes.cs.txt`.

Overlap note: the U05 usability report independently found the same root causes for findings 4, 3 and 7 below (workspace id, codex read, status filter) and a sibling of finding 8 (missing `role`); they are kept here because each also has a reliability consequence (silent wrong answer / data loss) and the synthesis can merge them.

## Findings (most severe first)

### 1. [Important] Campaign PUT and campaign import write back the whole row they read, silently reverting a concurrent Sanctum change
- **Where:** `Tower/CampaignEndpoints.cs:220` (read) and `:274` (`repo.UpdateAsync(existing)`); import `:472` (read) and `:670-674` (settings write after the prompt swap); `Infrastructure/Repositories/CampaignRepository.cs:481-491` (`_db.Campaigns.Update(campaign)` marks every column modified, including `SanctumConfigJson`, `Path`, `Settings`). Same pattern in `Api/Security/SanctumEndpoints.cs:70,92-96`, and in `Tower/PromptEndpoints.cs:221,330` with `PromptRepository.cs:165-170`.
- **Claim:** `GetByIdAsync` is a raw-SQL read that returns a detached entity; `UpdateAsync` attaches it with `Update`, so the commit rewrites every column from the snapshot taken at the start of the request. Two requests that edit different fields of one Campaign (rename/settings vs. Sanctum) are last-writer-wins on the whole row. `SessionEndpoints.cs:1361-1363` already documents exactly this hazard for Sessions and fixed it with `PatchSessionAsync`; Campaigns and Prompts were not.
- **Failure scenario:** operator enables Sanctum (`PUT /api/campaigns/{id}/sanctum {"enabled":true}`) while The Forge saves the Campaign's name/settings (or a `POST /api/campaigns/{id}/import` with settings is running, whose read precedes a prompt-replace transaction and so has a wide window). The Forge/import commit lands second and Sanctum is silently back to `enabled:false`; both callers got 200. A security policy is disabled with no record.
- **Evidence (probe P2, PASS = defect reproduced):** read the row in scope A exactly as the PUT handler does, commit the Sanctum PUT over HTTP, then `UpdateAsync` the scope-A entity with only the name changed:
  ```
  sanctum PUT 200 {"data":{"enabled":true,...}}
  after B: enabled=True
  after A: enabled=False
  ```
  Related, by trace: a rename that loses the `NameLower` unique race throws `DbUpdateException` out of `UpdateAsync` (no constraint mapping, unlike `AddWithinImmediateTransactionAsync`) -> 500; a Prompt PUT whose row was deleted between read and write gets `DbUpdateConcurrencyException` -> 500 (`ClassifyConstraintViolation` only maps constraints).
- **Suggested fix:** give `ICampaignRepository` field-scoped writes (`UpdateHeaderAsync(id, name?, type?, description?, settings?)`, `UpdateSanctumAsync(id, config)`), each one parameterized `UPDATE ... SET <named columns>` in one statement (as `PatchSessionAsync` does), and map the unique violation to `Campaign.DuplicateName`. Same for Prompt PUT. Add a regression test that interleaves the two writes as P2 does.
- **Effort:** M.

### 2. [Important] A Lore key containing `/` (or equal to `.`/`..`) can be written but never read or deleted
- **Where:** `Tower/LoreEndpoints.cs:120-131` (POST accepts any trimmed key up to 256 chars), `:42-57` (`GET /lore/{key}`), `:141-157` (`DELETE /lore/{key}`).
- **Claim:** POST takes the key in the body; GET and DELETE take it as a single route segment. Kestrel/routing leave `%2F` encoded in a route value, so the CLI's and The Forge's `Uri.EscapeDataString(key)` (`ArcanumApiClient.cs:2405,2486`; `LoreService.cs:24,37`) arrive as `team%2Fstandards` and never match the stored `team/standards`. `System.Uri` also collapses a `.`/`..` segment, sending those keys to a different route (by trace; only the `/` case was probed).
- **Failure scenario:** `arcanum lore set team/standards "use tabs"` succeeds; `arcanum lore get team/standards` and `arcanum lore delete team/standards` both answer 404 `Grimoire.LoreNotFound` forever; `lore list` still shows it. There is no route that can delete it (only overwrite its value).
- **Evidence (probe P1):**
  ```
  POST 200 {"data":{"key":"team/standards",...}}
  GET /api/lore/team%2Fstandards -> 404 Grimoire.LoreNotFound
  DELETE /api/lore/team%2Fstandards -> 404 Grimoire.LoreNotFound
  LIST contains key: True
  ```
  `LoreEndpointTests` (3 tests) never use a key with a reserved character.
- **Suggested fix:** refuse `/`, `\`, `.` and `..` keys at POST (validation error naming the rule) so no new orphan can be created, and give existing ones an exit: accept the key in a query string or body for get/delete (e.g. `GET|DELETE /api/lore?key=`), updating the CLI and Forge clients and API.md.
- **Effort:** S.

### 3. [Important] GET codex reports an unreadable or over-cap CODEX.md as an existing empty file, so the next save destroys it
- **Where:** `Tower/CodexEndpoints.cs:261-280` (`ReadCodexDtoAsync`: `content ?? string.Empty` plus `exists = File.Exists(fullPath)`), fed by `Infrastructure/Workspaces/CodexReader.cs:107-126` which returns `null` for every non-success `SecureFileReadStatus` (`TooLarge`, `InvalidUtf8`, `Rejected`, `AccessDenied`, `IoError`).
- **Claim:** a real read fault is converted into a successful answer `{exists:true, content:""}`, indistinguishable from a genuinely empty file. PUT then accepts and atomically replaces the file with whatever the client sends.
- **Failure scenario:** a CODEX.md saved in Windows-1252 (an accented character), or one over the 256 KiB code-owned cap, is opened in The Forge's codex editor (`CampaignService.GetCodexAsync`/`PutCodexAsync`), which shows an empty document; the operator types a line and saves; the original file is gone. The CLI and inference path likewise see nothing and say nothing.
- **Evidence (probe P3):**
  ```
  latin1: success=True exists=True contentLength=0
  after save: new line            <- original bytes replaced
  oversize: success=True exists=True contentLength=0
  ```
  Existing `CodexEndpointTests`/`CodexRouteTests` (19 tests, all pass) cover links and round-trips only.
- **Suggested fix:** carry the `SecureFileReadStatus` out of `CodexReader.ReadCodexFileAsync` and have `ReadCodexDtoAsync` fail with typed codes (`Codex.ContentTooLarge`, a new `Codex.InvalidEncoding`, `Codex.Unreadable`) instead of success; document them in API.md; have The Forge refuse to open a codex that failed to read.
- **Effort:** S.

### 4. [Important] `POST /api/memory/search` with `workspaceId` never matches the id `GET /api/workspaces` returns, and answers "complete, zero results"
- **Where:** `Tower/MemoryEndpoints.cs:1385-1409` (`SELECT Path, Name FROM Campaigns WHERE Id = @id OR NameLower = lower(@id)`, then `return` with no results when nothing resolved).
- **Claim:** `Campaigns.Id` is stored as uppercase dashed text (`GrimoireEntitySql.Format`), the workspace registry publishes ids as `campaign.Id.ToString("N")` (`CampaignBackedWorkspaceRegistry.cs:274-276`), and `CampaignDto` serializes the lowercase `D` form. The resolver compares the raw string, so neither published form matches; an unresolved id is treated as "search nothing" and reported with `hasMore:false`.
- **Failure scenario:** `arcanum memory search needle --scope workspace --workspace 69773cce6fef404a8c34524e9d6a3478` (the id `arcanum workspace list` prints; the Command Reference calls the option "Optional workspace ID") returns no hits while the same search without the filter returns them.
- **Evidence (probe P8, one seeded chunk in the Campaign's path):**
  ```
  workspaceId=(none)                                  -> 1 result
  workspaceId=69773cce6fef404a8c34524e9d6a3478 (N)   -> 0 results, hasMore:false
  workspaceId=69773cce-6fef-404a-8c34-524e9d6a3478   -> 0 results, hasMore:false
  workspaceId=69773CCE-6FEF-404A-8C34-524E9D6A3478   -> 1 result
  workspaceId=<name>                                 -> 1 result
  ```
  No existing test passes `workspaceId` (rg over the test project finds none).
- **Suggested fix:** parse `workspaceId` with `Guid.TryParse` (any format) and bind `GrimoireEntitySql.Format(guid)`; otherwise treat it as a name; when it resolves to nothing, return a 404 `Workspace.NotFound` rather than an empty "complete" page.
- **Effort:** S.

### 5. [Minor] Provider connectivity probe has no deadline once response headers arrive
- **Where:** `Tower/ProviderTestEndpoints.cs:109-114` (`HttpClient.Timeout = 5 s`), `:129-131` (`HttpCompletionOption.ResponseHeadersRead`), `:144` and `:212-234` (body read bounded only by the request token).
- **Claim:** with `ResponseHeadersRead`, `HttpClient.Timeout` covers only `SendAsync`; the body reads run on `RequestAborted` alone. An endpoint that sends headers and then stalls (a wedged proxy, a server mid-crash) holds the probe until the caller disconnects; the 5 s `ProbeTimeout` the code declares does not apply.
- **Failure scenario:** The Forge "Test provider" against such an endpoint spins until its 100-second headers deadline and then reports a transport error rather than "timed out"; the server-side probe runs for the same time.
- **Evidence (probe P6, loopback listener that sends `200`, `Content-Length: 1000` and one byte):** `provider test outcome after 20.0s: client gave up (ProbeTimeout is 5s)`. The route is `[ExcludeFromCodeCoverage]` and has no test.
- **Suggested fix:** create one `CancellationTokenSource(ProbeTimeout)` linked to the request token and pass it to both `GetAsync` and `TryReadCappedStringAsync`; report its expiry as the timeout result, and let a request-token cancellation propagate instead of being caught as `TaskCanceledException`/`Exception`.
- **Effort:** S.

### 6. [Minor] Genuine Grimoire connection faults are reported as a planned maintenance refusal and logged at Debug
- **Where:** `Tower/MemoryEndpoints.cs:1752-1755` (`OpenConnectionAsync`) and `Tower/SessionDivinationEndpoints.cs:236-239`: `if (acquired.IsFailure) throw new GrimoireMaintenanceUnavailableException();`.
- **Claim:** `GrimoireOrdinaryConnectionFactory.AcquireScopedAsync` deliberately separates `MaintenanceUnavailable()` (closed admission, will reopen) from `Refused(...)` (`Covenant.Unavailable`: native runtime unavailable, connection could not be opened, scoped connection with no admitted provenance, non-canonical target; factory lines 78-143 and the remark at 496-504). These two call sites fold every refusal into the maintenance exception, which `ArcanumExceptionHandler.cs:61-80` answers as a retryable 503 "maintenance owns connection admission" with only a Debug log.
- **Failure scenario:** a SQLCipher open failure (wrong passphrase source, disk error) makes `/api/memory/status|sources|search|explain` and `/api/sessions/divine` tell the operator to wait for maintenance that is not happening, and leaves no Error log to diagnose it; other routes for the same fault surface a 500 with an Error log (e.g. `GrimoireRepository.cs:1426-1429` throws `InvalidOperationException(acquired.Error.Message)`).
- **Evidence:** trace through the factory and handler, line numbers above; not executed (needs a broken native open).
- **Suggested fix:** throw the maintenance exception only when `acquired.Error.Code == GrimoireMaintenanceUnavailableException.Code`; otherwise return the `Result` error through `ArcanumErrorMapper` (or throw `InvalidOperationException` so it is logged at Error).
- **Effort:** S.

### 7. [Minor] Session status filters are case-sensitive in SQL and unvalidated, so a valid-looking value returns a silent empty page
- **Where:** `Tower/SessionDivinationEndpoints.cs:83-91` (allow-list checked `OrdinalIgnoreCase`) vs. `:284-288` (`s."Status" = @status` with the caller's casing); `Tower/SessionEndpoints.cs:113,127-139` passes `status` to `SessionRepository.cs:86,126-129` (`"Status" = $p`, only `all` is case-insensitive). Stored values are always lowercase.
- **Claim:** the divine route's own comment says the allow-list exists so a bad status "fails fast with a clear 400 instead of silently matching zero rows", but `Archived` passes the allow-list and then matches zero rows. `GET /api/sessions` and `/api/campaigns/{id}/sessions` accept any string.
- **Evidence (probe P7, one archived session):** `status=archived -> 1`, `Archived -> 0`, `ARCHIVED -> 0`, `bogus -> 0` (all 200).
- **Suggested fix:** normalize to lowercase after the allow-list in divine; validate `status` in the two list routes against `active|archived|all` (400 otherwise) and lowercase it.
- **Effort:** S.

### 8. [Minor] `POST /api/sessions/{id}/entries` persists an undefined `MessageRole`
- **Where:** `Tower/SessionEndpoints.cs:1286-1292` (`Role = request.Role` with no `Enum.IsDefined` check). `MessageRole` uses `JsonStringEnumConverter<MessageRole>`, which accepts integers by default.
- **Failure scenario:** `{"role":7,"content":"x"}` is stored as role 7; every reader then disagrees about it: `EntryDto.role` is `"7"`, history assembly maps it to `user` (`InferenceContextBuilder.cs:370-380`), compaction counts it as `user`. A client that maps role strings strictly cannot render the transcript. (The usability report found the sibling: an omitted `role` silently stores `system`.)
- **Evidence (probe P5):** `POST entry role=7 -> 200 {"data":{...,"role":"7",...}}` and `GET entries` returns `"role":"7"`.
- **Suggested fix:** refuse a missing or undefined role with `Validation.InvalidBody` (`Enum.IsDefined`, and make `Role` nullable in `AppendEntryRequest` so absence is detectable), or use a string-only converter as `MemorySearchActionKind` does.
- **Effort:** S.

### 9. [Minor] Memory search is case-insensitive for ASCII only
- **Where:** `Tower/MemoryEndpoints.cs:1209`, `:1272`, `:1335-1337`, `:1417-1418` (`instr(lower(col), lower(@query))`). SQLite's built-in `lower()` folds ASCII only; the Lexicon arm (`:1619-1624`) uses .NET `OrdinalIgnoreCase`, so scopes disagree.
- **Claim:** API.md §(`/api/memory/search`) promises "Case-insensitive persisted-memory inspection".
- **Evidence (probe P9, entry "Meet at the Café Étoile"):** `café -> 1`, `CAFÉ -> 0`, `étoile -> 0`, `ÉTOILE -> 1`.
- **Suggested fix:** register a Unicode-aware case-fold function on the connection (`SqliteConnection.CreateFunction("arcanum_fold", s => s.ToLowerInvariant())`, AOT-safe) and use it in the four predicates, or document ASCII-only folding.
- **Effort:** S.

### 10. [Minor] Pinned-entry cap is check-then-act, and a pin of a vanished entry reports success
- **Where:** `Tower/SessionEndpoints.cs:1814-1832`: `GetPinnedEntryCountAsync` then `SetEntryPinnedAsync`, outside the per-session write lock that `GrimoireRepository.SetEntryPinnedAsync` (`GrimoireRepository.cs:805-824`) takes only around its own UPDATE; its `bool` result is ignored here and at `:1917`.
- **Failure scenario:** two concurrent pins at `maxPinned-1` both pass the count and both commit, exceeding `Sessions:MaxPinnedEntries`; an entry deleted between `GetEntryByIdAsync` and the UPDATE answers `200 true` although nothing was pinned.
- **Evidence:** trace (lines above); not executed.
- **Suggested fix:** one conditional statement under the session lock: `UPDATE Entries SET IsPinned=1 WHERE SessionId=$s AND Id=$e AND (IsPinned=1 OR (SELECT COUNT(*) FROM Entries WHERE SessionId=$s AND IsPinned=1) < $max)`, and map 0 rows to `EntryNotFound`/`TooManyPinned`.
- **Effort:** S.

### 11. [Minor] Filtered prompt listings and Campaign export silently stop at 10,000 prompts
- **Where:** `Tower/PromptEndpoints.cs:44-50,77-78` and `Tower/CampaignEndpoints.cs:365-375,401-402` (a `q`/`tag` filter reads the first 10,000 rows, filters in memory, answers `hasMore:false`); `Tower/CampaignEndpoints.cs:937-941` (export reads `ListQueryLimit(10_000)` and ignores `promptPage.HasMore`).
- **Claim:** a filtered answer claims completeness while rows were never read; the export bundle omits prompts past 10,000 without listing them, although the same method lists omitted spells precisely "so a partial bundle never reads as complete" (`:903-905`). A `replace` import of that bundle on another machine deletes the target Campaign's prompts and writes only the first 10,000, with no warning.
- **Evidence:** trace (lines above); `ListQueryLimit` clamps to 10,000 (`ArcanumSettingClamps`). Not executed (needs >10,000 prompts).
- **Suggested fix:** page through the repository until `HasMore` is false for export (or refuse with a typed error), and push `q`/`tag` filtering into the SQL query, or return `hasMore:true` whenever the 10,000-row read itself had more.
- **Effort:** S (export) / M (SQL filter).

### 12. [Minor] Manual compaction deletes transcript sized to the default model's provider, not the Session's model
- **Where:** `Tower/SessionEndpoints.cs:1866-1878` (`contextWindowLimit` from `ProviderResolver.TryResolveProviderForModel(settings, settings.DefaultModel, ...)`, fallback 8192).
- **Claim:** `POST /api/sessions/{id}/compact` irreversibly deletes the oldest non-pinned entries until the transcript fits a threshold of `contextWindowLimit` (API.md §`/compact`). The limit is taken from whatever provider serves `DefaultModel`, so a Session run on a large-context provider while the default model is a small local one is cut to a few thousand tokens. API.md does not say which model's window governs.
- **Failure scenario:** default model `gemma4:e4b` (8192 context), a Session conducted on a 128k model; `arcanum session compact` deletes nearly the whole transcript.
- **Evidence:** trace (`ContextCompressionService.CompressSessionAsync`, lines 177-217, uses only the passed limit). Not executed.
- **Suggested fix:** resolve the window from the Session's most recent `ModelUsed` (falling back to the default model), or require/accept an explicit model on the request, and document the rule.
- **Effort:** S.

### 13. [Minor] Default context-pin label is cut inside a surrogate pair
- **Where:** `Tower/SessionEndpoints.cs:1229-1231` (`target[..157] + "..."`).
- **Evidence (probe P4, 156 ASCII chars + an emoji):** stored label ends `U+FFFD ...` (`label length=160 last3=[FFFD,002E,002E,002E]`); the lone high surrogate is replaced on the UTF-8 round trip. The codebase already has `Utf8Truncation.SafeCharSliceLength` for exactly this (used two files away, `SessionDivinationEndpoints.cs:314`).
- **Suggested fix:** `target[..Utf8Truncation.SafeCharSliceLength(target, 157)] + "..."`.
- **Effort:** S.

## What I checked and found sound
- **Session stream subscription vs. replay (no gap):** `SessionEventHub.SubscribeAsync` registers the subscriber inside the synchronous prefix of the first `MoveNextAsync`, which `PumpSessionLiveAsync` runs when it is called at `SessionEndpoints.cs:1569`, before the replay read at 1605/1623; live entries published during replay are buffered and de-duplicated by `replayIds`. Traced `SessionEventHub.cs:71-107`.
- **Stream teardown:** every path after the pump starts is inside the `try` whose `finally` cancels `pumpCts` and awaits the pump (`:1594-1694`); the SSE lease is disposed by the `using` block; `SseStreamWriter.StreamAsync` keeps one outstanding `MoveNextAsync`, disposes the heartbeat CTS per iteration, and quiesces before disposing the enumerator. Traced.
- **Bounded replay is documented:** the `since` cursor is a bounded window, not a replay guarantee (API.md line 624), so dropping entries beyond `MaxStreamReplayEntries` is not a finding.
- **`SessionEntrySseStreamWriter`:** buffer and `Utf8JsonWriter` reuse is safe for sequential frames; a large frame's buffer is released and the writer re-pointed in `finally`. Read.
- **Multipart upload bounds:** Content-Length pre-check, `MaxRequestBodySize = cap+1`, the aggregate read stream's +1 sentinel with `checked` arithmetic, and `MultipartBodyLengthLimit` together bound the request; the original body is restored in `finally`. Read `SessionEndpoints.cs:350-425,2160-2381`.
- **Lease ownership transfer:** every route that builds a `CovenantProtectedJsonResult` nulls its local before returning and disposes it in `finally` otherwise (MemoryEndpoints, MemoryErasure, MemoryReview, CovenantMutation, CovenantInspection, LexiconCuration, Campaign and Session export). Checked each.
- **No lock-order deadlock between the ordinary connection lease and Covenant leases:** `HandleSearchAsync` takes the connection lease then a Covenant read; `BuildSourcesAsync` the reverse. Covenant acquisitions are non-blocking (`CovenantOperationGate.cs:126-160` return synchronously) and drains time out (`:970-990`), so the inversion cannot wedge. Traced.
- **`InferenceExecuteWriter`:** `WithHeartbeats` keeps one move outstanding, cancels and awaits it before disposing the source; continue-then-replay drains without writing; every failure arm marks the idempotency claim never-cache before writing an error frame. Traced.
- **Codex write:** containment before directory creation, same-directory temp file and atomic replace with a containment re-check, no cancellation after the first byte. Read.
- **Campaign import:** the whole bundle is validated before the `replace` sweep; writes after the first commit run on `CancellationToken.None`. Read and confirmed by existing `CampaignEndpointTests`.
- **Memory search budgets:** the row and byte budgets are shared across scopes, the one-extra-row overflow detection and `hasMore` are honest (`OpenSlice`/`CloseSlice`). Traced.
- **Session divination window / `hasMore`:** at most two searches, `hasMore` true whenever the ranking was not exhausted. Read.
- **Saga bulk delete walk:** keyset by position, so purged rows cannot make the walk skip rows. Read.
- **`CanonicalCampaignContextResolver` / `CanonicalCampaignRootEnricher`:** a missing Session fails before any path work; stale root comparisons only when a workspace was observed. Read against DESIGN §10.12/§21.16 comments.
- **PATCH session** writes only supplied fields (`PatchSessionAsync`); the entries keyset half-cursor is refused with 400. Read.
- **Erasure, review, mutation and inspection routes** are thin: body read through `ApiRequestJson.ReadAsync` (415/400/Kestrel status), shape validation before the service, authority by metadata. The erasure DTOs have no throwing constructors (checked `MemoryErasureContracts.cs`, `CovenantErasureContracts.cs`).
- **Existing tests run in the shared tree:** `CodexEndpointTests|LoreEndpointTests|CodexRouteTests` -> 19 passed (they do not cover findings 2 and 3).

## Probes run
| Probe | What | Result |
|---|---|---|
| P1 | POST lore `team/standards`, then GET/DELETE via `Uri.EscapeDataString` | 200 / 404 / 404, still listed -> finding 2 |
| P2 | Read campaign in scope A, commit Sanctum PUT, `UpdateAsync` scope-A entity | `enabled` true -> false -> finding 1 |
| P3 | `ReadCodexDtoAsync` on Latin-1 and 300 KiB CODEX.md; `WriteCodexAsync` after | success, exists, empty; file replaced -> finding 3 |
| P4 | Context pin with emoji straddling char 157, no label | label ends U+FFFD -> finding 13 |
| P5 | Append entry with `"role":7` | 200, stored and returned as `"7"` -> finding 8 |
| P6 | `/api/providers/test` against a loopback server that stalls after headers | no answer after 20 s (probe timeout 5 s) -> finding 5 |
| P7 | Archived session; list with four status spellings | 1 / 0 / 0 / 0 -> finding 7 |
| P8 | Seed one workspace chunk; memory search with five `workspaceId` forms | only uppercase-D and name match -> finding 4 |
| P9 | Entry with accented words; search case variants | non-ASCII case variants miss -> finding 9 |

Scratch worktree created with `git worktree add --detach .../wt/U05-reliability ac0d2d8e`, only the Arcanum test project built, removed with `git worktree remove --force` afterwards. No published-binary runs were needed.

## Reading coverage
Read in full (every line): `CampaignPathPolicy.cs`, `MemoryCampaignScopeReport.cs`, `ExecuteWorkspaceResolver.cs`, `SessionMapping.cs`, `CanonicalCampaignRootEnricher.cs`, `SessionEntrySseStreamWriter.cs`, `CanonicalCampaignContextResolver.cs`, `LongRestEndpoints.cs`, `PromptMapping.cs`, `LoreEndpoints.cs`, `CanonicalPingResolution.cs`, `SpellAuthoringEndpoints.cs`, `LexiconCurationEndpoints.cs`, `ProviderTestEndpoints.cs`, `CovenantInspectionEndpoints.cs`, `SagaCurationEndpoints.cs`, `SessionDivinationEndpoints.cs`, `InferenceExecuteWriter.cs`, `CodexEndpoints.cs`, `CovenantMutationEndpoints.cs`, `SagaEndpoints.cs`, `MemoryReviewEndpoints.cs`, `MemoryErasureEndpoints.cs`, `SpellExecutionEndpoints.cs`, `CampaignEndpoints.cs`, `PromptEndpoints.cs`, `MemoryEndpoints.cs`, `SessionEndpoints.cs` (28/28).

Read in part, to judge Tower behaviour: `SessionEventHub.cs`, `SseStreamWriter.cs`, `ClientDisconnect.cs`, `ApiRequestJson.cs` (1-200), `CovenantProtectedResults.cs` (1-200), `CovenantSensitiveDeletion.cs`, `GrimoireOrdinaryConnectionFactory.cs` (1-260, 490-510), `CovenantOperationGate.cs` (120-200, 960-1000), `CampaignRepository.cs` (60-110, 238-520, 700-790), `PromptRepository.cs` (155-200), `SessionRepository.cs` (80-130, 700-790), `GrimoireRepository.cs` (799-830, 1415-1440), `CodexReader.cs`, `CodexPathPolicy.cs`, `SecureFileReader.cs` (status enum), `SessionContextPinStore.cs`, `CampaignBackedWorkspaceRegistry.cs` (ToWorkspaceInfo), `EntryWeavingService.cs` (440-500), `InferenceContextBuilder.cs` (365-460), `ContextCompressionService.cs` (177-256), `ArcanumExceptionHandler.cs` (55-90), `SanctumEndpoints.cs` (40-120), `SanctumConfig.cs`, CLI `MemoryCommands.cs` (search) and `ArcanumApiClient.cs` (lore), Forge `CampaignService.cs`/`LoreService.cs`.

Docs read: DESIGN §16 (Tower-relevant bullets), §19 headings, API.md rows for sessions, stream, lore, codex, memory search/explain, compact, provider test; Compendium.README replay/watch paragraphs; Command Reference `memory search`. The October 7-8 register rows were not available in the repo; the 2026-10-21 review has no Tower finding, and none of the owner rulings listed in the task touch these findings.
