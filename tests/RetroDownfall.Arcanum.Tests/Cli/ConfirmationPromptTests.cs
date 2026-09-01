using RetroDownfall.Arcanum.Cli.Infrastructure;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The confirmation an automation mode cannot answer.
/// </summary>
/// <remarks>
/// The stream probes are pinned to "nothing is redirected" on purpose: that is the attached terminal
/// a CI wrapper allocating a pty presents, and it is the one configuration in which the prompt used
/// to reach <c>Console.In</c>. A test that redirected a stream would pass on the condition that was
/// already checked and prove nothing about the structured-output modes.
/// </remarks>
public sealed class ConfirmationPromptTests
{

    private static CancellationToken Token => CancellationToken.None;

    /// <summary>
    /// A structured-output mode refuses instead of asking a question nobody is there to answer.
    /// </summary>
    /// <remarks>
    /// Both modes, because <c>--print</c> is the worse of the two: the reference states that under it
    /// "a real terminal behaves like a redirected one: no interactive picker opens, no prompt blocks",
    /// and every confirming verb blocked under it. The reader holds a "y" so a prompt that still asked
    /// would return <c>true</c> rather than fail for the unrelated reason that input ran out.
    /// </remarks>
    [Theory]

    [InlineData(true, false)]

    [InlineData(false, true)]

    public async Task A_structured_output_mode_refuses_rather_than_prompting(bool json, bool print)
    {

        ConfirmationPrompt prompt = Prompt(
            new CliInvocationOptions(json, Plain: false, Yes: false, Print: print),
            "y");

        await Assert.ThrowsAsync<NonInteractiveConfirmationException>(
            () => prompt.PromptForConfirmationAsync("Write this Covenant entry?", Token));

    }

    /// <summary>
    /// The refusal is about the missing answer, not about the mode.
    /// </summary>
    /// <remarks>
    /// <c>--yes</c> is the answer the documentation tells an operator to supply, so a guard that
    /// refused every <c>--json</c> confirmation would make the documented automation spelling
    /// impossible. Reading nothing proves the approval never touched the reader.
    /// </remarks>
    [Fact]
    public async Task A_structured_output_mode_that_carries_yes_still_approves()
    {

        ConfirmationPrompt prompt = Prompt(
            new CliInvocationOptions(Json: true, Plain: false, Yes: true, Print: true),
            string.Empty);

        Assert.True(await prompt.PromptForConfirmationAsync("Write this Covenant entry?", Token));

    }

    /// <summary>
    /// An attached terminal outside both modes is still asked.
    /// </summary>
    /// <remarks>
    /// The other half of the pair. Without it a fix that threw on every unredirected prompt would
    /// satisfy the refusal tests above while removing the interactive confirmation entirely.
    /// </remarks>
    [Fact]
    public async Task An_attached_terminal_outside_the_structured_modes_is_still_asked()
    {

        ConfirmationPrompt prompt = Prompt(
            new CliInvocationOptions(Json: false, Plain: false, Yes: false),
            "y");

        Assert.True(await prompt.PromptForConfirmationAsync("Write this Covenant entry?", Token));

    }

    private static ConfirmationPrompt Prompt(CliInvocationOptions options, string input) =>
        new(
            new ConsoleDispatcher(new StringWriter(), new StringWriter(), options),
            options,
            new StringReader(input),
            isOutputRedirected: static () => false,
            isInputRedirected: static () => false);

}
