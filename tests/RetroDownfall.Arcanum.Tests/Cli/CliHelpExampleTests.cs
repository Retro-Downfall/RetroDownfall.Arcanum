using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Examples are only useful where an operator already is. Keeping them in the command map but out
/// of <c>--help</c> would document them for tooling and hide them from people.
/// </summary>
[Collection("GlobalConsole")]
public sealed class CliHelpExampleTests
{

    [Fact]
    public void Command_help_renders_the_examples_for_that_command()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "run", "--help");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("Examples:", result.Output, StringComparison.Ordinal);

        Assert.Contains("arcanum run -c \"and now summarize that\"", result.Output, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData("session", "show")]
    [InlineData("watch", "health")]
    [InlineData("completion", "install")]
    public void Nested_command_help_renders_its_own_examples(string family, string verb)
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), family, verb, "--help");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("Examples:", result.Output, StringComparison.Ordinal);

        foreach (string example in CliSurfaceExamples.For($"{family} {verb}"))
        {

            Assert.Contains(example, result.Output, StringComparison.Ordinal);

        }

    }

    /// <summary>
    /// A grouping command has no examples of its own. It must not print an empty heading.
    /// </summary>
    [Fact]
    public void A_command_without_examples_prints_no_examples_heading()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "session", "--help");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.DoesNotContain("Examples:", result.Output, StringComparison.Ordinal);

    }

    /// <summary>
    /// A command that records why it has no safe example says so where the operator is looking,
    /// rather than appearing to have simply been forgotten.
    /// </summary>
    [Fact]
    public void A_command_exempt_from_examples_explains_why_in_its_help()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "key", "show", "--help");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("No example is shown", result.Output, StringComparison.Ordinal);

        Assert.Contains(
            CliSurfaceExamples.ExemptionReason("key show")!,
            result.Output,
            StringComparison.Ordinal);

    }

    /// <summary>
    /// Help is a payload, so it belongs on stdout and must not leak into the diagnostic stream.
    /// </summary>
    [Fact]
    public void Examples_are_written_to_stdout_with_the_rest_of_help()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "doctor", "--help");

        Assert.Contains("Examples:", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("Examples:", result.Error, StringComparison.Ordinal);

    }

    /// <summary>
    /// Help is what the parser resolved, not what argv happens to spell. <c>run</c>'s variadic
    /// prompt absorbs everything after <c>--</c>, so a prompt containing a bare <c>-h</c> is an
    /// ordinary turn — appending an <c>Examples:</c> block to it concatenates six lines of prose
    /// onto the command's own output, which under <c>--json</c> means stdout carries a JSON
    /// document followed by text no consumer can parse.
    /// </summary>
    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("-?")]
    public void An_escaped_help_shaped_prompt_token_renders_no_examples(string token)
    {

        string[] arguments = ["run", "--json", "--", token];

        Assert.Equal(string.Empty, AppendedExamples(arguments));

    }

    /// <summary>
    /// The other half: an actual <c>--help</c> still renders, so the gate cannot be tightened into
    /// silence.
    /// </summary>
    [Fact]
    public void A_real_help_request_still_renders_the_examples()
    {

        Assert.Contains("Examples:", AppendedExamples(["run", "--help"]), StringComparison.Ordinal);

    }

    /// <summary>
    /// The text <see cref="CliHelpExamples.Append"/> writes for one argv, isolated from the rest of
    /// the invocation so the block is judged on its own.
    /// </summary>
    private static string AppendedExamples(string[] arguments)
    {

        using ServiceProvider provider = CreateServices().BuildServiceProvider();

        RootCommand root = CliCommandTree.Build(provider, out _);

        StringWriter output = new();

        CliHelpExamples.Append(root, root.Parse(arguments), output);

        return output.ToString();

    }

    private static ServiceCollection CreateServices()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        return services;

    }

}
