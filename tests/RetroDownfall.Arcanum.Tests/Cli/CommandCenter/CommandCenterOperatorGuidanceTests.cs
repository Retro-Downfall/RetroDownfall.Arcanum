using RetroDownfall.Arcanum.Cli.CommandCenter;

using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// Command Center only ever tells the operator to run a spelling the CLI still parses. Arcanum
/// ships no compatibility aliases, so a message naming a removed command is a dead end that the
/// suggestion engine has to clean up after.
/// </summary>
public sealed class CommandCenterOperatorGuidanceTests
{

    public static TheoryData<string> HostDiagnostics =>
    [
        CommandCenterHost.DescribeTerminalTooSmall(70, 20),
        CommandCenterHost.StartFailureMessage,
    ];

    [Theory]
    [MemberData(nameof(HostDiagnostics))]
    public void Host_diagnostics_never_name_a_removed_command(string message)
    {

        foreach (string removed in CliSuggestionEngine.RemovedSpellings)
        {

            Assert.DoesNotContain(
                $"arcanum {removed}",
                message,
                StringComparison.OrdinalIgnoreCase);

        }

    }

    [Fact]
    public void Host_diagnostics_name_a_live_direct_command()
    {

        Assert.Contains(
            "arcanum run",
            CommandCenterHost.DescribeTerminalTooSmall(70, 20),
            StringComparison.Ordinal);

        Assert.Contains(
            "arcanum run",
            CommandCenterHost.StartFailureMessage,
            StringComparison.Ordinal);

    }

    /// <summary>The size gate must still lead with the remedy that actually applies.</summary>
    [Fact]
    public void The_size_gate_reports_the_detected_and_required_viewport()
    {

        string message = CommandCenterHost.DescribeTerminalTooSmall(70, 20);

        Assert.Contains("70x20", message, StringComparison.Ordinal);

        Assert.Contains(
            $"{CommandCenterApp.MinCols}x{CommandCenterApp.MinRows}",
            message,
            StringComparison.Ordinal);

        Assert.Contains("Resize the terminal", message, StringComparison.Ordinal);

    }

    /// <summary>
    /// The F1 overlay's slash summary is the operator's in-app command list, so every spelling it
    /// prints has to survive the parser rather than dead-ending on a removed form.
    /// </summary>
    [Fact]
    public void The_F1_slash_summary_lists_only_spellings_the_parser_accepts()
    {

        AssertEveryDocumentedFormParses(CommandCenterHost.HelpOverlaySlashSummary);

    }

    [Fact]
    public void The_canonical_resume_usage_is_accepted_by_the_parser()
    {

        AssertEveryDocumentedFormParses(ShellCommandDispatcher.ResumeUsage);

    }

    public static TheoryData<string> ResumeHints =>
    [
        ShellCommandDispatcher.ResumeUsageMessage,
        ShellCommandDispatcher.SessionListResumeHint,
        ShellCommandDispatcher.NoActiveSessionMessage,
    ];

    /// <summary>
    /// Every hint that tells the operator how to resume is built from the one canonical usage, so a
    /// removed spelling cannot survive in a corner of the UI the way `/session resume` did.
    /// </summary>
    [Theory]
    [MemberData(nameof(ResumeHints))]
    public void Every_resume_hint_quotes_the_canonical_usage(string hint)
    {

        Assert.Contains(ShellCommandDispatcher.ResumeUsage, hint, StringComparison.Ordinal);

        Assert.DoesNotContain("/session resume", hint, StringComparison.Ordinal);

    }

    public static TheoryData<string> PinUsages =>
    [
        ShellCommandDispatcher.PinUsage,
        ShellCommandDispatcher.UnpinUsage,
    ];

    /// <summary>
    /// The pin verbs' failure messages are the only place they tell the operator how to spell them,
    /// so they get replayed through the parser like every other piece of guidance.
    /// </summary>
    [Theory]
    [MemberData(nameof(PinUsages))]
    public void Every_pin_usage_is_accepted_by_the_parser(string usage)
    {

        AssertEveryDocumentedFormParses(usage);

    }

    /// <summary>
    /// <c>/context unpin &lt;id&gt;</c> is a removed spelling, so a failure message recommending it
    /// sends the operator to a form the parser rejects.
    /// </summary>
    [Fact]
    public void The_pin_failure_messages_quote_the_canonical_usage()
    {

        Assert.Contains(
            ShellCommandDispatcher.UnpinUsage,
            ShellCommandDispatcher.UnpinUsageMessage,
            StringComparison.Ordinal);

        Assert.Contains(
            ShellCommandDispatcher.PinUsage,
            ShellCommandDispatcher.PinUsageMessage,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "/context",
            ShellCommandDispatcher.UnpinUsageMessage,
            StringComparison.Ordinal);

    }

    private const string SampleId = "11111111-1111-1111-1111-111111111111";

    private static void AssertEveryDocumentedFormParses(string helpText)
    {

        ShellCommandParser parser = new();

        IReadOnlyList<string> forms = SlashUsageExpander.Expand(helpText, SampleId);

        Assert.NotEmpty(forms);

        foreach (string form in forms)
        {

            ParsedShellCommand parsed = parser.Parse(form);

            Assert.True(
                parsed.Kind is not (ShellCommandKind.Denied or ShellCommandKind.Unknown),
                $"`{helpText}` documents `{form}`, which the parser rejects: {parsed.DenialMessage}");

        }

    }

}

/// <summary>
/// Turns a documented slash usage such as <c>/session list|archive &lt;id&gt;</c> into the concrete
/// command lines it advertises, so help prose can be replayed through the real parser.
/// </summary>
internal static class SlashUsageExpander
{

    public static IReadOnlyList<string> Expand(string helpText, string sampleId)
    {

        int start = helpText.IndexOf('/', StringComparison.Ordinal);

        if (start < 0)
        {

            return [];

        }

        List<string> forms = [];

        foreach (string fragment in helpText[start..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {

            string body = fragment.Trim();

            if (body.Length == 0)
            {

                continue;

            }

            forms.AddRange(ExpandOne(body, sampleId));

        }

        return forms;

    }

    /// <summary>Drops <c>[optional]</c> groups; they document forms the parser also accepts without.</summary>
    public static string StripOptionalGroups(string usage)
    {

        System.Text.StringBuilder builder = new(usage.Length);

        int depth = 0;

        foreach (char c in usage)
        {

            if (c == '[')
            {

                depth++;

                continue;

            }

            if (c == ']')
            {

                depth = Math.Max(0, depth - 1);

                continue;

            }

            if (depth == 0)
            {

                _ = builder.Append(c);

            }

        }

        return builder.ToString();

    }

    private static IEnumerable<string> ExpandOne(string body, string sampleId)
    {

        string[] tokens = StripOptionalGroups(body)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {

            yield break;

        }

        int alternation = Array.FindIndex(
            tokens,
            static token => token.Contains('|', StringComparison.Ordinal));

        string[] choices = alternation < 0
            ? [string.Empty]
            : tokens[alternation].Split('|', StringSplitOptions.RemoveEmptyEntries);

        foreach (string choice in choices)
        {

            string[] variant = (string[])tokens.Clone();

            if (alternation >= 0)
            {

                variant[alternation] = choice;

            }

            for (int i = 0; i < variant.Length; i++)
            {

                if (variant[i].StartsWith('<'))
                {

                    variant[i] = sampleId;

                }

            }

            yield return "/" + string.Join(' ', variant);

        }

    }

}
