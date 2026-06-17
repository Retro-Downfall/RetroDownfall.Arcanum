using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Core.LlamaCpp;

/// <summary>
/// Pull request body for <c>POST /api/llama/models/pull</c>.
/// </summary>
public sealed record PullModelRequestDto
{

    public string SourceUrl { get; init; } = string.Empty;

    public string? CacheKey { get; init; }

    public string? Sha256 { get; init; }

}
