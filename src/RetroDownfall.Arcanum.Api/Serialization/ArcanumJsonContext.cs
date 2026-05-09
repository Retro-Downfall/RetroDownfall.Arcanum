using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApiResponse<string>))]
[JsonSerializable(typeof(ApiResponse<bool>))]
[JsonSerializable(typeof(Result<string>))]
[JsonSerializable(typeof(Result<bool>))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(Error))]
[JsonSerializable(typeof(PingRequest))]
[JsonSerializable(typeof(ChronosyncReport))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(AttachedFileDto))]
[JsonSerializable(typeof(List<AttachedFileDto>))]
[JsonSerializable(typeof(SubmitHumanResponseRequest))]
[JsonSerializable(typeof(PatternSnapshot))]
[JsonSerializable(typeof(ApiResponse<PatternSnapshot>))]
[JsonSerializable(typeof(Result<PatternSnapshot>))]
[JsonSerializable(typeof(DomainType))]
[JsonSerializable(typeof(IntelligenceEventType))]
[JsonSerializable(typeof(IntelligenceEvent))]
[JsonSerializable(typeof(McpServerStatusDto))]
[JsonSerializable(typeof(WorkspaceArsenalDto))]
[JsonSerializable(typeof(ApiResponse<WorkspaceArsenalDto>))]
[JsonSerializable(typeof(Result<WorkspaceArsenalDto>))]
[JsonSerializable(typeof(List<McpServerStatusDto>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(ConversationSummaryDto))]
[JsonSerializable(typeof(List<ConversationSummaryDto>))]
[JsonSerializable(typeof(ApiResponse<List<ConversationSummaryDto>>))]
[JsonSerializable(typeof(ConversationDetailDto))]
[JsonSerializable(typeof(ApiResponse<ConversationDetailDto>))]
[JsonSerializable(typeof(Result<ConversationDetailDto>))]
[JsonSerializable(typeof(ConversationMessageDto))]
[JsonSerializable(typeof(List<ConversationMessageDto>))]
[JsonSerializable(typeof(ApiResponse<List<ConversationMessageDto>>))]
[JsonSerializable(typeof(Result<List<ConversationMessageDto>>))]
[JsonSerializable(typeof(MessageRole))]
[JsonSerializable(typeof(LoreDto))]
[JsonSerializable(typeof(List<LoreDto>))]
[JsonSerializable(typeof(UpsertLoreRequest))]
[JsonSerializable(typeof(ApiResponse<LoreDto>))]
[JsonSerializable(typeof(ApiResponse<List<LoreDto>>))]
[JsonSerializable(typeof(Result<LoreDto>))]

public partial class ArcanumJsonContext : JsonSerializerContext;
