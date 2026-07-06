namespace RetroDownfall.Arcanum.Core.LlamaCpp;

/// <summary>
/// Payload for <c>POST /api/llama/servers/{cacheKey}/warmup</c> — result of exercising the
/// inference path against an already-running <c>llama-server</c> (distinct from <c>GET /api/health</c>,
/// which only checks liveness, not that the model actually responds to a completion request).
/// </summary>
public sealed record WarmupResultDto(bool Success, int LatencyMs, string ServerEndpoint);
