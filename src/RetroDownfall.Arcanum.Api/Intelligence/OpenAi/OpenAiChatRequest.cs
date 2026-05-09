namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

public sealed record OpenAiChatRequest(
    string? Model,
    List<OpenAiChatMessage>? Messages,
    bool Stream = false,
    float? Temperature = null,
    int? MaxTokens = null);
