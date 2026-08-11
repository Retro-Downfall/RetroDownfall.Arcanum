using System.CommandLine;
using System.Text;
using RetroDownfall.Arcanum.Api.Configuration;
using RetroDownfall.Arcanum.Cli;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class CliOperatorSurfaceTests
{

    [Fact]
    public void RemoveLastCharacter_removes_a_whole_surrogate_pair()
    {

        StringBuilder buffer = new("a\U0001F642");

        int removed = CliLineReader.RemoveLastCharacter(buffer);

        Assert.Equal(2, removed);

        Assert.Equal("a", buffer.ToString());

    }

    [Fact]
    public void RemoveLastCharacter_removes_one_unit_for_a_bmp_character()
    {

        StringBuilder buffer = new("你好");

        int removed = CliLineReader.RemoveLastCharacter(buffer);

        Assert.Equal(1, removed);

        Assert.Equal("你", buffer.ToString());

    }

    [Fact]
    public void RemoveLastCharacter_on_empty_buffer_is_a_no_op()
    {

        StringBuilder buffer = new();

        Assert.Equal(0, CliLineReader.RemoveLastCharacter(buffer));

    }

    /// <summary>
    /// A terminal erases columns, not UTF-16 code units. Four ideographs paint eight columns, so
    /// Ctrl+U must walk the cursor back eight cells or half the line stays on screen.
    /// </summary>
    [Fact]
    public void ClearLine_erases_the_columns_a_wide_line_painted()
    {

        StringBuilder buffer = new("你好世界");

        int erased = CliLineReader.ClearLine(buffer);

        Assert.Equal(8, erased);

        Assert.Equal(0, buffer.Length);

    }

    /// <summary>
    /// The mirror hazard: a narrow astral character is two code units but one column, so erasing by
    /// code unit walks the cursor back into the prompt.
    /// </summary>
    [Fact]
    public void ClearLine_erases_one_column_for_a_narrow_astral_character()
    {

        StringBuilder buffer = new("\U0001D400");

        Assert.Equal(1, CliLineReader.ClearLine(buffer));

    }

    [Fact]
    public void ClearLine_on_an_empty_buffer_erases_nothing()
    {

        StringBuilder buffer = new();

        Assert.Equal(0, CliLineReader.ClearLine(buffer));

    }

    [Fact]
    public void DeleteLastWord_erases_the_columns_the_word_painted()
    {

        StringBuilder buffer = new("hi 世界");

        int erased = CliLineReader.DeleteLastWord(buffer);

        Assert.Equal(4, erased);

        Assert.Equal("hi ", buffer.ToString());

    }

    [Fact]
    public void EraseLastCharacter_erases_both_columns_of_a_wide_glyph()
    {

        StringBuilder buffer = new("a好");

        int erased = CliLineReader.EraseLastCharacter(buffer);

        Assert.Equal(2, erased);

        Assert.Equal("a", buffer.ToString());

    }

    [Fact]
    public void EraseLastCharacter_erases_one_column_for_a_narrow_astral_character()
    {

        StringBuilder buffer = new("a\U0001D400");

        int erased = CliLineReader.EraseLastCharacter(buffer);

        Assert.Equal(1, erased);

        Assert.Equal("a", buffer.ToString());

    }

    [Theory]
    [InlineData("campaign", CliContextScope.Campaign)]
    [InlineData("WORKSPACE", CliContextScope.Workspace)]
    [InlineData(" model ", CliContextScope.Model)]
    public void TryParseScope_accepts_documented_scope_names(string value, CliContextScope expected)
    {

        Assert.True(CliCommandTree.TryParseScope(value, out CliContextScope scope));

        Assert.Equal(expected, scope);

    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("0")]
    [InlineData("campaign,workspace")]
    [InlineData("bogus")]
    public void TryParseScope_rejects_numeric_and_flag_list_spellings(string value)
    {

        Assert.False(CliCommandTree.TryParseScope(value, out _));

    }

    [Fact]
    public void TryParseScope_treats_omission_as_all()
    {

        Assert.True(CliCommandTree.TryParseScope(null, out CliContextScope scope));

        Assert.Equal(CliContextScope.All, scope);

    }

    [Fact]
    public void ResolveProcessTerminationTimeout_is_finite_for_ordinary_commands()
    {

        ParseResult parsed = BuildProbeRoot().Parse(["run", "hello"]);

        TimeSpan? timeout = CliApplicationFactory.ResolveProcessTerminationTimeout(parsed);

        Assert.NotNull(timeout);

        Assert.NotEqual(Timeout.InfiniteTimeSpan, timeout!.Value);

        Assert.True(timeout.Value > TimeSpan.Zero);

    }

    /// <summary>
    /// No parsed verb currently owns Ctrl+C: Command Center is entered before parsing, and the
    /// frameless REPL that used to claim it is gone. Every parsed command therefore gets the
    /// standard cooperative-shutdown grace window.
    /// </summary>
    [Fact]
    public void ResolveProcessTerminationTimeout_applies_to_every_parsed_command()
    {

        ParseResult parsed = BuildProbeRoot().Parse(["run", "hello"]);

        Assert.Equal(
            CliApplicationFactory.ProcessTerminationGrace,
            CliApplicationFactory.ResolveProcessTerminationTimeout(parsed));

    }

    [Theory]
    [InlineData(new[] { "doctor" }, true)]
    [InlineData(new[] { "config", "validate" }, true)]
    [InlineData(new[] { "run", "--help" }, true)]
    [InlineData(new[] { "--version" }, true)]
    [InlineData(new[] { "run", "hello" }, false)]
    [InlineData(new string[0], false)]
    public void AllowsDegradedConfiguration_keeps_only_repair_paths_alive(string[] args, bool expected)
    {

        Assert.Equal(expected, CliBootstrapDiagnostics.AllowsDegradedConfiguration(args));

    }

    [Fact]
    public void DescribeBootstrapFailure_names_the_file_and_the_remedy()
    {

        string message = CliBootstrapDiagnostics.DescribeBootstrapFailure(
            new InvalidOperationException("arcanum.json is invalid: TrailingCommaNotAllowedBeforeObjectEnd"),
            "/tmp/probe/arcanum.json");

        Assert.Contains("TrailingCommaNotAllowedBeforeObjectEnd", message, StringComparison.Ordinal);

        Assert.Contains("/tmp/probe/arcanum.json", message, StringComparison.Ordinal);

        Assert.Contains("arcanum config edit", message, StringComparison.Ordinal);

        Assert.Contains("arcanum doctor", message, StringComparison.Ordinal);

    }

    [Fact]
    public void Map_renders_configuration_validation_detail_instead_of_the_catch_all()
    {

        ConfigurationValidationException exception = new(
            new Error(
                "configuration.invalid",
                "Arcanum configuration is invalid.",
                [new ConfigurationValidationError("defaultModel", "DefaultModel 'gpt-4o' does not match any configured provider model.")]));

        CliFailure failure = CliFailureMapper.Map(exception);

        Assert.Equal(CliExitCode.ConfigurationError, failure.ExitCode);

        Assert.DoesNotContain("An unexpected CLI error occurred.", failure.SafeMessage, StringComparison.Ordinal);

        Assert.Contains("defaultModel", failure.SafeMessage, StringComparison.Ordinal);

        Assert.Contains("does not match any configured provider model.", failure.SafeMessage, StringComparison.Ordinal);

    }

    private static RootCommand BuildProbeRoot()
    {

        Command ask = new("ask", "probe");

        Command chat = new("chat", "probe");

        RootCommand root = new("probe");

        root.Add(ask);

        root.Add(chat);

        return root;

    }

    [Fact]
    public void BuildDurableOperationsCheck_reports_state_when_the_host_is_reachable()
    {

        DoctorCheck check = DoctorCommand.BuildDurableOperationsCheck(
            hostReachable: true,
            detail: "2 stale leases; 1 awaiting reconciliation.");

        Assert.Equal("DurableOperations", check.Name);

        Assert.Equal("ok", check.Status);

        Assert.Contains("awaiting reconciliation", check.Detail!, StringComparison.Ordinal);

        Assert.Contains("arcanum operation list", check.Detail!, StringComparison.Ordinal);

    }

    [Fact]
    public void BuildDurableOperationsCheck_degrades_to_a_warning_when_the_host_is_unreachable()
    {

        DoctorCheck check = DoctorCommand.BuildDurableOperationsCheck(hostReachable: false, detail: null);

        Assert.Equal("DurableOperations", check.Name);

        Assert.Equal("warn", check.Status);

        Assert.Contains("arcanum serve", check.Detail!, StringComparison.Ordinal);

    }

}
