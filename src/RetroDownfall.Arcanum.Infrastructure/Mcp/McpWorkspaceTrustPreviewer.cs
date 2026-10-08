using System.Text.Json;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Describes what a workspace-local <c>mcp.json</c> would be allowed to run, from the host's own copy of
/// the file, for the operator to read before <c>arcanum mcp trust</c> binds trust to it.
/// </summary>
/// <remarks>
/// <para>Trust is a grant to launch whatever commands the file names, so the preview lists each server with
/// the transport the host will actually use (<see cref="McpConnectionManager.InferTransport"/>) and the command
/// line or URL that transport runs or dials. An authored field the transport ignores (a command beside a URL
/// the host connects to, or the reverse) is still shown, marked as not used, so the preview hides nothing.
/// Environment values are never printed, only their names, because a value is frequently a credential. The read uses the same secure, size-capped reader the trust store
/// digests through, and the preview carries that digest, so a later trust request that names it is refused
/// by the store if the file is not still those bytes. Authored text is made safe to print by
/// <see cref="McpTrustPreviewText"/>.</para>
/// <para>The preview runs where the file is, which is the host: a client on another machine, or one whose
/// working directory is not the workspace, is shown the host's file, not its own.</para>
/// </remarks>
public sealed class McpWorkspaceTrustPreviewer : IMcpWorkspaceTrustPreviewer
{
    /// <inheritdoc />
    public async Task<Result<McpWorkspaceTrustPreview>> PreviewAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return new Error(
                ErrorCodes.Mcp.MissingWorkspace,
                "workingDirectory is required to preview a workspace-local mcp.json.");
        }

        string normalized;

        try
        {
            normalized = TrustedMcpWorkspaceStore.NormalizeWorkspaceRoot(workingDirectory);
        }
        catch (Exception exception)
            when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return new Error("Mcp.InvalidWorkspace", "workingDirectory is not a valid path.");
        }

        string configPath = Path.Combine(normalized, "mcp.json");

        SecureFileReadResult read;

        try
        {
            read = await SecureFileReader
                .ReadBytesAsync(
                    configPath,
                    McpSecurityLimits.MaxMcpConfigBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            return new Error(
                "Mcp.TrustFailed",
                "Workspace mcp.json could not be read. Verify access and retry.");
        }

        using (read)
        {
            switch (read.Status)
            {
                case SecureFileReadStatus.NotFound:
                    return new Error("Mcp.MissingConfig", "Workspace mcp.json was not found.");

                case SecureFileReadStatus.TooLarge:
                    return new Error(
                        "Mcp.TrustFailed",
                        "Workspace mcp.json exceeds the maximum trusted configuration size. Reduce it and retry.");

                case SecureFileReadStatus.Success:
                    break;

                default:
                    return new Error(
                        "Mcp.TrustFailed",
                        "Workspace mcp.json could not be validated as a safe regular file.");
            }

            string digest = McpConfigDigest.Compute(read.Bytes.Span);

            McpConfig? config;

            try
            {
                config = JsonSerializer.Deserialize(
                    read.Bytes.Span,
                    McpConfigJsonSerializerContext.Default.McpConfig);
            }
            catch (JsonException exception)
            {
                string reason = McpTrustPreviewText.Display(exception.Message, out _);

                return new Error(
                    "Mcp.InvalidConfig",
                    $"The workspace mcp.json is not valid JSON, so its servers cannot be previewed: {reason}");
            }

            Dictionary<string, McpServerConfig> servers = config?.McpServers ?? [];

            // JSON null binds without an exception: a null entry or list element is a file the host cannot
            // describe, and is refused as one rather than escaping the Result flow.
            if (servers.Values.Any(static server => server is null
                || server.Args?.Any(static argument => argument is null) == true
                || server.InheritEnv?.Any(static name => name is null) == true))
            {
                return new Error(
                    "Mcp.InvalidConfig",
                    "The workspace mcp.json has a null server entry or a null args or inheritEnv element, so its servers cannot be previewed.");
            }

            return Describe(normalized, servers, digest);
        }
    }

    private static McpWorkspaceTrustPreview Describe(
        string normalizedWorkspace,
        Dictionary<string, McpServerConfig> servers,
        string digest)
    {
        FieldWriter fields = new();

        string workspace = fields.Show(normalizedWorkspace);

        List<string> lines =
        [
            servers.Count == 0
                ? $"The mcp.json in {workspace} defines no MCP servers."
                : $"The mcp.json in {workspace} defines {servers.Count} MCP server(s):",
        ];

        foreach ((string name, McpServerConfig server) in servers.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            McpServerTransport transport = McpConnectionManager.InferTransport(server);

            bool dialsUrl = transport is not McpServerTransport.Stdio;

            lines.Add(
                $"  {fields.Show(name)} [{TransportLabel(transport)}] "
                + (dialsUrl ? Endpoint(server, fields) : CommandLine(server, fields)));

            if (!string.IsNullOrWhiteSpace(server.Type) && !IsRecognisedType(server.Type))
            {
                lines.Add($"    declared type (not recognised): {fields.Show(server.Type)}");
            }

            if (dialsUrl && !string.IsNullOrWhiteSpace(server.Command))
            {
                lines.Add($"    command (not run by this transport): {CommandLine(server, fields)}");
            }

            if (!dialsUrl && !string.IsNullOrWhiteSpace(server.Url))
            {
                lines.Add($"    url (not used by this transport): {fields.Show(server.Url)}");
            }

            if (!string.IsNullOrWhiteSpace(server.Cwd))
            {
                lines.Add($"    working directory: {fields.Show(server.Cwd)}");
            }

            if (server.Env is { Count: > 0 } env)
            {
                lines.Add(
                    "    environment: "
                    + string.Join(", ", env.Keys.Order(StringComparer.Ordinal).Select(fields.Show))
                    + " (values not shown)");
            }

            if (server.InheritEnv is { Length: > 0 } inherited)
            {
                lines.Add($"    inherits host environment: {string.Join(", ", inherited.Select(fields.Show))}");
            }
        }

        return new McpWorkspaceTrustPreview(workspace, [.. lines], fields.Truncated, digest);
    }

    private static string TransportLabel(McpServerTransport transport) => transport switch
    {
        McpServerTransport.Http => "http",
        McpServerTransport.Sse => "sse",
        _ => "stdio",
    };

    private static bool IsRecognisedType(string type) =>
        string.Equals(type, "stdio", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "http", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "sse", StringComparison.OrdinalIgnoreCase);

    private static string Endpoint(
        McpServerConfig server,
        FieldWriter fields) =>
        string.IsNullOrWhiteSpace(server.Url) ? "(no url)" : fields.Show(server.Url);

    private static string CommandLine(
        McpServerConfig server,
        FieldWriter fields)
    {
        string command = string.IsNullOrWhiteSpace(server.Command)
            ? "(no command)"
            : fields.ShowArgument(server.Command);

        return server.Args is { Length: > 0 }
            ? $"{command} {string.Join(' ', server.Args.Select(fields.ShowArgument))}"
            : command;
    }

    /// <summary>
    /// Renders each authored field of one preview and remembers whether any of them was cut short.
    /// </summary>
    private sealed class FieldWriter
    {
        public bool Truncated { get; private set; }

        public string Show(string value)
        {
            string shown = McpTrustPreviewText.Display(value, out bool truncated);

            Truncated |= truncated;

            return shown;
        }

        public string ShowArgument(string value)
        {
            string shown = McpTrustPreviewText.DisplayArgument(value, out bool truncated);

            Truncated |= truncated;

            return shown;
        }
    }
}
