# U05 (Arcanum.Api / Tower) -- USABILITY lens

Tree: /Users/mat/Source/apps/RetroDownfall.Arcanum/.claude/worktrees/codebase-review @ ac0d2d8e (read only).
Probe worktree (removed after use): .../review/wt/U05-usability, detached at ac0d2d8e, one added file (tests/RetroDownfall.Arcanum.Tests/Api/Tower/U05UsabilityProbeTests.cs).
Kept for reproduction: .../review/results/A/U05-probe-sources/U05UsabilityProbeTests.cs (all probes below; drop it into tests/RetroDownfall.Arcanum.Tests/Api/Tower and run
`dotnet test ... --filter FullyQualifiedName~U05` with the SQLCipher fixture available) and GeneratedRouteBuilderExtensions.g.cs (the Request Delegate Generator output of the Api project, finding 4).
Raw probe logs: .../review/results/A/U05-probe4.log (renamed U05-probe4.log), U05-probe5.log.

Unit note: units.json did not exist when I started (the review/ scratchpad directory had not been created), so the unit was taken
as all of src/RetroDownfall.Arcanum.Api/Tower: 28 files, 12,873 lines, which matches the "about 12873 lines" in the task.

## Findings (ranked)

### 1. [Important] memory search --workspace never matches an ID as `workspace list` prints it, and an unresolved ID is a silent empty answer
File: src/RetroDownfall.Arcanum.Api/Tower/MemoryEndpoints.cs:1385-1409 (SQL at 1390).
- `SearchWorkspaceAsync` resolves the optional `workspaceId` with `SELECT Path, Name FROM Campaigns WHERE Id = @id OR NameLower = lower(@id)` bound to `workspaceId.Trim()`
  verbatim. `Campaigns.Id` is `TEXT` (BINARY collation) and EF stores the Guid as uppercase dashed text (this same file writes
  `campaignId.ToString("D").ToUpperInvariant()` for the sibling tables at 1123/1153; SessionDivinationEndpoints.cs:339-343 documents the rule).
- The ID an operator holds is the workspace id: `CampaignBackedWorkspaceRegistry.ToWorkspaceInfo` returns `campaign.Id.ToString("N")` (32 lowercase hex), which is what
  `GET /api/workspaces` and `arcanum workspace list` print, and the Command Reference documents `arcanum memory search --workspace <workspace>` as "Optional workspace ID".
  The Campaign API's own `id` is lowercase dashed. Neither equals the stored uppercase text.
- Lines 1406-1409 then `return` when an id was supplied but nothing resolved, so the scope contributes nothing and the response is 200 with no error, no `scopes[].hasMore`, no hint.
- Probe (Probe_MemorySearch_WorkspaceIdForms, one Campaign with one matching workspace_file_chunks row):
  `workspaceId='probe-ws'` (name) => 200 results=1; `'0a27a71fe0f24783994e30adaab557e2'` (the id from GET /api/workspaces) => 200 results=0;
  lowercase dashed => results=0; UPPERCASE dashed => results=1; `'no-such-workspace'` => 200 results=0.
- Every other Tower route that takes a workspace/campaign/session id either parses a Guid (`IWorkspaceRegistry.GetAsync` in the attachment-reference route, SessionEndpoints.cs:733) or answers 404
  (`memory/search` with an unknown sessionId, probe: 404 Session.NotFound). No test names `workspaceId` for memory search (rg over tests found none), so every existing test passes with this broken.
Failure: `arcanum workspace list` -> copy the id -> `arcanum memory search needle --scope workspace --workspace <id>` prints nothing and exits 0; the operator concludes the workspace index has no match.
Fix: `Guid.TryParse(workspaceId)` then match the canonical stored form (or resolve through IWorkspaceRegistry), keep the name fallback, and answer `Workspace.NotFound` (404) when an id was given and nothing resolved. Effort S. Add the missing test (id in N form, D form, name, unknown).

