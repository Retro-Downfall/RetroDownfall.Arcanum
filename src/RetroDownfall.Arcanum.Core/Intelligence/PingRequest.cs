using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Core.Pattern.Entities;

namespace RetroDownfall.Arcanum.Core.Intelligence;

public sealed record PingRequest(
    string Prompt,
    string? Model = null,
    string WorkingDirectory = "",
    PatternSnapshot? ContextSnapshot = null,
    Guid? SessionId = null,
    bool DisableMcpTools = false,
    bool CliTerminalFormatting = false,
    bool UnattendedMode = false,
    List<AttachedFileDto>? AttachedFiles = null,
    ChronosyncReport? ChronosyncDelta = null,
    List<CoreChatMessage>? StatelessMessages = null,
    string? OverrideSpellName = null,
    bool SkipSpellRouting = false,
    float? Temperature = null,
    float? TopP = null,
    int? MaxOutputTokens = null,
    IReadOnlyList<string>? Stop = null,
    long? Seed = null,
    string? ResponseFormat = null,
    JsonElement? ResponseFormatJsonSchema = null,
    float? PresencePenalty = null,
    float? FrequencyPenalty = null,
    string? User = null,
    bool? ParallelToolCalls = null,
    List<DataStreamPayload>? DataStreams = null,
    Guid? CampaignId = null,
    ToolPolicy? ToolPolicy = null,
    string? AdditionalSystemPrompt = null,
    string? OverrideSpellPath = null,
    List<ScryingFocusDto>? ScryingFoci = null,
    OpenAiToolDefinition[]? ClientTools = null,
    JsonElement? ClientToolChoice = null,
    bool ForwardClientTools = false,
    List<Guid>? AttachmentReferences = null,
    /// <summary>
    /// When true, advertise and invoke zero tools (including hub-native builtins, MCP, scripts,
    /// browse, and client-forwarded tools). Used by OpenAI batches until durable side-effect
    /// semantics exist.
    /// </summary>
    bool DisableAllTools = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReasoningRequestOptions? Reasoning = null);