namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

public sealed record OpenAiChatChoice(int Index, OpenAiChatAssistantMessage Message, string? FinishReason);
