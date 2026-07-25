using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed record PromptSummaryDto(
    Guid Id,
    Guid? CampaignId,
    string Name,
    string Version,
    string? Description,
    string[] Tags,
    DateTimeOffset UpdatedAt);

public sealed record PromptDetailDto(
    Guid Id,
    Guid? CampaignId,
    string Name,
    string Version,
    string? Description,
    string[] Tags,
    string Template,
    JsonDocument? ParameterSchema,
    JsonDocument? DefaultParameters,
    string? Model,
    string? Provider,
    double? Temperature,
    double? TopP,
    int? MaxOutputTokens,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PromptVersionDto(
    Guid Id,
    string Version,
    DateTimeOffset UpdatedAt);

public sealed record CreatePromptRequest(
    string Name,
    string Version,
    string Template,
    string? Description,
    string[]? Tags,
    JsonDocument? ParameterSchema,
    JsonDocument? DefaultParameters,
    string? Model,
    string? Provider,
    double? Temperature,
    double? TopP,
    int? MaxOutputTokens,
    Guid? CampaignId);

public sealed record UpdatePromptRequest(
    string? Name,
    string? Version,
    string? Description,
    string[]? Tags,
    string? Template,
    JsonDocument? ParameterSchema,
    JsonDocument? DefaultParameters,
    string? Model,
    string? Provider,
    double? Temperature,
    double? TopP,
    int? MaxOutputTokens);

public sealed record PromptRenderRequest(
    Dictionary<string, string>? Parameters);

public sealed record PromptRenderResultDto(
    string RenderedText,
    int TokenCount);

public sealed record PromptTestResultDto(
    string AssembledText,
    int TokenCount,
    ResolvedSpellInfoDto? ResolvedSpell,
    int McpServerCount,
    ContextTokenBreakdown? ContextBreakdown = null);

public sealed record ResolvedSpellInfoDto(
    string Name,
    string? Version);

public sealed record PromptExportDto(
    string Name,
    string Version,
    string? Description,
    string[] Tags,
    string Template,
    JsonDocument? ParameterSchema,
    JsonDocument? DefaultParameters,
    string? Model,
    string? Provider,
    double? Temperature,
    double? TopP,
    int? MaxOutputTokens,
    Guid? CampaignId);

public sealed record PromptImportRequest(
    PromptExportDto Payload,
    Guid? CampaignId);

public sealed record ClonePromptRequest(
    string NewName,
    string NewVersion,
    Guid? CampaignId = null);
