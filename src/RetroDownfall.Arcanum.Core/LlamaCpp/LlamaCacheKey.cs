using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RetroDownfall.Arcanum.Core.LlamaCpp;

/// <summary>
/// Filesystem-safe cache keys for GGUF models.
/// </summary>
public static partial class LlamaCacheKey
{

    private const int MaxKeyLength = 200;

    /// <summary>
    /// Normalizes a model key or source URL into a cache directory name.
    /// For URLs, derives a collision-resistant key from the full URL hash plus sanitized filename.
    /// </summary>
    public static string Normalize(string input)
    {

        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Cache key input cannot be empty.", nameof(input));
        }

        string trimmed = input.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return NormalizeFromUrl(trimmed, uri);
        }

        return SanitizeModelKey(trimmed);

    }

    /// <summary>
    /// Normalizes an explicit model key (not a URL).
    /// </summary>
    public static string NormalizeModelKey(string modelKey) => SanitizeModelKey(modelKey.Trim());

    private static string NormalizeFromUrl(string fullUrl, Uri uri)
    {

        string fileName = Path.GetFileName(uri.LocalPath);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "model.gguf";
        }

        string sanitizedName = SanitizeSegment(fileName);

        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fullUrl));

        string hashPrefix = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();

        string combined = $"{sanitizedName}-{hashPrefix}";

        return combined.Length <= MaxKeyLength ? combined : combined[..MaxKeyLength];

    }

    private static string SanitizeModelKey(string key)
    {

        string sanitized = SanitizeSegment(key);

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            throw new ArgumentException("Cache key resolves to an empty string after sanitization.", nameof(key));
        }

        return sanitized.Length <= MaxKeyLength ? sanitized : sanitized[..MaxKeyLength];

    }

    private static string SanitizeSegment(string segment)
    {

        string replaced = InvalidCharsRegex().Replace(segment, "_");

        return replaced.Trim('_', '.', ' ');

    }

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]")]
    private static partial Regex InvalidCharsRegex();

}
