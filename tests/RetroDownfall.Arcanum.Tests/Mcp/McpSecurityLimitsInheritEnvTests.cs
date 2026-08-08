using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

[Collection("ProcessEnvironment")]
public sealed class McpSecurityLimitsInheritEnvTests
{

    [Fact]
    public void BuildChildProcessEnvironment_without_allowlist_still_blocks_path()
    {

        Dictionary<string, string> source = new(StringComparer.Ordinal)
        {
            ["PATH"] = "/from/config",
            ["MCP_OK"] = "1",
        };

        Dictionary<string, string> result = McpSecurityLimits.BuildChildProcessEnvironment(source);

        Assert.False(result.ContainsKey("PATH"));

        Assert.Equal("1", result["MCP_OK"]);

    }

    [Fact]
    public void BuildChildProcessEnvironment_allowlist_keeps_blocked_source_var()
    {

        Dictionary<string, string> source = new(StringComparer.Ordinal)
        {
            ["PATH"] = "/from/config",
        };

        HashSet<string> allowlist = new(StringComparer.OrdinalIgnoreCase) { "PATH" };

        Dictionary<string, string> result = McpSecurityLimits.BuildChildProcessEnvironment(
            source,
            allowlist,
            hostEnvironmentReader: _ => null);

        Assert.Equal("/from/config", result["PATH"]);

    }

    [Fact]
    public void BuildChildProcessEnvironment_allowlist_inherits_from_host_when_absent()
    {

        HashSet<string> allowlist = new(StringComparer.OrdinalIgnoreCase) { "PATH", "HOME" };

        Dictionary<string, string> hostEnv = new(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/wizard",
        };

        Dictionary<string, string> result = McpSecurityLimits.BuildChildProcessEnvironment(
            source: null,
            inheritAllowlist: allowlist,
            hostEnvironmentReader: name => hostEnv.GetValueOrDefault(name));

        Assert.Equal("/usr/bin", result["PATH"]);

        Assert.Equal("/home/wizard", result["HOME"]);

    }

    [Fact]
    public void BuildChildProcessEnvironment_source_value_wins_over_host()
    {

        Dictionary<string, string> source = new(StringComparer.Ordinal)
        {
            ["PATH"] = "/explicit",
        };

        HashSet<string> allowlist = new(StringComparer.OrdinalIgnoreCase) { "PATH" };

        Dictionary<string, string> result = McpSecurityLimits.BuildChildProcessEnvironment(
            source,
            allowlist,
            hostEnvironmentReader: _ => "/host");

        Assert.Equal("/explicit", result["PATH"]);

    }

    [Fact]
    public void BuildChildProcessEnvironment_allowlist_does_not_inherit_missing_host_var()
    {

        HashSet<string> allowlist = new(StringComparer.OrdinalIgnoreCase) { "PATH" };

        Dictionary<string, string> result = McpSecurityLimits.BuildChildProcessEnvironment(
            source: null,
            inheritAllowlist: allowlist,
            hostEnvironmentReader: _ => null);

        Assert.False(result.ContainsKey("PATH"));

    }

    [Fact]
    public void BuildChildProcessEnvironment_without_reader_inherits_from_process_environment()
    {

        string name = $"MCP_TEST_INHERIT_{Guid.NewGuid():N}";

        const string expected = "from-process-environment";

        try
        {

            System.Environment.SetEnvironmentVariable(name, expected);

            Dictionary<string, string> result = McpSecurityLimits.BuildChildProcessEnvironment(
                source: null,
                inheritAllowlist: new HashSet<string>(StringComparer.Ordinal) { name });

            Assert.Equal(expected, result[name]);

        }
        finally
        {

            System.Environment.SetEnvironmentVariable(name, null);

        }

    }

