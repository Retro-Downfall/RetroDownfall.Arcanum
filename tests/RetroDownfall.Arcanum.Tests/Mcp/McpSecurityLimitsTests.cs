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
            () => McpSecurityLimits.BoundToolDescription(longDescription));

        Assert.Contains("physical", error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BoundToolDescription_returns_empty_for_missing_descriptions(string? description)
    {

        Assert.Equal(string.Empty, McpSecurityLimits.BoundToolDescription(description!));

    }

    [Fact]
    public void BoundToolInputSchema_rejects_oversized_metadata_instead_of_erasing_the_contract()
    {

        string padding = new('y', McpSecurityLimits.MaxMcpToolInputSchemaUtf8Bytes);

        string hugeSchema = $"{{\"x\":\"{padding}\"}}";

        JsonElement schema = JsonDocument.Parse(hugeSchema).RootElement;

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => McpSecurityLimits.BoundToolInputSchema(
                schema,
                McpJsonSerializerContext.Default));

        Assert.Contains("physical", error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void BoundToolInputSchema_clones_schema_at_or_below_the_limit()
    {

        JsonElement bounded;

        using (JsonDocument document = JsonDocument.Parse("""{"type":"object"}"""))
        {

            bounded = McpSecurityLimits.BoundToolInputSchema(
                document.RootElement,
                McpJsonSerializerContext.Default);

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

}
