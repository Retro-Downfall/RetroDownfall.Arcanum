using System.Text.Json;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Services;

public enum WatchSseFrameType
{

    Data,

    Heartbeat,

    Done,

    Error,

    UnexpectedEof,

}

public sealed record WatchSseFrame(
    WatchSseFrameType Type,
    JsonElement? Data = null,
    string? RawJson = null,
    Error? Error = null,
    string? Diagnostic = null,
    bool Recoverable = false,
    bool Retryable = false);

internal readonly record struct HealthWatchResult(
    Result<RetroDownfall.Arcanum.Api.Models.HealthReportDto> Report,
    bool Retryable);
