namespace RetroDownfall.Arcanum.Core.Environment;

public static class ArcanumEnvironment
{

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

        }

        return configValue;

    }

    /// <summary>
    /// Returns whether the fixed-window rate limiter should be active. Explicit
    /// <c>Arcanum:Host:RateLimit:Enabled</c> enables it; otherwise binding to all interfaces
    /// (<see cref="IsHostAnyEnabled"/>) turns it on automatically.
    /// </summary>
    public static bool IsRateLimitEnabled(bool rateLimitConfigEnabled, bool listenAnyConfigValue) =>
        rateLimitConfigEnabled || IsHostAnyEnabled(listenAnyConfigValue);

}
