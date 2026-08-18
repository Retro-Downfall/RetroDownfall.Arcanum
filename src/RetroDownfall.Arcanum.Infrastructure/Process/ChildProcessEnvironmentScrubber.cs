using System.Diagnostics;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

internal static class ChildProcessEnvironmentScrubber
{

    /// <param name="operatorDeclaredSecretNames">
    /// The variable names the operator has configured Arcanum's own secrets into — the list
    /// <c>FamiliarSecretEnvironmentNames.Collect</c> builds. The <c>ARCANUM_</c> prefix scrub covers
    /// only the derived default names, so without this list a provider key the operator legitimately
    /// pointed at <c>MY_OPENAI_KEY</c> is inherited by every model-directed child.
    /// </param>
    internal static void ApplyProfile(
        ProcessStartInfo startInfo,
        ChildProcessEnvironmentProfile profile,
        IReadOnlyCollection<string>? operatorDeclaredSecretNames = null)
    {

        switch (profile)
        {

            case ChildProcessEnvironmentProfile.ToolExec:

                RemoveArcanumSecretVariables(startInfo.Environment);

                RemoveOperatorDeclaredSecretVariables(
                    startInfo.Environment,
                    operatorDeclaredSecretNames);

                RemoveHijackableEnvironmentVariables(startInfo.Environment);

                break;

            case ChildProcessEnvironmentProfile.SpellScript:

                // Same scrub as ToolExec — spell scripts must not inherit ARCANUM_* secrets
                // or loader/runtime hijack variables from the host process.
                RemoveArcanumSecretVariables(startInfo.Environment);

                RemoveOperatorDeclaredSecretVariables(
                    startInfo.Environment,
                    operatorDeclaredSecretNames);

                RemoveHijackableEnvironmentVariables(startInfo.Environment);

                break;

            case ChildProcessEnvironmentProfile.Familiar:

                // A Familiar authenticates itself against the operator's own subscription, so it
                // needs PATH and HOME and must never receive an Arcanum secret. The caller strips
                // configured provider credential variables by name on top of this.
                RemoveArcanumSecretVariables(startInfo.Environment);

                RemoveOperatorDeclaredSecretVariables(
                    startInfo.Environment,
                    operatorDeclaredSecretNames);

                RemoveHijackableEnvironmentVariables(startInfo.Environment);

                break;

            case ChildProcessEnvironmentProfile.McpChild:

                // MCP builds an explicit env block via BuildMcpChildEnvironment before start.
                break;

            case ChildProcessEnvironmentProfile.WorkspaceCheck:

                // WorkspaceCheckEnvironmentBuilder already cleared and reconstructed the entire
                // environment. Do not reintroduce or copy anything from the host here.
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
    /// Removes every variable the operator has told Arcanum holds one of <em>its</em> secrets. Those
    /// names are configuration, not a prefix: <c>Arcanum:Providers:*:CredentialEnvironmentVariable</c>,
    /// <c>Host:Https:CertificatePasswordEnvironmentVariable</c>,
    /// <c>Integrations:CommLink:WebhookUrlEnvironmentVariable</c>,
    /// <c>WebBrowsing:CredentialEnvironmentVariable</c> and
    /// <c>Integrations:A2A:OutboundCredentialEnvironmentVariable</c> all accept any portable name, so
    /// a key called <c>MY_OPENAI_KEY</c> is no less Arcanum's secret for being named that and
    /// <see cref="RemoveArcanumSecretVariables"/> alone would hand it straight to the child. Matching
    /// is case-insensitive: the configured spelling need not match the exported one, and
    /// over-removing a name the operator themselves called a secret is the safe direction.
    /// </summary>
    internal static void RemoveOperatorDeclaredSecretVariables(
        IDictionary<string, string?> environment,
        IReadOnlyCollection<string>? declaredNames)
    {

        ArgumentNullException.ThrowIfNull(environment);

        if (declaredNames is null || declaredNames.Count == 0)
        {

            return;

        }

        HashSet<string> denied = new(StringComparer.OrdinalIgnoreCase);

        foreach (string name in declaredNames)
        {

            if (!string.IsNullOrWhiteSpace(name))
            {

                _ = denied.Add(name.Trim());

            }

        }

        if (denied.Count == 0)
        {

            return;

        }

        string[] keys = environment.Keys.ToArray();

        foreach (string key in keys)
        {

            if (denied.Contains(key))
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
