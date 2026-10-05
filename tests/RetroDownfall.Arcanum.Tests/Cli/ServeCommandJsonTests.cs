using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// R-334: <c>serve</c> runs until it is stopped and has no result document, so under <c>--json</c> the
/// invocation's deferred stdout capture would hold every line it writes (a freshly generated master
/// API key included) until the host exits. It is refused up front instead, before any host state is
/// touched. The guard is a separate step so it can be pinned without starting a host.
/// </summary>
[Collection("GlobalConsole")]
public sealed class ServeCommandJsonTests
{
    [Fact]
    public void Json_output_is_refused_with_the_configuration_exit_code_and_a_diagnostic_on_stderr()
    {
        TextWriter priorOut = Console.Out;

        TextWriter priorError = Console.Error;

        StringWriter capturedOut = new();

        StringWriter capturedError = new();

        Console.SetOut(capturedOut);

        Console.SetError(capturedError);

        try
        {
            ServeCommand command = CreateCommand();

            int? refusal = command.RefuseJsonOutput(
                new CliInvocationOptions(Json: true, Plain: false, Yes: false));

            Assert.Equal((int)CliExitCode.ConfigurationError, refusal);

            Assert.Contains("--json", capturedError.ToString(), StringComparison.Ordinal);

            Assert.Contains("serve", capturedError.ToString(), StringComparison.Ordinal);

            Assert.Empty(capturedOut.ToString());
        }
        finally
        {
            Console.SetOut(priorOut);

            Console.SetError(priorError);
        }
    }

    /// <summary>
    /// R-334 (wiring): the guard is not only correct, it is the first thing <c>Run</c> does. Under
    /// <c>--json</c> the host is never started, so the generated master API key cannot be buffered behind a
    /// capture that waits for the host to exit. The host is a recording stand-in, so deleting the guard
    /// fails this test at once rather than starting a Kestrel host in the test process.
    /// </summary>
    [Fact]
    public async Task Run_under_json_returns_the_refusal_without_starting_the_host()
    {
        TextWriter priorError = Console.Error;

        Console.SetError(new StringWriter());

        try
        {
            ServeCommand command = CreateCommand();

            bool hostStarted = false;

            using IDisposable invocation = CliInvocationContext.Push(
                new CliInvocationOptions(Json: true, Plain: false, Yes: false));

            int exitCode = await command.Run(
                CancellationToken.None,
                _ =>
                {
                    hostStarted = true;

                    return Task.FromResult(0);
                });

            Assert.Equal((int)CliExitCode.ConfigurationError, exitCode);

            Assert.False(hostStarted, "serve --json started the host.");
        }
        finally
        {
            Console.SetError(priorError);
        }
    }

    [Fact]
    public async Task Run_without_json_starts_the_host_and_returns_its_exit_code()
    {
        ServeCommand command = CreateCommand();

        bool hostStarted = false;

        using IDisposable invocation = CliInvocationContext.Push(
            new CliInvocationOptions(Json: false, Plain: false, Yes: false));

        int exitCode = await command.Run(
            CancellationToken.None,
            _ =>
            {
                hostStarted = true;

                return Task.FromResult(7);
            });

        Assert.True(hostStarted);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public void Text_output_is_allowed_to_start_the_host()
    {
        ServeCommand command = CreateCommand();

        Assert.Null(
            command.RefuseJsonOutput(
                new CliInvocationOptions(Json: false, Plain: false, Yes: false)));
    }

    private static ServeCommand CreateCommand() =>
        new(
            new ConfiguredThemePalette(new ThemeSemanticColors(), new ThemeSemanticColors()),
            apiClient: null!,
            secureStorageNotice: null!);
}
