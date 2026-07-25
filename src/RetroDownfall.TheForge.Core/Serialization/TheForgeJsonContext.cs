using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Wards;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Chronicle;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Models.OpenAi;

namespace RetroDownfall.TheForge.Core.Serialization;

/// <summary>
/// Source-generated JSON context for every wire type the Arcanum API client touches: closed
/// <see cref="ApiResponse{T}"/> envelopes for request/response bodies, bare streaming frame types
/// for NDJSON (<see cref="IntelligenceEvent"/>) and SSE
/// (<see cref="EntryDto"/>, <see cref="McpServerEvent"/>, <see cref="LogEntry"/>,
/// <see cref="DaemonEvent"/>, <see cref="ChronicleFrame"/>) payloads.
///
/// camelCase to match the Arcanum wire contract. Deliberately does NOT register a blanket
/// <c>JsonStringEnumConverter</c> in <see cref="JsonSourceGenerationOptionsAttribute"/> — every
/// enum that serializes as a string on the wire already carries its own
/// <c>[JsonConverter(typeof(JsonStringEnumConverter&lt;T&gt;))]</c> attribute on the Core/re-declared
/// type (see docs/THE_FORGE.md "Enum serialization"). <see cref="HealthStatus"/> intentionally has no
/// such attribute — it is an integer on the wire.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]

// Primitives
[JsonSerializable(typeof(Error))]
[JsonSerializable(typeof(ConfigurationValidationError))]
[JsonSerializable(typeof(ApiResponse<string>))]
[JsonSerializable(typeof(ApiResponse<bool>))]

// Health / Meta / Budget (re-declared TheForge.Core mirrors — see Models/*.cs)
[JsonSerializable(typeof(ApiResponse<HealthReportDto>))]
[JsonSerializable(typeof(ApiResponse<InstanceMetadataDto>))]
[JsonSerializable(typeof(ApiResponse<GrimoireStatsDto>))]
[JsonSerializable(typeof(ApiResponse<BudgetSummaryDto>))]

// Campaigns
[JsonSerializable(typeof(RegisterCampaignRequest))]
[JsonSerializable(typeof(UpdateCampaignRequest))]
[JsonSerializable(typeof(ApiResponse<ListPageResult<CampaignDto>>), TypeInfoPropertyName = "ApiResponseListPageResultCampaignDto")]
[JsonSerializable(typeof(ApiResponse<CampaignDto>))]
[JsonSerializable(typeof(CodexPutRequest))]
[JsonSerializable(typeof(ApiResponse<CodexContentDto>))]
[JsonSerializable(typeof(CampaignExportDto))]
[JsonSerializable(typeof(CampaignExportSpellDto))]
[JsonSerializable(typeof(ApiResponse<CampaignExportDto>))]
[JsonSerializable(typeof(CampaignImportRequest))]
[JsonSerializable(typeof(CampaignImportResultDto))]
[JsonSerializable(typeof(ApiResponse<CampaignImportResultDto>))]

// Spells
[JsonSerializable(typeof(ApiResponse<SpellSummary[]>))]
[JsonSerializable(typeof(ApiResponse<SpellDetail>))]
[JsonSerializable(typeof(CreateSpellRequest))]
[JsonSerializable(typeof(UpdateSpellRequest))]
[JsonSerializable(typeof(SpellCastRequest))]
[JsonSerializable(typeof(ApiResponse<SpellCastResult>))]
[JsonSerializable(typeof(SpellExecuteRequest))]
[JsonSerializable(typeof(ApiResponse<SpellVersionDto[]>))]
[JsonSerializable(typeof(ApiResponse<SpellVersionDto>))]
[JsonSerializable(typeof(SpellVersionDetailDto))]
[JsonSerializable(typeof(ApiResponse<SpellVersionDetailDto>))]
[JsonSerializable(typeof(CreateSpellVersionRequest))]
[JsonSerializable(typeof(UpdateSpellVersionRequest))]
[JsonSerializable(typeof(ActivateSpellVersionRequest))]
[JsonSerializable(typeof(ManaCountRequest))]
[JsonSerializable(typeof(ApiResponse<ManaCountResult>))]
[JsonSerializable(typeof(SpellValidationResultDto))]
[JsonSerializable(typeof(ApiResponse<SpellValidationResultDto>))]
[JsonSerializable(typeof(SpellExportDto))]
[JsonSerializable(typeof(ApiResponse<SpellExportDto>))]
[JsonSerializable(typeof(SpellImportRequest))]
[JsonSerializable(typeof(ApiResponse<SpellSummary>))]
[JsonSerializable(typeof(CloneSpellRequest))]
[JsonSerializable(typeof(SkillMetadata))]
[JsonSerializable(typeof(JsonDocument))]

