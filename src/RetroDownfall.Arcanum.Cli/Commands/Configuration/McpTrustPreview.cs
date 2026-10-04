using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Cli.Commands.Configuration;

/// <summary>
/// Describes what a workspace-local <c>mcp.json</c> would be allowed to run, for the operator to read
/// before <c>arcanum mcp trust</c> binds trust to it.
/// </summary>
/// <remarks>
/// Trust is a grant to launch whatever commands the file names, so the preview lists each server with
/// its command line or URL. Environment values are never printed: only their names, because a value is
/// frequently a credential. The file is the repository author's text, so every string is stripped of
/// terminal control characters before it reaches a terminal. The read uses the same secure, size-capped
/// reader the host trusts through, so the preview shows the bytes the host would see.
/// </remarks>
internal static class McpTrustPreview
{
    private const int MaxDisplayChars = 512;

    internal static async Task<IReadOnlyList<string>> DescribeAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        string configPath;

        try
        {
            configPath = Path.Combine(
                TrustedMcpWorkspaceStore.NormalizeWorkspaceRoot(workspace),
                "mcp.json");
        }
        catch (Exception exception)
            when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return [$"The workspace path is not valid, so its mcp.json cannot be previewed ({exception.Message})."];
        }

        string shownPath = Display(configPath);

        using SecureFileReadResult read = await SecureFileReader
            .ReadBytesAsync(
                configPath,
                McpSecurityLimits.MaxMcpConfigBytes,
                cancellationToken)
            .ConfigureAwait(false);

        if (read.Status is SecureFileReadStatus.NotFound)
        {
            return
            [
                $"No mcp.json was found at {shownPath} on this machine, so its servers cannot be previewed. "
                + "The host trusts whatever mcp.json it reads there.",
            ];
        }

        if (read.Status is not SecureFileReadStatus.Success)
        {
            return
            [
                $"The mcp.json at {shownPath} could not be read for a preview ({read.Status}), "
                + "so its servers cannot be previewed.",
            ];
        }

        McpConfig? config;

        try
        {
            config = JsonSerializer.Deserialize(
                read.Bytes.Span,
                McpConfigJsonSerializerContext.Default.McpConfig);
        }
        catch (JsonException exception)
        {
            return
            [
                $"The mcp.json at {shownPath} is not valid JSON, so its servers cannot be previewed: "
                + Display(exception.Message),
            ];
        }

        Dictionary<string, McpServerConfig> servers = config?.McpServers ?? [];

        List<string> lines =
        [
            servers.Count == 0
                ? $"The mcp.json at {shownPath} defines no MCP servers."
                : $"The mcp.json at {shownPath} defines {servers.Count} MCP server(s):",
        ];

        foreach ((string name, McpServerConfig server) in servers.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            lines.Add($"  {Display(name)} [{Transport(server)}] {Launches(server)}");

            if (!string.IsNullOrWhiteSpace(server.Cwd))
            {
                lines.Add($"    working directory: {Display(server.Cwd)}");
            }

            if (server.Env is { Count: > 0 } env)
            {
                lines.Add(
                    "    environment: "
                    + string.Join(", ", env.Keys.Order(StringComparer.Ordinal).Select(Display))
                    + " (values not shown)");
            }

            if (server.InheritEnv is { Length: > 0 } inherited)
            {
                lines.Add($"    inherits host environment: {string.Join(", ", inherited.Select(Display))}");
            }
        }

        return lines;
    }

    private static string Transport(McpServerConfig server) =>
        string.IsNullOrWhiteSpace(server.Type)
            ? string.IsNullOrWhiteSpace(server.Url) ? "stdio" : "http"
            : Display(server.Type);

    private static string Launches(McpServerConfig server)
    {
        if (!string.IsNullOrWhiteSpace(server.Url)
            && string.IsNullOrWhiteSpace(server.Command))
        {
            return Display(server.Url);
        }

        string command = string.IsNullOrWhiteSpace(server.Command)
            ? "(no command)"
            : Display(server.Command);

        return server.Args is { Length: > 0 }
            ? $"{command} {string.Join(' ', server.Args.Select(Display))}"
            : command;
    }

    /// <summary>
    /// The text with every control character removed and a bounded length, so repository-authored bytes
    /// cannot move the cursor, retitle the window or write the clipboard.
    /// </summary>
    internal static string Display(string value)
    {
        StringBuilder builder = new(Math.Min(value.Length, MaxDisplayChars));

        foreach (char character in value)
        {
            if (builder.Length >= MaxDisplayChars)
            {
                builder.Append("...");

                break;
            }

            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
