namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

public sealed record OpenAiErrorDetail(string Message, string Type);

public sealed record OpenAiErrorResponse(OpenAiErrorDetail Error);
