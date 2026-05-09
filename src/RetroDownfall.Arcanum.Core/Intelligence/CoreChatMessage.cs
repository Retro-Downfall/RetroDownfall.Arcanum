namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// A single role/content pair for stateless multi-turn transcripts (e.g. OpenAI-compatible callers).
/// </summary>
public sealed record CoreChatMessage(string Role, string Content);
