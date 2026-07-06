namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// Defense-in-depth cross-check between a file's extension and its declared (multipart)
/// <c>Content-Type</c> for <c>POST /v1/files</c> (DESIGN.md §11.20). This is a secondary guard —
/// the primary XSS mitigation is that <c>GET /v1/files/{id}/content</c> always serves with
/// <c>Content-Disposition: attachment</c>, never <c>inline</c>, regardless of MIME type. Unknown
/// extensions are allowed through (there is nothing to cross-check them against); only a *known*
/// extension paired with an unexpected declared type is rejected.
/// </summary>
public static class UploadedFileMimeValidator
{

    private static readonly Dictionary<string, string[]> ExtensionToAllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = ["application/json", "text/json", "application/octet-stream"],
        [".jsonl"] = ["application/jsonl", "text/jsonl", "application/x-ndjson", "text/plain", "application/octet-stream"],
        [".txt"] = ["text/plain", "application/octet-stream"],
        [".md"] = ["text/markdown", "text/plain", "application/octet-stream"],
        [".csv"] = ["text/csv", "application/vnd.ms-excel", "text/plain", "application/octet-stream"],
        [".pdf"] = ["application/pdf", "application/octet-stream"],
        [".png"] = ["image/png"],
        [".jpg"] = ["image/jpeg"],
        [".jpeg"] = ["image/jpeg"],
        [".gif"] = ["image/gif"],
        [".webp"] = ["image/webp"],
        [".zip"] = ["application/zip", "application/x-zip-compressed", "application/octet-stream"],
    };

    /// <summary>
    /// Returns <c>true</c> when <paramref name="filename"/>'s extension is recognized and
    /// <paramref name="declaredMimeType"/> is not one of its expected values — i.e. a mismatch that
    /// should be rejected. Unknown extensions never mismatch (nothing to compare against).
    /// </summary>
    public static bool IsExtensionMimeMismatch(string filename, string? declaredMimeType)
    {

        string extension = Path.GetExtension(filename);

        if (string.IsNullOrEmpty(extension) || !ExtensionToAllowedMimeTypes.TryGetValue(extension, out string[]? allowed))
        {

            return false;

        }

        string effectiveMimeType = string.IsNullOrWhiteSpace(declaredMimeType) ? "application/octet-stream" : declaredMimeType;

        return !Array.Exists(allowed, m => string.Equals(m, effectiveMimeType, StringComparison.OrdinalIgnoreCase));

    }

}
