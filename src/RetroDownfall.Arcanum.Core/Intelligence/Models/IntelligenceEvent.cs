namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

public sealed record IntelligenceEvent(IntelligenceEventType Type, string Message, string? Data = null);
