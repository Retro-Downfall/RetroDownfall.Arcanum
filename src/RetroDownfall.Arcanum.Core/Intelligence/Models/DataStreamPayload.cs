namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

public sealed record DataStreamPayload(
    string StreamId,
    string ContentType,
    string Content);