// Prompts
[JsonSerializable(typeof(ApiResponse<ListPageResult<PromptSummaryDto>>), TypeInfoPropertyName = "ApiResponseListPageResultPromptSummaryDto")]
[JsonSerializable(typeof(ApiResponse<PromptDetailDto>))]
[JsonSerializable(typeof(ApiResponse<PromptVersionDto[]>))]
[JsonSerializable(typeof(CreatePromptRequest))]
[JsonSerializable(typeof(UpdatePromptRequest))]
[JsonSerializable(typeof(PromptRenderRequest))]
[JsonSerializable(typeof(ApiResponse<PromptRenderResultDto>))]
[JsonSerializable(typeof(PromptRenderResultDto))]
[JsonSerializable(typeof(TestPromptRequest))]
[JsonSerializable(typeof(ApiResponse<PromptTestResultDto>))]
[JsonSerializable(typeof(PromptTestResultDto))]
[JsonSerializable(typeof(PromptExecuteRequest))]
[JsonSerializable(typeof(ClonePromptRequest))]
[JsonSerializable(typeof(PromptExportDto))]
[JsonSerializable(typeof(ApiResponse<PromptExportDto>))]
[JsonSerializable(typeof(PromptImportRequest))]
[JsonSerializable(typeof(ApiResponse<PromptSummaryDto>))]
[JsonSerializable(typeof(ApiResponse<PromptVersionDto>))]

// Sessions
[JsonSerializable(typeof(ApiResponse<SessionQueryResult>))]
[JsonSerializable(typeof(ApiResponse<SessionDetailDto>))]
[JsonSerializable(typeof(CreateSessionRequest))]
[JsonSerializable(typeof(ForkSessionRequest))]
[JsonSerializable(typeof(ApiResponse<SessionExportResult>))]
[JsonSerializable(typeof(AppendEntryRequest))]
[JsonSerializable(typeof(ApiResponse<EntryDto>))]
[JsonSerializable(typeof(EntryDto))]
[JsonSerializable(typeof(PingRequest))]
[JsonSerializable(typeof(ReasoningRequestOptions))]
[JsonSerializable(typeof(ReasoningEffortLevel))]
[JsonSerializable(typeof(ReasoningOutputMode))]
[JsonSerializable(typeof(ReasoningContentSegment))]
[JsonSerializable(typeof(List<ReasoningContentSegment>))]
[JsonSerializable(typeof(ReasoningCapabilities))]
[JsonSerializable(typeof(ReasoningControlSupport))]
[JsonSerializable(typeof(ReasoningWireDialect))]
[JsonSerializable(typeof(List<Guid>))]
[JsonSerializable(typeof(IntelligenceEvent))]
[JsonSerializable(typeof(ContextTokenBreakdown))]
[JsonSerializable(typeof(ContextTokenComponent))]
[JsonSerializable(typeof(List<ContextTokenComponent>))]
[JsonSerializable(typeof(TokenEstimate))]
[JsonSerializable(typeof(ResolvedModelTokenizationProfile))]
[JsonSerializable(typeof(ContextTokenSource))]
[JsonSerializable(typeof(TokenEstimateClassification))]
[JsonSerializable(typeof(ModelTokenizationProfileType))]

// Sessions — memory management (Milestone H — Context and Memory)
[JsonSerializable(typeof(CompactResult))]
[JsonSerializable(typeof(ApiResponse<CompactResult>))]
[JsonSerializable(typeof(ApiResponse<EntryDto[]>))]

// Apprentices
[JsonSerializable(typeof(ApiResponse<ListPageResult<ApprenticeSummaryDto>>), TypeInfoPropertyName = "ApiResponseListPageResultApprenticeSummaryDto")]
[JsonSerializable(typeof(ApiResponse<ApprenticeDetailDto>))]
[JsonSerializable(typeof(CreateApprenticeRequest))]
[JsonSerializable(typeof(ReweaveApprenticeRequest))]
[JsonSerializable(typeof(InterveneApprenticeRequest))]
[JsonSerializable(typeof(ChronicleFrame))]