### 2. [Important] GET codex reports an unreadable or over-cap CODEX.md as `exists:true, content:""`
File: src/RetroDownfall.Arcanum.Api/Tower/CodexEndpoints.cs:275-279 (`content ?? string.Empty`, `exists = File.Exists`).
- `CodexReader.ReadCodexFileAsync` (Infrastructure/Workspaces/CodexReader.cs:TryReadAsync) returns null for every non-Success status of SecureFileReader: file over the cap (default 262,144 bytes),
  not valid UTF-8, aliased/multi-link file. The endpoint cannot tell that from an empty file.
- Probe (Probe_Codex_Oversize_And_InvalidUtf8): a 300,000-byte CODEX.md => `200 {"content":"","exists":true}`; a Latin-1 "caf\xE9 notes" file => `200 {"content":"","exists":true}`; a small file => content returned.
- Consumers: The Forge's CodexViewModel.ApplyDto (ViewModels/Workbench/CodexViewModel.cs:~291-296, :114) shows "Loaded." with an empty editor, so a Save replaces the operator's file; `arcanum campaign codex get` prints nothing;
  prompt assembly (CodexReader.ReadCodexAsync) silently drops the same file, so the model never sees the codex and nothing is logged. PUT refuses > cap (CodexEndpoints.cs:339) so only hand/external edits (e.g. a Windows ANSI save) get here.
- API.md:183 documents only `exists:false` for an absent file and 400 for linked files; nothing documents this state.
Fix: have the reader return the status; answer a typed error (new Codex code, 400/422) or `readable:false` + reason (`TooLarge` with the cap, `NotUtf8`, `Aliased`) so clients can refuse to overwrite; log the drop once at Information on the prompt-assembly path. Effort S-M.

### 3. [Important] Provider test: "See server logs for detail" points at a Debug line the host never keeps; the Forge then drops even the generic text
Files: src/RetroDownfall.Arcanum.Api/Tower/ProviderTestEndpoints.cs:72-83, 166-178; related: Infrastructure/Logging/LoggingBootstrapper.cs:45, TheForge.Ux/ViewModels/Arsenal/ModelsProvidersDataSource.cs:43.
- The URL-guard refusal and the connection/DNS/TLS failure are logged with `LogDebug` and the client text says "See server logs for detail.". The Serilog pipeline floor is a hard `MinimumLevel.Information()` (LoggingBootstrapper.cs:45;
  AuditLogPageReader.cs:246-250 states that a Debug event reaches neither the rolling file, the ring buffer behind GET /api/logs, nor `arcanum logs`), so the detail exists nowhere.
