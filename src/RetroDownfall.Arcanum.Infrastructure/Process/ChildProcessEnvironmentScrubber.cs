using System.Diagnostics;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

internal static class ChildProcessEnvironmentScrubber
{

    internal static void ApplyProfile(ProcessStartInfo startInfo, ChildProcessEnvironmentProfile profile)
    {

        switch (profile)
        {

            case ChildProcessEnvironmentProfile.ToolExec:

                RemoveArcanumSecretVariables(startInfo.Environment);

                break;

            case ChildProcessEnvironmentProfile.SpellScript:

                break;

            case ChildProcessEnvironmentProfile.McpChild:

                // MCP builds an explicit env block via BuildMcpChildEnvironment before start.
                break;

        }

    }

    internal static IReadOnlyDictionary<string, string>? BuildMcpChildEnvironment(
        IReadOnlyDictionary<string, string>? source,
        bool stripUserEnvironment,
        IReadOnlySet<string>? inheritAllowlist = null,
        Func<string, string?>? hostEnvironmentReader = null)
    {

        return McpSecurityLimits.ScrubProcessEnvironment(
            source,
            stripUserEnvironment,
            inheritAllowlist,
            hostEnvironmentReader);

    }

    /// <summary>
    /// Removes every <c>ARCANUM_</c>-prefixed variable from a child-process environment so provider API
    /// keys configured via the <c>ARCANUM_</c> env-var prefix cannot leak to commands spawned by
    /// <c>execute_command</c>. Every other variable (PATH, HOME, ...) is preserved.
    /// </summary>
    internal static void RemoveArcanumSecretVariables(IDictionary<string, string?> environment)
    {

        ArgumentNullException.ThrowIfNull(environment);

        string[] keys = environment.Keys.ToArray();

        foreach (string key in keys)
        {

            if (key.StartsWith("ARCANUM_", StringComparison.OrdinalIgnoreCase))
            {

                environment.Remove(key);

            }

        }

    }

}