// Wards
[JsonSerializable(typeof(ApiResponse<WardDto[]>))]
[JsonSerializable(typeof(ResolveWardRequest))]
[JsonSerializable(typeof(ApiResponse<WardResolutionDto>))]

// Trials (The Proving Grounds)
[JsonSerializable(typeof(Trial))]
[JsonSerializable(typeof(Inquisitor))]
[JsonSerializable(typeof(RegexInquisitor))]
[JsonSerializable(typeof(JsonSchemaInquisitor))]
[JsonSerializable(typeof(SemanticInquisitor))]
[JsonSerializable(typeof(List<Inquisitor>))]
[JsonSerializable(typeof(Inquisitor[]))]
[JsonSerializable(typeof(TrialTargetKind))]
[JsonSerializable(typeof(TrialResult))]
[JsonSerializable(typeof(InquisitorVerdict))]
[JsonSerializable(typeof(InquisitorVerdict[]))]
[JsonSerializable(typeof(List<InquisitorVerdict>))]
[JsonSerializable(typeof(ApiResponse<TrialResult>))]

// MCP (The Arsenal)
[JsonSerializable(typeof(ApiResponse<McpServerInfo[]>))]
[JsonSerializable(typeof(McpServerEvent))]

// MCP lifecycle actions + Arsenal aggregation (Milestone G — Operate Inference)
[JsonSerializable(typeof(OptionalWorkspaceRequest))]
[JsonSerializable(typeof(ApiResponse<WorkspaceArsenalDto>))]
[JsonSerializable(typeof(WorkspaceArsenalDto))]

// Built-in tool invocation — The Scrying Pool (POST /api/tools/invoke)
[JsonSerializable(typeof(ToolInvokeRequest))]
[JsonSerializable(typeof(ApiResponse<ToolInvokeResponse>))]
[JsonSerializable(typeof(ToolInvokeResponse))]

// Diagnostic MCP Invocation — POST /api/mcp/tools/invoke (external MCP only; internal + Forbidden Arts blocked)
[JsonSerializable(typeof(McpToolInvokeRequest))]
[JsonSerializable(typeof(ApiResponse<McpToolInvokeResponse>))]
[JsonSerializable(typeof(McpToolInvokeResponse))]

// Lore Browser
[JsonSerializable(typeof(ApiResponse<ListPageResult<LoreDto>>), TypeInfoPropertyName = "ApiResponseListPageResultLoreDto")]
[JsonSerializable(typeof(ApiResponse<LoreDto>))]
[JsonSerializable(typeof(UpsertLoreRequest))]

// The Archive (Saga memory)
[JsonSerializable(typeof(ApiResponse<SagaMemoryDto[]>))]
[JsonSerializable(typeof(SagaSearchRequest))]
[JsonSerializable(typeof(ApiResponse<SagaSearchResult>))]

// The Archive — Saga stats (Milestone H — Context and Memory)
[JsonSerializable(typeof(SagaStats))]
[JsonSerializable(typeof(ApiResponse<SagaStats>))]

// Compendium (Arcanum configuration)
[JsonSerializable(typeof(ArcanumSettings))]
[JsonSerializable(typeof(ApiResponse<ArcanumSettings>))]

// Models / Providers
[JsonSerializable(typeof(ApiResponse<ModelInfoDto[]>))]
[JsonSerializable(typeof(ApiResponse<ProviderInfoDto[]>))]
[JsonSerializable(typeof(ProviderTestRequest))]
[JsonSerializable(typeof(ApiResponse<ProviderTestResult>))]
[JsonSerializable(typeof(ProviderTestResult))]

// Workspaces (The Atelier)
[JsonSerializable(typeof(ApiResponse<WorkspaceInfo[]>))]

// Workspaces — file browser (Milestone H — Context and Memory)
[JsonSerializable(typeof(FileListResult))]
[JsonSerializable(typeof(ApiResponse<FileListResult>))]
[JsonSerializable(typeof(FileEntry))]
[JsonSerializable(typeof(ApiResponse<FileEntry>))]
[JsonSerializable(typeof(FileReadResult))]
[JsonSerializable(typeof(ApiResponse<FileReadResult>))]

