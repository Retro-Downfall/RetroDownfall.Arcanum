namespace RetroDownfall.Arcanum.Core.LlamaCpp;

/// <summary>
/// Optional overrides for <c>POST /api/llama/servers/{cacheKey}/start</c>.
/// </summary>
public sealed record StartLlamaServerRequestDto
{

    public int? GpuLayers { get; init; }

    public int? Port { get; init; }

}
