using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// Where The Conclave's A2A surface lives, resolved in one place so the mapped server route, the
/// callback route, and the URL advertised to a peer cannot disagree about it.
/// </summary>
/// <remarks>
/// Every A2A route must sit behind the API-key filter, and the API group is rooted at <c>/api</c>. A
/// configured path outside that prefix is therefore <em>mounted under</em> it rather than refused: an
/// operator who asks for <c>/conclave/a2a</c> gets <c>/api/conclave/a2a</c> and a working server
/// (issue #12, <c>docs/Arcanum.DESIGN.md</c> &#167;5.7.1).
/// <para>
/// The prefix test is deliberately segment-aware. <c>/apiary/a2a</c> is not a path inside <c>/api</c> —
/// it merely starts with the same four characters — and a plain <c>StartsWith("/api")</c> resolved it
/// two different ways in the two places that asked, mounting the anonymous callback route outside the
/// tree the server was reported at (&#167;5.7.1.4).
/// </para>
/// </remarks>
internal static class A2APathPolicy
{

    /// <summary>The authenticated API group every A2A route is mounted under.</summary>
    internal const string ApiGroupPrefix = "/api";

    /// <summary>
    /// Resolves the absolute path the A2A server is mounted at from the configured
    /// <c>Arcanum:Integrations:A2A:ServerPath</c>.
    /// </summary>
    internal static string ResolveServerPath(string? configuredPath)
    {

        string trimmed = configuredPath?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {

            return ArcanumRuntimeDefaults.Conclave.A2A.ServerPath;

        }

        if (trimmed == ApiGroupPrefix
            || trimmed.StartsWith($"{ApiGroupPrefix}/", StringComparison.Ordinal))
        {

            return trimmed.TrimEnd('/') is { Length: > 0 } normalized ? normalized : ApiGroupPrefix;

        }

        string relative = trimmed.Trim('/');

        return relative.Length == 0 ? ApiGroupPrefix : $"{ApiGroupPrefix}/{relative}";

    }

}