- Probe (Probe_ProviderTest_Messages_And_Logs): closed loopback port => 200 isReachable=false "The provider probe failed (connection, DNS, or TLS error). See server logs for detail."; `banana`, `file:///etc/passwd` and 169.254.169.254 all =>
  400 `Security.BlockedOutboundUrl` "The endpoint URL failed validation (invalid, unresolvable, or blocked). See server logs for detail." (a typo is coded as blocked). Afterwards GET /api/logs?limit=500 (ring buffer live: it holds this host's Information lines) contains no ProviderTest/probe line.
- The Forge (the only client of this route) maps any non-success envelope to null (`response is { IsSuccess: true, Data: ... } ? result : null`), so a 400 shows "Test failed." with no reason at all.
- Success path: a loopback server answering `200 text/html` (nginx page) => `isReachable:true, modelsFound:[]`, which the Forge renders "Reachable in 8 ms - 0 model(s).". A 401/403 says only "Endpoint returned HTTP 401." without pointing at the api key.
Failure: operator types `localhost:11434/v1` (no scheme) or has Ollama stopped and can neither fix it from the message nor from any log.
Fix: log both at Warning with the endpoint host only (no query/userinfo); return a sanitized reason class in the response (`InvalidUrl`, `Blocked`, `DnsFailed`, `ConnectionRefused`, `Tls`, `Unauthorized -> check apiKey`, `NotAModelList`) that leaks no resolved IP; code a malformed URL `Validation.InvalidBody`/`InvalidUrl`, not `BlockedOutboundUrl`; make the Forge show `Error.Message` for a failed envelope. Effort S-M.

### 4. [Minor] An unconvertible optional query value answers a bare 400 with no body; API.md says it gets the envelope
Files: src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs:728 (status-code hook), docs/Arcanum.API.md:364; affects every typed optional query parameter in the unit.
- API.md:364 states a query/route/header value "the binder cannot convert" is thrown (ThrowOnBadRequest) and answered 400 `Validation.InvalidBody` "Request parameters could not be bound to this route.".
  That holds for a *missing required* value (probe: `GET /api/campaigns/by-path` with no path => envelope) but not for an *unparsable optional* value: the Request Delegate Generator emits
  `else { wasParamCheckFailure = true; }` without calling `logOrThrowExceptionHelper.ParameterBindingFailed` and then `httpContext.Response.StatusCode = 400; return;`
  (generated GeneratedRouteBuilderExtensions.g.cs, every `int?/Guid?/DateTimeOffset?/enum?` parameter block). The status-code hook leaves bare 400s alone on purpose (ApiBootstrapper.cs:719-727).
- Probe (Probe_BadParameterMessages/Probe_BindingFailureShapes): `GET /api/saga?limit=abc`, `/api/saga?sessionId=abc`, `/api/lore?limit=abc`, `/api/campaigns?type=bogus`, `/api/sessions?role=bogus`, `/api/sessions?from=notadate`,
  `/api/spells/search?source=bogus` => `400`, content-type empty, length 0. (`DELETE /api/saga?confirm=yes` is the same shape: `bool?`.)
- Existing tests drive ArcanumExceptionHandler with a synthetic BadHttpRequestException (ArcanumExceptionHandlerTests.cs:~817-849), never a host request with a bad optional value, so they all pass.
Fix: have the hook envelope a bare 400 on a bodiless method (GET/DELETE) except endpoints that opt out (GET /api/presence), message naming the query keys the caller sent; or correct API.md. Effort S.

### 5. [Minor] Status filters: divine accepts "Archived" then matches nothing; GET /sessions silently returns an empty page for any non-exact status
Files: SessionDivinationEndpoints.cs:83-91 (validates OrdinalIgnoreCase) and :284-288 (binds `statusFilter.Trim()` to `s."Status" = @status`, a BINARY comparison); SessionEndpoints.cs:109-144 -> Infrastructure/Repositories/SessionRepository.cs:86,112-115.
- Probe (Probe_Divine_StatusCase, one active and one archived embedded session): `active` => 1 hit, `Active`/`ACTIVE` => 200 empty, `archived`/` archived ` => 1 hit, `Archived`/`ARCHIVED` => 200 empty. The validation comment promises "an unrecognized/garbage status fails fast ... instead of silently matching zero rows".
- `GET /api/sessions?status=Archived` and `?status=bogus` => 200 empty (probe), while `PATCH /api/sessions/{id}` validates and normalizes the same field (SessionEndpoints.cs:1340-1359). `arcanum session list --status` and `session divine --status` pass the string through.
Fix: lowercase after validation in divine; validate (`Session.InvalidStatus`, 400) and normalize in the list/campaign-sessions query, keeping `all`. Effort S.

### 6. [Minor] POST /api/prompts without `template` is a 500 Hub.Unhandled
File: PromptEndpoints.cs:143-149 (checks only name/version), :179 (`Template = request.Template`).
- `CreatePromptRequest.Template` is a non-nullable positional parameter but System.Text.Json passes null for a missing one; `Prompts.Template` is NOT NULL (Schema/Tables/Prompts.sql), the insert throws and is not a classified constraint violation.
- Probe: `{"name":"p","version":"1"}` => 500 `Hub.Unhandled` "An internal error occurred."; `"template":null` => same; with a template => 201. Import/clone/update already validate (PromptImportHelper.Validate).
Fix: reject null/absent template with `Prompt.InvalidRequest` (400) naming `template`; reuse PromptImportHelper.Validate. Effort S.

### 7. [Minor] "Memory management is disabled." names no key (four routes, default off)
File: SessionEndpoints.cs:1721, 1796, 1852, 1899 (delete entry, pin, compact, unpin).
- Probe: compact and delete-entry => 400 `Session.MemoryManagementDisabled` "Memory management is disabled.". `features.memoryManagement` defaults to false (Compendium.README.md:180), so every operator's first `session delete-entry|pin-entry|compact` hits it;
  the CLI prints only the message (CliFailureExit.Annotate adds nothing for non-connection errors), the Command Reference says only "when memory management is enabled" (and `delete-entry` does not mention the gate at all). Sibling messages do name keys (divine, CampaignRoots).
Fix: "Session memory management is disabled; set Arcanum:Features:MemoryManagement (features.memoryManagement) to true." Effort S.

### 8. [Minor] POST /api/sessions/{id}/entries without `role` silently stores a `system` entry
Files: SessionEndpoints.cs:1264-1292, Core/Tower/SessionDtos.cs:16 (`AppendEntryRequest(MessageRole Role, ...)`), Core/Storage/Entities/MessageRole.cs (System is the first member, value 0).
- A missing `role` deserializes to `default(MessageRole)` = `System`; SessionRepository.AddEntryAsync (Infrastructure/Repositories/SessionRepository.cs:419-469) applies no role rule. Probe: `{"content":"hello"}` => 200 `"role":"system"`; the entry then appears in
  `GET /entries` as a system message and is replayed to the model as a system instruction on the next turn (and the first-user-message auto-title does not run).
Fix: make `Role` nullable and answer 400 `Validation.InvalidBody` "role is required (system, user, assistant, tool)". Effort S.

### 9. [Minor] A wrong value or type in a valid JSON body is reported as "Request body could not be parsed as valid JSON." with no field
Files: src/RetroDownfall.Arcanum.Api/ApiRequestJson.cs:18 and :276-278 (`ResolveBodyFault`), Middleware/ArcanumExceptionHandler.cs:52-56; affects every bound body in the unit (role, scope, limit, campaignId, enum fields).
- Probe: `{"role":"bogus","content":"x"}`, `{"role":"user","content":5}` (entries), `{"query":"x","scope":"bogus"}`, `{"query":"x","limit":"abc"}` (memory search) => all 400 `Validation.InvalidBody`
  "Request body could not be parsed as valid JSON." with `details:null`. The JSON is valid; the message sends the operator to a syntax checker, and nothing names the field. The wrapped `JsonException` carries `Path` (`$.role`) which is safe to return (no value).
Fix: put `JsonException.Path` in `Error.Details` and word the message "Request body is not valid for this route (see details)."; keep the syntax-error wording for a true parse failure. Effort S.

### 10. [Minor] A provider-originated cancellation ends an NDJSON stream with "See server logs for details." and writes no log line
File: src/RetroDownfall.Arcanum.Api/Tower/InferenceExecuteWriter.cs:300-345 (the `catch (OperationCanceledException)` arm has no logger call; the sibling `catch (Exception ex)` arm at 361-368 logs type, responseStarted and trace).
- Probe (U05WriterProbeTests.Probe_ProviderTimeoutAsOce_LogsNothing_And_ExceptionPathLogs, same harness the repo's InferenceExecuteWriterTests uses): a stream that throws `TaskCanceledException` (HttpClient timeout shape) with RequestAborted not cancelled =>
  frame `{"type":"error","message":"Inference failed. Ensure the provider is running and reachable, then try again. See server logs for details.","data":"Hub.Error"}` and `logEntries=0`; an `InvalidOperationException` => the same frame and `logEntries=1`.
- DESIGN.md:1261 says an OperationCanceledException observed while the caller token is not cancelled "is treated as a provider failure: logs contain safe metadata/exception type only". InferenceExecuteWriterTests.cs:344-359 pins the frame but not a log line, so it passes.
  Whether production providers surface a raw OCE here depends on Api/Intelligence (outside this unit); the writer is the last place that can log it.
Fix: add `logger.LogError("Stream inference was cancelled by the provider path ... exception type {ExceptionType} (responseStarted=..., trace=...)", ex.GetType().FullName, ...)` in the arm before writing the frame (type only, as the design requires), and use `Activity.Current?.Id ?? TraceIdentifier` like every envelope. Effort S.


### Lower-confidence observations (not ranked)
- SessionDivinationEndpoints.cs:72 and SagaEndpoints.cs:122 say Embeddings AND SessionSearch/Saga "must both be true"; `ResolveEmbeddings` derives `Enabled` from the dependent feature alone (Compendium.README.md:164 says so), so the extra key is unnecessary. Harmless, so not ranked.
  The `weaveService.IsAvailable == false` branches ("The embedding provider is unavailable.") are effectively unreachable: ConfigurationValidator.cs:1753 refuses to start with an embedding-backed feature and no provider/model (my probe of that state failed at host start with ConfigurationValidationException).
- `GET /api/saga?sessionId=<unknown>` => 200 `[]` (probe) while memory status/sources/explain/search for an unknown session are 404 `Session.NotFound`: SagaEndpoints.cs:84-92. Minor, S: resolve/404 like the memory routes.

## What I checked and found sound
- Routes that read their own body (Saga curation, Lexicon curation, Long Rest, memory review, erasure) answer malformed JSON / wrong media type / oversize with the envelope (ApiRequestJson.ReadAsync; probe: malformed body on bound POST /api/prompts and /api/campaigns => 400 Validation.InvalidBody envelope).
- Status mapping for the Tower codes (Campaign.*, Prompt.*, Codex.*, Saga.*, Session.*, Lexicon.*, LongRest.*, MemoryErasure.*) is explicit in ArcanumErrorMapper; Codex.PathNotContained is 400 on GET/PUT/DELETE alike; Prompt.DuplicateVersion is 400 on create/clone/update/import alike.
- Documented limits are enforced and match API.md: lore/saga/campaign limits clamp to 1..10,000 (documented), memory search refuses out-of-range limit with 400 (documented), session entries limit clamps to 1..1000, divine limit clamps 1..50 and its window/hasMore logic matches API.md:85.
- Unknown ids answer 404 with the documented code for session/campaign/prompt/lore/lexicon/memory-status routes (probe 10).
- DELETE /api/sessions/{id} is a soft archive exactly as API.md:67 says (204, session stays readable as archived); export `format` is required and 400 Session.InvalidFormat otherwise, as documented.
- Spell version error text matches SpellVersionPathPolicy's regex ("alphanumeric and dots only"); campaign path errors name Arcanum:Security:CampaignRoots and require an existing directory (no accidental directory creation).
- Provider probe: size cap, ResponseHeadersRead, generic non-leaking messages and the 5 s timeout behave as the existing tests and DESIGN say (the defect is only where the detail goes, finding 3).
- Codex write path: containment before create, atomic replace, link refusal and over-cap refusal behave as API.md:184 documents.
- Stream plumbing (SessionEntrySseStreamWriter buffer reuse, WithHeartbeats idle timer, session SSE replay/live boundary and slow-client warnings) is consistent with DESIGN; slow-client and drop log lines carry the session id.
- Tower endpoints log almost nothing (6 log calls in 12.9k lines); failures are surfaced through typed envelopes, so the diagnostics gaps are the ones in findings 3 and 10.


## Probes run (all against the in-process ArcanumWebApplicationFactory host, Testing environment, real SQLCipher Grimoire)
1. Probe_MemorySearch_WorkspaceIdForms -- finding 1 (name 1 hit; N-form id 0; lowercase-D 0; UPPERCASE-D 1; unknown id 0, all HTTP 200).
2. Probe_Codex_Oversize_And_InvalidUtf8 -- finding 2 (300,000 bytes and a Latin-1 byte both 200 {content:"",exists:true}; small file returned).
3. Probe_ProviderTest_Messages_And_Logs -- finding 3 (closed port: 200/unreachable "... See server logs for detail."; banana, file:// and 169.254.169.254: 400 Security.BlockedOutboundUrl; HTML 200: isReachable true, 0 models; GET /api/logs after: no ProviderTest line while the buffer holds this host's Information lines).
4. Probe_BadParameterMessages / Probe_BindingFailureShapes -- finding 4 (bare 400, length 0, for saga/lore/campaigns/sessions/spells optional values; missing required `path` => envelope).
5. Probe_Divine_StatusCase -- finding 5 (see above). Probe_BindingFailureShapes also shows GET /api/sessions?status=Archived|bogus => 200 empty.
6. Probe_PromptCreate_NoTemplate -- finding 6 (500 Hub.Unhandled twice, 201 with a template).
7. Probe_MemoryManagementDisabledAndSagaConfirm -- finding 7 (400 Session.MemoryManagementDisabled on compact and delete-entry); also: DELETE /api/saga without confirm => 400 Saga.NotEmpty "requires ?confirm=true" (as documented);
   export without format => 400 Session.InvalidFormat (as documented); DELETE /api/sessions/{id} => 204 and the session stays readable with status archived (as documented).
8. Probe_EntryRoleAndEnumMessages -- findings 8 and 9.
9. U05WriterProbeTests.Probe_ProviderTimeoutAsOce_LogsNothing_And_ExceptionPathLogs -- finding 10.
10. Probe_MissingSessionAndMisc -- memory status/sources/explain/search with an unknown session => 404 Session.NotFound; GET /api/saga?sessionId=unknown => 200 [] (observation); unknown campaign/prompt/lore => 404 with the documented codes;
    providers/test `{}` => 400 "endpoint is required."; wrong type => 400 Validation.InvalidProviderType.
11. Probe_EmbeddingProviderNotConfigured -- host refused to start (ConfigurationValidationException): that state cannot be reached at runtime (see observations).

## Reading coverage
Read in full, line by line: all 28 files of src/RetroDownfall.Arcanum.Api/Tower (CampaignPathPolicy, MemoryCampaignScopeReport, ExecuteWorkspaceResolver, SessionMapping, CanonicalCampaignRootEnricher,
SessionEntrySseStreamWriter, CanonicalCampaignContextResolver, LongRestEndpoints, PromptMapping, LoreEndpoints, CanonicalPingResolution, SpellAuthoringEndpoints, LexiconCurationEndpoints, ProviderTestEndpoints,
CovenantInspectionEndpoints, SagaCurationEndpoints, SessionDivinationEndpoints, InferenceExecuteWriter, CodexEndpoints, CovenantMutationEndpoints, SagaEndpoints, MemoryReviewEndpoints, MemoryErasureEndpoints,
SpellExecutionEndpoints, CampaignEndpoints, PromptEndpoints, MemoryEndpoints, SessionEndpoints). Owning docs read: DESIGN section 19 in full plus the passages cited above (5110, 5134-5139, 1259-1261, 3846-3860),
API.md section 1 rows for sessions, lore, saga, memory, spells, campaigns, codex, prompts, providers/test and section 8.4 (line 364), Command Reference memory/session/campaign rows, Compendium.README rows for the keys the unit names.
Also read for context: ArcanumErrorMapper (all of it), ApiRequestJson, ArcanumExceptionHandler, ApiBootstrapper status-code hook, SpellApiResults, SpellVersionPathPolicy, CodexReader, WorkspaceRootPolicy, CampaignPathPolicy (Core),
CampaignBackedWorkspaceRegistry, PromptRepository.SaveAsync, SessionRepository.QueryAsync/AddEntryAsync/GetEntriesAsync, GrimoireRepository.ListLoreAsync, LoggingBootstrapper, WeaveService, ResolveEmbeddings.
