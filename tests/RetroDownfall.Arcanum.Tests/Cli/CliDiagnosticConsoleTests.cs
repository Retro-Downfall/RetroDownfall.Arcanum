using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Commands.Tower;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Every writer to standard error answers the same colour opt-outs, on a CI runner as well as on a
/// terminal. <c>--plain</c>, <c>--json</c>, <c>--print</c>, <c>NO_COLOR</c> and
/// <c>ARCANUM_NO_COLOR</c> reached the themed error writer, but the tool-call summary and the
/// <c>ask_human</c> diagnostics each built a default console that ignored all five.
/// </summary>
/// <remarks>
/// <c>GITHUB_ACTIONS=true</c> is set for the duration of each case, so the provider detector that
/// forces ANSI on is live here whether or not this machine is a runner; the control case proves it.
/// The collection is <c>ProcessEnvironment</c> because the opt-out variables are read live from the
/// process environment, and it disables parallelization, which also makes the
/// <see cref="Console.Error"/> swap safe.
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class CliDiagnosticConsoleTests
{

    private const string Markup = "[bold]diagnostic words[/]";

    [Fact]
    public void Control_a_stream_with_no_opt_out_is_forced_to_ansi_by_the_GitHub_Actions_detector()
    {

        string written = CaptureError(optOut: null, static () => CliErrorOutput.WriteMarkupLine(Markup));

        Assert.Contains('\u001b', written);

    }

    [Theory]
    [InlineData("plain")]
    [InlineData("json")]
    [InlineData("print")]
    [InlineData("no-color")]
    [InlineData("arcanum-no-color")]
    public void The_themed_error_writer_stays_free_of_ansi_inside_GitHub_Actions(string optOut)
    {

        string written = CaptureError(optOut, static () => CliErrorOutput.WriteMarkupLine(Markup));

        Assert.Contains("diagnostic words", written, StringComparison.Ordinal);

        Assert.DoesNotContain('\u001b', written);

    }

    [Theory]
    [InlineData("plain")]
    [InlineData("json")]
    [InlineData("print")]
    [InlineData("no-color")]
    [InlineData("arcanum-no-color")]
    public void The_ask_human_diagnostic_console_honours_the_opt_outs_inside_GitHub_Actions(string optOut)
    {

        string written = CaptureError(
            optOut,
            static () => ConsoleAskHumanCoordinator.CreateStandardErrorConsole().MarkupLine(Markup));

        Assert.Contains("diagnostic words", written, StringComparison.Ordinal);

        Assert.DoesNotContain('\u001b', written);

    }

    [Theory]
    [InlineData("plain")]
    [InlineData("json")]
    [InlineData("print")]
    [InlineData("no-color")]
    [InlineData("arcanum-no-color")]
    public void The_tool_call_summary_honours_the_opt_outs_inside_GitHub_Actions(string optOut)
    {

        PromptResponseDto response = new(
            "done",
            null,
            [new PromptToolCall("call-1", "read_file", "{\"path\":\"a.txt\"}")]);

        IThemePalette palette = new ConfiguredThemePalette(new ThemeSemanticColors(), new ThemeSemanticColors());

        string written = CaptureError(
            optOut,
            () => ExecuteResultRendering.WriteExecuteResultAsync(response, palette).GetAwaiter().GetResult());

        Assert.Contains("Tool calls during execution:", written, StringComparison.Ordinal);

        Assert.Contains("read_file", written, StringComparison.Ordinal);

        Assert.DoesNotContain('\u001b', written);

    }

    [Fact]
    public void Control_the_tool_call_summary_is_styled_when_nothing_opts_out()
    {

        PromptResponseDto response = new(
            "done",
            null,
            [new PromptToolCall("call-1", "read_file", "{\"path\":\"a.txt\"}")]);

        IThemePalette palette = new ConfiguredThemePalette(new ThemeSemanticColors(), new ThemeSemanticColors());

        string written = CaptureError(
            optOut: null,
            () => ExecuteResultRendering.WriteExecuteResultAsync(response, palette).GetAwaiter().GetResult());

        Assert.Contains('\u001b', written);

    }

    /// <summary>
    /// Runs <paramref name="write"/> inside GitHub Actions with the named opt-out in force and returns
    /// what reached standard error. Standard output is swapped too, because the tool-call summary
    /// writes the response text there.
    /// </summary>
    private static string CaptureError(string? optOut, Action write)
    {

        using EnvironmentScope environment = new(
            gitHubActions: "true",
            noColor: optOut == "no-color" ? "1" : null,
            arcanumNoColor: optOut == "arcanum-no-color" ? "1" : null);

        using IDisposable invocation = CliInvocationContext.Push(
            new CliInvocationOptions(
                Json: optOut == "json",
                Plain: optOut == "plain",
                Yes: false,
                Print: optOut == "print"));

        TextWriter originalOut = Console.Out;

        TextWriter originalError = Console.Error;

        StringWriter captured = new();

        try
        {

            Console.SetOut(new StringWriter());

            Console.SetError(captured);

            write();

        }
        finally
        {

            Console.SetOut(originalOut);

            Console.SetError(originalError);

        }

        return captured.ToString();

    }

    private sealed class EnvironmentScope : IDisposable
    {

        private readonly string? _originalGitHubActions = System.Environment.GetEnvironmentVariable("GITHUB_ACTIONS");

        private readonly string? _originalNoColor = System.Environment.GetEnvironmentVariable("NO_COLOR");

        private readonly string? _originalArcanumNoColor =
            System.Environment.GetEnvironmentVariable("ARCANUM_NO_COLOR");

        internal EnvironmentScope(string? gitHubActions, string? noColor, string? arcanumNoColor)
        {

            System.Environment.SetEnvironmentVariable("GITHUB_ACTIONS", gitHubActions);

            System.Environment.SetEnvironmentVariable("NO_COLOR", noColor);

            System.Environment.SetEnvironmentVariable("ARCANUM_NO_COLOR", arcanumNoColor);

        }

        public void Dispose()
        {

            System.Environment.SetEnvironmentVariable("GITHUB_ACTIONS", _originalGitHubActions);

            System.Environment.SetEnvironmentVariable("NO_COLOR", _originalNoColor);

            System.Environment.SetEnvironmentVariable("ARCANUM_NO_COLOR", _originalArcanumNoColor);

        }

    }

}
