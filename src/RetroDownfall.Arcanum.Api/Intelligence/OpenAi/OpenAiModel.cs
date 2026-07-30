using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// OpenAI-shaped model list entry. <see cref="Id"/>/<see cref="ObjectKind"/>/<see cref="Created"/>/
/// <see cref="OwnedBy"/> are the original OpenAI fields (unchanged). The remaining fields are
/// Arcanum's additive capability enrichment — mirrors the native <c>GET /api/models</c>
/// (<c>ModelInfoDto</c>, built by the same <see cref="ModelInfoBuilder"/>) so OpenAI SDK clients
/// can auto-detect context window, vision support, and provider identity without a second call.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
public sealed record OpenAiModel(
    string Id,
    [property: JsonPropertyName("object")] string ObjectKind = "model",
    long Created = 0,
    [property: JsonPropertyName("owned_by")] string OwnedBy = "system",
    [property: JsonPropertyName("context_window")] int ContextWindow = 0,
    [property: JsonPropertyName("supports_vision")] bool SupportsVision = false,
    [property: JsonPropertyName("provider_name")] string ProviderName = "",
    [property: JsonPropertyName("provider_type")] string ProviderType = "",
    [property: JsonPropertyName("supports_tools")] bool SupportsTools = true,
    [property: JsonPropertyName("supports_streaming")] bool SupportsStreaming = true,
    [property: JsonPropertyName("reasoning_wire_dialect"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReasoningWireDialect? WireDialect = null,
    [property: JsonPropertyName("reasoning_max_budget_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? MaxBudgetTokens = null,
    [property: JsonPropertyName("prompt_caching"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    PromptCachingProfile? PromptCaching = null);
