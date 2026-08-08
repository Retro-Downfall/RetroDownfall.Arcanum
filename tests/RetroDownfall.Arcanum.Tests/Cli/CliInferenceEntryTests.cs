using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Arcanum has exactly two turn entry points: bare <c>arcanum</c> (Command Center) for interactive
/// use and <c>arcanum run</c> for one-shot/headless use. <c>ask</c> and <c>chat</c> were parallel
/// implementations of the same turn and are gone; nothing may reintroduce a third path.
/// </summary>
[Collection("GlobalConsole")]
public sealed class CliInferenceEntryTests
{

    [Theory]
    [InlineData("ask")]
    [InlineData("chat")]
    public void Removed_inference_verbs_do_not_parse(string verb)
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), verb, "hello");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

    }

    [Fact]
    public void Session_chat_is_removed_from_the_management_tree()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "session", "chat");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

    }

    [Fact]
    public void Run_publishes_the_claude_aligned_continuation_flags()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "run", "--help");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("--continue", result.Output, StringComparison.Ordinal);

        Assert.Contains("--resume", result.Output, StringComparison.Ordinal);

        Assert.Contains("--print", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Run_keeps_campaign_on_the_uppercase_short_flag()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "run", "--help");

        Assert.Contains("-C, --campaign", result.Output, StringComparison.Ordinal);

        Assert.Contains("-c, --continue", result.Output, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData("--continue", "--resume", "abc")]
    [InlineData("--continue", "--session", "abc")]
    [InlineData("--session", "abc", "--resume", "def")]
    public void Combining_session_selectors_is_a_command_line_error(params string[] arguments)
    {

        CliTestResult result = CliTestHarness.Run(
            CreateServices(),
            ["run", .. arguments, "hello"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("select a session", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Center_accepts_continuation_flags_for_the_interactive_entry()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "center", "--help");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("--continue", result.Output, StringComparison.Ordinal);

        Assert.Contains("--resume", result.Output, StringComparison.Ordinal);

    }

    private static ServiceCollection CreateServices()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        return services;

    }

}
