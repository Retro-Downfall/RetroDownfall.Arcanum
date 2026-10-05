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
/// its transport and command line or URL. Environment values are never printed, only their names, because a
/// value is frequently a credential. The read uses the same secure, size-capped reader the trust store
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

            return Describe(normalized, config?.McpServers ?? [], digest);
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
            lines.Add($"  {fields.Show(name)} [{Transport(server, fields)}] {Launches(server, fields)}");

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

    private static string Transport(
        McpServerConfig server,
        FieldWriter fields) =>
        string.IsNullOrWhiteSpace(server.Type)
            ? string.IsNullOrWhiteSpace(server.Url) ? "stdio" : "http"
            : fields.Show(server.Type);

    private static string Launches(
        McpServerConfig server,
        FieldWriter fields)
    {
        if (!string.IsNullOrWhiteSpace(server.Url)
            && string.IsNullOrWhiteSpace(server.Command))
        {
            return fields.Show(server.Url);
        }

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