// Workspaces — file write (Milestone H, optional; server-gated by Arcanum:Workspaces:EnableFileWrite)
[JsonSerializable(typeof(FileWriteRequest))]
[JsonSerializable(typeof(FileWriteResult))]
[JsonSerializable(typeof(ApiResponse<FileWriteResult>))]
[JsonSerializable(typeof(TextBlockReplaceRequest))]
[JsonSerializable(typeof(TextBlockReplaceResult))]
[JsonSerializable(typeof(ApiResponse<TextBlockReplaceResult>))]
[JsonSerializable(typeof(FileDeleteResult))]
[JsonSerializable(typeof(ApiResponse<FileDeleteResult>))]
[JsonSerializable(typeof(DirectoryCreateResult))]
[JsonSerializable(typeof(ApiResponse<DirectoryCreateResult>))]

// Divination (semantic search)
[JsonSerializable(typeof(SemanticSearchRequest))]
[JsonSerializable(typeof(ApiResponse<SemanticSearchResult>))]
[JsonSerializable(typeof(WorkspaceSemanticSearchRequest))]
[JsonSerializable(typeof(ApiResponse<WorkspaceSearchResult[]>))]

// The Weave Inspector (Phase 7 — RAG substrate inspection, read-only) + embeddings reset
[JsonSerializable(typeof(WorkspaceIndexStatusDto))]
[JsonSerializable(typeof(ApiResponse<WorkspaceIndexStatusDto>))]
[JsonSerializable(typeof(WorkspaceFileChunkDto))]
[JsonSerializable(typeof(WorkspaceFileChunkDto[]))]
[JsonSerializable(typeof(WorkspaceFileChunkPage))]
[JsonSerializable(typeof(ApiResponse<WorkspaceFileChunkPage>))]
[JsonSerializable(typeof(EmbeddingsResetResult))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(ApiResponse<EmbeddingsResetResult>))]

// Comm Link Alert Dashboard
[JsonSerializable(typeof(CommLinkMessageRequestDto))]

// Sanctum Breach Monitor (config surface only for the alpha — breach browsing DTOs live in
// Api.Models and are deferred until that UI phase lands)
[JsonSerializable(typeof(SanctumConfig))]
[JsonSerializable(typeof(ApiResponse<SanctumConfig>))]

// The Foundry Floor (logs) / Audit Browser (inference + guardrails)
[JsonSerializable(typeof(ApiResponse<LogQueryResult>))]
[JsonSerializable(typeof(LogEntry))]
[JsonSerializable(typeof(InferenceAuditRecord))]
[JsonSerializable(typeof(InferenceAuditRecord[]))]
[JsonSerializable(typeof(ApiResponse<InferenceAuditRecord[]>))]
[JsonSerializable(typeof(GuardrailAuditRecord))]
[JsonSerializable(typeof(GuardrailAuditRecord[]))]
[JsonSerializable(typeof(ApiResponse<GuardrailAuditRecord[]>))]

// The Servants' Quarters (daemon / Unseen Servant — read paths only for the alpha)
[JsonSerializable(typeof(ApiResponse<DaemonJobInfo[]>))]
[JsonSerializable(typeof(ApiResponse<DaemonJobInfo>))]
[JsonSerializable(typeof(ApiResponse<DaemonExecutionSummary>))]
[JsonSerializable(typeof(ApiResponse<DaemonExecutionSummary[]>))]
[JsonSerializable(typeof(DaemonEvent))]

// OpenAI-compatible /v1/files + /v1/batches (Phase 9 — bare wire shapes, not ApiResponse envelopes)
[JsonSerializable(typeof(OpenAiErrorDetail))]
[JsonSerializable(typeof(OpenAiErrorResponse))]
[JsonSerializable(typeof(OpenAiFileObject))]
[JsonSerializable(typeof(OpenAiFileListResponse))]
[JsonSerializable(typeof(OpenAiFileDeleteResponse))]
[JsonSerializable(typeof(OpenAiBatchRequest))]
[JsonSerializable(typeof(OpenAiBatchObject))]
[JsonSerializable(typeof(OpenAiBatchRequestCounts))]
[JsonSerializable(typeof(OpenAiBatchListResponse))]
[JsonSerializable(typeof(List<OpenAiFileObject>))]
[JsonSerializable(typeof(List<OpenAiBatchObject>))]

public partial class TheForgeJsonContext : JsonSerializerContext;
