using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Cli;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Topic help is what makes the thematic vocabulary navigable. It has to answer "how do I do X"
/// for someone who has never read the metaphor table.
/// </summary>
[Collection("GlobalConsole")]
public sealed class CliHelpTopicTests
{

    public static TheoryData<string> Topics => [.. HelpTopics.Names];

    [Theory]
    [MemberData(nameof(Topics))]
    public void Each_topic_renders_terms_and_commands(string topic)
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "help", topic);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("Terms:", result.Output, StringComparison.Ordinal);

        Assert.Contains("Commands:", result.Output, StringComparison.Ordinal);

        Assert.Contains("arcanum ", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Bare_help_lists_every_topic()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "help");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        foreach (string topic in HelpTopics.Names)
        {

            Assert.Contains(topic, result.Output, StringComparison.Ordinal);

        }

    }

    [Fact]
    public void An_unknown_topic_lists_the_available_topics()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "help", "quantum");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

    }

    /// <summary>
    /// The point of a topic is the plain-language gloss. A topic whose terms are only restated
    /// thematically would help nobody, so each entry must carry an explanation after the term.
    /// </summary>
    [Fact]
    public void Every_topic_glosses_its_thematic_terms_in_plain_language()
    {

        foreach (HelpTopic topic in HelpTopics.All)
        {

            Assert.NotEmpty(topic.Glossary);

            Assert.NotEmpty(topic.Body);

            Assert.NotEmpty(topic.Commands);

            foreach (string term in topic.Glossary)
            {

                string[] parts = term.Split("  ", 2, StringSplitOptions.RemoveEmptyEntries);

                Assert.True(
                    parts.Length == 2 && parts[1].Trim().Length > 15,
                    $"{topic.Name}: '{term}' does not explain the term.");

            }

        }

    }

    /// <summary>
    /// Topic commands are advertised as runnable, so they carry the same parse obligation as the
    /// per-command examples: every one must still parse against the live tree, so a renamed verb
    /// breaks the build instead of sending an operator to a command that exits 2.
    /// </summary>
    [Fact]
    public void Every_topic_command_is_a_real_arcanum_invocation()
    {

        ServiceCollection services = CreateServices();

        using ServiceProvider provider = services.BuildServiceProvider();

        RootCommand root = CliCommandTree.Build(provider, out _);

        List<string> broken = [];

        foreach (HelpTopic topic in HelpTopics.All)
        {

            foreach (string command in topic.Commands)
            {

                Assert.Contains("arcanum", command, StringComparison.Ordinal);

                // Must match production exactly: RunAsync disables response-file expansion, so a
                // documented `@path` value is application syntax rather than a token replacer.
                ParseResult parsed = root.Parse(
                    CliSuggestionTests.Tokenize(command),
                    new ParserConfiguration { ResponseFileTokenReplacer = null });

                if (parsed.Errors.Count > 0)
                {

                    broken.Add($"[{topic.Name}] {command} -> {parsed.Errors[0].Message}");

                }

            }

        }

        Assert.True(broken.Count == 0, string.Join("\n", broken));

    }

    /// <summary>
    /// A doctor selector parses as free text, so a topic can advertise `--only &lt;subsystem&gt;` for a
    /// subsystem that does not exist and still look correct to the parser. The runner rejects it at
    /// execution time with exit 2, so the vocabulary has to be checked here instead.
    /// </summary>
    [Fact]
    public void Every_advertised_doctor_selector_names_a_real_subsystem()
    {

        HashSet<string> subsystems =
        [
            .. Enum.GetNames<DoctorSubsystem>(),
        ];

        List<string> unknown = [];

        foreach (HelpTopic topic in HelpTopics.All)
        {

            foreach (string command in topic.Commands)
            {

                string[] tokens = CliSuggestionTests.Tokenize(command);

                for (int index = 0; index < tokens.Length - 1; index++)
                {

                    if (tokens[index] is not ("--only" or "--skip"))
                    {

                        continue;

                    }

                    string selector = tokens[index + 1];

                    string ns = selector.Split('.', 2)[0];

                    if (!subsystems.Contains(ns, StringComparer.OrdinalIgnoreCase))
                    {

                        unknown.Add($"[{topic.Name}] {command} -> '{selector}' is not a DoctorSubsystem.");

                    }

                }

            }

        }

        Assert.True(unknown.Count == 0, string.Join("\n", unknown));

    }

    private static ServiceCollection CreateServices()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        return services;

    }

}
