using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.Services;

/// <summary>
/// Validates user input for configuration fields to prevent security issues.
/// </summary>
public static class ConfigurationInputValidator
{
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

        if (!EnvironmentCredentialResolver.IsValidEnvironmentVariableName(name.Trim()))
        {
            error = $"Environment variable name '{name}' is not portable. Use an ASCII letter or underscore first, followed by ASCII letters, digits, or underscores.";

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

        if (string.Equals(origin.Trim(), "*", StringComparison.Ordinal))
        {

            return true;

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

}
