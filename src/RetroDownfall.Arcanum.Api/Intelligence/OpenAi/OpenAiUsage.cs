namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

public sealed record OpenAiUsage(int PromptTokens, int CompletionTokens, int TotalTokens);