    [Fact]
    public void BuildChildProcessEnvironment_empty_non_null_allowlist_blocks_and_skips_host_inherit()
    {

        Dictionary<string, string> source = new(StringComparer.Ordinal)
        {
            ["PATH"] = "/from/config",
            ["OK"] = "1",
        };

        HashSet<string> emptyAllowlist = new(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string> result = McpSecurityLimits.BuildChildProcessEnvironment(
            source,
            emptyAllowlist,
            hostEnvironmentReader: _ => "/should-not-be-read");

        Assert.False(result.ContainsKey("PATH"));

        Assert.Equal("1", result["OK"]);

    }

    [Fact]
    public void BuildChildProcessEnvironment_inherit_skips_empty_allowlist_name()
    {

        HashSet<string> allowlist = new(StringComparer.Ordinal) { string.Empty, "PATH" };

        Dictionary<string, string> hostEnv = new(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
        };

        Dictionary<string, string> result = McpSecurityLimits.BuildChildProcessEnvironment(
            source: null,
            inheritAllowlist: allowlist,
            hostEnvironmentReader: name => hostEnv.GetValueOrDefault(name));

        Assert.Equal("/usr/bin", result["PATH"]);

        Assert.False(result.ContainsKey(string.Empty));

    }

    [Fact]
    public void BuildChildProcessEnvironment_skips_empty_source_key()
    {

        Dictionary<string, string> source = new(StringComparer.Ordinal)
        {
            [string.Empty] = "ignored",
            ["MCP_OK"] = "1",
        };

        Dictionary<string, string> result = McpSecurityLimits.BuildChildProcessEnvironment(source);

        Assert.False(result.ContainsKey(string.Empty));

        Assert.Equal("1", result["MCP_OK"]);

    }

    [Fact]
    public void ScrubProcessEnvironment_without_strip_returns_null_for_null_source()
    {

        IReadOnlyDictionary<string, string>? scrubbed = McpSecurityLimits.ScrubProcessEnvironment(
            source: null,
            stripUserEnvironment: false);

        Assert.Null(scrubbed);

    }

    [Fact]
    public void ScrubProcessEnvironment_without_strip_returns_null_for_empty_source()
    {

        IReadOnlyDictionary<string, string>? scrubbed = McpSecurityLimits.ScrubProcessEnvironment(
            source: new Dictionary<string, string>(StringComparer.Ordinal),
            stripUserEnvironment: false);

        Assert.Null(scrubbed);

    }

    [Fact]
    public void ScrubProcessEnvironment_without_strip_returns_null_when_all_entries_are_blocked()
    {

        Dictionary<string, string> source = new(StringComparer.Ordinal)
        {
            ["PATH"] = "/untrusted/bin",
            ["LD_PRELOAD"] = "/untrusted/library",
        };

        IReadOnlyDictionary<string, string>? scrubbed = McpSecurityLimits.ScrubProcessEnvironment(
            source,
            stripUserEnvironment: false);

        Assert.Null(scrubbed);

    }

    [Fact]
    public void ScrubProcessEnvironment_without_strip_returns_only_allowed_entries()
    {

        Dictionary<string, string> source = new(StringComparer.Ordinal)
        {
            ["MCP_ALLOWED"] = "1",
            ["PATH"] = "/untrusted/bin",
        };

        IReadOnlyDictionary<string, string>? scrubbed = McpSecurityLimits.ScrubProcessEnvironment(
            source,
            stripUserEnvironment: false);

        Assert.NotNull(scrubbed);

        KeyValuePair<string, string> entry = Assert.Single(scrubbed);

        Assert.Equal("MCP_ALLOWED", entry.Key);

        Assert.Equal("1", entry.Value);

    }

    [Fact]
    public void ScrubProcessEnvironment_strip_with_allowlist_inherits_host_vars()
    {

        Dictionary<string, string> hostEnv = new(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/local/bin",
        };

        HashSet<string> allowlist = new(StringComparer.OrdinalIgnoreCase) { "PATH" };

        IReadOnlyDictionary<string, string>? scrubbed = McpSecurityLimits.ScrubProcessEnvironment(
            source: null,
            stripUserEnvironment: true,
            inheritAllowlist: allowlist,
            hostEnvironmentReader: name => hostEnv.GetValueOrDefault(name));

        Assert.NotNull(scrubbed);

        Assert.Equal("/usr/local/bin", scrubbed!["PATH"]);

    }

    [Fact]
    public void BuildChildProcessEnvironment_inheritEnv_cannot_re_admit_ARCANUM_or_hijack_vars()
    {

        Dictionary<string, string> source = new(StringComparer.Ordinal)
        {
            ["ARCANUM_Arcanum__Providers__0__ApiKey"] = "sk-secret",
            ["LD_PRELOAD"] = "/tmp/evil.so",
            ["DOTNET_STARTUP_HOOKS"] = "/tmp/hook.dll",
            ["NODE_OPTIONS"] = "--require /tmp/evil.js",
            ["OPENAI_API_KEY"] = "sk-operator-mcp",
            ["PATH"] = "/from/config",
        };

        HashSet<string> allowlist = new(StringComparer.OrdinalIgnoreCase)
        {
            "PATH",
            "HOME",
            "ARCANUM_Arcanum__Providers__0__ApiKey",
            "LD_PRELOAD",
            "DOTNET_STARTUP_HOOKS",
            "NODE_OPTIONS",
        };

        Dictionary<string, string> hostEnv = new(StringComparer.Ordinal)
        {
            ["HOME"] = "/home/wizard",
            ["ARCANUM_Arcanum__Providers__0__ApiKey"] = "from-host",
            ["LD_PRELOAD"] = "/from/host.so",
        };

        Dictionary<string, string> result = McpSecurityLimits.BuildChildProcessEnvironment(
            source,
            allowlist,
            hostEnvironmentReader: name => hostEnv.GetValueOrDefault(name));

        Assert.False(result.ContainsKey("ARCANUM_Arcanum__Providers__0__ApiKey"));

        Assert.False(result.ContainsKey("LD_PRELOAD"));

        Assert.False(result.ContainsKey("DOTNET_STARTUP_HOOKS"));

        Assert.False(result.ContainsKey("NODE_OPTIONS"));

        Assert.Equal("/from/config", result["PATH"]);

        Assert.Equal("/home/wizard", result["HOME"]);

        // Operator-provided MCP credentials must still pass through cfg.Env.
        Assert.Equal("sk-operator-mcp", result["OPENAI_API_KEY"]);

    }

}
