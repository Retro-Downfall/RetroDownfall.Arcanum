namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// A single parsed Server-Sent-Events frame: the optional <c>event:</c> line and the accumulated
/// <c>data:</c> payload (joined with <c>\n</c> when a frame spans multiple <c>data:</c> lines).
/// </summary>
public sealed record SseEvent(string? Event, string Data);
