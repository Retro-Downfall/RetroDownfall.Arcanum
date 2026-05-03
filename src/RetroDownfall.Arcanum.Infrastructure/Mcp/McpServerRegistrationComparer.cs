namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Structural equality for MCP stdio server launch recipes (<see cref="McpServerConfig"/>), independent of <c>mcpServers</c> map keys.
/// </summary>
public static class McpServerRegistrationComparer
{
    /// <summary>
    /// Returns <see langword="true"/> if both registrations describe the same command, arguments, and environment bindings.
    /// </summary>
    public static bool Equals(McpServerConfig? a, McpServerConfig? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        string? cmdA = a.Command?.Trim();

        string? cmdB = b.Command?.Trim();

        if (!string.Equals(cmdA, cmdB, StringComparison.Ordinal))
        {
            return false;
        }

        string[] argsA = a.Args ?? [];

        string[] argsB = b.Args ?? [];

        if (argsA.Length != argsB.Length)
        {
            return false;
        }

        for (int i = 0; i < argsA.Length; i++)
        {
            if (!string.Equals(argsA[i], argsB[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        Dictionary<string, string>? envA = a.Env;

        Dictionary<string, string>? envB = b.Env;

        if (envA is null || envA.Count == 0)
        {
            return envB is null || envB.Count == 0;
        }

        if (envB is null || envB.Count != envA.Count)
        {
            return false;
        }

        string[] keys = new string[envA.Count];

        envA.Keys.CopyTo(keys, 0);

        Array.Sort(keys, StringComparer.Ordinal);

        foreach (string k in keys)
        {
            if (!envB.TryGetValue(k, out string? vB) || !string.Equals(envA[k], vB, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
