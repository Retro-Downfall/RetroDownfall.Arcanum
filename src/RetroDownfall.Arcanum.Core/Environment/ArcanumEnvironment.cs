using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Core.Environment;

public static class ArcanumEnvironment
{

    public const string EditionEnvVar = "ARCANUM_EDITION";

    /// <summary>
    /// Returns the effective host binding mode. The <c>ARCANUM_HOST_ANY</c> environment variable
    /// always wins when it is set to a recognized value; otherwise falls back to the configuration value.
    /// </summary>
    public static bool IsHostAnyEnabled(bool configValue)
    {

        // Environment override always wins so containerized deployments don't need rebuilds.
        string? env = global::System.Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

        if (!string.IsNullOrWhiteSpace(env))
        {

            return ParseTruthyEnv(env) ?? configValue;

        }

        return configValue;

    }

    /// <summary>
    /// Returns whether the fixed-window rate limiter should be active. Binding to all interfaces
    /// (<see cref="IsHostAnyEnabled"/>) always turns it on.
    /// </summary>
    public static bool IsRateLimitEnabled(bool rateLimitConfigEnabled, bool listenAnyConfigValue) =>
        rateLimitConfigEnabled || IsHostAnyEnabled(listenAnyConfigValue);

    /// <summary>
    /// Resolves runtime edition. <c>ARCANUM_EDITION</c> wins when set to a recognized value;
    /// otherwise uses the bound config value (default <see cref="ArcanumEdition.Local"/>).
    /// </summary>
    public static ArcanumEdition ResolveEdition(ArcanumEdition configValue)
    {
        string? env = global::System.Environment.GetEnvironmentVariable(EditionEnvVar);

        if (string.IsNullOrWhiteSpace(env))
        {
            return configValue;
        }

        string trimmed = env.Trim();

        if (string.Equals(trimmed, "development", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "dev", StringComparison.OrdinalIgnoreCase))
        {
            return ArcanumEdition.Development;
        }

        if (string.Equals(trimmed, "local", StringComparison.OrdinalIgnoreCase))
        {
            return ArcanumEdition.Local;
        }

        return configValue;
    }

    /// <summary>
    /// Startup escape hatch for host process tools. Recognized truthy values: <c>1</c>, <c>true</c>.
    /// Must be paired with <see cref="ArcanumEdition.Development"/> — see
    /// <see cref="Security.HostProcessToolPolicy"/>.
    /// </summary>
    public static bool IsAllowHostProcessToolsEnabled()
    {
        string? env = global::System.Environment.GetEnvironmentVariable(
            Security.HostProcessToolPolicy.AllowHostProcessToolsEnvVar);

        return ParseTruthyEnv(env) == true;
    }

    private static bool? ParseTruthyEnv(string? env)
    {
        if (string.IsNullOrWhiteSpace(env))
        {
            return null;
        }

        string trimmed = env.Trim();

        if (string.Equals(trimmed, "1", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(trimmed, "0", StringComparison.Ordinal))
        {
            return false;
        }

        if (bool.TryParse(trimmed, out bool parsedEnv))
        {
            return parsedEnv;
        }

        return null;
    }

}
