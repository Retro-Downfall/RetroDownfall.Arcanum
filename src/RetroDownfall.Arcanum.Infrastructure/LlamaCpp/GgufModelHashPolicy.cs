using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

internal static class GgufModelHashPolicy
{

    internal const string UnverifiedDownloadCode = "Llama.UnverifiedDownload";

    internal const string UnverifiedDownloadMessage =
        "A SHA-256 digest is required for GGUF downloads. Supply sha256 on the pull request or set Arcanum:LlamaCpp:ModelSha256Map.";

    internal const string UnverifiedDownloadWarning =
        "Downloading GGUF without SHA-256 verification. Supply sha256 on the pull request or set Arcanum:LlamaCpp:ModelSha256Map.";

    internal static string? ResolveExpectedSha256(
        string cacheKey,
        string? requestSha256,
        LlamaCppSettings settings)
    {

        if (!string.IsNullOrWhiteSpace(requestSha256))
        {

            return requestSha256.Trim();

        }

        if (settings.ModelSha256Map?.TryGetValue(cacheKey, out string? pinnedSha256) == true
            && !string.IsNullOrWhiteSpace(pinnedSha256))
        {

            return pinnedSha256.Trim();

        }

        return null;

    }

    internal static bool ShouldRejectUnverified(string? resolvedSha256, bool requireModelHash) =>
        requireModelHash && string.IsNullOrWhiteSpace(resolvedSha256);

    internal static bool IsVerifiedDownload(string? resolvedSha256) =>
        !string.IsNullOrWhiteSpace(resolvedSha256);

}
