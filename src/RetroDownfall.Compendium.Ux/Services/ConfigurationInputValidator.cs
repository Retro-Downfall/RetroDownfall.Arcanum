using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RetroDownfall.Compendium.Ux.Services;

/// <summary>
/// Validates user input for configuration fields to prevent security issues.
/// </summary>
public static partial class ConfigurationInputValidator
{
    // Environment variable names must be portable (POSIX standard):
    // - Start with letter or underscore
    // - Contain only letters, digits, and underscores
    // - Case-sensitive but we enforce uppercase for consistency
    [GeneratedRegex(@"^[A-Z_][A-Z0-9_]*$", RegexOptions.Compiled)]
    private static partial Regex EnvironmentVariableNameRegex();

    // Common sensitive environment variables that should not be referenced
    private static readonly HashSet<string> SensitiveEnvironmentVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH",
        "HOME",
        "USER",
        "SHELL",
        "PASSWORD",
        "SECRET",
        "TOKEN",
        "API_KEY",
        "PRIVATE_KEY",
        "CREDENTIALS",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_ACCESS_KEY_ID",
        "AZURE_CLIENT_SECRET",
        "GITHUB_TOKEN",
        "GITLAB_TOKEN",
        "DATABASE_URL",
        "CONNECTION_STRING"
    };

    /// <summary>
    /// Validates an environment variable name for security and portability.
    /// </summary>
    /// <param name="name">The environment variable name to validate.</param>
    /// <param name="error">Error message if validation fails.</param>
    /// <returns>True if valid, false otherwise.</returns>
    public static bool TryValidateEnvironmentVariableName(string? name, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            return true; // Empty is allowed (will use default)
        }

        if (!EnvironmentVariableNameRegex().IsMatch(name))
        {
            error = $"Environment variable name '{name}' is not portable. Use only uppercase letters, digits, and underscores. Must start with a letter or underscore.";
            return false;
        }

        if (SensitiveEnvironmentVariables.Contains(name))
        {
            error = $"Environment variable '{name}' is a sensitive system variable and should not be referenced directly.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates a CORS origin URL.
    /// </summary>
    /// <param name="origin">The origin URL to validate.</param>
    /// <param name="error">Error message if validation fails.</param>
    /// <returns>True if valid, false otherwise.</returns>
    public static bool TryValidateCorsOrigin(string? origin, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(origin))
        {
            return true; // Empty is allowed
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            error = $"CORS origin '{origin}' is not a valid absolute URL.";
            return false;
        }

        if (uri.Scheme != "http" && uri.Scheme != "https")
        {
            error = $"CORS origin '{origin}' must use http or https scheme.";
            return false;
        }

        // Reject wildcards and overly permissive origins
        if (origin.Contains('*'))
        {
            error = $"CORS origin '{origin}' contains wildcards which are not allowed for security reasons.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates multiple CORS origins.
    /// </summary>
    /// <param name="origins">Comma-separated list of origins.</param>
    /// <param name="errors">List of validation errors.</param>
    /// <returns>True if all origins are valid, false otherwise.</returns>
    public static bool TryValidateCorsOrigins(string? origins, out List<string> errors)
    {
        errors = new List<string>();

        if (string.IsNullOrWhiteSpace(origins))
        {
            return true;
        }

        string[] originList = origins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string origin in originList)
        {
            if (!TryValidateCorsOrigin(origin, out string? error))
            {
                errors.Add(error!);
            }
        }

        return errors.Count == 0;
    }

    /// <summary>
    /// Validates a file path for security issues.
    /// </summary>
    /// <param name="path">The file path to validate.</param>
    /// <param name="error">Error message if validation fails.</param>
    /// <returns>True if valid, false otherwise.</returns>
    public static bool TryValidatePath(string? path, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return true; // Empty is allowed
        }

        // Check for directory traversal attempts
        if (path.Contains("..") || path.Contains("~"))
        {
            error = "Path contains invalid characters (.. or ~)";
            return false;
        }

        // Check for null bytes
        if (path.Contains('\0'))
        {
            error = "Path contains null bytes";
            return false;
        }

        // Check for invalid characters on Windows
        if (OperatingSystem.IsWindows())
        {
            char[] invalidChars = Path.GetInvalidPathChars();
            if (path.IndexOfAny(invalidChars) >= 0)
            {
                error = "Path contains invalid characters";
                return false;
            }
        }

        // Check for overly long paths
        if (path.Length > 1024)
        {
            error = "Path is too long (maximum 1024 characters)";
            return false;
        }

        return true;
    }
}