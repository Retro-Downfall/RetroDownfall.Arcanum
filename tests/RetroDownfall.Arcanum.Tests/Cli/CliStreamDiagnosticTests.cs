using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using Spectre.Console.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Every diagnostic the streaming loop writes to stderr lays out from column 0 at
/// <c>Profile.Width</c>, while the answer stream writes raw tokens to stdout with no newline between
/// them. When both land on the same terminal, a diagnostic emitted part-way through an answer starts
/// from whatever column the last token left the caret on and wraps against a margin that is not
/// there. The reasoning panel is only the most visible instance of that class.
/// </summary>
public sealed class CliStreamDiagnosticTests
{

    [Fact]
    public void A_diagnostic_returns_the_answer_stream_to_column_zero_first()
    {

        CliStreamContent content = new();

        content.AppendAnswer("The answer is ");

        StringWriter answer = new();

        TestConsole diagnostics = new();

        CliStreamDiagnostic.WriteMarkupLine(
            diagnostics,
            content,
            "tool failed",
            answer,
            answerSharesTerminal: true);

        Assert.Equal(System.Environment.NewLine, answer.ToString());

        Assert.Contains("tool failed", diagnostics.Output, StringComparison.Ordinal);

    }

    /// <summary>
    /// The break belongs to the terminal, not to the payload: a redirected stdout must receive
    /// exactly the bytes the model produced.
    /// </summary>
    [Fact]
    public void A_diagnostic_leaves_a_redirected_answer_stream_untouched()
    {

        CliStreamContent content = new();

        content.AppendAnswer("The answer is ");

        StringWriter answer = new();

        CliStreamDiagnostic.WriteMarkupLine(
            new TestConsole(),
            content,
            "tool failed",
            answer,
            answerSharesTerminal: false);

        Assert.Equal(string.Empty, answer.ToString());

    }

    [Fact]
    public void A_diagnostic_does_not_break_a_line_the_answer_already_ended()
    {

        CliStreamContent content = new();

        content.AppendAnswer("The answer is complete.\n");

        StringWriter answer = new();

        CliStreamDiagnostic.WriteMarkupLine(
            new TestConsole(),
            content,
            "tool failed",
            answer,
            answerSharesTerminal: true);

        Assert.Equal(string.Empty, answer.ToString());

    }

    /// <summary>
    /// A tool round writes a status line and then flushes reasoning. Both lay out from column 0, so
    /// both consult the same record of where the answer left the caret — one break, not two blank
    /// rows in the transcript.
    /// </summary>
    [Fact]
    public void A_diagnostic_and_a_following_reasoning_flush_break_the_answer_line_once()
    {

        CliStreamContent content = new();

        content.AppendAnswer("The answer is ");

        StringWriter answer = new();

        CliStreamDiagnostic.WriteMarkupLine(
            new TestConsole(),
            content,
            "calling a tool",
            answer,
            answerSharesTerminal: true);

        Assert.True(content.AppendReasoning(new IntelligenceEvent(
            IntelligenceEventType.Reasoning,
            "because",
            Reasoning: new ReasoningContentSegment("because", ReasoningOutputMode.Summary))));

        _ = EphemeralReasoningRenderer.Flush(
            new TestConsole().Width(40),
            content,
            new CliStreamDiagnosticTheme(),
            answer,
            answerSharesTerminal: true);

        Assert.Equal(System.Environment.NewLine, answer.ToString());

    }

    private sealed class CliStreamDiagnosticTheme : IThemePalette
    {

        public Spectre.Console.Color Text { get; } = Spectre.Console.Color.White;

        public Spectre.Console.Color Heading { get; } = Spectre.Console.Color.White;

        public Spectre.Console.Color Highlight { get; } = Spectre.Console.Color.White;

        public Spectre.Console.Color Error { get; } = Spectre.Console.Color.Red;

        public Spectre.Console.Color Muted { get; } = Spectre.Console.Color.Grey;

    }

}
