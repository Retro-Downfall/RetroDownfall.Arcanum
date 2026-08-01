# Arcanum — API Reference

This document is the source of truth for Arcanum's native HTTP API, OpenAI-compatible API, wire
formats, authentication behavior, request and response contracts, status mapping, and public error
codes. It covers both general `/api` calls and the OpenAI-compatible `/v1` surface.

Architecture and design decisions belong in [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md). The readable
architecture guide is [`Arcanum.Design.Human.md`](Arcanum.Design.Human.md). The complete public
configuration model belongs in
[`Compendium.README.md`](Compendium.README.md#complete-configuration-reference).

The established §8 contract numbers are retained in this extracted reference so existing issue,
test, and documentation citations remain stable.

## 1. Complete API surface

**API surface (`MapArcanumEndpoints`):**

| Method | Path | Contract/purpose |
|--------|------|-----------------|
| GET | `/metrics` | Prometheus text-format metrics. |
| GET | `/api/health` | Health check. |
| GET | `/api/meta` | Instance metadata and feature flags for sidecar discovery (`ApiResponse<InstanceMetadataDto>`). |
| POST | `/api/server/quit` | Accept an authenticated operator shutdown request (`202` + `ApiResponse<bool>`), then stop the host after the acknowledgement completes. |
| GET | `/api/budget` | Daily budget snapshot (`ApiResponse<BudgetSummaryDto>`: enabled, daily limit, today's spend, remaining, spent percent, alert threshold; DESIGN §22.2). |
| GET | `/api/grimoire/stats` | Grimoire database statistics (`ApiResponse<GrimoireStatsDto>`; database + WAL byte sizes and per-table row counts via `GrimoireStatsService`). |
| GET | `/api/config` | Read live `ArcanumSettings`; provider endpoints remain redacted, while secret environment-variable references are returned without resolving their values (`ApiResponse<ArcanumSettings>`; §8.12). |
| PUT | `/api/config` | Validate and write a full settings snapshot to `arcanum.json` (`ApiResponse<bool>`; §8.12). |
| POST | `/api/config/validate` | Validate settings without writing (`ApiResponse<bool>`; §8.12). |
| GET | `/api/models` | Flatten configured models across all providers (`ApiResponse<ModelInfoDto[]>`; endpoint redacted as `"***"`; read-only, no connectivity checks; §8.12). |
| GET | `/api/providers` | List configured providers with `apiKey`/`endpoint` redacted (`ApiResponse<ProviderInfoDto[]>`; read-only; §8.12). |
| GET | `/api/perception/look` | Eye of the World snapshot (optional `directory` query; requires `Arcanum:Security:PerceptionWorkspaceRoots`; **403** when unset). |
| POST | `/api/intelligence/ping` | Buffered inference. |
| POST | `/api/intelligence/ping-stream` | NDJSON streaming inference (same `PingRequest` extensions as buffered ping). |
| POST | `/api/intelligence/human-response` | Submit human-in-the-loop answer. |
| POST | `/api/intelligence/arsenal` | Spell names, metadata-only `SpellSummary[]`, native tools, and MCP server status. |
| POST | `/api/intelligence/mana` | Read-only diagnostic Mana (token) counter (`ApiResponse<ManaCountResult>`; body `ManaCountRequest` { `messages`, `prompt`, `model`, `tools` }). |
| POST | `/api/intelligence/context/inspect` | Read-only effective-turn preview (`ContextPreviewRequest` → `ApiResponse<ContextPreviewResult>`); reuses production routing, retrieval, tool policy, DCI assembly, compression, and token accounting without main inference, tool invocation, or assistant-entry persistence. |
| POST | `/api/web/search` | First-class bounded web search (`WebSearchWorkflowRequest` → `ApiResponse<WebSearchWorkflowResult>`; citations and provider usage; DESIGN §11.27). |
| POST | `/api/web/browse` | First-class bounded static page read (`WebBrowseWorkflowRequest` → `ApiResponse<WebBrowseWorkflowResult>`; JavaScript mode degrades explicitly when no renderer is configured; DESIGN §11.27). |
| POST | `/api/web/research` | Server-owned bounded multi-hop research as NDJSON `WebResearchStreamFrame` lines (limits/progress/result/error; DESIGN §11.27). |
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
| GET | `/api/sessions/{id}/stream` | SSE replay + live entry stream. |
| GET | `/api/sessions/{id}/attachments` | Revalidate tracked sources asynchronously, then list **bound** session attachments (`ApiResponse<SessionAttachmentDto[]>`; includes `indexingStatus`, the snapshot `RelativePath` for Reveal, and sanitized source provenance/refreshability; never an absolute source path; DESIGN §10.2.5). |
| POST | `/api/sessions/{id}/attachments` | Create a snapshot-only bound attachment from multipart field `file` (optional `logicalName` form field); returns `201` + `ApiResponse<SessionAttachmentDto>`. The filename and declared MIME are hints; the server validates name, detected content, kind-specific byte limit, MIME/Scrying policy, strict encoding for text, and Session byte/version budgets. Unsupported binary/PDF/Office content remains a valid `Binary` attachment with `NotEligible` indexing status. |
| POST | `/api/sessions/{id}/attachments/reference` | Create a refreshable live reference from `CreateSessionAttachmentReferenceRequest` (`workspacePath`, optional `workspaceId`, optional `logicalName`); the server alone resolves/authorizes/reads the source and persists the already-verified bytes; returns `201` + `ApiResponse<SessionAttachmentDto>`. |
| GET | `/api/sessions/{id}/attachments/{attachmentId}/content` | Stream the authenticated plaintext of that stored bound snapshot with `Content-Disposition: attachment`, `Cache-Control: no-store`, and `X-Content-Type-Options: nosniff`. Never returns or redirects to a live source path. |
| POST | `/api/sessions/{id}/attachments/{attachmentId}/refresh` | Operator-triggered secure refresh through the same source-validation/persistence core as `refresh_session_file`; returns `ApiResponse<AttachmentRefreshEvent>` only after the backend has reused or persisted the confirmed current version. |
| GET | `/api/sessions/{id}/context-pins` | List durable, structured session context pins. |
| POST | `/api/sessions/{id}/context-pins` | Create or update a context pin by `(session, kind, stable target)`; accepts file, directory snapshot, symbol/range, session entry, attachment, URL, and diagnostic kinds. |
| DELETE | `/api/sessions/{id}/context-pins/{pinId}` | Remove a durable context pin without changing `Entries.IsPinned`. |
| POST | `/api/sessions/{id}/fork` | Create an independent branch of a session, optionally truncated at `upToEntryId` (**201**; DESIGN §11.16.1). |
| POST | `/api/embeddings/reset` | Truncate embedding tables for RAG dimension-change recovery (requires `?confirm=true`; optional `?scope=all\|entry\|workspaceFile\|saga\|sessionAttachment`, default `all`). |
| DELETE | `/api/sessions/{id}/entries/{entryId}` | Delete a single entry from a session (**204**). |
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
| GET | `/api/spells` | List built-in + workspace spells (`ApiResponse<SpellSummary[]>`; optional `workspace` query; §8.14). |
| GET | `/api/spells/{name}` | Spell detail (`ApiResponse<SpellDetail>`; optional `workspace` query; **404** when missing). |
| POST | `/api/spells` | Create workspace spell (`ApiResponse<bool>`; optional `workspace` query; **400** validation). |
| PUT | `/api/spells/{name}` | Update workspace spell (`ApiResponse<bool>`; optional `workspace` query; **400** on built-in or validation failure). |
| DELETE | `/api/spells/{name}` | Delete workspace spell (**204** on success; **400** on built-in or validation failure; §8.14). |
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
| POST | `/api/campaigns` | Register campaign directory (`ApiResponse<CampaignDto>`; **201** + `Location`; creates `.arcanum/`; DESIGN §19). |
| PUT | `/api/campaigns/{id}` | Update campaign (`ApiResponse<CampaignDto>`; DESIGN §19). |
| DELETE | `/api/campaigns/{id}` | Remove campaign (**204**; DESIGN §19). |
| GET | `/api/campaigns/{id}/spells` | Spells scoped to a campaign, merging built-ins with campaign spells shadowing them (`ApiResponse<SpellSummary[]>`; `?q=`, `?tag=`, `?tool=`; **404** `Campaign.NotFound`; DESIGN §19). |
| GET | `/api/campaigns/{id}/prompts` | Prompts scoped to a campaign (`ApiResponse<ListPageResult<PromptSummaryDto>>`; `?q=`, `?tag=`; **404** `Campaign.NotFound`; DESIGN §19). |
| GET | `/api/campaigns/{id}/sessions` | Sessions scoped to a campaign (`ApiResponse<SessionQueryResult>`; `?status=`, `?search=`, `?limit=`, `?beforeUpdatedAt=`; **404** `Campaign.NotFound`; DESIGN §19). |
| POST | `/api/campaigns/{id}/export` | Export spells + prompts + settings (`ApiResponse<CampaignExportDto>`; DESIGN §19). |
| POST | `/api/campaigns/{id}/import` | Import portable campaign bundle (`ApiResponse<CampaignImportResultDto>`; DESIGN §19). |
| GET | `/api/campaigns/{id}/codex` | Read campaign `CODEX.md` (`ApiResponse<CodexContentDto>`; `exists: false` when file absent; **404** `Campaign.NotFound`; DESIGN §19). |
| PUT | `/api/campaigns/{id}/codex` | Create or overwrite campaign `CODEX.md` (`ApiResponse<CodexContentDto>`; body `{ "content": "..." }`; **400** when over the code-owned CODEX size limit; DESIGN §19). |
| DELETE | `/api/campaigns/{id}/codex` | Delete campaign `CODEX.md` (**204**; DESIGN §19). |
| GET | `/api/codex` | Read global `~/.config/arcanum/CODEX.md` (`ApiResponse<CodexContentDto>`; DESIGN §19). |
| PUT | `/api/codex` | Create or overwrite global CODEX (`ApiResponse<CodexContentDto>`; DESIGN §19). |
| DELETE | `/api/codex` | Delete global CODEX (**204**; DESIGN §19). |
| GET | `/api/campaigns/{campaignId}/sanctum` | Campaign Sanctum config (`ApiResponse<SanctumConfig>`; default `Enabled: false`; DESIGN §11.15). |
| PUT | `/api/campaigns/{campaignId}/sanctum` | Update Sanctum config (`ApiResponse<SanctumConfig>`; body `SanctumConfig`). |
| GET | `/api/campaigns/{campaignId}/sanctum/breaches` | Paginated Sanctum breach history (`ApiResponse<SanctumBreachQueryResult>`; `?limit=` default 100 clamp 1–1,000, `?before=` ISO 8601 cursor, `?tool=` filter). |
| GET | `/api/wards` | List active wards (`ApiResponse<WardDto[]>`; DESIGN §11.14). |
| GET | `/api/wards/{id}` | Active ward detail (`ApiResponse<WardDto>`; **404** `Ward.NotFound`). |
| POST | `/api/wards/{id}` | Resolve a ward (`ResolveWardRequest`: `allow`, optional `reason`); returns `ApiResponse<WardResolutionDto>`. |
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
| GET | `/api/apprentices` | List Apprentices (`ApiResponse<ListPageResult<ApprenticeSummaryDto>>`; optional `?campaignId=`, `?status=`, `?limit=`, `?beforeUpdatedAt=`; DESIGN §19.6). |
| GET | `/api/apprentices/{id}` | Apprentice detail (`ApiResponse<ApprenticeDetailDto>`; **404** `Apprentice.NotFound`; DESIGN §19.6). |
| POST | `/api/apprentices` | Create Apprentice (`ApiResponse<ApprenticeDetailDto>`; **201** + `Location`; DESIGN §19.6). |
| DELETE | `/api/apprentices/{id}` | Delete terminal Apprentice (**204**; **409** `Apprentice.Running`; DESIGN §19.6). |
| POST | `/api/apprentices/{id}/start` | Start plan generation and execution (**202**; **409** `Apprentice.AlreadyRunning`; DESIGN §5.7). |
| POST | `/api/apprentices/{id}/pause` | Pause at step boundary (**202**; DESIGN §5.7). |
| POST | `/api/apprentices/{id}/resume` | Resume from checkpoint (**202**; **409** `Apprentice.NotPaused`; DESIGN §5.7). |
| POST | `/api/apprentices/{id}/cancel` | Cancel execution (**202**; DESIGN §5.7). |
| POST | `/api/apprentices/{id}/reweave` | Replace pending plan steps (`ApiResponse<ApprenticeDetailDto>`; **400** `Apprentice.InvalidPlan`; **409** `Apprentice.CannotReweave`; DESIGN §5.7). |
| POST | `/api/apprentices/{id}/intervene` | Resolve **Escalated** Apprentice with DM guidance (**202**; **409** `Apprentice.NotEscalated`; DESIGN §5.7). |
| POST | `/api/apprentices/{id}/cast` | **The Conclave** cross-Apprentice delegation: mint a child Apprentice from a parent (`ApiResponse<ApprenticeDetailDto>`; **201**; gated by `Arcanum:Features:Conclave` and the code-owned lineage/capacity limits). |
| GET | `/api/apprentices/{id}/chronicle` | Chronicle SSE stream (`text/event-stream`; DESIGN §5.7, DESIGN §19.6). |
| — | `/api/conclave/a2a/*` | A2A (Agent-to-Agent) JSON-RPC surface (`MapA2A`), mapped only when `Arcanum:Features:Conclave && Arcanum:Features:A2AServer`. |
| GET | `/api/conclave/a2a/agent-card` | Authenticated A2A Agent Card ("Heraldry") — not the public, unauthenticated `/.well-known/agent-card.json` convention. |
| GET | `/api/workspaces` | List registered workspaces (`ApiResponse<WorkspaceInfo[]>`; §8.17). |
| GET | `/api/workspaces/{id}` | Workspace metadata (`ApiResponse<WorkspaceInfo>`; **404** when missing). |
| POST | `/api/workspaces` | Register a workspace directory (`ApiResponse<WorkspaceInfo>`; **201** with `Location`; **400** validation). |
| PUT | `/api/workspaces/{id}` | Update workspace name/type (`ApiResponse<WorkspaceInfo>`; **404** when missing). |
| DELETE | `/api/workspaces/{id}` | Unregister workspace (**204** on success; **404** when missing). |
| GET | `/api/workspaces/{id}/files` | List files in a registered workspace (`ApiResponse<FileListResult>`; optional `relativePath`, `recursive`, `searchPattern`; §8.17). |
| GET | `/api/workspaces/{id}/files/info` | File or directory metadata (`ApiResponse<FileEntry>`; optional `relativePath`; §8.17). |
| GET | `/api/workspaces/{id}/files/contents` | Read file contents as UTF-8 text (`ApiResponse<FileReadResult>`; required `relativePath`; §8.17). |
| HEAD | `/api/workspaces/{id}/files/contents` | Size/freshness check for a file. |
| PUT | `/api/workspaces/{id}/files/contents` | Create or overwrite a file (`ApiResponse<FileWriteResult>`; **200**; required `relativePath` plus a `FileWriteRequest` body; gated by `Arcanum:Workspaces:EnableFileWrite`, otherwise **403** `Workspace.FileWriteDisabled`). |
| PATCH | `/api/workspaces/{id}/files/contents` | Replace a verbatim text block in an existing file (`ApiResponse<TextBlockReplaceResult>`; **200**; required `relativePath`; §8.17). |
| DELETE | `/api/workspaces/{id}/files` | Delete a file or directory (`ApiResponse<FileDeleteResult>`; **200**; required `relativePath`; optional `recursive`; §8.17). |
| POST | `/api/workspaces/{id}/files/directory` | Create a directory, including parents (`ApiResponse<DirectoryCreateResult>`; **201**; required `relativePath`; §8.17). |
| POST | `/api/workspaces/{id}/files/divine` | Semantic search over a workspace's indexed files (`ApiResponse<WorkspaceSearchResult[]>`; body `WorkspaceSemanticSearchRequest` with required `query` and optional `limit`). |
| POST | `/api/workspaces/{id}/files/index` | Kick off an immediate background re-index of the workspace via `WorkspaceIndexingService.IndexNowAsync` (`ApiResponse<bool>`; **202**; the work continues until host shutdown rather than client disconnect). |
| GET | `/api/workspaces/{id}/files/index/status` | Read-only indexing status for a workspace (`ApiResponse<WorkspaceIndexStatusDto>`): vector mode/diagnostic, `IndexingEnabled`, durable file/chunk counts, and volatile `Watching`/`Degraded`/`Overflowed`/`Reconciling` plus last-event/last-success timestamps. |
| GET | `/api/workspaces/{id}/files/chunks` | Bounded, paginated chunk previews for a workspace (`ApiResponse<WorkspaceFileChunkPage>`; optional `relativePath` filter, clamped) including character offsets and one-based source line ranges. |
| GET | `/api/unseen-servant/jobs` | List Unseen Servant jobs with base and effective polling intervals (**canonical** Unseen Servant pacer API; §8.15). |
| POST | `/api/unseen-servant/jobs/{name}/initiative` | Set adaptive initiative (dynamic interval) for a job by name; returns updated status. |
| GET | `/api/daemons` | List registered daemon jobs (`ApiResponse<DaemonJobInfo[]>`; **plural** `daemons` — registry; §8.15). |
| GET | `/api/daemons/{id}` | Daemon job metadata (`ApiResponse<DaemonJobInfo>`; **404** when missing). |
| POST | `/api/daemons/{id}/run` | Run a daemon job on demand; returns `ApiResponse<DaemonExecutionSummary>` with execution id (**400** when not found, disabled, or already running on-demand). |
| GET | `/api/daemons/{id}/history` | Execution history for a daemon (`ApiResponse<DaemonExecutionSummary[]>`). |
| GET | `/api/executions/{id}` | Execution detail (`ApiResponse<DaemonExecutionDetail>`; **404** when missing). |
| POST | `/api/executions/{id}/cancel` | Cancel a running execution; returns updated `ApiResponse<DaemonExecutionSummary>` (**400** `Daemon.NotRunning` when not running). |
| GET | `/api/logs` | Paginated in-memory log query (`ApiResponse<LogQueryResult>`; optional `minLevel`, `category`, `from`, `to`, `search`, `limit`, `beforeSequence`; §8.16). |
| GET | `/api/audit` | Persisted inference audit log query (`ApiResponse<InferenceAuditRecord[]>`; optional `from`, `to`, `model`, `sessionId`, `limit`; §8.26). |
| GET | `/api/guardrails/audit` | Persisted guardrails violation audit log query (`ApiResponse<GuardrailAuditRecord[]>`; optional `from`, `to`, `stage`, `violationType`, `sessionId`, `limit`; §8.27). |
| GET | `/api/operations` | List durable operations with optional `kind`, `state`, `limit`, and `offset` filters. Returns safe summaries only; encrypted checkpoint payloads and references are never serialized (DESIGN §10.8). |
| GET | `/api/operations/{id}` | Show one durable operation's lifecycle, links, lease, attempt, checkpoint version/presence, safe summary, and terminal error code. |
| POST | `/api/operations/{id}/cancel` | CAS-protected transition to `Cancelling`; **404** unknown, **409** stale/terminal. |
| POST | `/api/operations/{id}/retry` | CAS-protected reset of `Failed`, `Abandoned`, or `ReconciliationRequired` to `Pending`; checkpoint remains available to the recovery policy. |
| POST | `/api/operations/reconcile` | Run a bounded authenticated recovery pass and return `LongRunningOperationReconciliationSummary`. |
| GET | `/api/events/daemon` | SSE stream of `DaemonEvent` frames (daemon job lifecycle for scheduled and on-demand runs); **not** wrapped in `ApiResponse<T>`. |
| GET | `/api/events/mcp` | SSE stream of `McpServerEvent` frames (MCP server lifecycle); **not** wrapped in `ApiResponse<T>`. |
| GET | `/api/events/logs` | SSE stream of `LogEntry` frames (live log tail from ring buffer); **not** wrapped in `ApiResponse<T>`. |
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
| POST | `/v1/files` | Upload standalone file storage, `multipart/form-data`; **201** + `OpenAiFileObject`. |
| GET | `/v1/files` | List uploaded files, optional `?purpose=` filter. |
| GET | `/v1/files/{id}` | File metadata; **404** for unknown/malformed id. |
| DELETE | `/v1/files/{id}` | Deletes metadata row + on-disk bytes. |
| GET | `/v1/files/{id}/content` | Raw bytes; always `Content-Disposition: attachment`. |
| POST | `/v1/batches` | Create an async bulk chat-completion job over an uploaded JSONL file; **200** + `OpenAiBatchObject`, `status: "validating"`. |
| GET | `/v1/batches` | List batches, optional `?status=` filter. |
| GET | `/v1/batches/{id}` | Batch status + `request_counts`; **404** for unknown/malformed id. |
| POST | `/v1/batches/{id}/cancel` | Idempotent cancel; stops in-flight processing within ~2s. |
| POST | `/v1/batches/{id}/reset` | Reset a stuck `in_progress` batch back to `validating` (input file must still exist on disk; **409** if currently in-flight; **200** `OpenAiBatchObject`; DESIGN §11.21). |
| GET | `/v1/models` | OpenAI-compatible models list (flattened configured models across providers via the same `ModelInfoBuilder` that backs `GET /api/models`); **not** wrapped in `ApiResponse<T>`. |
**JSON wire shape (`/api` and shared primitives):** JSON endpoints under `/api` use the `ApiResponse<T>` envelope (`Data`, `IsSuccess`, `Error`, `TraceId`) except for these non-envelope routes:

| Route | Wire format | Section |
|-------|-------------|---------|
| `POST /api/intelligence/ping-stream` | NDJSON event lines (`application/x-ndjson`) | §8.5 |
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

- **`GET /api/meta`** wraps **`InstanceMetadataDto`** (version, OS, runtime, process identity, Grimoire paths, effective host binding, and intelligence feature flags).
- **`GET /api/config`** / **`PUT /api/config`** / **`POST /api/config/validate`** use **`ArcanumSettings`** as the payload type (§8.12). Read masks provider endpoints and returns only environment-variable references for provider credentials, HTTPS certificate passwords, and CommLink—not their secret values. Raw bodies fail closed on every unknown/obsolete path before source-generated deserialization; writes merge only endpoint masks.
- **`DELETE /api/sessions/{id}`** returns **204** with no body on success (soft-delete archive; idempotent — DESIGN §11.16); **`POST /api/sessions/{id}/rest`** returns **202** with `ApiResponse<bool>` when the job is queued, or **503** with `Session.RestQueueFull` when enqueue is rejected.
- **`POST /api/commlink/send`** returns **502** with `ApiResponse<bool>` when the outbound webhook HTTP call fails (non-success status or transport error).

**Daemon route families:** **`/api/unseen-servant/*`** manages Unseen Servant job **configuration** and runtime scheduling intervals (`GET /api/unseen-servant/jobs`, `POST /api/unseen-servant/jobs/{name}/initiative`). **`/api/daemons/*`** and **`/api/executions/*`** are the daemon job **registry** and **execution history** API for all registered `IDaemonJob` types (§8.15).

The `/api` and `/v1` groups are protected by `ApiKeyEndpointFilter` (DESIGN §11), including the OpenAPI document and Scalar reference UI on `/api` (`MapOpenApi` / `MapScalarApiReference` are registered on the same keyed group, so browsers need a valid API key like any other `/api` caller).

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

### 8.2 `ArcanumJsonContext` — source-generated, public

CamelCase source-gen context for HTTP wire types (index 0 of resolver chain). Every `ApiResponse<T>` payload and `/v1` DTO needs `[JsonSerializable]`. Separate contexts: `GrimoireJsonContext`, `ConfigurationJsonContext` (Core), `McpJsonSerializerContext` / `McpConfigJsonSerializerContext` (Infrastructure). `[JsonPropertyName]` only for external snake_case/spec wires (OpenAI `/v1`, MCP JSON-RPC, selected NDJSON tool fields) — not arbitrary `/api` DTOs.

### 8.3 Service registration in `AddArcanumApiServices`

Registers Infrastructure + daemon services, `ApiKeyEndpointFilter`, OpenAPI/JSON (`ArcanumJsonContext` head of chain), named OpenAI `HttpClient`, `IChatClientFactory`, tokenizer, scoped `WizardIntelligenceProvider`. Singletons use `IOptionsMonitor`; scoped/request use `IOptionsSnapshot`.

### 8.4 Returning the envelope from a Minimal API handler

Successful endpoints use `Results.Ok(ApiResponse<T>.FromResult(result, traceId))`. Failable endpoints use `Results.Json` with the source-generated `JsonTypeInfo` and an explicit HTTP status code. No anonymous DTOs; no reflection-based model binding.

**Selected status contracts:**

- **`POST /api/intelligence/ping`** — `ApiResponse<PromptResponseDto>` on every path: **400** for request/reasoning validation, **200** on success, and shared `ArcanumErrorMapper` status for inference failures (for example 404/403/400/503/500 by stable code). The payload contract is detailed in §8.10.

- **`POST /api/intelligence/human-response`** — **400** validation (including the code-owned answer UTF-8 byte limit); **404** + `ApiResponse<bool>` failure when no waiter exists for `promptId` (`Intelligence.HumanPromptNotFound`); **200** + `ApiResponse<bool>` with `Data: true` when the answer is accepted.

- **`POST /api/mcp/reload`** and **`POST /api/intelligence/arsenal`** — Optional JSON body **`OptionalWorkspaceRequest`** (`{ "workingDirectory": "..." }` only). Responses remain `ApiResponse<T>` as today.

### 8.5 NDJSON streaming pipeline

`/api/intelligence/ping-stream` uses NDJSON (`application/x-ndjson`) for real-time token streaming:

- **Server:** Events serialized via `Utf8JsonWriter` + `ArcanumJsonContext`, newline-terminated, flushed per event. Writer: **`InferenceExecuteWriter`** (also used by spell/prompt `execute-stream`).
- **Wire shape:** Each line is an `IntelligenceEvent` with **camelCase string** discriminator **`type`**: **`"status"`**, **`"sessionBound"`**, **`"conversationBound"`** (deprecated alias emitted alongside **`sessionBound`** for one release), **`"context"`**, **`"token"`**, **`"reasoning"`**, **`"result"`**, **`"error"`**, **`"toolCall"`**, **`"toolResult"`**, **`"warded"`**, **`"wardResolved"`**, **`"toolError"`** (tolerated tool exception, emitted immediately before its `toolResult`; DESIGN §10.2.1). `context` carries the latest pre-call `ContextTokenBreakdown`; a second frame for the same call may add provider-reported input and variance after usage arrives. The enum is annotated with `[JsonConverter(typeof(JsonStringEnumConverter<IntelligenceEventType>))]` and per-member `[JsonStringEnumMemberName]` so the AOT JSON source generator emits the canonical strings. **`PingRequest.SessionId`** continues a Grimoire thread; when omitted the hub creates a new session on first assistant turn.
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

For attachment privacy, the successful source turn records only typed consultation metadata. The
Campaign Logger adds logical key, version, opaque attachment id, and source type for consultations
inside the summarized window; it never loads attachment bytes, automatically submits the session's
attachment index, or copies hashes/host paths into the summarizer payload. The prompt asks for useful
decisions and source references, not an attachment archive.

Under the same **Session-Based Consolidation model of AI memory**, **Chronosync reporting** (DESIGN §5.4.2) addresses **spatial** drift: thread lines and `DomainType` deltas vs the last persisted `PatternSnapshot`, not chat log length. Campaign Logger and Chronosync are separate triggers; the hub folds `ChronosyncReport` into the system prompt via `PingRequest.ChronosyncDelta`; MCP context remains separate.

### 8.8 OpenAI `/v1` Chat Completions compatibility subset

`OpenAiV1Endpoints` advertises a **Chat Completions compatibility subset**, not full OpenAI API parity. Moderations/images/audio remain **`501 not_supported`**. Polymorphic `content` (string | parts) is AOT-safe; unsupported part types / over `MaxContentPartsPerMessage` → **400** `invalid_value` before mapping. Vision parts map to MEAI `TextContent`/`UriContent`/`DataContent` (DESIGN §10.2.4).

**Parameters applied** (`ApplyInferenceParameters`): temperature, top_p, max tokens, penalties, seed, stop, response_format. Reasoning controls are additive: `reasoning_effort` = `none|minimal|low|medium|high|xhigh`, `reasoning_budget` = positive integer, and `reasoning_output` = `none|summary|full`. `reasoning_effort` and `reasoning_budget` are mutually exclusive and map to native `PingRequest.reasoning`; capability validation runs before provider I/O for buffered and `stream:true` requests. `reasoning_output` is an Arcanum-local projection/exposure preference and is passed to Microsoft.Extensions.AI only as a best-effort hint. It is not a guaranteed provider wire control, and Arcanum does not patch an unsupported `reasoning_output` field into provider JSON. When omitted, the resolved capability chooses `full` when `SupportsFull`, otherwise `summary` when `SupportsSummary`; `AllowsClientOutput` is required, and streaming also requires `SupportsStreaming`. Native effort/output and configured control-support/wire-dialect enums are strict string-only AOT contracts. OpenAI `reasoning_effort` and `reasoning_output` are also string-only. A numeric enum (defined or undefined) or an unknown enum string fails JSON binding before semantic validation. `n` must be `1` when present. Client `tools`/`tool_choice` rejected **400** `unsupported_parameter` unless `ClientToolForwarding:Enabled` (then schema/count validation; §8.8.3).

**Responses:** buffered answers remain in `choices[].message.content`; additive reasoning is in `reasoning_summary` and/or `reasoning_content`. Streaming answers remain in `choices[].delta.content`; reasoning uses the same additive fields on the delta, in provider order. A client that ignores the fields still reads an unchanged answer. Usage keeps `completion_tokens` and `total_tokens` authoritative and projects the reasoning subset at `completion_tokens_details.reasoning_tokens`; cached prompt subsets use `prompt_tokens_details.cached_tokens`. Buffered `message.tool_calls` still reports server-executed calls (§8.8.1); streaming SSE includes keep-alives and usage only when requested by `stream_options`. Semantic reasoning failures are typed OpenAI error bodies/chunks, never `delta.content`: they use HTTP **400**, `type:"invalid_request_error"`, `param:"reasoning"`, and the reachable stable code `invalid_reasoning_options` (effort plus budget), `invalid_reasoning_budget` (budget outside 1–2,097,152), `unsupported_reasoning_control`, `reasoning_budget_exceeds_model_limit`, or `unsupported_reasoning_output`. Numeric/unknown reasoning enum JSON never reaches those semantic branches; strict binding returns HTTP **400** `invalid_request_error`, code `invalid_json`, and no parameter. Unknown model → **404** `model_not_found`; tool-loop/timeout → **503** `server_error`.

**Current streaming projection topology:** production `/v1/chat/completions` obtains native `IntelligenceEvent` frames from `WizardIntelligenceProvider` (`TurnExecutionCoordinator` → `IntelligenceEventProjection`) and maps them to SSE chunks in `OpenAiV1Endpoints`. That endpoint mapper is the authoritative compatibility implementation. `OpenAiSseProjection` is a separate semantic helper/characterization path, not the projection instance used by the production route. The two paths share reasoning-field and typed-error rules only; `OpenAiSseProjection` does not define production terminal usage chunks, `stream_options.include_usage`, or tool-argument fragmentation. Those wire contracts are covered directly by production endpoint tests rather than by an exact-parity claim.

#### 8.8.1 Server-executed tools on `/v1` (buffered + streaming tool_calls)

Arcanum executes MCP tools server-side; `/v1` surfaces calls for observability/replay. Buffered: `PromptTurnResult.ToolCalls` → `message.tool_calls`. Streaming: `ToolCall` events → `delta.tool_calls` (40-char argument fragments; monotonic per-response `index`; fresh `call_…` ids). **`toolResult` never surfaced** on `/v1`. Forwarding mode preserves provider-minted ids and returns `finish_reason: "tool_calls"` without executing client tools. Richer native surface: `/api/intelligence/ping(-stream)`.

#### 8.8.2 `GET /v1/models` capability enrichment

`ModelInfoBuilder` is shared with `GET /api/models`. Additive OpenAI fields: `context_window`, `supports_vision`, `provider_name`/`provider_type`, `supports_tools`/`supports_streaming` (always true), plus the same optional typed `reasoning` capability object returned by the native endpoint.

#### 8.8.3 Client tool security (forwarding mode)

When `Arcanum:Features:ClientTools` is enabled, Sanctum/Ward/tool audit do **not** apply to client-supplied tools (provider executes). Default remains reject.

### 8.9 NDJSON anti-buffering headers (`/api/intelligence/ping-stream`)

The NDJSON streaming endpoint sets `Cache-Control: no-cache` and `X-Accel-Buffering: no` (parity with the SSE endpoint in §8.5/§8.8) so reverse proxies (nginx, Cloudflare, k8s ingress) do not coalesce incremental frames.

### 8.10 Buffered `/api/intelligence/ping` envelope

The buffered ping endpoint wraps a **`PromptResponseDto`** (Core) inside `ApiResponse<T>`: `text` (assistant answer only), `usage` (native token counts, including additive top-level `reasoning_tokens`), `toolCalls` (the assistant-issued calls executed server-side, when any), `finishReason`, and `reasoning` (an ordered array of `{ text, output }` client-safe segments; empty by default). Reasoning is never concatenated into `text`. Previously the envelope held only the assistant text as a bare `string`; clients now get the full turn context without falling back to NDJSON.

### 8.10.1 Mana counter (`POST /api/intelligence/mana`)

Read-only model-aware estimate (`ManaCountRequest` → `ManaCountResult`); no inference/Grimoire writes. `model` resolves the configured provider/canonical model profile, while an unconfigured model uses the conservative fallback. The result retains legacy `manaCount` / `encoding` / per-message fields and adds classification, profile id, safety margin, and the complete `ContextTokenBreakdown`. `tools:true` materializes the current native + MCP declarations and includes their names, descriptions, and full JSON schemas in both the total and source breakdown. **400** when neither `messages` nor `prompt` is supplied.

### 8.10.2 Effective context preview (`POST /api/intelligence/context/inspect`)

`ContextPreviewRequest` accepts an optional `prompt`, `model`, `workingDirectory`, `sessionId`, and `campaignId`, plus `showContent` and `noRetrieval`. An empty prompt is valid so an existing Session can be inspected as it stands. The response reports the effective provider/model and context window, selected Spell and routing mode, resonant dependencies, included and excluded tools with reasons, every `ContextTokenSource` row with estimate classification, reserved output, the production compression decision, and auxiliary routing/embedding work.

Prompt input uses the production ping bound. When `campaignId` is supplied without `workingDirectory`, the endpoint resolves the Campaign's server-host path exactly as buffered and streaming inference do; an unknown Campaign returns **404**.

The endpoint uses `WizardIntelligenceProvider`'s production routing, RAG readers, tool builder and policy filters, `SystemPromptBuilder.BuildDocument`, `InferenceContextBuilder.TryApplyContextCompressionIfNeeded`, and `IModelTokenEstimator`. It never enters the turn coordinator, invokes a tool, reserves turn budget, creates an assistant Entry, or calls the main inference model. `noRetrieval:true` skips embedding/RAG and automatic semantic Spell routing, with explicit unavailable reasons. Model-visible content is omitted unless `showContent:true`; that opt-in returns the assembled system prompt and messages through the authenticated operator API. Pre-call token values are labeled `exact`, `estimated`, `unknown`, or `reserved`; provider-reported values are never fabricated.

### 8.11 Daemon event SSE bus (`GET /api/events/daemon`)

In-process `IEventBus` uses code-owned bounded per-subscriber channels with `DropOldest`. Wire: `text/event-stream` `DaemonEvent` frames + best-effort `[DONE]`. `Arcanum:Execution:MaxSseConnections` and `MaxSseConnectionsPerType` feed `SseConnectionGate` → **503** `Api.TooManyConnections`. Anti-buffering headers; API key on the `/api` group. Rate limiting admits the HTTP request only, not open-stream duration.

### 8.12 Configuration API (`GET` / `PUT` / `POST /api/config`)

Read: redacted secret-bearing URLs/endpoints (`***`) plus non-secret credential references; environment values are never read into the response. Write: merge redacted URL placeholders from the current snapshot, validate, and atomically replace `arcanum.json`. Validate-only also merges recognized endpoint masks against the current snapshot before outbound and semantic validation, so an unchanged redacted `GET` document remains a valid update candidate; it never writes. Residual masks for new/unmatched providers fail closed. Provider API keys and PFX passwords are not accepted fields. The source-generated settings snapshot is loaded at process start, so configuration changes require a host restart; referenced secret environment values are resolved only at provider/certificate use. Status: **400** `Configuration.ValidationFailed`, **500** `Configuration.WriteFailed`.

### 8.13 MCP server event SSE bus (`GET /api/events/mcp`)

`McpConnectionManager` publishes `McpServerEvent` on state changes. Same SSE back-pressure/caps/auth as §8.11.

### 8.14 Spell Management API (`/api/spells`)

Workspace resolution: `?workspace=` → `Arcanum:Workspaces:DefaultRoot` → CWD. CRUD needs a resolvable workspace; empty `Arcanum:Security:SpellWorkspaceRoots` denies all (**403** `Spell.PathNotAllowed`). Built-ins under `~/.config/arcanum/spells/` are read-only (`Spell.BuiltinReadOnly`). Format: `SPELL.md` frontmatter + body; optional `SPELL.json` (legacy `SKILL.json` read fallback; writes always `SPELL.json`). Search shadow order: campaign > workspace > builtin. Versions: string labels `SPELL.v{label}.md` (`^[A-Za-z0-9.]+$`); activate swaps into `SPELL.md` and records `activeVersion`. Clone/cast/import quirks and status codes: §1. Per-workspace locks; delete only under `{workspace}/spells/{name}`.

### 8.15 Daemon job management (`/api/daemons`, `/api/executions`)

**Route families:** `/api/unseen-servant/*` = Unseen Servant interval control; `/api/daemons/*` + `/api/executions/*` = job registry + execution history. Watermarks: DESIGN §5.5.5. On-demand `POST .../run` waits for completion; scheduled path shares `DaemonRunner` single-flight per daemon. History process-local (`ExecutionHistoryLimit`); detail includes correlated ring-buffer logs.

### 8.16 Log ring buffer (`GET /api/logs`, `GET /api/events/logs`)

Serilog → `SerilogLogRingBufferSink` → a code-owned bounded in-memory ring that overwrites the oldest entry. Query filters + `beforeSequence` cursor. Live SSE uses the same caps as §8.11. It is not persisted across restarts. Post-build sink registration avoids a Build()-time logging DI deadlock.

### 8.17 Workspace registry and file browser/writer (`/api/workspaces`)

Campaign-backed when Grimoire ready (`persisted: true`); else in-memory. Writes gated by `Arcanum:Workspaces:EnableFileWrite` (default off) → **403** `Workspace.FileWriteDisabled`. Path policy: reject `..`/absolute; symlink escape → `Workspace.SymbolicLinkEscape`; revalidate before I/O. Atomic temp+rename for PUT/PATCH. Size clamps: DESIGN §3.4. PATCH ordinal replace with ambiguous/not-found codes. HEAD contents returns size/`Last-Modified` only.

The CLI exposes this boundary directly as `arcanum workspace list|current|register|show|tree|info|read|search|index|index-status|chunks|unregister`. `tree`, `info`, and `read` call the authenticated file-browser routes and never read the client filesystem directly. File writes remain absent from this command family, so `Arcanum:Workspaces:EnableFileWrite` is neither bypassed nor implicitly enabled. `register [path]` sends the path to the server registry; omission uses the client current directory only because the shipping CLI targets the bundled loopback host. Help and output call every such value a server-host path so this convenience cannot silently become a remote path assumption.

### 8.18 Session API

Search, export, analytics, CRUD, manual entry append, SSE live stream, and Campaign Log **`/rest`** use the Grimoire-backed **`/api/sessions`** surface. See **DESIGN §11.16 Session lifecycle** for persistence and lifecycle architecture.

#### 8.18.1 Standalone session attachments

All routes are API-key protected and operate only on a bound attachment belonging to the route
Session. Successes use the native `ApiResponse<T>` envelope except the content stream.

- **Snapshot:** `POST /api/sessions/{id}/attachments` requires multipart form field `file`; an
  optional `logicalName` chooses the version family. It persists the supplied bytes as an immutable
  encrypted snapshot with `SourceKind=SnapshotOnly` / `SourceStatus=NotApplicable`. The request may
  originate from any client-readable path or stdin because no client path is sent or retained.
  Unsupported binary/PDF/Office content is stored as `Binary` and remains `NotEligible` for text
  extraction; it is not rejected merely because Arcanum cannot materialize it into model context.
  The parser admits a maximum-size file plus 64 KiB of multipart envelope, but rejects aggregate
  overflow with `Attachment.TooLarge` for both declared-length and chunked requests.
- **Live reference:** `POST /api/sessions/{id}/attachments/reference` accepts
  `{ "workspacePath": "docs/notes.md", "workspaceId": "optional-id", "logicalName": "optional-key" }`.
  `workspacePath` is interpreted only on the server, relative to the selected registered workspace
  or the configured active/default workspace. Absolute values do not widen authority: canonical
  containment, link/file identity, stable bounded reads, and Campaign Sanctum must all pass. The
  response exposes only opaque/sanitized provenance; canonical paths and file identities stay
  encrypted server metadata.
- **List/versions:** `GET /api/sessions/{id}/attachments` returns all bound versions. Clients derive
  a latest-per-logical-key list or a version history without a second persistence authority.
  `SessionAttachmentDto.SessionId` enables direct CLI output; `RelativePath` names the encrypted
  stored snapshot, never the live source. CLI Reveal uses it only when the corresponding local file
  is present and has an `ARCABLOB` envelope; otherwise the user is directed to authenticated export.
- **Refresh:** `POST /api/sessions/{id}/attachments/{attachmentId}/refresh` accepts no body or path.
  It calls the same `ToolExecutionPipeline.RefreshSessionAttachmentAsync` core used by
  `refresh_session_file`. An unchanged hash reuses the row; changed verified bytes create the next
  version under the existing per-Session byte and per-key version caps. Detected MIME determines the
  refreshed version's current Text/Image/Binary kind. Kind-specific policy is reapplied, but this
  operator endpoint does not require model vision capability because it injects no content.
- **Content:** `GET /api/sessions/{id}/attachments/{attachmentId}/content` opens only the authenticated
  stored snapshot through `ISessionAttachmentStore.OpenReadAsync`. It returns a download, never
  inline content, with `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, and no source
  path in headers. Attachment bytes are therefore available for atomic client export without ever
  being printed by metadata commands.
- **Pins:** the existing context-pin routes create/delete `kind=attachment` pins whose target is an
  attachment id and whose content version is its snapshot hash. Text pins may materialize
  implicitly within the shared pin/turn budgets. Image pins remain stored but materialize with
  `Unsupported`; a vision-capable turn must explicitly pass that bound attachment id.

`Attachment.Disabled` → **403**; `Attachment.InvalidRequest`, `InvalidContent`,
`InvalidReference`, and `SourceUnavailable` → **400**; `Attachment.NotFound` and `SourceNotFound` →
**404**; `Attachment.LimitExceeded` → **409**; `Attachment.TooLarge` → **413**. Error messages are
bounded and never echo a source path. Refresh failures use this same mapping instead of collapsing
every outcome to a conflict.

### 8.19 Server lifecycle (PID file)

The code-owned path is `~/.config/arcanum/arcanum.pid`. Startup fails if a live PID is present; a stale file is overwritten. Shutdown deletes the file only if it still names this process. DevHost and `serve` share the same path and therefore cannot run concurrently.

### 8.21 The Proving Grounds (`POST /api/proving-grounds/trials/run`)

Ephemeral Trial + Inquisitors (`regex` / `jsonSchema` / `semantic` FastModel judge). Targets: spell / prompt / apprenticeGoal. Terminology strict — industry LLM-test jargon prohibited. Errors §8.23.

### 8.22 Metrics endpoint (`GET /metrics`)

Prometheus text `0.0.4` via `System.Diagnostics.Metrics` + hand-rolled exporter (no OTel/prometheus-net — AOT). Catalog: HTTP requests, inference duration/tokens, tool outcomes, SSE gauge, active sessions (scrape-time query), Sanctum breaches, plus `arcanum_estimated_input_tokens`, `arcanum_provider_reported_input_tokens`, absolute `arcanum_input_token_estimation_variance` (low-cardinality `direction=underestimated|overestimated|exact|inconsistent`), and `arcanum_context_budget_rejections_total`. `arcanum_tool_invocations_total` has the closed `outcome=success|denied|error` domain (Ward and Sanctum refusals are `denied`) and uses the invocation's canonical `tool_name` directly. Unknown names are therefore distinct label values; input/tool-name length limits bound each value's size, but the implementation does **not** enforce a closed label-value set or a global cardinality cap. `arcanum_apply_patch_artifact_cleanup_total` is count-only with closed `outcome=complete|retained`; it never labels paths, sessions, or receipt IDs. Token histograms use token-scale buckets rather than duration buckets; provider/model labels remain low-cardinality (+ runtime meters via `MeterListener`). Path outside `/api`/`/v1`. `Arcanum:Features:Metrics=false` → **404**. `Arcanum:Security:MetricsRequireApiKey` defaults true and is forced true on ListenAny. Auth: `X-Arcanum-Key` or Bearer.

### 8.23 Error code catalog and HTTP status mapping

Wire-stable codes live on `ErrorCodes` (Core). HTTP mapping authority: `ArcanumErrorMapper.ResolveStatusCode` (Api). `ResolveStatusCodeDefaultBadRequest` treats unmapped codes as **400** on Apprentice/Campaign/Spell/Prompt/ProvingGrounds routes while still honoring explicit **500** mappings (`ProvingGrounds.InferenceFailed`, `Workspace.WriteFailed`, `Workspace.DeleteFailed`, `Saga.SearchFailed`, `Hub.Error`). Unrecognized strings (including `Hub.Error` via default arm) → **500**. Keep in sync with `ErrorCodes.cs` / `ArcanumErrorMapper.cs` (`ArcanumErrorMapperTests`).

**Default / unmapped:** unlisted codes → **500**; `ResolveStatusCodeDefaultBadRequest` downgrades unmapped → **400** except the explicit **500** set above.

**/api vs /v1:** native `/api` uses `ApiResponse<T>` + codes below. OpenAI `/v1` uses the OpenAI error envelope (`message`/`type`/`code`/`param`); hub failures map similarly (e.g. timeout → **503** `server_error`; unknown model → **404** `model_not_found`). Client-tool forwarding surfaces OpenAI codes `unsupported_parameter` / `too_many_tools` / `invalid_schema` while Core codes remain `ClientTools.*`.

| Codes (grouped) | HTTP | Semantics |
|-----------------|------|-----------|
| `Validation.InvalidPrompt`, `InvalidBody`, `InvalidQuery`, `InvalidProviderType`, `AttachedFiles` | 400 | Request shape / bounds validation |
| `Hub.Model` | 404 | Model not in any provider `models` |
| `Hub.Error` | 500 | Generic inference failure (mapper default arm) |
| `Campaign.NotFound`; `Session.NotFound` / `EntryNotFound`; `Attachment.NotFound` / `SourceNotFound`; `Grimoire.LoreNotFound`; `Apprentice.NotFound`; `Workspace.NotFound` / `FileNotFound`; `Spell.NotFound`; `Prompt.NotFound`; `Intelligence.HumanPromptNotFound`; `Mcp.ServerNotFound` / `ToolNotFound`; `Daemon.NotFound`; `Files.NotFound`; `Batches.NotFound` / `InputFileNotFound`; `Saga.NotFound`; `ProvingGrounds.SpellNotFound` / `PromptNotFound`; `Workspace.ReplacementNotFound` | 404 | Missing resource |
| `Campaign.InvalidPath` / `MaxReached`; `Session.Archived` / `InvalidStatus` / `TooManyEntries` / `EntryTooLarge` / `MemoryManagementDisabled` / `EmptyContent`; `Attachment.InvalidRequest` / `InvalidContent` / `InvalidReference` / `SourceUnavailable`; `Apprentice.Disabled` / `PendingQueueFull` / `InvalidGuidance` / `InvalidPlan` / `InvalidGoal` / `InvalidWorkspace`; `Workspace.NameEmpty` / `SymbolicLinkEscape` / `PathTraversal` / `DirectoryNotEmpty` / `ReplacementAmbiguous` / `PathIsDirectory` / `PathIsFile`; `Spell.NoWorkspace` / `InvalidWorkspace` / `InvalidName` / `NameCollision` / `BuiltinReadOnly` / `DuplicateVersion` / `InvalidVersion`; `Prompt.CodexPathNotContained` / `DuplicateVersion` / `InvalidName` / `InvalidVersion` / `InvalidRequest`; `Mcp.AmbiguousServer` / `MissingWorkspace` / `ServerNotRunning` / `AmbiguousTool` / `ToolError`; `Sending.TaskRejected`; `Security.BlockedOutboundUrl` / `IdempotencyKeyTooLong`; `Files.InvalidMimeType`; `Batches.InvalidEndpoint`; `Embeddings.ConfirmationRequired`; `ProvingGrounds.InvalidTrial` / `WorkspaceNotAllowed`; `Saga.NotEmpty`; `Scrying.VisionNotSupported` / `TooManyImages` / `UnsupportedMimeType`; `WebBrowsing.TooLarge` (reserved; today truncates) / `InvalidUrl`; `ClientTools.Disabled` / `TooMany` / `InvalidSchema`; `Guardrails.PiiDetected` / `Blocked`; `StructuredOutput.ValidationFailed` / `SchemaInvalid` | 400 | Domain validation / policy refusal (non-auth) |
| `Campaign.PathNotAllowed`; `Workspace.PathNotAllowed` / `AccessDenied` / `FileWriteDisabled`; `Spell.PathNotAllowed`; `Sending.Disabled` / `AgentNotAllowed`; `Mcp.WorkspaceNotTrusted` / `DiagnosticBlocked`; `Scrying.FeatureDisabled`; `Attachment.Disabled`; `WebBrowsing.SsrfBlocked` | 403 | Path/network/feature deny |
| `Security.MissingApiKey` | 401 | Missing/invalid API key |
| `Session.TooManyPinned`; `Attachment.LimitExceeded`; `Apprentice.AlreadyRunning` / `Running` / `NotPaused` / `CannotReweave` / `NotEscalated` / `MaxReached` / `ConclaveDisabled`; `Security.IdempotencyConflict`; `Security.IdempotencyInProgress` | 409 | State or idempotency conflict |
| `Sending.MaxTasksReached`; `RateLimit.TooManyRequests` | 429 | Concurrency / rate limit |
| `Workspace.FileTooLarge`; `Files.TooLarge`; `Scrying.ImageTooLarge`; `Attachment.TooLarge` | 413 | Payload too large |
| `Sending.AgentUnreachable` / `AgentCardInvalid`; `CommLink.Suppressed` | 502 | Downstream / webhook failure |
| `Api.TooManyConnections`; `Connection.Unreachable`; `Embeddings.ProviderUnavailable` / `FeatureDisabled`; `Session.RestQueueFull` | 503 | Capacity / provider unavailable, or bounded Campaign Logger queue rejection |
| `Mcp.DiagnosticTimeout`; `Connection.Timeout`; `WebBrowsing.Timeout` | 504 | Bounded downstream transport/diagnostic operation timeout |
| `Workspace.WriteFailed` / `DeleteFailed`; `ProvingGrounds.InferenceFailed`; `Saga.SearchFailed` | 500 | Explicit infra/search failures (never downgraded by DefaultBadRequest) |

**Ollama:** providers use the `OpenAICompatible` contract and surface failures as `Hub.Error`.

### 8.24 OpenAI embeddings (`POST /v1/embeddings`)

Composes `IWeaveService` + tokenizer. `model` must match `Arcanum:Integrations:Embeddings:Model` or be omitted → else **404** `model_not_found`. Long inputs use code-owned chunking + mean-pool/L2. `encoding_format` is `float|base64` (`EmbeddingBlobCodec`). Idempotency-Key is supported. Errors use the OpenAI envelope (**400** invalid input/chars; **503** when The Weave is unavailable).

### 8.25 HTTP response compression

Brotli+Gzip via ASP.NET ResponseCompression; early pipeline. Excludes `text/event-stream` and `application/x-ndjson`. `EnableForHttps` left false (framework default).

### 8.26 Persisted inference audit log

Opt-in JSONL (`Arcanum:Host:AuditLog:*`); dated files, owner-only, soft size + retention. A row is written only after a turn completes successfully (ping / ping-stream / v1-completion today); errors, timeouts, cancellations, and interrupted streams are not audit rows. Tool names and counts are metadata; `Arcanum:Host:AuditLog:RedactToolArguments=true` (default) makes `toolArgumentsJson` null, while opting out records the exact raw argument snapshots at operator risk. Tool results, prompt/answer bodies, and reasoning bodies are never fields in this log. Audit failure is warning-only and never changes the already-successful turn. Query: `GET /api/audit`.

### 8.27 Content guardrails (PII / toxicity / topics)

Opt-in via `Arcanum:Features:Guardrails` (default false), with policy under `Arcanum:Security:Guardrails`. Input PII (GeneratedRegex) → `Guardrails.PiiDetected`; toxicity/topics → `Guardrails.Blocked`. Streaming output filtering is code-owned **buffered** mode. Audit JSONL + `GET /api/guardrails/audit`. Only redacted matched spans appear in logs/errors.

---

*End of API reference.*
