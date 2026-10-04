using System.Globalization;

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
/// terminal control characters before it reaches a terminal, and every run of whitespace shows as a single
/// space, so padding cannot push the part of a command line that matters out of view. A field longer than
/// <see cref="MaxDisplayChars"/> is cut with a count of what was left out, and the result says so, because a
/// preview that leaves text out cannot be the basis for approving it. The read uses the same secure,
/// size-capped reader the host trusts through, so the preview shows the bytes the host would see.
/// </remarks>
internal static class McpTrustPreview
{
    /// <summary>
    /// The most characters one field (a name, command, argument, URL, directory or variable name) shows,
    /// after whitespace is collapsed. A real command line is far shorter; anything longer is refused rather
    /// than approved on partial text.
    /// </summary>
    internal const int MaxDisplayChars = 4096;

    internal static async Task<McpTrustPreviewResult> DescribeAsync(
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
            return Complete($"The workspace path is not valid, so its mcp.json cannot be previewed ({Display(exception.Message)}).");
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
            return Complete(
                $"No mcp.json was found at {shownPath} on this machine, so its servers cannot be previewed. "
                + "The host trusts whatever mcp.json it reads there.");
        }

        if (read.Status is not SecureFileReadStatus.Success)
        {
            return Complete(
                $"The mcp.json at {shownPath} could not be read for a preview ({read.Status}), "
                + "so its servers cannot be previewed.");
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
            return Complete(
                $"The mcp.json at {shownPath} is not valid JSON, so its servers cannot be previewed: "
                + Display(exception.Message));
        }

        Dictionary<string, McpServerConfig> servers = config?.McpServers ?? [];

        FieldWriter fields = new();

        List<string> lines =
        [
            servers.Count == 0
                ? $"The mcp.json at {shownPath} defines no MCP servers."
                : $"The mcp.json at {shownPath} defines {servers.Count} MCP server(s):",
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

        return new McpTrustPreviewResult(lines, fields.Truncated);
    }

    private static McpTrustPreviewResult Complete(string line) => new([line], Truncated: false);

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
            : fields.Show(server.Command);

        return server.Args is { Length: > 0 }
            ? $"{command} {string.Join(' ', server.Args.Select(fields.Show))}"
            : command;
    }

    /// <summary>
    /// The text with every control character removed, every run of whitespace shown as one space, and a
    /// bounded length, so repository-authored bytes cannot move the cursor, retitle the window, write the
    /// clipboard or hide a command behind padding. Text past <see cref="MaxDisplayChars"/> is replaced by
    /// a count of the characters left out.
    /// </summary>
    internal static string Display(string value) => Display(value, out _);

    /// <summary>
    /// <see cref="Display(string)"/> that also reports whether any of the field was left out.
    /// </summary>
    internal static string Display(
        string value,
        out bool truncated)
    {
        StringBuilder builder = new(Math.Min(value.Length, MaxDisplayChars));

        bool separatorPending = false;

        int hidden = 0;

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];

            if (char.IsWhiteSpace(character))
            {
                separatorPending = true;

                continue;
            }

            if (char.IsControl(character))
            {
                continue;
            }

            if (builder.Length + (separatorPending ? 2 : 1) > MaxDisplayChars)
            {
                hidden = value.Length - index;

                break;
            }

            if (separatorPending)
            {
                builder.Append(' ');

                separatorPending = false;
            }

            builder.Append(character);
        }

        truncated = hidden > 0;

        if (truncated)
        {
            builder.Append(CultureInfo.InvariantCulture, $" [{hidden} more characters not shown]");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders each authored field of one preview and remembers whether any of them was cut short.
    /// </summary>
    private sealed class FieldWriter
    {
        public bool Truncated { get; private set; }

        public string Show(string value)
        {
            string shown = Display(value, out bool truncated);

            Truncated |= truncated;

            return shown;
        }
    }
}

/// <summary>
/// What a workspace-local <c>mcp.json</c> would be allowed to run, as lines for the operator, and whether any
/// field was too long to show in full.
/// </summary>
/// <param name="Lines">The preview, one line per entry, already stripped of control characters.</param>
/// <param name="Truncated">True when at least one field was cut short, so the operator did not see all of it.</param>
internal sealed record McpTrustPreviewResult(
    IReadOnlyList<string> Lines,
    bool Truncated);
