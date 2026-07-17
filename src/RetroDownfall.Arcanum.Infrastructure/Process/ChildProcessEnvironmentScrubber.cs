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

                RemoveHijackableEnvironmentVariables(startInfo.Environment);

                break;

            case ChildProcessEnvironmentProfile.SpellScript:

                // Same scrub as ToolExec — spell scripts must not inherit ARCANUM_* secrets
                // or loader/runtime hijack variables from the host process.
                RemoveArcanumSecretVariables(startInfo.Environment);

                RemoveHijackableEnvironmentVariables(startInfo.Environment);

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

    /// <summary>
    /// Removes environment variables that can hijack a spawned tool's behavior — dynamic
    /// linker/interpreter preload hooks (<c>LD_*</c>, <c>DYLD_*</c>, <c>PYTHONPATH</c>,
    /// <c>NODE_OPTIONS</c>, ...), credential-phishing SSH/Git helpers (<c>GIT_SSH_COMMAND</c>,
    /// <c>*_ASKPASS</c>), TLS key logging, and proxy redirection — mirroring the same denylist MCP
    /// child processes are scrubbed against by default (see
    /// <see cref="McpSecurityLimits.IsBlockedEnvironmentVariable"/>). <c>PATH</c> is deliberately
    /// preserved: unlike an MCP server (a single operator-configured executable), <c>execute_command</c>'s
    /// entire purpose is running arbitrary shell commands that need normal PATH resolution to work at all.
    /// </summary>
    internal static void RemoveHijackableEnvironmentVariables(IDictionary<string, string?> environment)
    {

        ArgumentNullException.ThrowIfNull(environment);

        string[] keys = environment.Keys.ToArray();

        foreach (string key in keys)
        {

            if (key.Equals("PATH", StringComparison.OrdinalIgnoreCase))
            {

                continue;

            }

            if (McpSecurityLimits.IsBlockedEnvironmentVariable(key))
            {

                environment.Remove(key);

            }

        }

    }

}
