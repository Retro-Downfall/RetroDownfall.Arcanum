# Arcanum — API Reference

This document is the source of truth for Arcanum's native HTTP API, OpenAI-compatible API, wire formats, authentication behavior, request and response contracts, status mapping, and public error codes. It covers both general `/api` calls and the OpenAI-compatible `/v1` surface.

Architecture and design decisions belong in [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md). The readable architecture guide is [`Arcanum.Design.Human.md`](Arcanum.Design.Human.md). The complete public configuration model belongs in [`Compendium.README.md`](Compendium.README.md#complete-configuration-reference). Complete CLI syntax and option behavior belong in [`Arcanum.Command.Reference.md`](Arcanum.Command.Reference.md).

The established §8 contract numbers are retained in this extracted reference so existing issue, test, and documentation citations remain stable.

## 1. Complete API surface

**API surface (`MapArcanumEndpoints`):**

| Method | Path | Contract/purpose |
|--------|------|-----------------|
| GET | `/metrics` | Prometheus text-format metrics. |
| GET | `/api/health` | Authenticated `ApiResponse<HealthReportDto>` readiness snapshot. Healthy/Degraded reports return **200**; a valid Unhealthy report remains a success envelope with its component detail and returns **503**. Components include **`Conclave`**, whose detail leads with `state=disabled\|configured\|degraded\|healthy` (DESIGN §5.7.1); only `degraded` affects readiness. |
| GET | `/api/meta` | Instance metadata and feature flags for sidecar discovery (`ApiResponse<InstanceMetadataDto>`). Carries `conclaveEnabled`, `a2AServerEnabled`, `a2AClientEnabled`, `conclaveA2AState`, `a2AServerPath`, `a2AAgentCardPath`, and `a2AAllowedRemoteAgentCount`. |
| POST | `/api/server/quit` | Accept an authenticated operator shutdown request (`202` + `ApiResponse<bool>`), then stop the host after the acknowledgement completes. |
| GET | `/api/budget` | Daily budget snapshot (`ApiResponse<BudgetSummaryDto>`: enabled, daily limit, today's spend, remaining, spent percent, alert threshold; DESIGN §22.2). Spend is split into `localSpendUsd` (this instance's own inference — committed billable operations for the UTC day plus outstanding reservations, the same authority the budget gate enforces on) and `externalSpendUsd` (delegated A2A work, as reported by peers), with `todaySpendUsd` their sum. `unpricedDelegatedSendings` counts Sendings that settled today whose peer reported **no** cost — counted, never costed, so a non-zero value means the spend figures are a floor rather than the whole bill. CLI `arcanum budget`. |
| GET | `/api/grimoire/stats` | Grimoire database statistics (`ApiResponse<GrimoireStatsDto>`; database + WAL byte sizes and per-table row counts via `GrimoireStatsService`). |
| GET | `/api/data/status` | Unified retained-data inventory (`ApiResponse<DataRetentionStatus>`): typed rows/files/estimated bytes, effective policy, physical store, safe provenance, aggregate totals, and categories preserved outside the selected root (§8.20). |
| GET | `/api/data/retention` | Read the effective `Arcanum:Retention` policy (`ApiResponse<RetentionSettings>`; §8.20). |
| PUT | `/api/data/retention` | Update one typed rule (`RetentionRuleUpdateRequest` → `ApiResponse<RetentionSettings>`); values are normalized and clamped by the server (§8.20). |
| POST | `/api/data/prune/plan` | Build the complete non-mutating selected plan (`DataRetentionRequest` → `ApiResponse<DataRetentionPlan>`), represented through internal checkpoint pages rather than a total candidate ceiling (§8.20). |
| POST | `/api/data/prune` | Re-plan and apply through a durable operation (`DataRetentionApplyRequest` → `ApiResponse<DataRetentionApplyResult>`; optional stale-plan guard; §8.20). |
| DELETE | `/api/data/sessions/{id}` | Explicit dependency-aware session deletion (`ApiResponse<DataRetentionApplyResult>`; pins, holds, and active work block; §8.20). |
| DELETE | `/api/data/attachments/{id}` | Explicit attachment-version deletion including owned bytes and derived indexes (`ApiResponse<DataRetentionApplyResult>`; §8.20). |
| POST | `/api/data/memory/reset` | Reset exactly one `MemoryResetScope` from `MemoryResetRequest` (`ApiResponse<DataRetentionApplyResult>`; §8.20). |
| POST | `/api/data/factory-reset` | Reset managed data beneath the selected root from `FactoryResetRequest`; requires exact `confirmation: "factory-reset"` and preserves backups/configuration/security material/out-of-root data (`ApiResponse<DataRetentionApplyResult>`; §8.20). |
| POST | `/api/data/factory-reset/plan` | Loopback-only data-phase planning for an installation reset. Accepts `Global` with no workspace binding or `Workspace` with an exact registered Campaign binding (`ApiResponse<DataRetentionPlan>`; §8.20). |
| GET | `/api/config` | Read the latest successfully persisted `ArcanumSettings`; provider endpoints remain redacted, while secret environment-variable references are returned without resolving their values (`ApiResponse<ArcanumSettings>`; §8.12). |
| PUT | `/api/config` | Validate and write a full settings snapshot to `arcanum.json` (`ApiResponse<bool>`; §8.12). |
| POST | `/api/config/validate` | Validate settings without writing (`ApiResponse<bool>`; §8.12). |
| GET | `/api/models` | Flatten models from the latest successfully persisted provider snapshot (`ApiResponse<ModelInfoDto[]>`; endpoint redacted as `"***"`; models on a Familiar provider's `hiddenModels` list are omitted; read-only, no connectivity checks; §8.12). |
| GET | `/api/providers` | List the latest successfully persisted providers with `apiKey`/`endpoint` redacted (`ApiResponse<ProviderInfoDto[]>`; `models` excludes the hide list and `hiddenModels` reports it; a Familiar reports an empty `endpoint` and `credentialEnvironmentVariable`; read-only; §8.12). |
| GET | `/api/providers/{name}/familiar-probe` | Familiar readiness for one `ClaudeCodeCli`/`CodexCli` provider (`ApiResponse<FamiliarProbeResult>`; asks the CLI for its own status, never for a completion, so it is non-billable; never reads the CLI's credential store and carries no account material; **404** `Provider.NotFound`, **400** `Validation.InvalidProviderType` for an OpenAI-compatible row; §8.12.1). |
| GET | `/api/perception/look` | Eye of the World snapshot (optional `directory` query; requires `Arcanum:Security:PerceptionWorkspaceRoots`; **403** when unset). |
| POST | `/api/intelligence/ping` | Buffered inference. |
| POST | `/api/intelligence/ping-stream` | NDJSON streaming inference (same `PingRequest` extensions as buffered ping). |
| POST | `/api/intelligence/human-response` | Submit human-in-the-loop answer. |
| POST | `/api/intelligence/arsenal` | Spell names, metadata-only `SpellSummary[]`, native tools, and MCP server status. |
| POST | `/api/intelligence/mana` | Read-only diagnostic Mana (token) counter (`ApiResponse<ManaCountResult>`; body `ManaCountRequest` { `messages`, `prompt`, `model`, `tools` }). |
| POST | `/api/intelligence/context/inspect` | Read-only effective-turn preview (`ContextPreviewRequest` → `ApiResponse<ContextPreviewResult>`); accepts optional forced-Spell, preview-only text/image context, tool/system-prompt shaping, and inference-option fields while reusing production assembly components without main inference, tool invocation, attachment persistence, or assistant-entry persistence. |
| POST | `/api/web/search` | First-class bounded web search (`WebSearchWorkflowRequest` → `ApiResponse<WebSearchWorkflowResult>`; citations and provider usage; DESIGN §11.27). |
| POST | `/api/web/browse` | First-class bounded static page read (`WebBrowseWorkflowRequest` → `ApiResponse<WebBrowseWorkflowResult>`; JavaScript mode degrades explicitly when no renderer is configured; DESIGN §11.27). |
| POST | `/api/web/research` | Server-owned progress-driven research as NDJSON `WebResearchStreamFrame` lines (policy/progress/terminal-reason/result/error); the request can carry effective Campaign/Workspace/Session/Model context, current-turn text/image context, and synthesis sampling controls (DESIGN §11.27). |
| GET | `/api/mcp` | List managed MCP servers (`ApiResponse<McpServerInfo[]>`; DESIGN §5.6). |
| GET | `/api/mcp/{name}` | One managed MCP server (`ApiResponse<McpServerInfo>`); optional `workingDirectory` query for disambiguation. |
| POST | `/api/mcp/{name}/start` | Start one MCP server (`ApiResponse<bool>`); optional `workingDirectory` query. |
| POST | `/api/mcp/{name}/stop` | Stop one MCP server (`ApiResponse<bool>`); optional `workingDirectory` query. |
| POST | `/api/mcp/{name}/restart` | Restart one MCP server (`ApiResponse<bool>`); optional `workingDirectory` query. |
| POST | `/api/mcp/trust-workspace` | Approve a workspace-local `mcp.json` for auto-start (`ApiResponse<bool>`; body `{ "workingDirectory": "..." }`; DESIGN §5.6). |
| POST | `/api/mcp/reload` | Reload MCP connections (global nuclear reload — DESIGN §5.6). |
| POST | `/api/mcp/tools/invoke` | **Diagnostic MCP Invocation** — policy-constrained direct invoke of an **external** MCP tool by an operator (`ApiResponse<McpToolInvokeResponse>`). |
| GET | `/api/sessions` | Search/list Grimoire sessions (`ApiResponse<SessionQueryResult>`; DESIGN §11.16). |
| POST | `/api/sessions` | Create session (`ApiResponse<SessionDetailDto>`; **201**). |
| GET | `/api/sessions/analytics` | Session analytics (`ApiResponse<SessionAnalytics>`; DESIGN §11.16). |
| GET | `/api/sessions/{id}` | Session metadata (`ApiResponse<SessionDetailDto>`; **404** when missing). |
| GET | `/api/sessions/{id}/entries` | Entry history (`ApiResponse<EntryDto[]>`; optional `offset`, `limit`, and keyset cursor parameters `beforeCreatedAt` / `beforeId`). `?countOnly=true` returns `ApiResponse<SessionEntryCountDto>` instead of entry rows. |
| POST | `/api/sessions/{id}/entries` | Append entry manually (**404** / **400**; publishes live SSE). |
| PATCH | `/api/sessions/{id}` | Update title or status. |
| DELETE | `/api/sessions/{id}` | Archive session (**204**; soft delete). |
| GET | `/api/sessions/{id}/export` | Export JSON or Markdown (`ApiResponse<SessionExportResult>`). |
| POST | `/api/sessions/{id}/rest` | Enqueue Campaign Log consolidation (**202** + `ApiResponse<bool>` when accepted; **503** + `Session.RestQueueFull` when the bounded queue rejects). |
| GET | `/api/sessions/{id}/stream` | SSE replay + live entry stream; optional `since=<entry-guid>` resumes after an Entry in that Session. A missing or foreign cursor returns **404** `Session.EntryNotFound` before SSE headers are written. |
| GET | `/api/sessions/{id}/attachments` | Revalidate tracked sources asynchronously, then list **bound** session attachments (`ApiResponse<SessionAttachmentDto[]>`; includes `indexingStatus`, the snapshot `RelativePath` for Reveal, and sanitized source provenance/refreshability; never an absolute source path; DESIGN §10.2.5). |
| POST | `/api/sessions/{id}/attachments` | Create a snapshot-only bound attachment from multipart field `file` (optional `logicalName` form field); returns `201` + `ApiResponse<SessionAttachmentDto>`. The filename and declared MIME are hints; the server validates name, detected content, kind-specific byte limit, MIME/Scrying policy, strict encoding for text, and Session byte/version budgets. Unsupported binary/PDF/Office content remains a valid `Binary` attachment with `NotEligible` indexing status. |
| POST | `/api/sessions/{id}/attachments/reference` | Create a refreshable live reference from `CreateSessionAttachmentReferenceRequest` (`workspacePath`, optional `workspaceId`, optional `logicalName`); the server alone resolves/authorizes/reads the source and persists the already-verified bytes; returns `201` + `ApiResponse<SessionAttachmentDto>`. |
| GET | `/api/sessions/{id}/attachments/{attachmentId}/content` | Stream the authenticated plaintext of that stored bound snapshot with `Content-Disposition: attachment`, `Cache-Control: no-store`, and `X-Content-Type-Options: nosniff`. Never returns or redirects to a live source path. |
| POST | `/api/sessions/{id}/attachments/{attachmentId}/refresh` | Operator-triggered secure refresh through the same source-validation/persistence core as `refresh_session_file`; returns `ApiResponse<AttachmentRefreshEvent>` only after the backend has reused or persisted the confirmed current version. |
| GET | `/api/sessions/{id}/context-pins` | List durable, structured session context pins. |
| POST | `/api/sessions/{id}/context-pins` | Create or update a context pin by `(session, kind, stable target)`; accepts file, directory snapshot, symbol/range, session entry, attachment, URL, and diagnostic kinds. |
| DELETE | `/api/sessions/{id}/context-pins/{pinId}` | Remove a durable context pin without changing `Entries.IsPinned`. |
| POST | `/api/sessions/{id}/fork` | Create an independent branch of a session, optionally truncated at `upToEntryId` (**201**; DESIGN §11.16.1). |
| POST | `/api/embeddings/reset` | Truncate embedding tables for RAG dimension-change recovery (requires `?confirm=true`; optional `?scope=all\|entry\|workspaceFile\|saga\|sessionAttachment\|tapestry`, default `all`; snake-case aliases accepted). `tapestry` drops exactly the three `tapestry_*` tables — the leaf corpora stay indexed and the background sweep rebuilds every tree (DESIGN §21.11). |
| DELETE | `/api/sessions/{id}/entries/{entryId}` | Delete a single entry and its `entry_embeddings` / optional vector row in the same transaction (**204**). |
| POST | `/api/sessions/{id}/entries/{entryId}/pin` | Pin an entry so it is always included in inference context, even when compression would otherwise drop it. |
| DELETE | `/api/sessions/{id}/entries/{entryId}/pin` | Unpin a previously pinned entry. |
| POST | `/api/sessions/{id}/compact` | Manually compress session context by deleting the oldest non-pinned entries until the token count is below the effective threshold. |
| POST | `/api/sessions/divine` | Session Divination — semantic search over Grimoire entries embedded by `EntryWeavingService` (`ApiResponse<SemanticSearchResult>`; body `SemanticSearchRequest` with required `query` and optional `campaignId`, `status` (`active` or `archived`), and `limit`). |
| GET | `/api/lore` | List lore entries (`ApiResponse<ListPageResult<LoreDto>>`; paginated with optional `?limit=` and `?offset=`; the default page size is code-owned). |
| GET | `/api/lore/{key}` | Get lore by key. |
| POST | `/api/lore` | Upsert lore entry. |
| DELETE | `/api/lore/{key}` | Delete lore entry. |
| GET | `/api/saga` | Paginated listing of Saga memories (`ApiResponse<SagaMemoryDto[]>`; optional `?q=` substring, `?sessionId=`, `?limit=` clamped to 1–10,000, and non-negative `?offset=`). |
| POST | `/api/saga/divine` | Semantic search over Saga memories (`ApiResponse<SagaSearchResult>`; body `SagaSearchRequest` with required `query` and optional `limit`; **503** when the embedding provider is unavailable). |
| DELETE | `/api/saga/{id}` | Delete a single Saga memory (**204**; **404** `Saga.NotFound`; DESIGN §21.9). |
| DELETE | `/api/saga` | Delete every Saga memory, embedding, and extraction watermark (**204**; requires `?confirm=true`, else **400** `Saga.NotEmpty`; DESIGN §21.9). |
| GET | `/api/saga/stats` | Aggregate Saga memory summary (`ApiResponse<SagaStats>`: total count, session count, oldest/newest `CreatedAt`; DESIGN §21.9). |
| GET | `/api/memory/status` | Global gates and persisted counts for each distinct memory store (`ApiResponse<MemoryStatusDto>`), including the `Tapestry` row gated by `Arcanum:Features:Tapestry`. Reads are not blocked when a prompt-time feature is disabled. The Tapestry count includes only **published** generations — a staging generation is invisible here for the same reason it is invisible to retrieval. |
| GET | `/api/memory/status/{sessionId}` | Session-narrowed memory status; **404** when the session is missing. Session-owned entries, pins, Summary, attachments/chunks, Saga, Campaign-backed workspace index, and session-scoped Tapestry trees are counted independently; installation-scoped workspace trees are excluded from a session-narrowed count. |
| GET | `/api/memory/sources[/{sessionId}]` | Safe provenance and retention descriptions plus gates/counts (`ApiResponse<MemorySourcesDto>`); no attachment bytes or host absolute paths. |
| POST | `/api/memory/search` | Case-insensitive persisted-memory inspection (`MemorySearchRequest` → `ApiResponse<MemorySearchResponse>`). `scope` is `session`, `attachments`, `workspace`, `saga`, `lexicon`, or `all`; default `all` is echoed. Optional `sessionId`/`workspaceId` narrow ownership. Every result includes scope, source id, provenance, and retention. No embeddings or promotion side effect. |
| GET | `/api/memory/explain[/{sessionId}]` | Explains conditional next-turn eligibility by source (`ApiResponse<MemoryExplainDto>`), distinguishing persisted data from actual inclusion. |
| GET | `/api/memory/lexicon` | Lists every Lexicon entity (`ApiResponse<LexiconListDto>`); optional `?q=` searches name, type, and facts without the prompt-time match cap. |
| GET | `/api/memory/lexicon/{name}` | Exact case-insensitive Lexicon lookup (`ApiResponse<LexiconEntryDto>`; **404** `Lexicon.NotFound`). |
| DELETE | `/api/memory/lexicon/{name}` | Deletes exactly one named Lexicon entity (**204**; **404** `Lexicon.NotFound`). CLI callers must confirm. Other stores are unchanged. |
| GET | `/api/spells` | Compatibility list is still `ApiResponse<SpellSummary[]>`; `paged=true` selects the bounded `ApiResponse<SpellCatalogPage>` contract with `workspace`, `q`, `tag`, `tool`, `source`, and opaque `cursor` (§8.14). |
| GET | `/api/spells/{name}` | Spell detail (`ApiResponse<SpellDetail>`; optional `workspace` query; **404** when missing). |
| POST | `/api/spells` | Create workspace spell (`ApiResponse<bool>`; optional `workspace` query; **400** validation). |
| PUT | `/api/spells/{name}` | Update workspace spell (`ApiResponse<bool>`; optional `workspace` query; **404** `Spell.NotFound`; **400** on built-in or validation failure). |
| DELETE | `/api/spells/{name}` | Delete workspace spell (**204** on success; **404** `Spell.NotFound` so delete-if-exists can treat a missing spell as already gone; **400** on built-in or validation failure; §8.14). |
| GET | `/api/spells/search` | Multi-source spell search (`ApiResponse<SpellSummary[]>`; `?q=`, `?tag=`, `?tool=`, `?source=`, `?campaignId=`, `?workspace=`; §8.14). |
| POST | `/api/spells/{name}/validate` | Validate spell metadata and declared tools (`ApiResponse<SpellValidationResultDto>`; §8.14). |
| POST | `/api/spells/{name}/export` | Export portable spell bundle (`ApiResponse<SpellExportDto>`; §8.14). |
| POST | `/api/spells/import` | Import spell into workspace (`ApiResponse<SpellSummary>`; **400** `Spell.NameCollision`; §8.14). |
| POST | `/api/spells/{name}/execute` | Forced-spell buffered inference (`ApiResponse<PromptResponseDto>`; body `SpellExecuteRequest`; optional `?workspace=`, `?version=` (string label); **404** `Spell.NotFound`; DESIGN §19). |
| POST | `/api/spells/{name}/execute-stream` | Forced-spell NDJSON streaming inference (same request/query as execute; DESIGN §19). |
| GET | `/api/spells/{name}/versions` | List `SPELL.md` (active row) and `SPELL.v{label}.md` files (`ApiResponse<SpellVersionDto[]>`; **string** `version` label and `isActive` flag; optional `?workspace=` / `?campaignId=` resolution). |
| GET | `/api/spells/{name}/versions/{version}` | Read a spell version's editable body (`ApiResponse<SpellVersionDetailDto>`; optional `?workspace=` / `?campaignId=`; use the version label reported by the list route, including `(active)` when no explicit active label exists). |
| POST | `/api/spells/{name}/versions` | Create a new spell version file (`ApiResponse<SpellVersionDto>`; body `CreateSpellVersionRequest` with required `version` / `body` and optional `workspace`; **201**). |
| PUT | `/api/spells/{name}/versions/{version}` | Overwrite an existing version's body, preserving frontmatter (`ApiResponse<SpellVersionDto>`; body `UpdateSpellVersionRequest`; **404** when the version does not exist; §8.14). |
| POST | `/api/spells/{name}/versions/{version}/activate` | Activate a version, swapping its content into `SPELL.md` and preserving the prior active content as `SPELL.v{previousLabel}.md`. |
| POST | `/api/spells/{name}/clone` | Clone a spell (built-in or workspace) into a new workspace spell (`ApiResponse<SpellSummary>`; body `CloneSpellRequest` with required `newName` and optional `workspace`). |
| POST | `/api/spells/{name}/cast` | Dry-run cast preview: assembled system prompt, resonant dependencies, attuned tools, and spell scripts, **without** LLM inference. |
| GET | `/api/campaigns` | List Grimoire-backed campaigns (`ApiResponse<ListPageResult<CampaignDto>>`; optional `?type=`; DESIGN §19). |
| GET | `/api/campaigns/by-path` | Lookup campaign by filesystem path (`ApiResponse<CampaignDto>`; required `?path=`; **404** `Campaign.NotFound`; DESIGN §19). |
| GET | `/api/campaigns/{id}` | Campaign detail (`ApiResponse<CampaignDto>`; **404** when missing; DESIGN §19). |
| POST | `/api/campaigns` | Register campaign directory (`ApiResponse<CampaignDto>`; **201** + `Location`; creates `.arcanum/`; DESIGN §19). Name and path uniqueness is pre-checked and then enforced by the database unique index: a registration that loses a concurrent race is rejected by the index and `CampaignRepository.AddAsync` re-reads the conflicting row to return the same **400** `Campaign.DuplicateName` or `Campaign.DuplicatePath` the pre-check would have produced. |
| PUT | `/api/campaigns/{id}` | Update campaign (`ApiResponse<CampaignDto>`; DESIGN §19). |
| DELETE | `/api/campaigns/{id}` | Remove campaign (**204**; DESIGN §19). |
| GET | `/api/campaigns/{id}/spells` | Spells scoped to a campaign, merging built-ins with campaign spells shadowing them (`ApiResponse<SpellSummary[]>`; `?q=`, `?tag=`, `?tool=`; **404** `Campaign.NotFound`; DESIGN §19). |
| GET | `/api/campaigns/{id}/prompts` | Prompts scoped to a campaign (`ApiResponse<ListPageResult<PromptSummaryDto>>`; `?q=`, `?tag=`; **404** `Campaign.NotFound`; DESIGN §19). |
| GET | `/api/campaigns/{id}/sessions` | Sessions scoped to a campaign (`ApiResponse<SessionQueryResult>`; `?status=`, `?search=`, `?limit=`, `?beforeUpdatedAt=`; **404** `Campaign.NotFound`; DESIGN §19). |
| POST | `/api/campaigns/{id}/export` | Export spells + prompts + settings (`ApiResponse<CampaignExportDto>`; DESIGN §19). |
| POST | `/api/campaigns/{id}/import` | Import portable campaign bundle (`ApiResponse<CampaignImportResultDto>`; DESIGN §19). |
| GET | `/api/campaigns/{id}/codex` | Read campaign `CODEX.md` (`ApiResponse<CodexContentDto>`; `exists: false` when file absent; **404** `Campaign.NotFound`; **400** `Codex.PathNotContained` when `CODEX.md` is a link resolving outside the campaign root; DESIGN §19). |
| PUT | `/api/campaigns/{id}/codex` | Create or overwrite campaign `CODEX.md` (`ApiResponse<CodexContentDto>`; body `{ "content": "..." }`; **400** when over the code-owned CODEX size limit or `Codex.PathNotContained`; DESIGN §19). |
| DELETE | `/api/campaigns/{id}/codex` | Delete campaign `CODEX.md` (**204**; unlinks the entry itself, so a link is removed rather than followed; DESIGN §19). |
| GET | `/api/codex` | Read global `~/.config/arcanum/CODEX.md` (`ApiResponse<CodexContentDto>`; **400** `Codex.PathNotContained` when the file resolves outside the Grimoire directory; DESIGN §19). |
| PUT | `/api/codex` | Create or overwrite global CODEX (`ApiResponse<CodexContentDto>`; same size and containment failures as the campaign route; DESIGN §19). |
| DELETE | `/api/codex` | Delete global CODEX (**204**; DESIGN §19). |
| GET | `/api/campaigns/{campaignId}/sanctum` | Campaign Sanctum config (`ApiResponse<SanctumConfig>`; default `Enabled: false`; DESIGN §11.15). |
| PUT | `/api/campaigns/{campaignId}/sanctum` | Update Sanctum config (`ApiResponse<SanctumConfig>`; body `SanctumConfig`). |
| GET | `/api/campaigns/{campaignId}/sanctum/breaches` | Paginated Sanctum breach history (`ApiResponse<SanctumBreachQueryResult>`; `?limit=` default 100 clamp 1–1,000, `?before=` ISO 8601 cursor, `?tool=` filter). |
| GET | `/api/wards` | List active wards (`ApiResponse<WardDto[]>`; DESIGN §11.14). |
| GET | `/api/wards/{id}` | Active ward detail (`ApiResponse<WardDto>`; **404** `Ward.NotFound`). |
| POST | `/api/wards/{id}` | Resolve a ward (`ResolveWardRequest`: `allow`, optional `reason`); returns `ApiResponse<WardResolutionDto>` whose additive `origin` is always `human` here. **409** `Ward.AlreadyResolved` when another resolver — or the auto-approval policy — got there first. |
| GET | `/api/prompts` | List/search prompts (`ApiResponse<ListPageResult<PromptSummaryDto>>`; `?campaignId=`, `?q=`, `?tag=`; DESIGN §19). |
| GET | `/api/prompts/{id}` | Prompt detail (`ApiResponse<PromptDetailDto>`; **404** `Prompt.NotFound`; DESIGN §19). |
| GET | `/api/prompts/by-name/{name}/versions` | List versions for a prompt name (`ApiResponse<PromptVersionDto[]>`; optional `?campaignId=`; DESIGN §19). |
| POST | `/api/prompts` | Create prompt version (`ApiResponse<PromptDetailDto>`; **201**; **400** `Prompt.DuplicateVersion`; DESIGN §19). |
| PUT | `/api/prompts/{id}` | Update prompt (`ApiResponse<PromptDetailDto>`; DESIGN §19). |
| DELETE | `/api/prompts/{id}` | Delete prompt (**204**; DESIGN §19). |
| POST | `/api/prompts/{id}/render` | Render template with parameters (`ApiResponse<PromptRenderResultDto>`; **400** `Prompt.MissingParameter` / `Prompt.UnknownParameter`; DESIGN §19). |
| POST | `/api/prompts/{id}/test` | Assemble system prompt without LLM (`ApiResponse<PromptTestResultDto>`; DESIGN §19). |
| POST | `/api/prompts/{id}/execute` | Render template and run session-backed inference (`ApiResponse<PromptResponseDto>`; body `PromptExecuteRequest`; honors `sessionId`; DESIGN §19). |
| POST | `/api/prompts/{id}/execute-stream` | Same as execute with NDJSON `IntelligenceEvent` stream. |
| POST | `/api/prompts/{id}/export` | Portable prompt JSON (`ApiResponse<PromptExportDto>`; DESIGN §19). |
| POST | `/api/prompts/import` | Import prompt (`ApiResponse<PromptSummaryDto>`; DESIGN §19). |
| POST | `/api/prompts/{id}/clone` | Clone a prompt to a new name/version, optionally overriding the campaign scope (`ApiResponse<PromptDetailDto>`; body `ClonePromptRequest` with required `newName` / `newVersion` and optional `campaignId`). |
| GET | `/api/apprentices` | List Apprentices (`ApiResponse<ListPageResult<ApprenticeSummaryDto>>`; optional `?campaignId=`, `?status=`, `?limit=`, `?beforeUpdatedAt=`). Ordered `(UpdatedAt DESC, Id DESC)`; `beforeUpdatedAt` is a bare timestamp consumed as a strict `<`, so a page ending inside a group of Apprentices sharing an `UpdatedAt` is cut at that tie boundary and may return fewer than `limit`, and a page that is entirely one timestamp widens to the whole tie group and may exceed it. DESIGN §19.6. |
| GET | `/api/apprentices/{id}` | Apprentice detail (`ApiResponse<ApprenticeDetailDto>`; **404** `Apprentice.NotFound`; DESIGN §19.6). |
| POST | `/api/apprentices` | Create Apprentice (`ApiResponse<ApprenticeDetailDto>`; **201** + `Location`; DESIGN §19.6). |
| DELETE | `/api/apprentices/{id}` | Delete terminal Apprentice (**204**; **409** `Apprentice.Running`; DESIGN §19.6). |
| POST | `/api/apprentices/{id}/start` | Start plan generation and execution (**202**; **409** `Apprentice.AlreadyRunning`; DESIGN §5.7). |
| POST | `/api/apprentices/{id}/pause` | Pause at step boundary (**202**; DESIGN §5.7). |
| POST | `/api/apprentices/{id}/resume` | Resume from checkpoint (**202**; **409** `Apprentice.NotPaused`; DESIGN §5.7). |
| POST | `/api/apprentices/{id}/cancel` | Cancel execution (**202**; DESIGN §5.7). |
| POST | `/api/apprentices/{id}/reweave` | Replace pending plan steps (`ApiResponse<ApprenticeDetailDto>`; **400** `Apprentice.InvalidPlan`; **409** `Apprentice.CannotReweave`; DESIGN §5.7). |
| POST | `/api/apprentices/{id}/intervene` | Resolve **Escalated** Apprentice with DM guidance (**202**; **409** `Apprentice.NotEscalated`; DESIGN §5.7). |
| POST | `/api/apprentices/{id}/cast` | **The Conclave** cross-Apprentice delegation: mint a child Apprentice from a parent (`ApiResponse<ApprenticeDetailDto>`; **201**; gated by `Arcanum:Features:Conclave`. No fixed depth/breadth ceiling — loops are caught by cycle detection, DESIGN §5.7.1). |
| GET | `/api/apprentices/{id}/chronicle` | Chronicle SSE stream (`text/event-stream`; CLI `watch apprentice`; DESIGN §5.7, DESIGN §19.6). A2A Sending frames (`sendingDispatched`, `sendingProgress`, `sendingCompleted`, `sendingFailed`) additionally carry `sendingDirection` (`outbound`), `sendingState` (the remote A2A task state, e.g. `working`), and — on the terminal frames — `durationMs`, `remoteCostKnown`, `remoteTotalTokens`, and `remoteCostUsd`. `sendingProgress` fires on remote **state transitions**, not once per poll, and never carries the peer's status prose, credentials, or prompt bodies (DESIGN §5.7.1). |
| GET | `/api/conclave/status` | Conclave/A2A state (`ApiResponse<ConclaveStatusDto>`): `state` is `disabled` \| `configured` \| `degraded` \| `healthy`, plus the effective server/Agent Card paths, the enabled surfaces, and the **count** of allowed remote agents (never the entries). CLI `arcanum conclave status`; DESIGN §5.7.1. |
| POST | `/api/conclave/sendings` | Dispatch a Sending to a remote A2A agent and block until it settles (`{ agentUrl, goal, name?, continuable?, callback?, skillId?, acceptedOutputModes? }` → `ApiResponse<SendingDispatchDto>`; **400** `Validation.InvalidBody`; **403** `Sending.Disabled` / `Sending.AgentNotAllowed`; **502** `Sending.AgentUnreachable` / `Sending.AgentCardInvalid`; **400** `Sending.TaskRejected` / `Sending.ModalityMismatch` / `Sending.SkillNotAdvertised`). `SendingDispatchDto` carries `taskId`, `responseText`, `costKnown` + `remoteTotalTokens` / `remoteCostUsd` (**`costKnown: false` means the peer reported nothing — not that the Sending was free**), the distinct `dispatchedAt` / `settledAt` instants, and `continuationNeed` (`input` \| `auth` \| `null`). With `continuable: true` a remote that stops at `input-required` / `auth-required` returns a continuation instead of ending, and the remote task is left alive. `skillId` / `acceptedOutputModes` are checked against the peer's Agent Card **before** the remote task is created, so a mismatch is a named local failure rather than something discovered mid-exchange (DESIGN §5.7.1). `callback: true` asks the peer to report back so the Sending stops holding a concurrency slot, falling back to the ordinary wait when the peer cannot accept one (DESIGN §5.7.1.4); it takes precedence over `continuable`. Cancelling the request cancels the remote task. CLI `arcanum conclave dispatch`. |
| POST | `/api/conclave/sendings/{taskId}/continue` | Answer a Sending the remote parked at `input-required` / `auth-required` and resume the **same** remote task (`{ agentUrl, message, continuable?, skillId?, acceptedOutputModes? }` → `ApiResponse<SendingDispatchDto>`, same shape and error codes as dispatch). Nothing is re-run. CLI `arcanum conclave continue`; DESIGN §5.7.1.3. |
| — | `/api/conclave/a2a/*` | A2A (Agent-to-Agent) JSON-RPC surface (`MapA2A`), mapped only when `Arcanum:Features:Conclave && Arcanum:Features:A2AServer`. The path follows `Arcanum:Integrations:A2A:ServerPath`; a configured path outside `/api` is mounted under it rather than refused. |
| GET | `/api/conclave/a2a/agent-card` | Authenticated A2A Agent Card ("Heraldry") — not the public, unauthenticated `/.well-known/agent-card.json` convention. Path follows `ServerPath`; the effective value is reported by `/api/conclave/status` and `/api/meta`. `capabilities.pushNotifications` is advertised only when `Arcanum:Integrations:A2A:PushNotifications` is on. |
| POST | `/api/conclave/a2a/callbacks/{configId}` | **The one A2A route outside the API-key filter.** A remote agent posts an outbound Sending's task transitions here; the caller is a peer agent, which does not hold this instance's operator API key, so the route authenticates on a 256-bit per-Sending secret presented in `X-A2A-Notification-Token` and compared in constant time against a stored digest. **202** when accepted, **404** for a wrong secret or a callback nobody is waiting on (the endpoint never confirms which config ids exist). Mapped only when `Arcanum:Integrations:A2A:PushNotifications` is on; path follows `ServerPath`. DESIGN §5.7.1.4. |
| GET | `/api/workspaces` | List registered workspaces (`ApiResponse<WorkspaceInfo[]>`; §8.17). |
| GET | `/api/workspaces/{id}` | Workspace metadata (`ApiResponse<WorkspaceInfo>`; **404** when missing). |
| POST | `/api/workspaces` | Register a workspace directory (`ApiResponse<WorkspaceInfo>`; **201** with `Location`; **400** validation). |
| PUT | `/api/workspaces/{id}` | Update workspace name/type (`ApiResponse<WorkspaceInfo>`; **404** when missing). |
| DELETE | `/api/workspaces/{id}` | Unregister workspace (**204** on success; **404** when missing). |
| GET | `/api/workspaces/{id}/files` | List files in a registered workspace (`ApiResponse<FileListResult>`; optional `relativePath`, `recursive`, `searchPattern`, and opaque `cursor`; §8.17). Each response contains at most 500 entries plus `nextCursor` / `continuationAction` when more remain; reuse the same list arguments with `cursor=nextCursor`. The cursor is bound to that scope and exact last entry, so earlier mutations do not shift later pages; a vanished checkpoint returns `Workspace.ContinuationCheckpointMissing` with a restart-without-cursor action. |
| GET | `/api/workspaces/{id}/files/info` | File or directory metadata (`ApiResponse<FileEntry>`; optional `relativePath`; §8.17). |
| GET | `/api/workspaces/{id}/files/contents` | Read file contents as UTF-8 text (`ApiResponse<FileReadResult>`; required `relativePath`; §8.17). |
| HEAD | `/api/workspaces/{id}/files/contents` | Size/freshness check for a file. |
| PUT | `/api/workspaces/{id}/files/contents` | Create or overwrite a file (`ApiResponse<FileWriteResult>`; **200**; required `relativePath` plus a `FileWriteRequest` body; gated by `Arcanum:Workspaces:EnableFileWrite`, otherwise **403** `Workspace.FileWriteDisabled`). A body whose `content` is missing or null is **400** `Validation.InvalidBody`. |
| PATCH | `/api/workspaces/{id}/files/contents` | Replace a verbatim text block in an existing file (`ApiResponse<TextBlockReplaceResult>`; **200**; required `relativePath`; §8.17). A body whose `oldString` or `newString` is missing or null is **400** `Validation.InvalidBody`. |
| DELETE | `/api/workspaces/{id}/files` | Delete a file or directory (`ApiResponse<FileDeleteResult>`; **200**; required `relativePath`; optional `recursive`; §8.17). |
| POST | `/api/workspaces/{id}/files/directory` | Create a directory, including parents (`ApiResponse<DirectoryCreateResult>`; **201**; required `relativePath`; §8.17). |
| POST | `/api/workspaces/{id}/files/divine` | Semantic search over a workspace's indexed files (`ApiResponse<WorkspaceSearchResult[]>`; body `WorkspaceSemanticSearchRequest` with required `query` and optional `limit`). |
| POST | `/api/workspaces/{id}/files/index` | Kick off an immediate background re-index of the workspace via `WorkspaceIndexingService.IndexNowAsync` (`ApiResponse<bool>`; **202**; the work continues until host shutdown rather than client disconnect). Reconciliation is single-flight per workspace, so a request that arrives while that workspace is already indexing coalesces onto the in-flight run — still **202** — instead of starting a duplicate scan. |
| GET | `/api/workspaces/{id}/files/index/status` | Read-only indexing status for a workspace (`ApiResponse<WorkspaceIndexStatusDto>`): vector mode/diagnostic, `IndexingEnabled`, durable file/chunk counts, and volatile `Watching`/`Degraded`/`Overflowed`/`Reconciling` plus last-event/last-success timestamps. |
| GET | `/api/workspaces/{id}/files/chunks` | Bounded, paginated chunk previews for a workspace (`ApiResponse<WorkspaceFileChunkPage>`; optional `relativePath` filter, clamped) including character offsets and one-based source line ranges. |
| GET | `/api/unseen-servant/jobs` | List Unseen Servant jobs with base and effective polling intervals (**canonical** Unseen Servant pacer API; §8.15). |
| POST | `/api/unseen-servant/jobs/{name}/initiative` | Set adaptive initiative (dynamic interval) for a job by name; returns updated status (`ApiResponse<UnseenServantJobStatusDto>`). **400** `Validation.InvalidBody` when `intervalMinutes` is outside **1..10080**; **404** `Daemon.NotFound` when `{name}` is not configured under `Arcanum:Daemon:Jobs` (§8.4). |
| GET | `/api/daemons` | List registered daemon jobs (`ApiResponse<DaemonJobInfo[]>`; **plural** `daemons` — registry; §8.15). |
| GET | `/api/daemons/{id}` | Daemon job metadata (`ApiResponse<DaemonJobInfo>`; **404** when missing). |
| POST | `/api/daemons/{id}/run` | Run a daemon job on demand; returns `ApiResponse<DaemonExecutionSummary>` with execution id (**400** when not found, disabled, or already running on-demand). |
| GET | `/api/daemons/{id}/history` | Execution history for a daemon (`ApiResponse<DaemonExecutionSummary[]>`). |
| GET | `/api/executions/{id}` | Execution detail (`ApiResponse<DaemonExecutionDetail>`; **404** when missing). |
| POST | `/api/executions/{id}/cancel` | Cancel a running execution; returns updated `ApiResponse<DaemonExecutionSummary>` (**400** `Daemon.NotRunning` when not running). |
| GET | `/api/logs` | Paginated in-memory log query (`ApiResponse<LogQueryResult>`; optional `minLevel`, `category`, `from`, `to`, `search`, `limit`, `beforeSequence`; §8.16). |
| GET | `/api/audit` | Persisted inference audit log query (`ApiResponse<InferenceAuditRecord[]>`; optional `from`, `to`, `model`, `sessionId`, `limit` up to the 1,000-record page, and opaque `cursor`; continue with `X-Arcanum-Next-Cursor`; §8.26). |
| GET | `/api/guardrails/audit` | Persisted guardrails violation audit log query (`ApiResponse<GuardrailAuditRecord[]>`; optional `from`, `to`, `stage`, `violationType`, `sessionId`, `limit` up to the 1,000-record page, and opaque `cursor`; continue with `X-Arcanum-Next-Cursor`; §8.27). |
| GET | `/api/operations` | List durable operations with optional `kind`, `state`, `limit`, and `offset` filters. Returns safe summaries only; encrypted checkpoint payloads and references are never serialized (DESIGN §10.8). |
| GET | `/api/operations/{id}` | Show one durable operation's lifecycle, links, lease, attempt, checkpoint version/presence, safe summary, and terminal error code. |
| POST | `/api/operations/{id}/cancel` | CAS-protected transition to `Cancelling`; **404** unknown, **409** stale/terminal. |
| POST | `/api/operations/{id}/retry` | CAS-protected reset of `Failed`, `Abandoned`, or `ReconciliationRequired` to `Pending`; checkpoint remains available to the recovery policy. |
| POST | `/api/operations/reconcile` | Run a bounded authenticated recovery pass and return `LongRunningOperationReconciliationSummary`. |
| GET | `/api/events/daemon` | SSE stream of `DaemonEvent` frames (daemon job lifecycle for scheduled and on-demand runs; CLI `watch daemons`); **not** wrapped in `ApiResponse<T>`. |
| GET | `/api/events/mcp` | SSE stream of `McpServerEvent` frames (MCP server lifecycle; CLI `watch mcp`); **not** wrapped in `ApiResponse<T>`. |
| GET | `/api/events/logs` | SSE stream of `LogEntry` frames after an initial `: connected` comment (live log tail from ring buffer; optional nullable `LogLevel` `level` plus free-form `category` and `search`; CLI `watch logs`); **not** wrapped in `ApiResponse<T>`. An unknown nonblank `level` returns **400** `Validation.InvalidQuery` before SSE headers are written. |
| POST | `/api/commlink/send` | Dispatch a **Comm Link** alert (`CommLinkMessageRequestDto`); **200** + `ApiResponse<bool>`; **400** validation; **502** + envelope on webhook HTTP failure. |
| POST | `/api/tools/invoke` | Diagnostic built-in tool invocation (`ApiResponse<ToolInvokeResponse>`; DESIGN §11.27). |
| POST | `/api/providers/test` | Read-only provider connectivity probe (`ApiResponse<ProviderTestResult>`; body `endpoint`, optional `apiKey`, `type` = `OpenAICompatible`; does not write `arcanum.json`; DESIGN §19). |
| POST | `/api/proving-grounds/trials/run` | Run an ephemeral **Trial** through **The Proving Grounds** (`Trial` body → `ApiResponse<TrialResult>`; DESIGN §20). |
| POST | `/v1/chat/completions` | OpenAI-compatible chat (JSON or SSE); **not** wrapped in `ApiResponse<T>`. |
| POST | `/v1/embeddings` | OpenAI-compatible embeddings; **not** wrapped in `ApiResponse<T>`. |
| POST | `/v1/moderations` | Always **501** `not_supported`; no configuration setting enables it. |
| POST | `/v1/images/generations` | Always **501** `not_supported`. |
| POST | `/v1/images/edits` | Always **501** `not_supported`. |
| POST | `/v1/images/variations` | Always **501** `not_supported`. |
| POST | `/v1/audio/transcriptions` | Always **501** `not_supported`. |
| POST | `/v1/audio/translations` | Always **501** `not_supported`. |
| POST | `/v1/audio/speech` | Always **501** `not_supported`. |
| POST | `/v1/files` | Upload standalone file storage, `multipart/form-data`. `purpose` accepts any non-empty value except the reserved batch-artifact purposes `batch_output` and `error`, which are owned by `/v1/batches`' publisher and encrypted under a different blob purpose — those return **400** `invalid_value` on `param: "purpose"` rather than storing bytes that could never be read back. Publication captures the new owned-file identity before waiting for the database writer and revalidates it under the same immediate transaction that inserts metadata. A concurrent reset/delete that wins causes a sanitized **500** `internal_error` with no metadata row; **201** + `OpenAiFileObject` means the committed metadata and exact encrypted bytes were visible together. |
| GET | `/v1/files` | List uploaded files, optional `?purpose=` filter. |
| GET | `/v1/files/{id}` | File metadata; **404** for unknown/malformed id. |
| DELETE | `/v1/files/{id}` | Deletes metadata + owned bytes only when no batch input/output/error role references the file. Every batch insert conditionally resolves all supplied file roles, and every artifact-reference update conditionally resolves its new output/error roles, in the same database write; a concurrent delete therefore either observes the committed reference or wins and causes the reference write to be rejected. Bytes move to identity-safe same-parent quarantine around the serialized metadata mutation, are restored on rejection/failure, and are finalized after commit. **200** `deleted: true` means both are absent; terminal or active references return **409** `file_referenced_by_batch`; storage conflict/recovery returns **500** `file_delete_storage_conflict` / `file_delete_recovery_required`. |
| GET | `/v1/files/{id}/content` | Raw bytes; always `Content-Disposition: attachment`. |
| POST | `/v1/batches` | Create an async bulk chat-completion job over an uploaded JSONL file; the final insert conditionally rechecks the input metadata in the same database write and returns **404** if a concurrent file deletion won; otherwise **200** + `OpenAiBatchObject`, `status: "validating"`. |
| GET | `/v1/batches` | Newest-first metadata page; optional `status`, `limit` (default 20, maximum 100), and opaque status-bound `after`; returns `has_more` plus `next_cursor`. |
| GET | `/v1/batches/{id}` | Batch status + durable 64-bit `request_counts` without opening artifacts; **404** for unknown/malformed id. |
| POST | `/v1/batches/{id}/cancel` | Idempotent cancel; stops in-flight processing within ~2s, seals every claimed unresolved line as `batch_interrupted_after_dispatch`, then publishes it before checkpoint cleanup. |
| POST | `/v1/batches/{id}/reset` | Reset a stuck `in_progress` batch back to `validating` (input file must still exist on disk; **409** if currently in-flight; **200** `OpenAiBatchObject`). Recovery resumes completed per-line checkpoints and seals a previously dispatched line without a result as `batch_interrupted_after_dispatch`; it never replays that ambiguous provider call (DESIGN §11.21). |
| GET | `/v1/models` | OpenAI-compatible models list (flattened configured models across providers via the same `ModelInfoBuilder` that backs `GET /api/models`); **not** wrapped in `ApiResponse<T>`. |
**JSON wire shape (`/api` and shared primitives):** JSON endpoints under `/api` use the `ApiResponse<T>` envelope (`Data`, `IsSuccess`, `Error`, `TraceId`) except for these non-envelope routes:

| Route | Wire format | Section |
|-------|-------------|---------|
| `POST /api/intelligence/ping-stream` | NDJSON event lines (`application/x-ndjson`) | §8.5 |
| `POST /api/web/research` | NDJSON `WebResearchStreamFrame` lines (`limits`, `progress`, `result`, or `error`) | §8.10.3 / DESIGN §11.27 |
| `POST /api/spells/{name}/execute-stream` | NDJSON `IntelligenceEvent` lines (`application/x-ndjson`) | DESIGN §19 |
| `POST /api/prompts/{id}/execute-stream` | NDJSON `IntelligenceEvent` lines (`application/x-ndjson`) | DESIGN §19 |
| `GET /api/events/daemon` | SSE `DaemonEvent` frames (`text/event-stream`) | §8.11 |
| `GET /api/events/mcp` | SSE `McpServerEvent` frames (`text/event-stream`) | §8.13 |
| `GET /api/events/logs` | SSE `LogEntry` frames (`text/event-stream`) | §8.16 |
| `GET /api/sessions/{id}/stream` | SSE entry frames (`text/event-stream`) | DESIGN §11.16 |
| `GET /api/apprentices/{id}/chronicle` | SSE Chronicle frames (`text/event-stream`) | DESIGN §5.7 |
| `GET /api/openapi/v1.json` / `GET /api/scalar` | OpenAPI document and Scalar UI (not application `ApiResponse`) | DESIGN §11.5 |
| `POST /v1/chat/completions` | OpenAI-shaped JSON or `text/event-stream` | §1 table |
| `GET /v1/models` | OpenAI-shaped JSON list | §1 table |

Envelope-payload specifics:

- **`GET /api/meta`** wraps **`InstanceMetadataDto`** (version, OS, runtime, process identity, Grimoire paths, effective host binding, and intelligence feature flags). `EmbeddingsVectorMode` / `EmbeddingsVectorDiagnostic` identify sqlite-vec versus the complete streamed managed fallback; the retained compatibility integer `EmbeddingsManagedSearchRowBudget` is `0`, meaning no total row budget.
- **`GET /api/health`** wraps **`HealthReportDto`** even when overall status is Unhealthy. An Unhealthy report returns HTTP **503** with `IsSuccess: true` and populated `Data`; clients must not discard that readiness snapshot merely because the status is non-2xx. The **`DurableOperations`** component's detail carries the last reconciliation summary *and* the states that pass could not fix — `stale=<n>`, `awaiting_repair=<n>`, per-kind repair guidance keyed by terminal error code, and any operation kind with no registered recovery handler (DESIGN §10.8.3). It is `Degraded` whenever any of those is non-empty. The detail is drawn only from the closed kind/state/error-code vocabularies: it never contains an operation id, a public summary, or checkpoint content.
- **`GET /api/config`** / **`PUT /api/config`** / **`POST /api/config/validate`** use **`ArcanumSettings`** as the payload type (§8.12). Read masks provider endpoints and returns only environment-variable references for provider credentials, HTTPS certificate passwords, and CommLink—not their secret values. Raw bodies fail closed on every unknown/obsolete path before source-generated deserialization; writes merge only endpoint masks.
- **`DELETE /api/sessions/{id}`** returns **204** with no body on success (soft-delete archive; idempotent — DESIGN §11.16); **`POST /api/sessions/{id}/rest`** returns **202** with `ApiResponse<bool>` when the job is queued, or **503** with `Session.RestQueueFull` when enqueue is rejected.
- **`POST /api/commlink/send`** returns **502** with `ApiResponse<bool>` when the outbound webhook HTTP call fails (non-success status or transport error).

**Daemon route families:** **`/api/unseen-servant/*`** manages Unseen Servant job **configuration** and runtime scheduling intervals (`GET /api/unseen-servant/jobs`, `POST /api/unseen-servant/jobs/{name}/initiative`). **`/api/daemons/*`** and **`/api/executions/*`** are the daemon job **registry** and **execution history** API for all registered `IDaemonJob` types (§8.15).

The `/api` and `/v1` groups are protected by the API key (DESIGN §11.3), including the OpenAPI document and Scalar reference UI on `/api` (`MapOpenApi` / `MapScalarApiReference` are registered on the same keyed group, so browsers need a valid API key like any other `/api` caller). The check runs as **middleware, before parameter binding**, so an unauthenticated request is answered **401 `Auth.Unauthorized` regardless of its body**: the body is never read, buffered, or deserialized first, and a malformed body no longer yields a binding `400` ahead of the `401`. Endpoint **matching** still precedes the key check, so `404` (no route), `405` (wrong method), and `415` (`Content-Type` the route does not accept) remain reachable without a key — none of them touch the body. `ApiKeyEndpointFilter` remains attached to the same routes as defence in depth.

---

## 8. HTTP JSON and Minimal API design (`Api` project)

### 8.1 Wire contract: the `ApiResponse<T>` envelope

```csharp
public sealed record ApiResponse<T>(T? Data, bool IsSuccess, Error? Error, string? TraceId = null);
```

- **`ApiResponse<T>`** is the default envelope for JSON under **`/api`**; streaming and OpenAI compatibility are exceptions (§1, §8.5). `sealed record` for value equality and immutability.
- `Error?` is literal `null` on success. `TraceId` from `Activity.Current?.Id ?? HttpContext.TraceIdentifier`.
- `ApiResponse<T>.FromResult` is the single mapping point from `Result<T>` to wire envelope.
- **404 bodies:** JSON routes under `/api` return an `ApiResponse<T>` envelope on **404** (for example `Campaign.NotFound`, `Session.NotFound`) — not an empty body. Use `Results.Json(..., ArcanumJsonContext.Default.ApiResponse…, statusCode: 404)` or `Results.NotFound(envelope)` so clients always receive `isSuccess`, `error`, and `traceId`.
- **Media type:** JSON routes that read the request body themselves go through `ApiRequestJson.ReadAsync`, which requires a JSON `Content-Type` before reading. A route that must inspect the raw `JsonDocument` tree instead (`PUT /api/config`, `POST /api/config/validate`, which run `ConfigurationValidator.RejectObsoleteJsonKeys` before deserializing) still gates on `HasJsonContentType()` and returns the same `ApiRequestJson.UnsupportedMediaTypeResult` envelope first. A missing or non-JSON media type is a **415** `ApiResponse<T>` failure envelope carrying `Validation.UnsupportedMediaType` and the message `Request body must be sent with 'Content-Type: application/json'.`, not an unhandled framework `InvalidOperationException` surfacing as **500** `Hub.Unhandled`. A JSON-typed but unparsable body remains **400** `Validation.InvalidBody` (`Request body could not be parsed as valid JSON.`), and a well-formed body that deserializes to null remains **400** `Validation.InvalidBody`.

### 8.2 `ArcanumJsonContext` — source-generated, public

CamelCase source-gen context for HTTP wire types (index 0 of resolver chain). Every `ApiResponse<T>` payload and `/v1` DTO needs `[JsonSerializable]`. Separate contexts: `GrimoireJsonContext`, `ConfigurationJsonContext` (Core), `McpJsonSerializerContext` / `McpConfigJsonSerializerContext` (Infrastructure). `[JsonPropertyName]` only for external snake_case/spec wires (OpenAI `/v1`, MCP JSON-RPC, selected NDJSON tool fields) — not arbitrary `/api` DTOs.

### 8.3 Service registration in `AddArcanumApiServices`

Registers Infrastructure + daemon services, `ApiKeyEndpointFilter`, OpenAPI/JSON (`ArcanumJsonContext` head of chain), named OpenAI `HttpClient`, `IChatClientFactory`, tokenizer, scoped `WizardIntelligenceProvider`. Singletons use `IOptionsMonitor`; scoped/request use `IOptionsSnapshot`.

### 8.4 Returning the envelope from a Minimal API handler

Successful endpoints use `Results.Ok(ApiResponse<T>.FromResult(result, traceId))`. Failable endpoints use `Results.Json` with the source-generated `JsonTypeInfo` and an explicit HTTP status code. No anonymous DTOs; no reflection-based model binding.

**Selected status contracts:**

- **`POST /api/intelligence/ping`** — `ApiResponse<PromptResponseDto>` on every path: **400** for request/reasoning validation, **200** on success, and shared `ArcanumErrorMapper` status for inference failures (for example 404/403/400/503/500 by stable code). The payload contract is detailed in §8.10.

- **`POST /api/intelligence/human-response`** — **400** validation (including the code-owned answer UTF-8 byte limit); **404** + `ApiResponse<bool>` failure when no waiter exists for `promptId` (`Intelligence.HumanPromptNotFound`); **200** + `ApiResponse<bool>` with `Data: true` when the answer is accepted.

- **`POST /api/unseen-servant/jobs/{name}/initiative`** — `ApiResponse<UnseenServantJobStatusDto>` on every path. **400** `Validation.InvalidBody` for a missing or unparsable body and for an `intervalMinutes` outside the inclusive range **1..10080**; **400** `Validation.InvalidJobName` for a blank route name; **404** `Daemon.NotFound` when no job of that trimmed name is configured under `Arcanum:Daemon:Jobs`, with a message naming `arcanum daemon jobs` as the way to list the configured names; **200** with the recomputed job status once the dynamic interval is applied. `UnseenServantPacer.SetDynamicInterval` is a no-op for an unconfigured name, so the **404** is what keeps a mistyped name from reading as an applied change.

- **`POST /api/mcp/reload`** and **`POST /api/intelligence/arsenal`** — Optional JSON body **`OptionalWorkspaceRequest`** (`{ "workingDirectory": "..." }` only). Responses remain `ApiResponse<T>` as today.

### 8.5 NDJSON streaming pipeline

`/api/intelligence/ping-stream` uses NDJSON (`application/x-ndjson`) for real-time token streaming:

- **Server:** Events serialized via `Utf8JsonWriter` + `ArcanumJsonContext`, newline-terminated, flushed per event. Writer: **`InferenceExecuteWriter`** (also used by spell/prompt `execute-stream`).
- **Wire shape:** Each line is an `IntelligenceEvent` with **camelCase string** discriminator **`type`**: **`"status"`**, **`"sessionBound"`**, **`"conversationBound"`** (deprecated alias emitted alongside **`sessionBound`** for one release), **`"context"`**, **`"token"`**, **`"reasoning"`**, **`"result"`**, **`"error"`**, **`"toolCall"`**, **`"toolResult"`**, **`"warded"`**, **`"wardResolved"`**, **`"toolError"`** (tolerated tool exception, emitted immediately before its `toolResult`; DESIGN §10.2.1). `context` carries the latest pre-call `ContextTokenBreakdown`; a second frame for the same call may add provider-reported input and variance after usage arrives. The enum is annotated with `[JsonConverter(typeof(JsonStringEnumConverter<IntelligenceEventType>))]` and per-member `[JsonStringEnumMemberName]` so the AOT JSON source generator emits the canonical strings. **`PingRequest.SessionId`** continues a Grimoire thread; when omitted the hub creates a new session on first assistant turn.
- **Ward frames:** `type:"warded"` and `type:"wardResolved"` carry `wardId`, `toolName`, and (on resolve) `allowed` / `reason`. Both may carry an additive **`origin`** — `human`, `autoApproved`, `autoDenied`, `timedOut`, `cancelled`, or `hostRestarted` — omitted when unknown, so a client that ignores the field is unaffected. A non-`human` origin means the host already resolved the ward: there is nothing to approve and a `POST /api/wards/{id}` would only return **409** `Ward.AlreadyResolved`, so clients surface it as an event rather than an approval prompt. `autoApproved` is produced by the opt-in `Arcanum:Security:Ward:AutoApprove` policy and substitutes for operator consent only — Sanctum and every other containment check still run (DESIGN §11.14).
- **Reasoning frame:** `type:"reasoning"` carries a typed, client-safe payload separate from answer `data`: `{"type":"reasoning","message":"client-safe summary","reasoning":{"text":"client-safe summary","output":"summary"}}` (the shared event envelope may also contain its normal null/default members). `reasoning.output` is exactly `none`, `summary`, or `full`; projected frames use `summary` or `full`. Provider `ProtectedData` is deliberately absent.
- **Disconnect / cancellation (`InferenceExecuteWriter`):** the code-owned policy is **`Auto`**. With an `Idempotency-Key`, continue-then-replay — do **not** link `RequestAborted` to the inference token; drain the hub enumerator and keep exact-byte capture so the claim may Complete. Without a key, caller cancellation abandons the claim. Arcanum adds no inference deadline; caller/host cancellation propagates, while unexpected provider/transport cancellation is sanitized as a generic inference failure. Either way, ledger provider-billed partial usage and reconcile/release the reservation.
- **Clients (`ArcanumApiClient` and The Forge):** `StreamReader` reassembles transport-fragmented UTF-8 into complete lines, including multibyte characters split across transport reads. Before strict source-generated deserialization, an AOT-safe `Utf8JsonReader` scan validates the root `type`. Canonical values are matched case-insensitively and normalized before `ArcanumJsonContext` / `TheForgeJsonContext` deserialization; a truly unknown, nonblank future string is silently skipped so later frames continue. Invalid JSON, a missing/non-string/blank discriminator, or any whitespace-padded discriminator is **malformed** and retains the surface's diagnostic behavior. This narrow pre-scan does not install a permissive enum converter or reflection serializer: direct source-generated deserialization remains strict. The terminal **`result`** event carries native **`usage`** (`prompt_tokens`, `completion_tokens`, `total_tokens`, optional `cached_tokens`, optional `reasoning_tokens`) on the `IntelligenceEvent` payload; **`data`** still duplicates **`total_tokens`** as a decimal string for backward compatibility, while the final answer remains in accumulated **`token`** frames and the result `message`. Assistant text is never reconstructed from legacy result `data`.

### 8.6 Request Delegate Generator

`<EnableRequestDelegateGenerator>true</EnableRequestDelegateGenerator>` on `Api` ensures Minimal API endpoints in a referenced class library are source-generated.

### 8.7 Session-Based Consolidation (Campaign Logger)

Three mechanisms trigger Campaign Log consolidation:

1. **Message-count threshold** (`CampaignLogThreshold`) — safety valve for unbounded growth.
2. **Idle timeout** (`CampaignLogIdleTimeoutMinutes`) — natural session boundary.
3. **Explicit rest** — `POST /api/sessions/{id}/rest`.

The queue consumer resolves **`IArcanumIntelligenceProvider`** in a per-item DI scope alongside **`IGrimoireRepository`**, loads the session header via **`GetSessionHeaderAsync`**, and batches rows with **`CreatedAt > (LastSummarizedMessageAt ?? DateTime.MinValue)`**. It builds a stateless **`PingRequest`**: empty `Prompt`, `StatelessMessages` (system persona + user payload with prior summary and batched turns), **`SkipSpellRouting: true`**, **`DisableMcpTools: true`**, **`UnattendedMode: true`**, **`Model`** from **`Arcanum:FastModel`** when set else **`Arcanum:DefaultModel`**, else omitted for first-provider fallback, and **no** `SessionId` so the hub does not append a new **`Entry`**. On **`ExecutePromptAsync`** success, **`UpdateSessionCampaignRollupAsync`** atomically persists the LLM text into **`Session.Summary`** and sets **`LastSummarizedMessageAt`** to the latest batched entry time. On **`Result.IsFailure`** or exception, **no** DB update — the session remains eligible on the next sweep. The intelligence hub **reads** `Summary` for optional read-time compression (DESIGN §10.2.3).

For attachment privacy, the successful source turn records only typed consultation metadata. The Campaign Logger adds logical key, version, opaque attachment id, and source type for consultations inside the summarized window; it never loads attachment bytes, automatically submits the session's attachment index, or copies hashes/host paths into the summarizer payload. The prompt asks for useful decisions and source references, not an attachment archive.

Under the same **Session-Based Consolidation model of AI memory**, **Chronosync reporting** (DESIGN §5.4.2) addresses **spatial** drift: thread lines and `DomainType` deltas vs the last persisted `PatternSnapshot`, not chat log length. Campaign Logger and Chronosync are separate triggers; the hub folds `ChronosyncReport` into the system prompt via `PingRequest.ChronosyncDelta`; MCP context remains separate.

### 8.8 OpenAI `/v1` Chat Completions compatibility subset

`OpenAiV1Endpoints` advertises a **Chat Completions compatibility subset**, not full OpenAI API parity. Moderations/images/audio remain **`501 not_supported`**. Polymorphic `content` (string | parts) is AOT-safe; unsupported part types / over `MaxContentPartsPerMessage` → **400** `invalid_value` before mapping. Vision parts map to MEAI `TextContent`/`UriContent`/`DataContent` (DESIGN §10.2.4).

**Request body framing** (`POST /v1/chat/completions` reads the body itself rather than model-binding it, so it applies the §8.1 media-type rule in the OpenAI envelope): a missing or non-JSON `Content-Type` is **415** `invalid_request_error` / `unsupported_media_type` with the message `Request body must be sent with 'Content-Type: application/json'.`, not an unhandled `InvalidOperationException` surfacing as **500** `api_error` / `inference_failed`. A body over the endpoint's 16 MiB `WithLargeRequestBody` ceiling keeps Kestrel's **413** as `invalid_request_error` / `payload_too_large`; any other Kestrel `BadHttpRequestException` keeps its own status code as `invalid_request_error` / `invalid_request`. A JSON-typed but unparsable body remains **400** `invalid_json`, and a body that deserializes to null remains **400** `missing_body`.

**Parameters applied** (`ApplyInferenceParameters`): temperature, top_p, max tokens, penalties, seed, stop, response_format. Reasoning controls are additive: `reasoning_effort` = `none|minimal|low|medium|high|xhigh`, `reasoning_budget` = positive integer, and `reasoning_output` = `none|summary|full`. `reasoning_effort` and `reasoning_budget` are mutually exclusive and map to native `PingRequest.reasoning`; capability validation runs before provider I/O for buffered and `stream:true` requests. `reasoning_output` is an Arcanum-local projection/exposure preference and is passed to Microsoft.Extensions.AI only as a best-effort hint. It is not a guaranteed provider wire control, and Arcanum does not patch an unsupported `reasoning_output` field into provider JSON. When omitted, the resolved capability chooses `full` when `SupportsFull`, otherwise `summary` when `SupportsSummary`; `AllowsClientOutput` is required, and streaming also requires `SupportsStreaming`. Native effort/output and configured control-support/wire-dialect enums are strict string-only AOT contracts. OpenAI `reasoning_effort` and `reasoning_output` are also string-only. A numeric enum (defined or undefined) or an unknown enum string fails JSON binding before semantic validation. `n` must be `1` when present. Client `tools`/`tool_choice` rejected **400** `unsupported_parameter` unless `ClientToolForwarding:Enabled` (then schema/count validation; §8.8.3).

**Responses:** buffered answers remain in `choices[].message.content`; additive reasoning is in `reasoning_summary` and/or `reasoning_content`. Streaming answers remain in `choices[].delta.content`; reasoning uses the same additive fields on the delta, in provider order. A client that ignores the fields still reads an unchanged answer. Usage keeps `completion_tokens` and `total_tokens` authoritative and projects the reasoning subset at `completion_tokens_details.reasoning_tokens`; cached prompt subsets use `prompt_tokens_details.cached_tokens`. Buffered `message.tool_calls` still reports server-executed calls (§8.8.1); streaming SSE includes keep-alives and usage only when requested by `stream_options`. Semantic reasoning failures are typed OpenAI error bodies/chunks, never `delta.content`: they use HTTP **400**, `type:"invalid_request_error"`, `param:"reasoning"`, and the reachable stable code `invalid_reasoning_options` (effort plus budget), `invalid_reasoning_budget` (budget outside 1–2,097,152), `unsupported_reasoning_control`, `reasoning_budget_exceeds_model_limit`, or `unsupported_reasoning_output`. Numeric/unknown reasoning enum JSON never reaches those semantic branches; strict binding returns HTTP **400** `invalid_request_error`, code `invalid_json`, and no parameter. Unknown model → **404** `model_not_found`; tool-loop/timeout → **503** `server_error`.

**Current streaming projection topology:** production `/v1/chat/completions` obtains native `IntelligenceEvent` frames from `WizardIntelligenceProvider` (`TurnExecutionCoordinator` → `IntelligenceEventProjection`) and maps them to SSE chunks in `OpenAiV1Endpoints`. That endpoint mapper is the authoritative compatibility implementation. `OpenAiSseProjection` is a separate semantic helper/characterization path, not the projection instance used by the production route. The two paths share reasoning-field and typed-error rules only; `OpenAiSseProjection` does not define production terminal usage chunks, `stream_options.include_usage`, or tool-argument fragmentation. Those wire contracts are covered directly by production endpoint tests rather than by an exact-parity claim.

#### 8.8.1 Server-executed tools on `/v1` (buffered + streaming tool_calls)

Arcanum executes MCP tools server-side; `/v1` surfaces calls for observability/replay. Buffered: `PromptTurnResult.ToolCalls` → `message.tool_calls`. Streaming: `ToolCall` events → `delta.tool_calls` (40-char argument fragments; monotonic per-response `index`; fresh `call_…` ids). **`toolResult` never surfaced** on `/v1`. Forwarding mode preserves provider-minted ids and returns `finish_reason: "tool_calls"` without executing client tools. Richer native surface: `/api/intelligence/ping(-stream)`.

#### 8.8.2 `GET /v1/models` capability enrichment

`ModelInfoBuilder` is shared with `GET /api/models`, and both routes read the same latest successfully persisted configuration snapshot. Additive OpenAI fields: `context_window`, `supports_vision`, `provider_name`/`provider_type`, `supports_tools`/`supports_streaming` (always true), plus the same optional typed `reasoning` capability object returned by the native endpoint.

#### 8.8.3 Client tool security (forwarding mode)

When `Arcanum:Features:ClientTools` is enabled, Sanctum/Ward/tool audit do **not** apply to client-supplied tools (provider executes). Default remains reject.

### 8.9 NDJSON anti-buffering headers (`/api/intelligence/ping-stream`)

The NDJSON streaming endpoint sets `Cache-Control: no-cache` and `X-Accel-Buffering: no` (parity with the SSE endpoint in §8.5/§8.8) so reverse proxies (nginx, Cloudflare, k8s ingress) do not coalesce incremental frames.

### 8.10 Buffered `/api/intelligence/ping` envelope

The buffered ping endpoint wraps a **`PromptResponseDto`** (Core) inside `ApiResponse<T>`: `text` (assistant answer only), `usage` (native token counts, including additive top-level `reasoning_tokens`), `toolCalls` (the assistant-issued calls executed server-side, when any), `finishReason`, and `reasoning` (an ordered array of `{ text, output }` client-safe segments; empty by default). Reasoning is never concatenated into `text`. Previously the envelope held only the assistant text as a bare `string`; clients now get the full turn context without falling back to NDJSON.

### 8.10.1 Mana counter (`POST /api/intelligence/mana`)

Read-only model-aware estimate (`ManaCountRequest` → `ManaCountResult`); no inference/Grimoire writes. `model` resolves the configured provider/canonical model profile, while an unconfigured model uses the conservative fallback. The result retains legacy `manaCount` / `encoding` / per-message fields and adds classification, profile id, safety margin, and the complete `ContextTokenBreakdown`. `tools:true` materializes the current native + MCP declarations and includes their names, descriptions, and full JSON schemas in both the total and source breakdown. **400** when neither `messages` nor `prompt` is supplied.

### 8.10.2 Effective context preview (`POST /api/intelligence/context/inspect`)

`ContextPreviewRequest` accepts optional `prompt`, `model`, `workingDirectory`, `sessionId`, and `campaignId`, plus `showContent` and `noRetrieval`. Unified-run previews can additionally supply `overrideSpellName`, preview-only `attachedFiles` (`AttachedFileDto[]`) and `scryingFoci` (`ScryingFocusDto[]`), `disableAllTools`, `unattendedMode`, `additionalSystemPrompt`, `maxOutputTokens`, and the production sampling fields `temperature`, `topP`, `stop`, `seed`, `responseFormat`, `presencePenalty`, and `frequencyPenalty`. The assembled request passes through the shared native preflight validation pipeline used by live inference, including attached-file, request-bound, and Scrying validation. An empty prompt is valid so an existing Session can be inspected as it stands. The response reports the effective provider/model and context window, selected Spell and routing mode, resonant dependencies, included and excluded tools with reasons, every `ContextTokenSource` row with estimate classification, reserved output, the production compression decision, and auxiliary routing/embedding work.

Prompt input uses the production ping bound. When `campaignId` is supplied without `workingDirectory`, the endpoint resolves the Campaign's server-host path exactly as buffered and streaming inference do; an unknown Campaign returns **404**.

**Canonical Campaign resolution (§10.12).** Every inference and preview route now resolves Campaign authority through the one canonical resolver before doing any other work, and the sources must agree rather than the first populated one winning:

| Condition | Status | Code |
|---|---|---|
| `sessionId` names a Session that does not exist | **404** | `Session.NotFound` |
| `sessionId` names a Session that predates immutable Campaign binding | **409** | `Session.CampaignBindingRequired` |
| `campaignId`, the Session's binding, or the supplied `workingDirectory` name different Campaigns | **409** | `Covenant.CampaignBindingConflict` |
| `workingDirectory` is supplied but is not contained by the resolved Campaign's registered root | **409** | `Covenant.CampaignBindingConflict` |
| a Global-only Session is given any Campaign source | **409** | `Covenant.CampaignBindingConflict` |
| the resolved Campaign no longer exists | **404** | `Campaign.NotFound` |

A supplied Session ID is never silently replaced with a new Session, and an unregistered working directory contributes nothing only while no other source has established a Campaign. The Campaign a turn resolves to is server-owned: no request body, MCP schema, or tool argument can name it directly.

The endpoint uses `WizardIntelligenceProvider`'s production routing, RAG readers, tool builder and policy filters, `SystemPromptBuilder.BuildDocument`, `InferenceContextBuilder.TryApplyContextCompressionIfNeeded`, and `IModelTokenEstimator`. It never enters the turn coordinator, invokes a tool, reserves turn budget, creates an assistant Entry, persists preview files/images as attachments, or calls the main inference model. `overrideSpellName` uses the same forced-Spell load and dependency resolution as a live turn and continues to resolve when `noRetrieval:true`; only automatic semantic Spell routing is skipped. `disableAllTools` and `additionalSystemPrompt` let a research dry run reproduce synthesis policy without executing it. `noRetrieval:true` skips embedding/RAG and automatic semantic Spell routing, with explicit unavailable reasons. Model-visible content is omitted unless `showContent:true`; that opt-in returns the assembled system prompt and messages through the authenticated operator API. Pre-call token values are labeled `exact`, `estimated`, `unknown`, or `reserved`; provider-reported values are never fabricated.

`arcanum run --dry-run` always requests `noRetrieval:true`, making it a spend-free static, pre-inference plan rather than an exact copy of the eventual live `PingRequest`. It validates and shows the resolved user payload, forced Spell, server context, tool policy, output reserve, and sampling controls without search or provider inference. A later live Agent handoff may still add locally produced `PatternSnapshot` and `ChronosyncDelta` context before inference.

### 8.10.3 Web research workflow request (`POST /api/web/research`)

`WebResearchWorkflowRequest` carries bounded `question`, optional positive `sourceTarget`, `model`, `tokenBudget`, optional `costBudgetUsd`, `continueSessionId`, and `attachToSessionId` fields. It also accepts optional `workingDirectory`, `campaignId`, current-turn `attachedFiles`, current-turn `scryingFoci`, and synthesis controls `temperature`, `topP`, `stop`, `seed`, `responseFormat`, `presencePenalty`, `frequencyPenalty`, and `unattendedMode`. These additions let `arcanum run --research` carry the same effective Campaign, Workspace, Session, Model, and explicit turn context as the ordinary Agent route. `tokenBudget` remains the synthesis output-token authority.

Before any search-provider work, the server validates the request and prospective synthesis request, resolves a Campaign-only request to its server-host working directory, and validates the continuing and attachment target Sessions. It then performs progress-driven search/read passes and uses the preflighted `PingRequest` for final synthesis. The research-only untrusted-source system instruction and `DisableAllTools` remain server-owned. Client filesystem paths are never dereferenced by this endpoint: callers send typed content, and the host validates and materializes it through the normal inference path.

There is no hop counter or default total-source ceiling. Passes continue while they discover new unique URLs and stop at an optional `sourceTarget`, deterministic source exhaustion/no-progress, caller cancellation, explicit token/cost policy, provider/context failure, or a safety denial.

On live synthesis, supplied `attachedFiles` and `scryingFoci` enter the normal attachment pipeline: when Attachments are enabled they are persisted and bound to the synthesis Session before model inference; when Attachments are disabled they remain in-memory current-turn content. A context preview never persists them. `attachToSessionId` is separate: it stores the final research Markdown, not the supplied synthesis context.

### 8.11 Daemon event SSE bus (`GET /api/events/daemon`)

In-process `IEventBus` uses code-owned bounded per-subscriber channels with `DropOldest`. Wire: `text/event-stream` `DaemonEvent` frames + best-effort `[DONE]`. `Arcanum:Execution:MaxSseConnections` and `MaxSseConnectionsPerType` feed `SseConnectionGate` → **503** `Api.TooManyConnections`. Anti-buffering headers; API key on the `/api` group. Rate limiting admits the HTTP request only, not open-stream duration.

`arcanum watch daemons` is the terminal consumer. It keeps heartbeats and `[DONE]` out of data output, supports free-form event/tool filtering, and offers opt-in reconnect. Reconnect cannot recover process-local daemon frames and therefore always warns that a gap may exist.

### 8.12 Configuration API (`GET` / `PUT` / `POST /api/config`)

Read: redacted secret-bearing URLs/endpoints (`***`) plus non-secret credential references; environment values are never read into the response. `GET /api/config`, `GET /api/models`, `GET /api/providers`, and `GET /v1/models` resolve one latest-successfully-persisted snapshot, so their configuration and discovery projections agree immediately after a successful write. Write: under the configuration-writer lock, re-read the current file, merge recognized redacted URL placeholders, run outbound and semantic validation, and atomically replace `arcanum.json`. Holding that lock across the entire read-validate-write transaction prevents a concurrent retention-rule update from committing between the full PUT's snapshot and replacement. Validate-only also merges recognized endpoint masks against the current snapshot before outbound and semantic validation, so an unchanged redacted `GET` document remains a valid update candidate; it never writes. Residual masks for new/unmatched providers fail closed. Provider API keys and PFX passwords are not accepted fields. Other non-retention runtime consumers remain bound to the process-start snapshot and require a host restart to adopt configuration changes; referenced secret environment values are resolved only at provider/certificate use. Status: **400** `Configuration.ValidationFailed`, `Config.UnresolvedMask`, or `Security.BlockedOutboundUrl`; **500** `Configuration.WriteFailed` or `Configuration.LockUnavailable` (another process held the configuration transaction past the bounded acquisition window). `POST /api/config/validate` answers **200** with `isSuccess: true` and `data: true` only when the merged document clears residual-mask, outbound, and semantic validation; every failure — semantic included — is a **400** failure envelope, so a status-code-driven probe cannot read an invalid configuration as validated.

#### 8.12.1 Familiar readiness (`GET /api/providers/{name}/familiar-probe`)

Answers whether a subscription-backed CLI provider is ready, without asking it for a completion. Runs host-side next to the process runner so Compendium — an editor that does not spawn processes — can ask over HTTP instead of growing a second implementation. Non-billable (ADR 0002 / `NonBillableSurfaces.FamiliarProbe`), alongside `GET /v1/models`.

`FamiliarProbeResult` carries `providerName`, `providerType`, `status` (`NotInstalled` | `NotConfigured` | `Configured`), `version` when the CLI reports one, a one-line `summary`, `remediation` text, a copyable `remediationCommand` the **operator** runs, `enumeration` (`Discovered` | `OperatorDeclared` | `Unknown`), `models`, and `hiddenModels`.

Redaction is structural rather than a filtering pass: the bound DTOs have no field for the account e-mail, organisation identifiers, or local paths that `claude auth status --json` and `codex doctor --json` print, so that material cannot reach this payload or a log. Arcanum never authenticates — `remediationCommand` is text the operator runs themselves. `enumeration: "Unknown"` is the ordinary outcome, because neither CLI publishes a machine-readable model list; clients fall back to free-text model entry and everything still works.

### 8.13 MCP server event SSE bus (`GET /api/events/mcp`)

`McpConnectionManager` publishes `McpServerEvent` on state changes. Same SSE back-pressure/caps/auth as §8.11. `arcanum watch mcp` uses the common watcher, including repeatable `--tool` / `--tool-name` and `--event-type` filters. Reconnect does not imply replay of MCP lifecycle events.

### 8.14 Spell Management API (`/api/spells`)

Workspace resolution: `?workspace=` → `Arcanum:Workspaces:DefaultRoot` → CWD. CRUD needs a resolvable workspace; empty `Arcanum:Security:SpellWorkspaceRoots` denies all (**403** `Spell.PathNotAllowed`). Built-ins under `~/.config/arcanum/spells/` are read-only (`Spell.BuiltinReadOnly`). Format: `SPELL.md` frontmatter + body; optional `SPELL.json` (legacy `SKILL.json` read fallback; writes always `SPELL.json`). Search shadow order is campaign > workspace > builtin and returns the complete deterministic match set rather than silently dropping results after 1,000. Declared dependency lists and cycle-safe graph traversal have no count/depth ceiling; single-file allocation, cancellation, visited identity, `MaxDeclaredTools`, and provider-facing `MaxResonantBytes` remain. Versions: string labels `SPELL.v{label}.md` (`^[A-Za-z0-9.]+$`); activate swaps into `SPELL.md` and records `activeVersion`. Clone/cast/import quirks and status codes: §1. Per-workspace locks; delete only under `{workspace}/spells/{name}`. Updating or deleting a spell that does not exist in the resolved workspace returns **404** `Spell.NotFound` through `ArcanumErrorMapper` (§8.23), matching `GET /api/spells/{name}`; built-in and validation refusals stay **400**.

`GET /api/spells` preserves the legacy `ApiResponse<SpellSummary[]>` response unless the nullable query flag is explicitly `paged=true`. Paged mode returns `ApiResponse<SpellCatalogPage>` with 50 metadata items, `hasMore`, opaque `nextCursor`, and `continuationAction`; it accepts `workspace`, `q`, `tag`, `tool`, `source`, and `cursor`. The cursor binds all filters/workspace plus the exact source/name/path identity of the prior anchor. A mismatched query, malformed cursor, or missing or changed anchor returns a structured **400** that instructs the client to restart with `cursor` omitted. Replaying the same input cursor is deterministic; the returned cursor advances. One cursor-name frame retains a 65,536-byte strict-UTF-8 boundary; **400** `Spell.ContinuationFrameTooLarge` reports the measurement and asks the operator to rename the spell or narrow the filters, then restart. The resource selector follows every advancing page, while Command Center `/spell list [opaque-cursor]` fetches one page and prints the exact next command. Legacy array consumers remain unchanged.

### 8.15 Daemon job management (`/api/daemons`, `/api/executions`)

**Route families:** `/api/unseen-servant/*` = Unseen Servant interval control; `/api/daemons/*` + `/api/executions/*` = job registry + execution history. Watermarks: DESIGN §5.5.5. On-demand `POST .../run` waits for completion; scheduled path shares `DaemonRunner` single-flight per daemon. History process-local (`ExecutionHistoryLimit`); detail includes correlated ring-buffer logs.

### 8.16 Log ring buffer (`GET /api/logs`, `GET /api/events/logs`)

Serilog → `SerilogLogRingBufferSink` → a code-owned bounded in-memory ring that overwrites the oldest entry. Query filters + `beforeSequence` cursor. Live SSE accepts nullable `LogLevel` `level` (`trace`, `debug`, `information`, `warning`, `error`, or `critical`) plus free-form `category` and `search`. The CLI forwards the level token without adding another client-side allowlist; API enum binding remains authoritative, and an unknown nonblank value returns **400** `Validation.InvalidQuery` before the response becomes SSE. A successful stream begins with the SSE comment `: connected`, not a synthetic data object. The route uses the same caps/auth as §8.11 and is not persisted across restarts. Post-build sink registration avoids a Build()-time logging DI deadlock.

`arcanum watch logs` maps `--level`, `--category`, and `--search` to those server filters and may add repeatable client-side `--event-type` / `--tool` filters. Like the other process-local streams, an opt-in reconnect continues across retryable disconnects until cancellation or clean completion, uses capped exponential delays, warns on every possible gap, and never claims missed log entries were replayed. Permanent 4xx/authentication/cap denials remain terminal.

### 8.16.1 Unified CLI watch projection

The six CLI sources map one-to-one to existing server contracts:

| CLI source | Server contract |
|---|---|
| `watch session [session]` | `GET /api/sessions/{id}/stream`; optional `since=<entry-guid>` |
| `watch apprentice [apprentice]` | `GET /api/apprentices/{id}/chronicle` |
| `watch logs` | `GET /api/events/logs`; optional `level`, `category`, `search` |
| `watch mcp` | `GET /api/events/mcp` |
| `watch daemons` | `GET /api/events/daemon` |
| `watch health` | Repeated `GET /api/health`; `--interval` defaults to five seconds |

`watch <source>` is the only live-stream entry; the CLI ships no alias spellings for these. Every request uses the normal `/api` credential; the CLI neither widens authorization nor bypasses SSE connection caps. The shared SSE consumer joins multi-line `data:` fields without a new client-only payload cap, surfaces heartbeat comments as stderr liveness diagnostics while excluding them from data, stops cleanly on `[DONE]`, uses UTC timestamps and event-type coloring in terminal mode, and returns **130** on Ctrl+C. `--json` reserves stdout for compact newline-delimited source objects and routes all diagnostics, including reconnect/gap messages, to stderr.

Repeatable `--event-type` and repeatable `--tool` (alias `--tool-name`) accept arbitrary case-insensitive values; blank values are ignored, and log tool matching recognizes structured `properties.ToolName` metadata. Reconnect is opt-in, retries unexpected disconnects and transient HTTP 408/425/429/5xx failures until cancellation with a code-owned capped exponential delay, and always reports a possible gap. Permanent validation/authentication/not-found/cap errors exit. A Session cursor may reduce the gap by continuing after the last received valid Entry id, but the bounded Session window is not a replay guarantee; Chronicle, log, MCP, and daemon streams have no replay cursor. Health polling accepts any positive whole-second interval and renders a valid Unhealthy 503 envelope as an observation. These are per-process CLI options and add no configuration surface.

### 8.17 Workspace registry and file browser/writer (`/api/workspaces`)

Campaign-backed when Grimoire ready (`persisted: true`); else in-memory. Writes gated by `Arcanum:Workspaces:EnableFileWrite` (default off) → **403** `Workspace.FileWriteDisabled`. Path policy: reject `..`/absolute; symlink escape → `Workspace.SymbolicLinkEscape`; revalidate before I/O. Atomic temp+rename for PUT/PATCH. Size clamps: DESIGN §3.4. PATCH ordinal replace with ambiguous/not-found codes; its read of the existing target is bounded by the same maximum file read size as GET contents, so an oversized target returns `Workspace.FileTooLarge` rather than buffering the whole file. HEAD contents returns size/`Last-Modified` only. The read routes (`GET .../files`, `.../files/info`, `.../files/contents`) map failures through the §8.23 authority exactly as HEAD and the write routes do — `Workspace.FileNotFound` → **404**, `Workspace.AccessDenied` → **403**, `Workspace.FileTooLarge` → **413** — while the list route's paging/search codes (`Workspace.ContinuationInvalid`, `Workspace.ContinuationCheckpointMissing`, `Workspace.InvalidSearchPattern`) stay **400**.

Registration creates the workspace `.arcanum` directory **before** the Campaign row is persisted, mirroring `POST /api/campaigns`. A directory that cannot be created returns `Workspace.AccessDenied` / `Workspace.WriteFailed` and leaves nothing registered, so a retry is never blocked by a half-completed first attempt. Recursive file listing skips a subdirectory that vanished or cannot be read and returns the rest of the tree instead of failing the whole page.

The CLI exposes this boundary directly as `arcanum workspace list|current|register|show|tree|info|read|search|index|index-status|chunks|unregister`. `tree`, `info`, and `read` call the authenticated file-browser routes and never read the client filesystem directly. File writes remain absent from this command family, so `Arcanum:Workspaces:EnableFileWrite` is neither bypassed nor implicitly enabled. `register [path]` sends the path to the server registry; omission uses the client current directory only because the shipping CLI targets the bundled loopback host. Help and output call every such value a server-host path so this convenience cannot silently become a remote path assumption.

### 8.18 Session API

Search, export, analytics, CRUD, manual entry append, SSE live stream, and Campaign Log **`/rest`** use the Grimoire-backed **`/api/sessions`** surface. See **DESIGN §11.16 Session lifecycle** for persistence and lifecycle architecture.

#### 8.18.1 Standalone session attachments

All routes are API-key protected and operate only on a bound attachment belonging to the route Session. Successes use the native `ApiResponse<T>` envelope except the content stream.

- **Snapshot:** `POST /api/sessions/{id}/attachments` requires multipart form field `file`; an optional `logicalName` chooses the version family. It persists the supplied bytes as an immutable encrypted snapshot with `SourceKind=SnapshotOnly` / `SourceStatus=NotApplicable`. The request may originate from any client-readable path or stdin because no client path is sent or retained. Unsupported binary/PDF/Office content is stored as `Binary` and remains `NotEligible` for text extraction; it is not rejected merely because Arcanum cannot materialize it into model context. The parser admits a maximum-size file plus 64 KiB of multipart envelope, but rejects aggregate overflow with `Attachment.TooLarge` for both declared-length and chunked requests.
- **Live reference:** `POST /api/sessions/{id}/attachments/reference` accepts `{ "workspacePath": "docs/notes.md", "workspaceId": "optional-id", "logicalName": "optional-key" }`. `workspacePath` is interpreted only on the server, relative to the selected registered workspace or the configured active/default workspace. Absolute values do not widen authority: canonical containment, link/file identity, stable bounded reads, and Campaign Sanctum must all pass. The response exposes only opaque/sanitized provenance; canonical paths and file identities stay encrypted server metadata.
- **List/versions:** `GET /api/sessions/{id}/attachments` returns all bound versions. Clients derive a latest-per-logical-key list or a version history without a second persistence authority. The server fulfills that all-version array contract by exhausting bounded SQL keyset pages; turn indexes and logical-name tool selectors use SQL-selected latest projections and retrieve version ordinals only for the requested index keys, rather than loading every full historical row first. `SessionAttachmentDto.SessionId` enables direct CLI output; `RelativePath` names the encrypted stored snapshot, never the live source. CLI Reveal uses it only when the corresponding local file is present and has an `ARCABLOB` envelope; otherwise the user is directed to authenticated export.
- **Refresh:** `POST /api/sessions/{id}/attachments/{attachmentId}/refresh` accepts no body or path. It calls the same `ToolExecutionPipeline.RefreshSessionAttachmentAsync` core used by `refresh_session_file`. An unchanged hash reuses the row; changed verified bytes create the next version under the measured per-Session byte protection without an incidental per-key version-count cap. Detected MIME determines the refreshed version's current Text/Image/Binary kind. Kind-specific policy is reapplied, but this operator endpoint does not require model vision capability because it injects no content.
- **Content:** `GET /api/sessions/{id}/attachments/{attachmentId}/content` opens only the authenticated stored snapshot through `ISessionAttachmentStore.OpenReadAsync`. It returns a download, never inline content, with `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, and no source path in headers. Attachment bytes are therefore available for atomic client export without ever being printed by metadata commands.
- **Pins:** the existing context-pin routes create/delete `kind=attachment` pins whose target is an attachment id and whose content version is its snapshot hash. Text pins may materialize implicitly within the shared pin/turn budgets. Image pins remain stored but materialize with `Unsupported`; a vision-capable turn must explicitly pass that bound attachment id.
- **Inference references:** native `attachmentReferences` remain ordered opaque IDs for Bound snapshots in the effective Session. Preparation validates metadata without loading every file. After provider/model resolution, the turn opens and admits each reference independently against the actual context window; overflow stops before opening later references or calling the provider. Successful content retains request order and typed attachment provenance. Caller cancellation propagates immediately and does not materialize later references.

`Attachment.Disabled` → **403**; `Attachment.InvalidRequest`, `InvalidContent`, `InvalidReference`, and `SourceUnavailable` → **400**; `Attachment.NotFound` and `SourceNotFound` → **404**; `Attachment.LimitExceeded` → **409**; `Attachment.TooLarge` → **413**. Error messages are bounded and never echo a source path. Refresh failures use this same mapping instead of collapsing every outcome to a conflict.

### 8.19 Server lifecycle (PID file)

The code-owned path is `~/.config/arcanum/arcanum.pid`. Startup fails if a live PID is present; a stale file is overwritten. Shutdown deletes the file only if it still names this process. DevHost and `serve` share the same path and therefore cannot run concurrently.

### 8.20 Unified data lifecycle API (`/api/data`)

Every route in this family is protected by the normal `/api` key filter. Status and planning are read-only. Mutations are operation-specific; there is no generic memory-delete route and the CLI never mutates the Grimoire directly. CLI confirmation is an additional operator gate, not a substitute for HTTP authentication. Factory reset also requires the exact body token shown below.

**Requests (camel-case property names; canonical enum values retain CLR casing):**

```jsonc
// POST /api/data/prune/plan
{
  "operation": "Prune",
  "targetId": null,
  "memoryScope": null
}

// POST /api/data/prune
{
  "request": {
    "operation": "Prune",
    "targetId": null,
    "memoryScope": null
  },
  "expectedPlanId": "optional SHA-256 plan id from a prior preview"
}

// PUT /api/data/retention
{
  "dataClass": "archived-sessions",
  "enabled": true,
  "days": 180
}

// PUT /api/data/retention (disable without replacing the prior day value)
{
  "dataClass": "archived-sessions",
  "enabled": false
}

// POST /api/data/memory/reset
{
  "scope": "Entry"
}

// POST /api/data/factory-reset
{
  "confirmation": "factory-reset"
}

// POST /api/data/factory-reset/plan
{
  "scope": "Workspace",
  "workspace": {
    "campaignId": "b43d153c-60bf-4cb9-a3e4-46353abfb9ec",
    "workspaceRoot": "/absolute/registered/campaign/root"
  }
}
```

`POST /api/data/factory-reset/plan` is a loopback-only planning seam used by the installation-reset coordinator. `Global` forbids a workspace binding. `Workspace` requires the exact registered Campaign id and canonical root. The existing `POST /api/data/factory-reset` confirmation contract remains available for compatibility and retains its data-only boundary.

`DataRetentionOperation` is emitted as `Prune`, `DeleteSession`, `DeleteAttachment`, `ResetMemory`, `FactoryReset`, or `ResetWorkspace`. The two DELETE routes construct the corresponding target operation from `{id}`; callers do not send a body. `MemoryResetScope` is emitted as `Entry`, `Attachments`, `Workspace`, `Saga`, or `Lexicon`. Enum input matching is case-insensitive; responses use the canonical casing shown here. `RetentionDataClass` response values likewise retain their CLR casing, such as `ActiveSessions`. `RetentionRuleUpdateRequest.dataClass` names one configured rule. Matching is case-insensitive after hyphens, underscores, and spaces are removed; grouped aliases include `attachments`, `workspace-indexes`, `accounting`, and `daemon-history`, while typed attachment, batch-file, workspace, and accounting subclasses resolve to their governing rule. `days` is clamped to 1–3,650 and is required when `enabled` is `true`. It is optional when `enabled` is `false`; disabling a rule preserves its prior day value and does not add a capability restriction to explicit deletion. `GET /api/data/retention` returns the complete effective policy, including sweep bounds, accounting floor, and protected session ids. A successful PUT persists the normalized rule and makes it immediately authoritative for subsequent GET, status, and planning calls. The same process-authoritative snapshot controls subsequent inference- and guardrail-log planning and apply calls. Log writers only create, secure, and append dated files; they never enumerate or delete historical files. The former host/security day fields are removed. A query without `from` searches all dated files, and only the canonical `AuditLogs` / `GuardrailLogs` retention rules can trigger age-based mutation. Mutation selectors and `enabled` are required: omission never selects a default destructive scope or silently substitutes `false`. The sole presence-aware field is retention `days`, under the rule above. Data-class and memory-scope choices accept documented names and aliases only; JSON/CLI numeric enum spellings are rejected before mutation.

**Status.** `DataRetentionStatus` contains `generatedAt`, `items`, aggregate `rows`, `files`, and `estimatedBytes`, plus `preservedOutsideSelectedRoot`. Each `DataRetentionStatusItem` has `dataClass`, `rows`, `files`, `estimatedBytes`, `policyEnabled`, nullable `retentionDays`, `store`, and safe `provenance`. The typed class list and physical ownership are defined in DESIGN §5.4.7. For a composite physical owner, `rows` includes its canonical row plus owned companion, index, and provenance rows. File counts and `estimatedBytes` describe managed files that actually exist at inspection time; they do not estimate missing metadata targets or SQLite page allocation. Batch input/output/error items count role references, so one uploaded file may appear in more than one of those items; the response-level aggregate totals exclude those reference-only roles and sum physical owners. The response contains no plaintext content, secret, or host source path.

**Dry-run plan.** `DataRetentionPlan` contains `planId`, the normalized `request`, `generatedAt`, per-class `items`, `blockers`, `conflicts`, aggregate `rows`/`files`/`estimatedBytes`/ `derivedRecords`, deterministic `candidateIds`, and `requiresConfirmation`. Every item reports `dataClass`, `rows`, `files`, `estimatedBytes`, and `derivedRecords`. Blockers report `dataClass`, `resourceId`, `reasonCode`, and a sanitized `message`; conflicts report `code`, `resourceId`, and `message`. Planning reads the same dependency graph used by apply and does not write rows, files, operation history, or checkpoints.

**Apply.** Apply recomputes the current plan. If `expectedPlanId` is present and differs, the server returns `Data.PlanChanged`; clients must preview again. The bundled CLI always fetches the exact plan immediately before `--apply`, shows it in human mode, confirms its id and totals, and sends that id as `expectedPlanId`; `--json --yes` performs the same binding but emits only the final apply result as one machine-readable JSON value. Policy prune leaves blocked/conflicting candidates untouched while applying unrelated eligible candidates. Explicit session/attachment deletion and scoped/global reset fail when their selected target has a blocker or conflict. A prune candidate that becomes protected after planning is preserved and reported through a `Data.PlanChanged` conflict while later independent candidates may still run. Its checkpoint stays before the earliest preserved candidate so recovery re-evaluates that boundary instead of silently skipping it. A successful `DataRetentionApplyResult` reports `operationId`, the applied `planId`, deleted row/file/estimated-byte/derived-record totals, `reconciled`, and the plan diagnostics. Execution is bounded, durable-operation-backed, checkpointed, and restart-idempotent. Each planned candidate receives a bounded post-delete ownership check for its selected rows, derived records, and owned files; `reconciled` is true only after every applicable candidate check succeeds. This is not a global orphan-vacuum operation.

For every file-bearing prune candidate and explicit deletion, the durable checkpoint carries an exact `ARCAMUT2` manifest with the mutation subtype/target, managed-root role, normalized relative path, captured no-follow identity metadata, and operation-scoped same-parent quarantine prefix. Recovery examines only that manifest-declared namespace; unrelated or malformed quarantine is rejected fail-closed. Factory reset uses a separate whole-root recovery contract and re-inventories the complete managed roots when it resumes.

The dependency contract removes owned derived rows and bytes—attachment chunks/embeddings/index state with the attachment, Entry embeddings with Entries, and workspace embeddings with chunks. Managed-file deletion uses no-follow identity capture and apply-boundary revalidation; an object that changes identity is preserved with its metadata and the operation fails closed. Batch references protect uploaded files. Pinned Entries/context/attachments, protected sessions, active operations/inference/idempotency leases, outstanding budget reservations, and in-progress batches remain visible as blockers/conflicts. The effective accounting age is never shorter than `accountingMinimumDays`, and `Sessions.TotalCostUsd` is not accounting authority.

Deleting an attachment does not silently delete independently retained Saga or Lexicon facts. Their typed provenance remains and resolves the missing source as unavailable. Factory reset clears only managed data beneath the configured root and never silently deletes external backups, registered out-of-root workspace content, configuration, or security/credential/key material. The Forge-owned local histories are outside this API's implementation boundary and remain untouched; no coordinated cleanup integration is added by this feature. Prior terminal operation history is cleared in dependency order, while the successful factory reset necessarily leaves its own completed durable-operation marker as the audit/recovery record for that mutation. The `DaemonExecutions` status/plan item includes both process-local execution summaries and persisted Unseen Servant schedule watermarks, so factory-reset previews account for both before clearing them. Factory previews exclude reference-only batch roles and count dependency/index/provenance rows as derived records. Apply uses a restart-idempotent immediate database transaction plus a daemon-start gate, so an interrupted reset can resume and new managed work cannot enter after the final conflict check. Prune and explicit data mutations also share an atomic cross-kind single-flight start, while elapsed-time heartbeats keep ownership during a slow candidate. Inference and guardrail audit writers share a singleton managed-log publication gate with factory reset. The reset takes that gate before its database writer transaction and holds it through log inventory, quarantine, commit, cleanup, and reconciliation: an append that wins is counted and cleared, while an append waiting behind the reset is published afterward and is not reported as a deleted file. Each logger retains its private append serialization and best-effort failure behavior. Managed factory-reset files move into identity-verified, owner-only same-parent quarantine before the database commit. Rollback restores them; after commit cleanup removes them, and restart recovery re-inventories the managed roots, discovers any quarantine left by a crash, and resumes the idempotent whole-root cleanup. Logical row deletion and file unlinking do not promise physical secure erasure on SSD, copy-on-write, snapshot, WAL/free-page, cache, encrypted-replica, or backup media.

This route family maps `Data.InvalidRequest` and `Data.ConfirmationRequired` to **400**, `Data.NotFound` to **404**, `Data.PlanChanged`, `Data.Blocked`, and `Data.Conflict` to **409**, and `Data.ReconciliationFailed` to **500**. An unexpected apply failure is also **500**. The retention rule PUT route returns **400** for `Data.InvalidRequest`; an unexpected policy-store failure is **500**.

### 8.21 The Proving Grounds (`POST /api/proving-grounds/trials/run`)

Ephemeral Trial + Inquisitors (`regex` / `jsonSchema` / `semantic` FastModel judge). Targets: spell / prompt / apprenticeGoal. Terminology strict — industry LLM-test jargon prohibited. Errors §8.23.

### 8.22 Metrics endpoint (`GET /metrics`)

Prometheus text `0.0.4` via `System.Diagnostics.Metrics` + hand-rolled exporter (no OTel/prometheus-net — AOT). Catalog: HTTP requests, inference duration/tokens, tool outcomes, SSE gauge, active sessions (scrape-time query), Sanctum breaches, plus `arcanum_estimated_input_tokens`, `arcanum_provider_reported_input_tokens`, absolute `arcanum_input_token_estimation_variance` (low-cardinality `direction=underestimated|overestimated|exact|inconsistent`), and `arcanum_context_budget_rejections_total`. `arcanum_tool_invocations_total` has the closed `outcome=success|denied|error` domain (Ward and Sanctum refusals are `denied`) and uses the invocation's canonical `tool_name` directly. Unknown names are therefore distinct label values; input/tool-name length limits bound each value's size, but the implementation does **not** enforce a closed label-value set or a global cardinality cap. `arcanum_ward_decisions_total` counts Ward outcomes with exactly `tool_name` and the closed `origin=human|auto_approved|auto_denied|timed_out|cancelled|host_restarted` domain; resolution reason text, tool arguments, and paths are never labels. `arcanum_apply_patch_artifact_cleanup_total` is count-only with closed `outcome=complete|retained`; it never labels paths, sessions, or receipt IDs. Token histograms use token-scale buckets rather than duration buckets; provider/model labels remain low-cardinality (+ runtime meters via `MeterListener`). Path outside `/api`/`/v1`. `Arcanum:Features:Metrics=false` → **404**. `Arcanum:Security:MetricsRequireApiKey` defaults true and is forced true on ListenAny. Auth: `X-Arcanum-Key` or Bearer.

### 8.23 Error code catalog and HTTP status mapping

Wire-stable codes live on `ErrorCodes` (Core). General HTTP mapping authority is `ArcanumErrorMapper.ResolveStatusCode` (Api); the data-lifecycle family has the route-local mapping defined in §8.20. `ResolveStatusCodeDefaultBadRequest` treats unmapped codes as **400** on Apprentice/Campaign/Spell/Prompt/ProvingGrounds routes while still honoring explicit **500** mappings (`ProvingGrounds.InferenceFailed`, `Workspace.WriteFailed`, `Workspace.DeleteFailed`, `Saga.SearchFailed`, `Hub.Error`). Unrecognized strings (including `Hub.Error` via default arm) → **500**. Keep in sync with `ErrorCodes.cs`, `ArcanumErrorMapper.cs`, and the route-local endpoint mappers and tests.

**Default / unmapped:** unlisted codes → **500**; `ResolveStatusCodeDefaultBadRequest` downgrades unmapped → **400** except the explicit **500** set above.

**/api vs /v1:** native `/api` uses `ApiResponse<T>` + codes below. OpenAI `/v1` uses the OpenAI error envelope (`message`/`type`/`code`/`param`); hub failures map similarly (e.g. timeout → **503** `server_error`; unknown model → **404** `model_not_found`). Client-tool forwarding surfaces OpenAI codes `unsupported_parameter` / `too_many_tools` / `invalid_schema` while Core codes remain `ClientTools.*`.

| Codes (grouped) | HTTP | Semantics |
|-----------------|------|-----------|
| `Validation.InvalidPrompt`, `InvalidBody`, `InvalidQuery`, `InvalidProviderType`, `AttachedFiles` | 400 | Request shape / bounds validation |
| `Validation.UnsupportedMediaType` | 415 | Missing or non-JSON `Content-Type` on a JSON body route (status set by `ApiRequestJson`, not the mapper; §8.1) |
| `Hub.Model` | 404 | Model not in any provider `models` |
| `Hub.Error` | 500 | Generic inference failure (mapper default arm) |
| `Campaign.NotFound`; `Session.NotFound` / `EntryNotFound`; `Attachment.NotFound` / `SourceNotFound`; `Grimoire.LoreNotFound`; `Apprentice.NotFound`; `Workspace.NotFound` / `FileNotFound`; `Spell.NotFound`; `Prompt.NotFound`; `Intelligence.HumanPromptNotFound`; `Mcp.ServerNotFound` / `ToolNotFound`; `Daemon.NotFound`; `Files.NotFound`; `Batches.NotFound` / `InputFileNotFound`; `Saga.NotFound`; `ProvingGrounds.SpellNotFound` / `PromptNotFound`; `Workspace.ReplacementNotFound` | 404 | Missing resource |
| `Mcp.DiagnosticDisabled` | 404 | The diagnostic MCP invocation route exists only on the Development edition (status set at the route; §8.21) |
| `Campaign.InvalidPath` / `DuplicateName` / `DuplicatePath`; `Session.Archived` / `InvalidStatus` / `TooManyEntries` / `EntryTooLarge` / `MemoryManagementDisabled` / `EmptyContent`; `Attachment.InvalidRequest` / `InvalidContent` / `InvalidReference` / `SourceUnavailable`; `Apprentice.Disabled` / `PendingQueueFull` / `InvalidGuidance` / `InvalidPlan` / `InvalidGoal` / `InvalidWorkspace`; `Workspace.NameEmpty` / `SymbolicLinkEscape` / `PathTraversal` / `DirectoryNotEmpty` / `ReplacementAmbiguous` / `PathIsDirectory` / `PathIsFile`; `Spell.NoWorkspace` / `InvalidWorkspace` / `InvalidName` / `NameCollision` / `BuiltinReadOnly` / `DuplicateVersion` / `InvalidVersion`; `Prompt.CodexPathNotContained` / `DuplicateVersion` / `InvalidName` / `InvalidVersion` / `InvalidRequest`; `Mcp.AmbiguousServer` / `MissingWorkspace` / `ServerNotRunning` / `AmbiguousTool` / `ToolError`; `Sending.TaskRejected` / `ModalityMismatch` / `SkillNotAdvertised` / `PushNotificationRejected` / `PushNotificationsDisabled`; `Security.BlockedOutboundUrl` / `IdempotencyKeyTooLong`; `Files.InvalidMimeType`; `Batches.InvalidEndpoint`; `Embeddings.ConfirmationRequired`; `ProvingGrounds.InvalidTrial` / `WorkspaceNotAllowed`; `Saga.NotEmpty`; `Scrying.VisionNotSupported` / `TooManyImages` / `UnsupportedMimeType` / `InvalidImageData`; `WebBrowsing.TooLarge` (reserved; today truncates) / `InvalidUrl`; `ClientTools.Disabled` / `TooMany` / `InvalidSchema`; `Guardrails.PiiDetected` / `Blocked`; `StructuredOutput.ValidationFailed` / `SchemaInvalid` | 400 | Domain validation / policy refusal (non-auth) |
| `Campaign.PathNotAllowed`; `Workspace.PathNotAllowed` / `AccessDenied` / `FileWriteDisabled`; `Spell.PathNotAllowed`; `Sending.Disabled` / `AgentNotAllowed`; `Mcp.WorkspaceNotTrusted` / `DiagnosticBlocked`; `Scrying.FeatureDisabled`; `Attachment.Disabled`; `WebBrowsing.SsrfBlocked` | 403 | Path/network/feature deny |
| `Auth.Unauthorized` | 401 | Missing/invalid/ambiguous/oversized API key. **The only 401 the server emits** — written directly by `ApiKeyEndpointFilter` (§11.3), not by `ArcanumErrorMapper`. Clients must switch on this code |
| `Security.MissingApiKey` | 401 | **Client-synthesized only.** The CLI and The Forge produce this locally when no API key is configured, so no request is sent; the server never returns it. The mapper still resolves it to 401 for a `Result` that carries it |
| `Data.InvalidRequest` / `ConfirmationRequired` | 400 | Invalid data-lifecycle operation, rule, scope, or required factory-reset confirmation |
| `Data.NotFound` | 404 | Explicit data-lifecycle target not found |
| `Data.PlanChanged` / `Blocked` / `Conflict` | 409 | Stale preview, retained dependency/hold, or active-work conflict |
| `Session.TooManyPinned`; `Attachment.LimitExceeded`; `Apprentice.AlreadyRunning` / `Running` / `NotPaused` / `CannotReweave` / `NotEscalated` / `MaxReached` / `ConclaveDisabled`; `Security.IdempotencyConflict`; `Security.IdempotencyInProgress`; `Ward.AlreadyResolved` | 409 | State or idempotency conflict |
| `Sending.MaxTasksReached`; `RateLimit.TooManyRequests` | 429 | Defensive compatibility/custom-provider mapping or explicit rate limit; the built-in outbound A2A client queues cancellably instead of emitting `Sending.MaxTasksReached` when its concurrency slots are occupied |
| `Workspace.FileTooLarge`; `Files.TooLarge`; `Scrying.ImageTooLarge`; `Attachment.TooLarge` | 413 | Payload too large |
| `Sending.AgentUnreachable` / `AgentCardInvalid`; `CommLink.Suppressed` | 502 | Downstream / webhook failure |
| `Api.TooManyConnections`; `Connection.Unreachable`; `Embeddings.ProviderUnavailable` / `FeatureDisabled`; `Session.RestQueueFull` | 503 | Capacity / provider unavailable, or bounded Campaign Logger queue rejection |
| `Mcp.DiagnosticTimeout`; `Connection.Timeout`; `WebBrowsing.Timeout` | 504 | Bounded downstream transport/diagnostic operation timeout |
| `Workspace.WriteFailed` / `DeleteFailed`; `ProvingGrounds.InferenceFailed`; `Saga.SearchFailed` | 500 | Explicit infra/search failures (never downgraded by DefaultBadRequest) |
| `Data.ReconciliationFailed` | 500 | Data-lifecycle apply failed or post-delete reconciliation requires operator review |
| `Hub.Unhandled` | 500 | An exception escaped every endpoint and `ArcanumExceptionHandler` wrote the envelope |
| `Validation.SpellOverride` | 500 | `OverrideSpellName` matched no spell. Client-side input, but the mapper has no entry for it, so the default arm still answers **500** |
| `Covenant.InvalidScope` / `InvalidKey` / `InvalidContent` / `InvalidCursor` | 400 | Covenant request shape, key grammar, content bounds, or an opaque cursor that failed bounds or binding validation (§8.28) |
| `Covenant.ForbiddenAuthority`; `Covenant.SensitiveEgressRequiresApproval` | 403 | The caller holds no authority for this effect, or a Covenant-bearing disclosure the operator did not approve |
| `Covenant.NotFound` | 404 | No such scope, key, version, or lane head. Also returned for an artifact owned by another Session, so a distinct code cannot confirm the identity exists |
| `Covenant.ArtifactErased` | 410 | The durable receipt proves this existed and was securely erased; the result can be reported but never returned |
| `Covenant.RevisionConflict` / `LifecycleConflict` / `StaleSnapshot` / `StaleCursor` / `CapacityExceeded` / `SensitiveHistoryRequiresContext` / `CampaignBindingConflict`; `Session.CampaignBindingRequired`; `Campaign.PathIdentityRequired`; `Hub.SessionTurnBusy` / `SessionHistoryChanged` / `SessionTurnRestoredInterrupted` | 409 | Optimistic-concurrency, lifecycle, epoch, capacity, binding, path-identity, and Session-turn conflicts. `StaleCursor` is separate from `InvalidCursor`: it authenticated here and its dataset moved on, while an invalid one cannot be trusted to say which query it belonged to |
| `Hub.ContextBudgetExceeded` | 429 | Confirmed Covenant content could not fit; admission is all-or-fail, with an equivalent structured MCP failure before any side effect |
| `Hub.ProviderToolBufferExceeded` | 502 | A provider streamed more buffered tool bytes or simultaneous call indexes than the code-owned transport bounds permit — an upstream fault, not a caller fault |
| `Covenant.Unavailable` / `OperatorAuthorityUnavailable` / `HostToolsTransitionRequired` / `MaintenanceFailed` / `ManualArtifactErasureRequired` / `ManualRecoveryRequired` / `ErasureIncomplete` / `IntegrityFailure` | 503 | A Covenant tier, authority, maintenance step, erasure, or integrity contract is not currently able to serve. Each names an operator action rather than a retry |
| `Covenant.IneligibleTurn` | — | **MCP-only, deliberately unmapped.** No HTTP route can produce it: the operator API arrives with authenticated authority and never asks whether a turn carried a staging capability. Giving it an arm would invite a route to start returning it |

**Ollama:** providers use the `OpenAICompatible` contract and surface failures as `Hub.Error`. **Familiars:** `ClaudeCodeCli`/`CodexCli` providers surface a missing binary, a refused spawn, a deadline, or a non-zero exit as `Hub.Error` carrying the CLI's own message. `Provider.NotFound` maps to **404**.

### 8.24 OpenAI embeddings (`POST /v1/embeddings`)

Composes `IWeaveService` + tokenizer. `model` must match `Arcanum:Integrations:Embeddings:Model` or be omitted → else **404** `model_not_found`. Long inputs use code-owned chunking + mean-pool/L2. `encoding_format` is `float|base64` (`EmbeddingBlobCodec`). Idempotency-Key is supported. Embedding provider calls have no Arcanum whole-operation deadline; request-abort/caller cancellation propagates directly, while provider failures are sanitized as **503**. Errors use the OpenAI envelope (**400** invalid input/chars; **503** when The Weave is unavailable).

### 8.25 HTTP response compression

Brotli+Gzip via ASP.NET ResponseCompression; early pipeline. Excludes `text/event-stream` and `application/x-ndjson`. `EnableForHttps` left false (framework default).

### 8.26 Persisted inference audit log

Opt-in JSONL (`Arcanum:Host:AuditLog:*`); dated files, owner-only, with a soft daily size cap. A row is written only after a turn completes successfully (ping / ping-stream / v1-completion today); errors, cancellations, and interrupted streams are not audit rows. The writer never deletes historical files: age-based removal runs only through the bounded, durable unified data-retention planner/service under `Arcanum:Retention:AuditLogs`. When an audit query omits `from`, it searches every dated file instead of applying a hidden lookback. `GET /api/audit` keeps the existing `ApiResponse<InferenceAuditRecord[]>` body and supports `from`, `to`, `model`, `sessionId`, and `limit` (default 100, maximum 1,000). Records are newest-first. When more retained matches may remain, the response includes `X-Arcanum-Next-Cursor`; repeat the unchanged query with that opaque value as `cursor`. The cursor binds the audit family, configured file family, all filters, the first-page snapshot time, dated-file prefix identity, and reverse byte boundary, so active appends cannot shift the traversal. A malformed, filter-mismatched, replaced, or retained-away cursor returns **400** `Validation.InvalidQuery` with an exact restart-without-cursor action. Readers reverse-scan fixed 64 KiB blocks and stream one JSON record segment at a time; neither the complete file nor skipped records are materialized as a string array. Tool names and counts are metadata; `Arcanum:Host:AuditLog:RedactToolArguments=true` (default) makes `toolArgumentsJson` null, while opting out records the exact raw argument snapshots at operator risk. Tool results, prompt/answer bodies, and reasoning bodies are never fields in this log. Audit failure is warning-only and never changes the already-successful turn.

### 8.27 Content guardrails (PII / toxicity / topics)

Opt-in via `Arcanum:Features:Guardrails` (default false), with policy under `Arcanum:Security:Guardrails`. Input PII (GeneratedRegex) → `Guardrails.PiiDetected`; toxicity/topics → `Guardrails.Blocked`. Streaming output filtering is code-owned **buffered** mode. Audit JSONL + `GET /api/guardrails/audit` keeps the existing `ApiResponse<GuardrailAuditRecord[]>` body and accepts `from`, `to`, `stage`, `violationType`, `sessionId`, `limit`, and optional `cursor`. Its default/max page, newest-first snapshot semantics, `X-Arcanum-Next-Cursor` continuation, bounded reverse scan, query binding, and **400** `Validation.InvalidQuery` restart contract match `/api/audit`. Only redacted matched spans appear in logs/errors.

### 8.28 Covenant public contract (frozen, not yet routed)

Issue #88 freezes the request and response shapes the Covenant surfaces will use; issue #89 maps the routes and #87 implements maintenance and recovery. **No route in this section is registered yet.** It is documented here because four later slices build against these shapes, and a contract that four slices each invent separately is four contracts.

> **Issue #89 status.** The authenticated boundary these routes will sit behind is now in place — see [§8.29](#829-x-arcanum-context-policy-and-the-covenant-pre-binding-boundary) — and `MemoryStatusDto` gained its content-free `covenant` block. The routes themselves are still unmapped.

Every shape is a named positional record registered with `ArcanumJsonContext`, every enum is string-only, and every request record owns a `Validate()` that produces the typed refusal below before anything reaches storage. `CovenantPublicContractInventory` is the closed list; `CovenantPublicContractInventoryTests` fails when a public Covenant `*Request`/`*Dto` is missing from it, when a declared entry names a type that no longer exists, when a shape carries an `object`/`JsonElement`/`JsonNode` member, or when a wire enum would accept an integer.

| Planned route | Request | Response |
|---|---|---|
| `POST /api/memory/covenant/list` | `CovenantListRequest` | `CovenantPageDto` |
| `POST /api/memory/covenant/query` | `CovenantQueryRequest` | `CovenantPageDto` |
| `POST /api/memory/covenant/detail` | `CovenantDetailRequest` | `CovenantDetailDto` |
| `POST /api/memory/covenant/versions` | `CovenantVersionsRequest` | `CovenantVersionPageDto` |
| `POST /api/memory/covenant/sources` | `CovenantSourcesRequest` | `CovenantSourcesDto` |
| `POST /api/memory/covenant/explain` | `CovenantExplainRequest` | `CovenantExplainDto` |
| `POST /api/memory/covenant/set/prepare` | `CovenantSetPrepareRequest` | `CovenantMutationPreflightDto` |
| `POST /api/memory/covenant/retire/prepare` | `CovenantRetirePrepareRequest` | `CovenantMutationPreflightDto` |
| `PUT /api/memory/covenant` | `CovenantSetRequest` | `CovenantMutationResultDto` |
| `POST /api/memory/covenant/retire` | `CovenantRetireRequest` | `CovenantMutationResultDto` |
| `POST /api/memory/covenant/schema/repair` | `CovenantSchemaRepairRequest` | `CovenantSchemaRepairResultDto` |
| `POST /api/memory/covenant/index/rebuild` | `CovenantIndexRebuildRequest` | `LongRunningOperationDto` (**202**) |
| `POST /api/memory/covenant/schema/reinitialize/prepare` | `CovenantFamilyReinitializePrepareRequest` | `CovenantFamilyReinitializePlanDto` |
| `POST /api/memory/covenant/schema/reinitialize` | `CovenantFamilyReinitializeApplyRequest` | `LongRunningOperationDto` (**202**) |
| `POST /api/campaigns/path/status` | `CampaignPathIdentityStatusRequest` | `CampaignPathIdentityStatusPageDto` |
| `POST /api/campaigns/{id}/path/prepare` | `CampaignPathPrepareRequest` | `CampaignPathIdentityPlanDto` |
| `POST /api/campaigns/{id}/path/apply` | `CampaignPathApplyRequest` | `CampaignPathIdentityResultDto` |
| `POST /api/sessions/campaign-binding/status` | `SessionCampaignBindingStatusRequest` | `SessionCampaignBindingStatusPageDto` |
| `POST /api/sessions/campaign-binding/prepare` | `SessionCampaignBindingPrepareRequest` | `SessionCampaignBindingPlanDto` |
| `POST /api/sessions/campaign-binding/apply` | `SessionCampaignBindingApplyRequest` | `SessionCampaignBindingResultDto` |

**Bodies, never query strings.** Campaign identities, Covenant keys, filters, search text, and cursors are body fields on every route above, including the read-only ones. A URL is the one part of a request that nothing redacts, and an access log keeps it forever.

**Frozen request bounds.** Search text ≤ 512 UTF-8 bytes and ≤ 32 terms (`Validation.InvalidQuery`); authored content 1–2,048 UTF-8 bytes (`Covenant.InvalidContent`); keys match `[a-z0-9][a-z0-9._-]{0,127}` (`Covenant.InvalidKey`); cursors and tokens ≤ 4,096 encoded characters, checked before decoding (`Covenant.InvalidCursor`); apply-request digests are exactly 64 hexadecimal characters (`Validation.InvalidBody`). `limit` is **clamped** to 1–200 with a default of 50 rather than refused, and the clamped value is exposed as `EffectiveLimit`. Byte bounds are UTF-8 bytes, not characters.

**Explicit scope, always.** `CovenantCursorScopeSelection` (`Global`, `Campaign`, `AllScopes`) has no default; an omitted value is `Covenant.InvalidScope`, so an installation-wide read is never the result of a missing field. `Global`/`AllScopes` must not carry `campaignId`; `Campaign` must. Detail carries `CovenantScope`, which has no all-scopes member at all, because the same key can exist in Global and in every Campaign. A `Set` carries no lane — Confirmed is the only lane an operator authors — and a Global Proposed retirement is `Covenant.InvalidScope`.

**Effective versus local state.** `CovenantHeadDto` reports `lifecycle` (local) and `shadow`/`materialization` (effective for the evaluated Campaign) separately. Both effective fields are `NotEvaluated` when the request supplied no `effectiveForCampaignId`, because the honest answer differs per Campaign; the evaluation Campaign is part of the filter digest a cursor binds.

**Protected responses.** Every content-bearing Covenant read is written through `CovenantProtectedJsonResult<T>` or `CovenantProtectedStreamResult`: the operation lease is revalidated immediately before the first byte, the response carries the exact tuple in [§8.29](#829-x-arcanum-context-policy-and-the-covenant-pre-binding-boundary) with any `ETag` and `Last-Modified` removed, and the lease is released after serialization completes. A conditional revalidation of protected content is a 304 on data the installation may since have erased.

**Idempotency.** Covenant set and retirement use their canonical `mutationId` and the durable receipt ledger as the sole replay mechanism; a supplied HTTP `Idempotency-Key` has no semantic effect on these routes. Long-running Covenant operations bind a caller-generated `operationId` plus a stable `applyRequestDigest`: the same pair replays the durable operation, a different digest under the same id is `Security.IdempotencyConflict`, and neither depends on the process that issued the original **202** still being alive.

`MemorySearchScope.All` continues to exclude Covenant. Covenant content search, sources, and explain use the typed routes above and always require Covenant read authority, so no existing default `All` request silently gains privilege.

### 8.29 `X-Arcanum-Context-Policy` and the Covenant pre-binding boundary

Issue #89 lands the authenticated boundary every Covenant route will sit behind. It applies today to every `/api` and `/v1` route, whether or not any Covenant route exists yet.

**Decision order.** API-key authentication → `X-Arcanum-Context-Policy` validation → Covenant authority issuance → body-size enforcement → source-generated binding. Everything before "body-size enforcement" happens with **zero bytes of the request body read**. Authentication is strictly first: a wrong key presented with a malformed policy header returns **401**, never 400, because a 400 would tell an unauthenticated caller that they reached a real route and that only their header spelling was wrong.

**The header.**

| Sent | Result |
|---|---|
| absent | `Default` — durable context may be injected |
| exactly `none` (one header, lowercase ASCII) | `None` — durable context is suppressed for this request; the server echoes `X-Arcanum-Context-Policy: none` |
| `NONE`, `None`, ` none`, `none `, `""`, `none,none`, any other value | **400** `Covenant.InvalidScope`, before binding |
| the header repeated | **400** `Covenant.InvalidScope` — refused, never merged |
| any value on a route that cannot inject Covenant content | **400** `Covenant.InvalidScope` |

Repetition is refused rather than merged because every merge rule — first wins, last wins, comma-join — is a guess about which of two disagreeing senders meant it. A route that cannot inject context refuses the header rather than ignoring it, because a caller that sent `none` to a route that silently discarded it believes it suppressed content it in fact sent. `X-Arcanum-Context-Policy` is in the CORS exposed-header set so a browser client can read back the decision instead of assuming it.

Routes that accept the header today: `PostIntelligencePing`, `PostIntelligencePingStream`, `PostIntelligenceContextInspect`, `PostOpenAiChatCompletions`, `TestPrompt`, `Prompt_Execute`, `Prompt_ExecuteStream`, `Spell_Execute`, `Spell_ExecuteStream`, `Spell_Cast`.

**The protected response tuple.** Every protected success *and* failure emits exactly:

```text
Cache-Control: no-store, private
Pragma: no-cache
Expires: 0
```

with `ETag` and `Last-Modified` removed. `private` survives intermediaries that treat an unqualified `no-store` as advisory; `Pragma` and `Expires` cover HTTP/1.0-era caches that ignore `Cache-Control`. It is applied on response start, so it also covers framework-generated refusals no handler wrote. Streaming responses keep their `Cache-Control: no-cache` default when they are *not* protected and are never downgraded when they are — headers cannot be corrected after the first byte.

**Authority.** A route declares exactly one `CovenantAuthorityRequirement`; the boundary issues a context bound to it and to the current clean authority epoch, and an endpoint filter rechecks — never reissues — that epoch after binding. A missing context is **403** `Covenant.ForbiddenAuthority`; an epoch that moved (host-tools taint, key rotation) is **503** `Covenant.OperatorAuthorityUnavailable`. `ProtectedRead` cannot be declared as an operator requirement, so a context minted for an inspection page can never authorize a mutation.

---

*End of API reference.*
