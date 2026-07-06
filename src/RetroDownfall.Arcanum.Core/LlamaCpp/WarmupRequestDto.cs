namespace RetroDownfall.Arcanum.Core.LlamaCpp;

/// <summary>
/// Optional body for <c>POST /api/llama/servers/{cacheKey}/warmup</c>. Kept minimal by default —
/// the point is to prime the KV-cache and verify the server is responsive beyond a health check,
/// not to run a real inference.
/// </summary>
public sealed record WarmupRequestDto
{

    public string Prompt { get; init; } = "Hello";

    public int MaxTokens { get; init; } = 1;

}
