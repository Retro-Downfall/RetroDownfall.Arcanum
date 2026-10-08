using System.CommandLine;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Configuration;
using RetroDownfall.Arcanum.Cli;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Commands.Tower;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
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

        int erased = CliLineReader.ClearLine(buffer, new FakeLineTerminal(80), originColumn: 0);

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

        Assert.Equal(1, CliLineReader.ClearLine(buffer, new FakeLineTerminal(80), originColumn: 0));
    }

    [Fact]
    public void ClearLine_on_an_empty_buffer_erases_nothing()
    {
        StringBuilder buffer = new();

        Assert.Equal(0, CliLineReader.ClearLine(buffer, new FakeLineTerminal(80), originColumn: 0));
    }

    [Fact]
    public void DeleteLastWord_erases_the_columns_the_word_painted()
    {
        StringBuilder buffer = new("hi 世界");

        int erased = CliLineReader.DeleteLastWord(buffer, new FakeLineTerminal(80), originColumn: 0);

        Assert.Equal(4, erased);

        Assert.Equal("hi ", buffer.ToString());
    }

    [Fact]
    public void EraseLastCharacter_erases_both_columns_of_a_wide_glyph()
    {
        StringBuilder buffer = new("a好");

        int erased = CliLineReader.EraseLastCharacter(buffer, new FakeLineTerminal(80), originColumn: 0);

        Assert.Equal(2, erased);

        Assert.Equal("a", buffer.ToString());
    }

    [Fact]
    public void EraseLastCharacter_erases_one_column_for_a_narrow_astral_character()
    {
        StringBuilder buffer = new("a\U0001D400");

        int erased = CliLineReader.EraseLastCharacter(buffer, new FakeLineTerminal(80), originColumn: 0);

        Assert.Equal(1, erased);

        Assert.Equal("a", buffer.ToString());
    }

    /// <summary>
    /// Backspace is a no-op at column 0 on every common terminal, so it cannot walk the caret back
    /// onto the previous visual row. An erase that spans a wrap boundary painted with backspaces
    /// leaves the first row's text on screen while the buffer no longer holds it, and spills its
    /// blanks onto a row below. The erase has to move the cursor instead.
    /// </summary>
    [Fact]
    public void ClearLine_crossing_a_wrap_boundary_moves_the_cursor_instead_of_backspacing()
    {
        FakeLineTerminal terminal = new(20);

        StringBuilder buffer = new(new string('x', 30));

        int erased = CliLineReader.ClearLine(buffer, terminal, originColumn: 10);

        Assert.Equal(30, erased);

        Assert.DoesNotContain("\b", terminal.Output, StringComparison.Ordinal);

        Assert.Equal("\u001b[1A\u001b[11G\u001b[0J", terminal.Output);
    }

    /// <summary>
    /// A composed line that exactly fills its row leaves the caret in the terminal's deferred-wrap
    /// state, where it is displayed on the last column it painted rather than at column 0 of the next
    /// row. Backspacing from there is off by one, so this erase is positioned explicitly too.
    /// </summary>
    [Fact]
    public void EraseLastCharacter_on_an_exactly_filled_row_positions_the_cursor_explicitly()
    {
        FakeLineTerminal terminal = new(20);

        StringBuilder buffer = new(new string('x', 10));

        int erased = CliLineReader.EraseLastCharacter(buffer, terminal, originColumn: 10);

        Assert.Equal(1, erased);

        Assert.Equal("\u001b[20G\u001b[0J", terminal.Output);
    }

    [Fact]
    public void ClearLine_within_a_single_row_still_erases_with_backspaces()
    {
        FakeLineTerminal terminal = new(80);

        StringBuilder buffer = new("hello");

        int erased = CliLineReader.ClearLine(buffer, terminal, originColumn: 10);

        Assert.Equal(5, erased);

        Assert.Equal("\b\b\b\b\b     \b\b\b\b\b", terminal.Output);
    }

    /// <summary>
    /// A terminal that cannot move the cursor still cannot cross the boundary, so the erase reports
    /// only the columns it actually blanked rather than the count it was asked for.
    /// </summary>
    [Fact]
    public void ClearLine_without_cursor_motion_reports_only_the_columns_it_could_reach()
    {
        FakeLineTerminal terminal = new(20) { SupportsAnsi = false };

        StringBuilder buffer = new(new string('x', 30));

        int erased = CliLineReader.ClearLine(buffer, terminal, originColumn: 10);

        Assert.Equal(20, erased);
    }

    /// <summary>
    /// The read result exists so the caller — not the process-termination handler — decides what an
    /// interrupt means. Flattening Ctrl+C, Ctrl+D and an empty submission to <c>null</c> takes that
    /// decision away, and the ask_human coordinator then reports a deliberate cancel as
    /// "no answer was provided".
    /// </summary>
    [Fact]
    public void ReadLine_translation_separates_an_interrupt_from_a_submitted_line()
    {
        Assert.Equal(
            string.Empty,
            CliLineReader.TranslateToLine(
                new CliLineReadResult(CliLineReadOutcome.Submitted, string.Empty, false),
                CancellationToken.None));

        _ = Assert.Throws<OperationCanceledException>(() => CliLineReader.TranslateToLine(
            new CliLineReadResult(CliLineReadOutcome.Interrupted, null, true),
            CancellationToken.None));

        _ = Assert.Throws<InvalidOperationException>(() => CliLineReader.TranslateToLine(
            new CliLineReadResult(CliLineReadOutcome.EndOfInput, null, false),
            CancellationToken.None));
    }

    [Fact]
    public void ReadInteractive_reports_an_interrupt_and_the_text_it_discarded()
    {
        FakeLineTerminal terminal = new(80, Printable('h'), Printable('i'), ControlKey(ConsoleKey.C));

        CliLineReadResult result = CliLineReader.ReadInteractive(
            terminal,
            allowEmpty: false,
            originColumn: 0,
            CancellationToken.None);

        Assert.Equal(CliLineReadOutcome.Interrupted, result.Outcome);

        Assert.True(result.HadPendingText);

        Assert.Null(result.Line);
    }

    /// <summary>
    /// A dismissed prompt has to be able to take the console back. Console.ReadKey has no cancellable
    /// overload, so a read that blocks on it outlives the question it belonged to and the operator
    /// has to type into a prompt that no longer means anything before the command can exit.
    /// </summary>
    [Fact]
    public void ReadInteractive_gives_the_console_back_when_the_caller_cancels()
    {
        using CancellationTokenSource cts = new();

        cts.Cancel();

        FakeLineTerminal terminal = new(80);

        CliLineReadResult result = CliLineReader.ReadInteractive(
            terminal,
            allowEmpty: false,
            originColumn: 0,
            cts.Token);

        Assert.Equal(CliLineReadOutcome.Cancelled, result.Outcome);
    }

    /// <summary>
    /// A required line that is still empty when Enter arrives is not submitted, and the caret must not
    /// move: echoing the newline put it on the next row at column 0 with no prompt, while every later
    /// erase was still measured from the prompt's column on the row above.
    /// </summary>
    [Fact]
    public void ReadInteractive_rejects_an_empty_required_line_without_moving_the_caret()
    {
        FakeLineTerminal terminal = new(
            80,
            EnterKey(),
            Printable('o'),
            Printable('k'),
            EnterKey());

        CliLineReadResult result = CliLineReader.ReadInteractive(
            terminal,
            allowEmpty: false,
            originColumn: 8,
            CancellationToken.None);

        Assert.Equal(CliLineReadOutcome.Submitted, result.Outcome);

        Assert.Equal("ok", result.Line);

        // Exactly one newline, from the accepted Enter: the rejected one wrote nothing.
        Assert.Equal("ok\n", terminal.Output);
    }

    [Fact]
    public void ReadInteractive_rejects_a_blank_required_line_and_keeps_the_typed_blanks()
    {
        FakeLineTerminal terminal = new(80, Printable(' '), EnterKey(), Printable('x'), EnterKey());

        CliLineReadResult result = CliLineReader.ReadInteractive(
            terminal,
            allowEmpty: false,
            originColumn: 3,
            CancellationToken.None);

        Assert.Equal(" x", result.Line);

        Assert.Equal(" x\n", terminal.Output);
    }

    [Fact]
    public void ReadInteractive_submits_an_empty_line_when_empty_is_allowed()
    {
        FakeLineTerminal terminal = new(80, EnterKey());

        CliLineReadResult result = CliLineReader.ReadInteractive(
            terminal,
            allowEmpty: true,
            originColumn: 0,
            CancellationToken.None);

        Assert.Equal(CliLineReadOutcome.Submitted, result.Outcome);

        Assert.Equal(string.Empty, result.Line);

        Assert.Equal("\n", terminal.Output);
    }

    private static ConsoleKeyInfo EnterKey() =>
        new('\r', ConsoleKey.Enter, shift: false, alt: false, control: false);

    private static ConsoleKeyInfo Printable(char value) =>
        new(value, ConsoleKey.None, shift: false, alt: false, control: false);

    private static ConsoleKeyInfo ControlKey(ConsoleKey key) =>
        new((char)(key - ConsoleKey.A + 1), key, shift: false, alt: false, control: true);

    /// <summary>
    /// A terminal whose geometry, capabilities and keystrokes are all fixed by the test. Reading a
    /// key that is not queued is the fake's stand-in for the blocking read: it fails loudly instead
    /// of hanging the suite.
    /// </summary>
    private sealed class FakeLineTerminal(int width, params ConsoleKeyInfo[] keys) : ICliLineTerminal
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

        private readonly StringBuilder _output = new();

        public int Width { get; } = width;

        public int CursorLeft => 0;

        public bool SupportsAnsi { get; init; } = true;

        public bool KeyAvailable => _keys.Count > 0;

        public string Output => _output.ToString();

        public ConsoleKeyInfo ReadKey() =>
            _keys.Count > 0
                ? _keys.Dequeue()
                : throw new InvalidOperationException("ReadKey blocked: no keystroke is available.");

        public void Write(string text) => _ = _output.Append(text);

        public void WriteLine() => _ = _output.Append('\n');
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
    /// A command this probe root does not define gets the standard grace window.
    /// </summary>
    /// <remarks>
    /// The probe root holds only <c>ask</c> and <c>chat</c>, so these tokens resolve to the root
    /// itself and nothing claims the keypress. The verbs that really do claim it are pinned against
    /// the production command tree below, because a name has to resolve to a command before the
    /// opt-out can see it.
    /// </remarks>
    [Fact]
    public void ResolveProcessTerminationTimeout_applies_to_every_parsed_command()
    {
        ParseResult parsed = BuildProbeRoot().Parse(["run", "hello"]);

        Assert.Equal(
            CliApplicationFactory.ProcessTerminationGrace,
            CliApplicationFactory.ResolveProcessTerminationTimeout(parsed));
    }

    /// <summary>
    /// A verb that installs its own Ctrl+C handler is not also torn down by System.CommandLine's.
    /// </summary>
    /// <remarks>
    /// A route that installs its own Ctrl+C handler is not also torn down by System.CommandLine's.
    ///
    /// <para><c>run</c> without <c>--research</c> and without <c>--dry-run</c> reaches
    /// <c>AskCommand</c>, which installs <c>Console.CancelKeyPress</c>; <c>--spell</c> takes the same
    /// arm. Every <c>watch</c> verb goes through <c>WatchCommands.WithCancellationAsync</c>, which
    /// installs one too. Both inherited the ten-second termination grace, so a cooperative unwind
    /// longer than that — the inference cancel path drains the human-in-the-loop queue before
    /// returning 130 — was torn down by the framework's handler rather than finishing under the
    /// verb's own contract.</para>
    ///
    /// <para>Parsed against the production tree, not a probe root: the opt-out matches a resolved
    /// command, so a fake root would report the answer for a command that does not exist.</para>
    /// </remarks>
    [Theory]

    [InlineData("run hi")]

    [InlineData("run --spell summarize hi")]

    [InlineData("watch session 9f2a1c40-6f4a-4d2b-9a1e-1b2c3d4e5f60")]

    [InlineData("watch logs")]

    public void ResolveProcessTerminationTimeout_is_null_for_a_verb_that_owns_the_keypress(string commandLine)
    {
        ParseResult parsed = BuildProductionRoot().Parse(commandLine);

        Assert.Null(CliApplicationFactory.ResolveProcessTerminationTimeout(parsed));
    }

    /// <summary>
    /// The two <c>run</c> routes that install no handler of their own keep the grace window.
    /// </summary>
    /// <remarks>
    /// <c>run</c> has three terminal branches and only one of them claims the keypress:
    /// <c>--dry-run</c> goes to the context preview and <c>--research</c> to the web workflow, neither
    /// of which installs <c>Console.CancelKeyPress</c>. Opting the whole verb out left those two with
    /// no handler at all — a Ctrl+C would take the CLR default and terminate immediately, with no
    /// cooperative unwind and no exit 130, where before they had ten seconds.
    ///
    /// <para><c>--dry-run</c> is checked before the route, so the pair together is the same answer as
    /// either alone. These cases also pin the option spellings the opt-out reads: rename either flag
    /// without updating it and the verb silently opts out again, which is the drift this whole
    /// contract exists to prevent.</para>
    /// </remarks>
    [Theory]

    [InlineData("run --research hi")]

    [InlineData("run --dry-run hi")]

    [InlineData("run --dry-run --research hi")]

    public void ResolveProcessTerminationTimeout_is_the_grace_for_a_run_route_that_claims_no_keypress(
        string commandLine)
    {
        ParseResult parsed = BuildProductionRoot().Parse(commandLine);

        Assert.Equal(
            CliApplicationFactory.ProcessTerminationGrace,
            CliApplicationFactory.ResolveProcessTerminationTimeout(parsed));
    }

    /// <summary>
    /// Everything else still gets the grace window, including the other verb spelled <c>run</c>.
    /// </summary>
    /// <remarks>
    /// <c>trial run</c> is the control that matters. It shares a bare name with the top-level verb and
    /// installs no keypress handler of its own, so an opt-out that matched names at any depth would
    /// silently leave a Ctrl+C there with nothing to answer it.
    /// </remarks>
    [Theory]

    [InlineData("trial run")]

    [InlineData("session list")]

    [InlineData("doctor")]

    public void ResolveProcessTerminationTimeout_is_the_grace_for_every_other_verb(string commandLine)
    {
        ParseResult parsed = BuildProductionRoot().Parse(commandLine);

        Assert.Equal(
            CliApplicationFactory.ProcessTerminationGrace,
            CliApplicationFactory.ResolveProcessTerminationTimeout(parsed));
    }

    /// <summary>
    /// Every invocation builds the whole tree, so a handler resolved at build time is a handler
    /// constructed for nothing: ~45 of them, each with its own dependency graph, before the one
    /// command the operator typed even runs. Resolution belongs inside the action delegates.
    /// </summary>
    [Fact]
    public void Building_the_tree_does_not_construct_command_handlers()
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        ResolutionRecordingProvider recording = new(provider);

        RootCommand root = CliCommandTree.Build(recording, out _);

        Assert.NotEmpty(root.Subcommands);

        Assert.Empty(recording.Requested);

        _ = root.Parse("context current");

        Assert.Empty(recording.Requested);
    }

    /// <summary>
    /// The deferred resolution still happens: running a command constructs its own handler when the
    /// action executes, and no other command's.
    /// </summary>
    [Fact]
    public async Task Running_a_command_resolves_its_handler_when_the_action_executes()
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<ICliContextStore>();

        services.AddSingleton<ICliContextStore>(
            new FaultingContextStore(new InvalidOperationException("probe")));

        using ServiceProvider provider = services.BuildServiceProvider();

        ResolutionRecordingProvider recording = new(provider);

        RootCommand root = CliCommandTree.Build(recording, out _);

        TextWriter originalOut = Console.Out;

        TextWriter originalError = Console.Error;

        try
        {
            Console.SetOut(new StringWriter());

            Console.SetError(new StringWriter());

            // The framework's default handler turns the store fault into a non-zero exit code. The
            // fault only proves the action ran far enough to read the store; it is not under test.
            int exitCode = await root.Parse("context current").InvokeAsync();

            Assert.NotEqual(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);

            Console.SetError(originalError);
        }

        Assert.Contains(typeof(ContextCommands), recording.Requested);

        Assert.DoesNotContain(typeof(SessionCommands), recording.Requested);

        Assert.DoesNotContain(typeof(RunCommand), recording.Requested);
    }

    /// <summary>
    /// The production provider owns singletons that hold pooled or unmanaged resources, and nothing
    /// disposed it: process exit was the only cleanup. The code that builds it disposes it once the
    /// invocation has finished.
    /// </summary>
    [Fact]
    public async Task Run_and_dispose_disposes_the_provider_after_the_invocation()
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<DisposalProbe>();

        ServiceProvider provider = services.BuildServiceProvider();

        DisposalProbe probe = provider.GetRequiredService<DisposalProbe>();

        TextWriter originalOut = Console.Out;

        TextWriter originalError = Console.Error;

        try
        {
            Console.SetOut(new StringWriter());

            Console.SetError(new StringWriter());

            await CliApplicationFactory.RunAndDisposeProviderAsync(["--version"], provider);
        }
        finally
        {
            Console.SetOut(originalOut);

            Console.SetError(originalError);
        }

        Assert.True(probe.Disposed);
    }

    /// <summary>
    /// The provider is disposed after the command has finished, so a singleton whose release throws
    /// must not replace the command's exit code with an unhandled crash. The failure is named by type
    /// on stderr (its message can carry a path or a secret) and the command's own result stands.
    /// </summary>
    [Fact]
    public async Task Run_and_dispose_keeps_the_command_result_when_a_service_fails_to_dispose()
    {
        const string Marker = ThrowingDisposalProbe.Marker;

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        // A type registration, not an instance: the container disposes only what it created.
        services.AddSingleton<ThrowingDisposalProbe>();

        ServiceProvider provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<ThrowingDisposalProbe>();

        TextWriter originalOut = Console.Out;

        TextWriter originalError = Console.Error;

        StringWriter error = new();

        int exitCode;

        try
        {
            Console.SetOut(new StringWriter());

            Console.SetError(error);

            exitCode = await CliApplicationFactory.RunAndDisposeProviderAsync(["--version"], provider);
        }
        finally
        {
            Console.SetOut(originalOut);

            Console.SetError(originalError);
        }

        Assert.Equal(0, exitCode);

        Assert.Contains(typeof(InvalidOperationException).FullName!, error.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain(Marker, error.ToString(), StringComparison.Ordinal);
    }

    private sealed class ThrowingDisposalProbe : IDisposable
    {
        internal const string Marker = "marker-secret-path";

        public void Dispose() => throw new InvalidOperationException(Marker);
    }

    private sealed class ResolutionRecordingProvider(IServiceProvider inner) : IServiceProvider
    {
        public List<Type> Requested { get; } = [];

        public object? GetService(Type serviceType)
        {
            Requested.Add(serviceType);

            return inner.GetService(serviceType);
        }
    }

    private sealed class DisposalProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private static RootCommand BuildProductionRoot()
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        return CliCommandTree.Build(provider, out _);
    }

    /// <summary>
    /// The recursive root options are legal before the subcommand token, and `arcanum --json doctor`
    /// is the form operators and scripts actually write, so the verb has to be located by skipping
    /// them rather than by reading argv[0]. `help` belongs here too: on a malformed arcanum.json it
    /// is the other way an operator finds out what to run. Nothing after `--` is the CLI's argument,
    /// so a help flag there must not unlock the degraded path.
    /// </summary>
    [Theory]
    [InlineData(new[] { "doctor" }, true)]
    [InlineData(new[] { "config", "validate" }, true)]
    [InlineData(new[] { "data", "factory-reset", "--global", "--dry-run" }, true)]
    [InlineData(new[] { "run", "--help" }, true)]
    [InlineData(new[] { "--version" }, true)]
    [InlineData(new[] { "run", "hello" }, false)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--json", "doctor" }, true)]
    [InlineData(new[] { "-v", "config", "validate" }, true)]
    [InlineData(new[] { "--output-format", "json", "doctor" }, true)]
    [InlineData(new[] { "--output-format=json", "config", "edit" }, true)]
    [InlineData(new[] { "--plain", "run", "hello" }, false)]
    [InlineData(new[] { "help" }, true)]
    [InlineData(new[] { "--json", "help", "doctor" }, true)]
    [InlineData(new[] { "run", "--", "--help" }, false)]
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

    /// <summary>
    /// An exception the mapper does not recognise used to be flattened to one sentence with no type,
    /// no log line and no way to ask for more. Under <c>-v</c> the exception type (never its message,
    /// which can carry a secret or a path) now goes to stderr beside that sentence.
    /// </summary>
    [Fact]
    public async Task Unexpected_exception_names_its_type_under_verbose()
    {
        const string Marker = "marker-secret-path";

        CliTestResult verbose = await RunContextCurrentWithFaultingStoreAsync(
            new InvalidOperationException(Marker),
            "-v");

        Assert.Equal((int)CliExitCode.GenericError, verbose.ExitCode);

        Assert.Contains("An unexpected CLI error occurred.", verbose.Error, StringComparison.Ordinal);

        Assert.Contains(typeof(InvalidOperationException).FullName!, verbose.Error, StringComparison.Ordinal);

        Assert.DoesNotContain(Marker, verbose.Output + verbose.Error, StringComparison.Ordinal);

        CliTestResult quiet = await RunContextCurrentWithFaultingStoreAsync(
            new InvalidOperationException(Marker));

        Assert.Equal((int)CliExitCode.GenericError, quiet.ExitCode);

        Assert.DoesNotContain(typeof(InvalidOperationException).FullName!, quiet.Error, StringComparison.Ordinal);

        Assert.DoesNotContain(Marker, quiet.Output + quiet.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpected_exception_keeps_the_json_error_payload_free_of_the_type_and_message()
    {
        const string Marker = "marker-secret-path";

        CliTestResult result = await RunContextCurrentWithFaultingStoreAsync(
            new InvalidOperationException(Marker),
            "--json",
            "-v");

        CliErrorPayload payload = Assert.IsType<CliErrorPayload>(
            System.Text.Json.JsonSerializer.Deserialize(
                result.Output,
                CliJsonContext.Default.CliErrorPayload));

        Assert.Equal("An unexpected CLI error occurred.", payload.Error);

        Assert.Equal((int)CliExitCode.GenericError, payload.ExitCode);

        Assert.DoesNotContain(Marker, result.Output + result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A local file fault is not an unexpected program error: the operator can fix it, so the failure
    /// says it was a local file access and still keeps the exception message (a path) out of the line.
    /// </summary>
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    [InlineData(typeof(PathTooLongException))]
    [InlineData(typeof(DriveNotFoundException))]
    public void Map_names_a_local_file_access_failure_for_io_and_permission_faults(Type exceptionType)
    {
        Exception exception = (Exception)Activator.CreateInstance(exceptionType, "marker-secret-path")!;

        CliFailure failure = CliFailureMapper.Map(exception);

        Assert.Equal(CliExitCode.GenericError, failure.ExitCode);

        Assert.Equal("Local file access failed. Check the path and its permissions.", failure.SafeMessage);

        Assert.DoesNotContain("marker-secret-path", failure.SafeMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal to replace a saved context this build cannot use is an <see cref="IOException"/> whose
    /// message is the operator-facing explanation. It keeps that message if it ever reaches the mapper
    /// instead of being flattened to the file-permission hint or the catch-all.
    /// </summary>
    [Fact]
    public void Map_keeps_the_operator_facing_message_of_a_context_refusal()
    {
        CliFailure failure = CliFailureMapper.Map(
            new CliContextFileUnusableException("The saved CLI context at /x was left unchanged."));

        Assert.Equal(CliExitCode.GenericError, failure.ExitCode);

        Assert.Equal("The saved CLI context at /x was left unchanged.", failure.SafeMessage);
    }

    /// <summary>
    /// An <see cref="IOException"/> is only a local file fault when the file system raised it. One that
    /// wraps another failure is somebody else's: Kestrel reports a port that cannot be bound as an
    /// <see cref="IOException"/> around the socket error, and `arcanum serve` has no catch of its own, so
    /// the file-permission hint would send the operator to the wrong place. It stays an unrecognized
    /// internal error, which `-v` still names by type.
    /// </summary>
    [Fact]
    public void Map_does_not_blame_the_file_system_for_an_io_failure_that_wraps_another_failure()
    {
        IOException bindFailure = new(
            "Failed to bind to address http://127.0.0.1:5000: address already in use.",
            new System.Net.Sockets.SocketException(98));

        CliFailure failure = CliFailureMapper.Map(bindFailure);

        Assert.Equal(CliExitCode.GenericError, failure.ExitCode);

        Assert.Equal("An unexpected CLI error occurred.", failure.SafeMessage);
    }

    /// <summary>
    /// <c>HttpIOException</c> derives from <see cref="IOException"/> but is a transport fault, so it
    /// keeps the network exit code instead of being reported as a local file problem.
    /// </summary>
    [Fact]
    public void Map_keeps_a_transport_io_fault_on_the_network_exit_code()
    {
        CliFailure failure = CliFailureMapper.Map(
            new System.Net.Http.HttpIOException(System.Net.Http.HttpRequestError.ResponseEnded));

        Assert.Equal(CliExitCode.NetworkError, failure.ExitCode);

        Assert.Equal("A network operation failed.", failure.SafeMessage);
    }

    /// <summary>
    /// Runs <c>context current</c> against a saved-context store whose read throws
    /// <paramref name="fault"/>. The read happens inside the command action, after the global options
    /// were parsed, which is the point an unexpected failure has to be reported from.
    /// </summary>
    private static async Task<CliTestResult> RunContextCurrentWithFaultingStoreAsync(
        Exception fault,
        params string[] globalOptions)
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<ICliContextStore>();

        services.AddSingleton<ICliContextStore>(new FaultingContextStore(fault));

        return await CliTestHarness.RunAsync(services, [.. globalOptions, "context", "current"]);
    }

    private sealed class FaultingContextStore(Exception fault) : ICliContextStore
    {
        public string FilePath => "unused";

        public CliContextDocument Load() => throw fault;
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
