namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

public sealed record OpenAiChatStreamChoice(int Index, OpenAiDelta Delta, string? FinishReason);
