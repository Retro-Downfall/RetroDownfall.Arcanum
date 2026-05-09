using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

public sealed record OpenAiChatMessage(string Role, string? Content);
