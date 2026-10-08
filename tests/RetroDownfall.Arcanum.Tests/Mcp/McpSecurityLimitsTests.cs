using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpSecurityLimitsTests
{
    private const int DefaultMaxJsonRpcLineBytes = 2_097_152;

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task Bounded_file_reader_rejects_invalid_caps(int maxBytes)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => SecureFileReader.ReadBytesAsync(
                "unused",
                maxBytes,
                CancellationToken.None));
    }

    [Fact]
    public async Task Bounded_file_reader_does_not_rent_the_maximum_for_a_small_file()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-bounded-reader-{Guid.NewGuid():N}.json");

        try
        {
            await File.WriteAllTextAsync(path, "{}");

            SecureFileReadResult result =
                await SecureFileReader.ReadBytesAsync(
                    path,
                    McpSecurityLimits.MaxMcpConfigBytes,
                    CancellationToken.None);

            Assert.Equal(SecureFileReadStatus.Success, result.Status);
            Assert.Equal("{}", Encoding.UTF8.GetString(result.Bytes.Span));
            Assert.True(result.BufferCapacity < McpSecurityLimits.MaxMcpConfigBytes);

            result.Dispose();

            Assert.True(result.Bytes.IsEmpty);
            Assert.Equal(0, result.BufferCapacity);

            result.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Bounded_file_reader_reports_missing_parent_directory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-missing-parent-{Guid.NewGuid():N}",
            "mcp.json");

        using SecureFileReadResult result =
            await SecureFileReader.ReadBytesAsync(
                path,
                maxBytes: 1,
                CancellationToken.None);

        Assert.Equal(SecureFileReadStatus.NotFound, result.Status);
        Assert.True(result.Bytes.IsEmpty);
    }

    [Fact]
    public void ExceedsMaxLineUtf8Bytes_detects_oversized_lines()
    {
        string small = new('a', 16);

        string huge = new('a', DefaultMaxJsonRpcLineBytes + 1);

        Assert.False(McpSecurityLimits.ExceedsMaxLineUtf8Bytes(small, DefaultMaxJsonRpcLineBytes));

        Assert.True(McpSecurityLimits.ExceedsMaxLineUtf8Bytes(huge, DefaultMaxJsonRpcLineBytes));
    }

    [Fact]
    public void BoundToolDescription_rejects_oversized_metadata_instead_of_silently_truncating()
    {
        string longDescription = new('x', McpSecurityLimits.MaxMcpToolDescriptionUtf8Bytes + 64);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => McpSecurityLimits.BoundToolDescription("probe_tool", longDescription));

        Assert.Contains("physical", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoundToolDescription_error_names_the_tool_and_size()
    {
        int size = McpSecurityLimits.MaxMcpToolDescriptionUtf8Bytes + 64;

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => McpSecurityLimits.BoundToolDescription("probe_tool", new string('x', size)));

        Assert.Contains("probe_tool", error.Message, StringComparison.Ordinal);

        Assert.Contains($"{size} UTF-8 bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundToolInputSchema_error_names_the_tool_and_size()
    {
        string schemaJson = $"{{\"x\":\"{new string('y', McpSecurityLimits.MaxMcpToolInputSchemaUtf8Bytes)}\"}}";

        using JsonDocument schema = JsonDocument.Parse(schemaJson);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => McpSecurityLimits.BoundToolInputSchema("probe_tool", schema.RootElement));

        Assert.Contains("probe_tool", error.Message, StringComparison.Ordinal);

        Assert.Contains($"{schemaJson.Length} UTF-8 bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_metadata_errors_do_not_echo_control_characters_or_unbounded_tool_names()
    {
        string hostileName = "evil\nINJECTED" + new string('n', 500);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => McpSecurityLimits.BoundToolDescription(
                hostileName,
                new string('x', McpSecurityLimits.MaxMcpToolDescriptionUtf8Bytes + 1)));

        Assert.DoesNotContain('\n', error.Message);

        Assert.True(error.Message.Length < 400, "The message quoted the whole hostile tool name.");
    }

    [Fact]
    public void A_tool_name_cut_for_the_error_label_never_splits_a_surrogate_pair()
    {
        // 79 ASCII characters then an emoji: the 80-character cut lands between the pair's two UTF-16 units.
        string hostileName = new string('a', 79) + "\U0001F600" + new string('b', 40);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => McpSecurityLimits.BoundToolDescription(
                hostileName,
                new string('x', McpSecurityLimits.MaxMcpToolDescriptionUtf8Bytes + 1)));

        AssertWellFormedUtf16(error.Message);

        // The whole character is kept or dropped, never half of it.
        Assert.Contains(new string('a', 79) + "\U0001F600", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tool_name_with_a_lone_surrogate_is_not_echoed_as_one()
    {
        // The name arrives from an external server's JSON, which can carry an unpaired escape such as
        // \ud800. System.Text.Json refuses to write one, so quoting it would break the status payload that
        // carries this message.
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => McpSecurityLimits.BoundToolDescription(
                "evil\ud800name",
                new string('x', McpSecurityLimits.MaxMcpToolDescriptionUtf8Bytes + 1)));

        AssertWellFormedUtf16(error.Message);

        Assert.Contains("evil", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\u202E")]
    [InlineData("\u2066")]
    [InlineData("\u200B")]
    [InlineData("\u0085")]
    [InlineData("\r")]
    public void A_tool_name_cannot_inject_a_line_break_or_a_bidirectional_control_into_the_label(
        string hostileCharacter)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => McpSecurityLimits.BoundToolDescription(
                "evil" + hostileCharacter + "INJECTED",
                new string('x', McpSecurityLimits.MaxMcpToolDescriptionUtf8Bytes + 1)));

        Assert.DoesNotContain(hostileCharacter, error.Message, StringComparison.Ordinal);

        Assert.Contains("evil?INJECTED", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tool_with_no_name_still_fails_with_the_metadata_boundary_error_and_an_empty_label()
    {
        // The name is an external server's JSON, which can omit it: the SDK then hands a null to a
        // non-nullable property, and the label must quote nothing rather than throw a NullReferenceException
        // that would hide the boundary refusal.
        int size = McpSecurityLimits.MaxMcpToolDescriptionUtf8Bytes + 1;

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => McpSecurityLimits.BoundToolDescription(null!, new string('x', size)));

        Assert.Contains("MCP tool ''", error.Message, StringComparison.Ordinal);

        Assert.Contains($"{size} UTF-8 bytes", error.Message, StringComparison.Ordinal);
    }

    private static void AssertWellFormedUtf16(string text)
    {
        // A strict UTF-8 encoder throws on an unpaired surrogate, which is what System.Text.Json does too.
        UTF8Encoding strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        _ = strict.GetBytes(text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BoundToolDescription_returns_empty_for_missing_descriptions(string? description)
    {
        Assert.Equal(string.Empty, McpSecurityLimits.BoundToolDescription("probe_tool", description!));
    }

    [Fact]
    public void BoundToolDescription_returns_a_description_at_the_limit_unchanged()
    {
        string atLimit = new('x', McpSecurityLimits.MaxMcpToolDescriptionUtf8Bytes);

        Assert.Same(atLimit, McpSecurityLimits.BoundToolDescription("probe_tool", atLimit));
    }

    [Fact]
    public void BoundToolInputSchema_rejects_oversized_metadata_instead_of_erasing_the_contract()
    {
        string padding = new('y', McpSecurityLimits.MaxMcpToolInputSchemaUtf8Bytes);

        string hugeSchema = $"{{\"x\":\"{padding}\"}}";

        JsonElement schema = JsonDocument.Parse(hugeSchema).RootElement;

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => McpSecurityLimits.BoundToolInputSchema("probe_tool", schema));

        Assert.Contains("physical", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoundToolInputSchema_clones_schema_at_or_below_the_limit()
    {
        JsonElement bounded;

        using (JsonDocument document = JsonDocument.Parse("""{"type":"object"}"""))
        {
            bounded = McpSecurityLimits.BoundToolInputSchema("probe_tool", document.RootElement);
        }

        Assert.Equal("""{"type":"object"}""", bounded.GetRawText());
    }

    [Theory]
    [InlineData("ARCANUM_Arcanum__Providers__0__ApiKey")]
    [InlineData("LD_PRELOAD")]
    [InlineData("DYLD_INSERT_LIBRARIES")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("DOTNET_ADDITIONAL_DEPS")]
    [InlineData("CORECLR_PROFILER")]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("PYTHONPATH")]
    [InlineData("PERL5LIB")]
    [InlineData("RUBYLIB")]
    [InlineData("GEM_PATH")]
    [InlineData("GEM_HOME")]
    [InlineData("JAVA_TOOL_OPTIONS")]
    [InlineData("_JAVA_OPTIONS")]
    [InlineData("JDK_JAVA_OPTIONS")]
    [InlineData("BASH_ENV")]
    [InlineData("ENV")]
    [InlineData("SSLKEYLOGFILE")]
    [InlineData("GIT_SSH_COMMAND")]
    [InlineData("GIT_ASKPASS")]
    [InlineData("SSH_ASKPASS")]
    [InlineData("GCONV_PATH")]
    [InlineData("LOCPATH")]
    [InlineData("HOSTALIASES")]
    [InlineData("RES_OPTIONS")]
    public void IsAbsolutelyDeniedEnvironmentVariable_blocks_runtime_hijacks(string key)
    {
        Assert.True(McpSecurityLimits.IsAbsolutelyDeniedEnvironmentVariable(key));

        Assert.True(McpSecurityLimits.IsBlockedEnvironmentVariable(key));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("HTTP_PROXY")]
    [InlineData("HTTPS_PROXY")]
    [InlineData("ALL_PROXY")]
    [InlineData("http_proxy")]
    [InlineData("https_proxy")]
    [InlineData("all_proxy")]
    public void IsBlockedEnvironmentVariable_blocks_process_scope_keys(string key)
    {
        Assert.False(McpSecurityLimits.IsAbsolutelyDeniedEnvironmentVariable(key));

        Assert.True(McpSecurityLimits.IsBlockedEnvironmentVariable(key));
    }

    [Fact]
    public void Environment_variable_checks_allow_regular_operator_values()
    {
        Assert.False(McpSecurityLimits.IsAbsolutelyDeniedEnvironmentVariable("OPENAI_API_KEY"));

        Assert.False(McpSecurityLimits.IsBlockedEnvironmentVariable("OPENAI_API_KEY"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_environment_variable_names_are_always_denied(string? key)
    {
        Assert.True(McpSecurityLimits.IsAbsolutelyDeniedEnvironmentVariable(key!));

        Assert.True(McpSecurityLimits.IsBlockedEnvironmentVariable(key!));
    }

    [Fact]
    public void TruncateUtf8_preserves_valid_prefix_for_multibyte_characters()
    {
        string text = "ascii" + new string('é', 20);

        string truncated = McpSecurityLimits.TruncateUtf8(text, 12);

        Assert.True(Encoding.UTF8.GetByteCount(truncated) <= 12 + 64);

        Assert.StartsWith("ascii", truncated, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncateUtf8_returns_original_text_when_it_fits_the_limit()
    {
        const string text = "exactly-safe";

        Assert.Same(text, McpSecurityLimits.TruncateUtf8(text, Encoding.UTF8.GetByteCount(text)));
    }

    [Theory]
    [InlineData("", 1L)]
    [InlineData("text", 0L)]
    [InlineData("text", -1L)]
    public void TruncateUtf8_returns_empty_for_empty_text_or_nonpositive_limit(
        string text,
        long maxUtf8Bytes)
    {
        Assert.Equal(string.Empty, McpSecurityLimits.TruncateUtf8(text, maxUtf8Bytes));
    }

    [Fact]
    public void TruncateUtf8_when_no_complete_scalar_fits_returns_only_the_truncation_marker()
    {
        string truncated = McpSecurityLimits.TruncateUtf8("😀", 1L);

        Assert.Equal("\n[truncated: exceeded 1 bytes]", truncated);

        Assert.DoesNotContain('\uFFFD', truncated);
    }

    // stdio framing for external MCP servers is owned by the SDK's StdioClientTransport
    // (McpConnectionManager.Lifecycle). No first-party stdio line reader may survive alongside it:
    // an uncalled one advertises itself as the framing path and misdirects anyone hardening it.
    [Fact]
    public void No_first_party_stdio_line_reader_shadows_the_sdk_transport()
    {
        Assert.Null(
            typeof(McpSecurityLimits).Assembly.GetType(
                "RetroDownfall.Arcanum.Infrastructure.Mcp.McpStdioLineReader"));
    }
}
