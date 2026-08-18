using System.Diagnostics;
using System.Text;
using RetroDownfall.Arcanum.Infrastructure.Familiars;

namespace RetroDownfall.Arcanum.Tests.Familiars;

/// <summary>
/// The runner is the security boundary for calling a Familiar: it decides what the child inherits,
/// how it is torn down, and how a refused invocation is reported. These facts pin the boundary
/// against a real child process rather than a mock, because the properties that matter — argv never
/// becoming a shell string, secrets never reaching the environment, a wedged CLI never holding a
/// turn — are properties of the spawn, not of the abstraction over it.
/// </summary>
[Collection("ChildProcess")]
public sealed class FamiliarProcessRunnerTests
{

    private readonly FamiliarProcessRunner _runner = new();

    [Fact]
    public async Task Lines_are_streamed_in_order()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create(["one", "two", "three"]);

        List<string> lines = await CollectAsync(stub);

        Assert.Equal(["one", "two", "three"], lines);

    }

    [Fact]
    public async Task Blank_lines_are_skipped_so_a_trailing_newline_is_not_a_frame()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create(["one", "", "  ", "two"]);

        List<string> lines = await CollectAsync(stub);

        Assert.Equal(["one", "two"], lines);

    }

    /// <summary>
    /// The frame ceiling has to bound what is <em>held</em>, not just what is handed on. A frame
    /// that outgrows it is dropped whole rather than cut at a character offset: a truncated fragment
    /// is not valid JSON, so passing it on would discard the frame silently anyway while the
    /// allocation it forced had already happened.
    /// </summary>
    [Fact]
    public async Task A_frame_that_outgrows_the_ceiling_is_dropped_rather_than_delivered_truncated()
    {

        string oversize = new('x', FamiliarProcessLimits.MaxLineCharacters + 64);

        using StubFamiliarCli stub = StubFamiliarCli.Create(["one", oversize, "two"]);

        List<string> lines = await CollectAsync(stub);

        // Compared by length so a failure prints three integers rather than the whole frame.
        Assert.Equal([3, 3], [.. lines.Select(static line => line.Length)]);

        Assert.Equal(["one", "two"], lines);

    }

    /// <summary>
    /// The reader stops accumulating at the ceiling and says so, rather than letting an unterminated
    /// run grow until end of stream. Pinned with a small limit, where the whole property fits in a
    /// test that does not need a megabyte to be meaningful.
    /// </summary>
    [Fact]
    public async Task An_unterminated_run_stops_accumulating_at_the_ceiling()
    {

        FamiliarStdoutLineReader reader = new(new StringReader(new string('x', 64 * 1024)), 16);

        FamiliarStdoutLine? frame = await reader.ReadLineAsync(CancellationToken.None);

        Assert.NotNull(frame);

        Assert.True(frame.Value.Exceeded);

        Assert.Equal(16, frame.Value.Text.Length);

    }

    /// <summary>
    /// An over-long frame costs its own frame and no more: the remainder is drained to the next
    /// newline so the frames behind it still arrive, and a CRLF terminator renders the same way
    /// <c>ReadLineAsync</c> rendered it.
    /// </summary>
    [Fact]
    public async Task The_reader_resumes_at_the_next_frame_after_an_over_long_one()
    {

        FamiliarStdoutLineReader reader = new(
            new StringReader($"{new string('x', 8192)}\r\nafter\r\n"),
            16);

        FamiliarStdoutLine? oversize = await reader.ReadLineAsync(CancellationToken.None);

        Assert.True(oversize!.Value.Exceeded);

        FamiliarStdoutLine? next = await reader.ReadLineAsync(CancellationToken.None);

        Assert.Equal("after", next!.Value.Text);

        Assert.False(next.Value.Exceeded);

        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));

    }

    /// <summary>
    /// The prompt goes in on stdin, so nothing about the conversation can be mistaken for an option
    /// and no argv length limit applies.
    /// </summary>
    [Fact]
    public async Task The_prompt_is_written_to_standard_input_and_the_stream_is_closed()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create(["done"]);

        _ = await CollectAsync(stub, standardInput: "the prompt\nsecond line");

        Assert.Equal("the prompt\nsecond line", stub.ReadRecordedStandardInput().TrimEnd('\r', '\n'));

    }

    /// <summary>
    /// A folded system prompt routinely clears a quarter of a megabyte, which is orders of magnitude
    /// past any OS pipe buffer. If the runner insists on finishing that write before it reads a
    /// single stdout byte, a CLI that speaks before it listens fills its own pipe, stops reading,
    /// and both sides wait for the other until the fifteen-minute deadline reports a timeout the
    /// operator has no way to interpret. The write and the read have to overlap.
    /// </summary>
    [Fact]
    public async Task A_large_prompt_does_not_wedge_against_a_familiar_that_writes_before_it_reads()
    {

        string frame = new('x', 1_024);

        using StubFamiliarCli stub = StubFamiliarCli.CreateEmittingBeforeReadingStandardInput(
            Enumerable.Repeat(frame, 512));

        string prompt = new('p', 512 * 1024);

        List<string> lines = [];

        await foreach (string line in RunAsync(
            stub,
            standardInput: prompt,
            timeout: TimeSpan.FromSeconds(15)))
        {
            lines.Add(line);
        }

        Assert.Equal(512, lines.Count);

        Assert.Equal(prompt, stub.ReadRecordedStandardInput().TrimEnd('\r', '\n'));

    }

    /// <summary>
    /// Arguments are handed over as a list. A value containing spaces, quotes, or a leading dash
    /// must arrive as exactly one argument — this is the "dependency mess turns into a security
    /// mess" vector, and the only defence is never building a command string in the first place.
    /// </summary>
    [Fact]
    public async Task Arguments_are_passed_as_a_list_so_a_hostile_value_stays_one_argument()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create(["done"]);

        string[] arguments =
        [
            "--model",
            "a model; rm -rf / \"$(whoami)\" --allow-dangerously-skip-permissions",
        ];

        _ = await CollectAsync(stub, arguments: arguments);

        Assert.Equal(arguments, stub.ReadRecordedArgv());

    }

    [Fact]
    public async Task Arcanum_environment_variables_never_reach_the_child()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create(["done"]);

        using EnvironmentVariableScope scope = new();

        scope.Set("ARCANUM_PROVIDER_TEST_API_KEY", "super-secret");

        scope.Set("ARCANUM_GRIMOIRE_DEV_KEY", "another-secret");

        _ = await CollectAsync(stub);

        string[] childEnvironment = [.. stub.ReadRecordedEnvironment()];

        Assert.DoesNotContain(childEnvironment, static line => line.StartsWith("ARCANUM_", StringComparison.Ordinal));

        Assert.DoesNotContain(childEnvironment, static line => line.Contains("super-secret", StringComparison.Ordinal));

    }

    [Fact]
    public async Task Loader_hijack_variables_never_reach_the_child()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create(["done"]);

        using EnvironmentVariableScope scope = new();

        scope.Set("NODE_OPTIONS", "--require /tmp/evil.js");

        scope.Set("LD_PRELOAD", "/tmp/evil.so");

        _ = await CollectAsync(stub);

        string[] childEnvironment = [.. stub.ReadRecordedEnvironment()];

        Assert.DoesNotContain(childEnvironment, static line => line.StartsWith("NODE_OPTIONS=", StringComparison.Ordinal));

        Assert.DoesNotContain(childEnvironment, static line => line.StartsWith("LD_PRELOAD=", StringComparison.Ordinal));

    }

    /// <summary>
    /// A provider key configured for an HTTP provider is Arcanum's secret to hold, not the
    /// Familiar's to receive — even when the operator named it something without an ARCANUM_ prefix.
    /// </summary>
    [Fact]
    public async Task Configured_provider_credential_variables_are_stripped_by_name()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create(["done"]);

        using EnvironmentVariableScope scope = new();

        scope.Set("MY_OPENAI_KEY", "sk-should-not-leak");

        _ = await CollectAsync(stub, deniedEnvironmentVariables: ["MY_OPENAI_KEY"]);

        string[] childEnvironment = [.. stub.ReadRecordedEnvironment()];

        Assert.DoesNotContain(childEnvironment, static line => line.StartsWith("MY_OPENAI_KEY=", StringComparison.Ordinal));

    }

    /// <summary>
    /// PATH and HOME stay: the CLI is the operator's install, and it finds its own runtime and its
    /// own auth store the same way their shell does. Arcanum invokes; it does not relocate.
    /// </summary>
    [Fact]
    public async Task Path_survives_the_scrub_so_the_operators_own_install_still_resolves()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create(["done"]);

        _ = await CollectAsync(stub);

        string[] childEnvironment = [.. stub.ReadRecordedEnvironment()];

        Assert.Contains(childEnvironment, static line => line.StartsWith("PATH=", StringComparison.OrdinalIgnoreCase));

    }

    /// <summary>
    /// Both CLIs emit UTF-8 unconditionally, and the prompt Arcanum sends them is UTF-16 text that
    /// has to arrive byte-exact. Left unset, Windows encodes stdin with the console input code page
    /// and decodes stdout and stderr with the console output code page — CP437 on a default en-US
    /// console, CP_ACP when `arcanum serve` runs as a service with no console at all — so an accented
    /// name goes in as '?' and a curly quote comes back as mojibake that still parses as JSON. The
    /// corruption is silent, so the encodings have to be pinned on the start info rather than left to
    /// the host.
    /// </summary>
    [Fact]
    public void Child_streams_are_decoded_as_utf8_never_as_the_hosts_console_code_page()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        ProcessStartInfo startInfo = FamiliarProcessRunner.BuildStartInfo(
            new FamiliarProcessRequest { FileName = stub.FileName });

        Encoding?[] streamEncodings =
        [
            startInfo.StandardInputEncoding,
            startInfo.StandardOutputEncoding,
            startInfo.StandardErrorEncoding,
        ];

        Assert.All(
            streamEncodings,
            static encoding =>
            {

                Assert.Equal(Encoding.UTF8.CodePage, encoding?.CodePage);

                // A byte-order mark on stdin is a stray three bytes in front of the prompt.
                Assert.Empty(encoding!.GetPreamble());

            });

    }

    [Fact]
    public async Task A_missing_binary_fails_closed_as_not_installed()
    {

        FamiliarProcessException failure = await Assert.ThrowsAsync<FamiliarProcessException>(
            async () =>
            {

                await foreach (string _ in _runner.RunLinesAsync(
                    new FamiliarProcessRequest { FileName = StubFamiliarCli.MissingExecutablePath() },
                    CancellationToken.None))
                {
                }

            });

        Assert.Equal(FamiliarProcessFailure.NotInstalled, failure.Failure);

    }

    /// <summary>
    /// A non-zero exit is surfaced after the frames that did arrive, so a CLI that streams a partial
    /// answer and then fails cannot look like a clean short completion.
    /// </summary>
    [Fact]
    public async Task A_non_zero_exit_faults_the_stream_after_the_frames_it_did_emit()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create(
            ["partial"],
            stderr: "the familiar refused",
            exitCode: 3);

        List<string> received = [];

        FamiliarProcessException failure = await Assert.ThrowsAsync<FamiliarProcessException>(
            async () =>
            {

                await foreach (string line in RunAsync(stub))
                {
                    received.Add(line);
                }

            });

        Assert.Equal(["partial"], received);

        Assert.Equal(FamiliarProcessFailure.NonZeroExit, failure.Failure);

        Assert.Equal(3, failure.ExitCode);

        Assert.Contains("the familiar refused", failure.StandardError, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_wedged_familiar_is_torn_down_by_its_deadline()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create(
            ["one", "two", "three", "four", "five"],
            perLineDelayMilliseconds: 2000);

        FamiliarProcessException failure = await Assert.ThrowsAsync<FamiliarProcessException>(
            async () =>
            {

                await foreach (string _ in RunAsync(stub, timeout: TimeSpan.FromMilliseconds(750)))
                {
                }

            });

        Assert.Equal(FamiliarProcessFailure.TimedOut, failure.Failure);

    }

    [Fact]
    public async Task Caller_cancellation_stops_the_stream_without_a_transport_failure()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create(
            ["one", "two", "three", "four", "five"],
            perLineDelayMilliseconds: 1000);

        using CancellationTokenSource cts = new();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
            {

                await foreach (string _ in RunAsync(stub, cancellationToken: cts.Token))
                {
                    await cts.CancelAsync();
                }

            });

    }

    [Fact]
    public async Task Run_to_completion_reports_exit_code_and_both_streams_without_throwing()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create(
            ["{\"loggedIn\":false}"],
            stderr: "not signed in",
            exitCode: 1);

        FamiliarProcessOutput output = await _runner.RunToCompletionAsync(
            new FamiliarProcessRequest
            {
                FileName = stub.FileName,
                Arguments = stub.Arguments,
            },
            CancellationToken.None);

        Assert.Equal(FamiliarProcessFailure.NonZeroExit, output.Failure);

        Assert.Equal(1, output.ExitCode);

        Assert.Contains("loggedIn", output.StandardOutput, StringComparison.Ordinal);

        Assert.Contains("not signed in", output.StandardError, StringComparison.Ordinal);

    }

    /// <summary>
    /// A caller that cancels its own token gets the cancellation, never this turn's timeout
    /// classification — the <c>when</c> filter on the probe's timeout arm deliberately excludes caller
    /// cancellation, so the read loop's exception propagates instead of being reported as a Familiar that
    /// never answered. That propagating exit is the one neither explicit await covered before the prompt
    /// write moved into a <c>finally</c>, so it is pinned here with a prompt large enough that the write
    /// is genuinely overlapped with the read rather than long finished.
    /// </summary>
    [Fact]
    public async Task Run_to_completion_surfaces_caller_cancellation_rather_than_a_timeout()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create(
            ["one", "two", "three", "four", "five"],
            perLineDelayMilliseconds: 1000);

        using CancellationTokenSource cts = new();

        cts.CancelAfter(TimeSpan.FromMilliseconds(250));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _runner.RunToCompletionAsync(
                new FamiliarProcessRequest
                {

                    FileName = stub.FileName,

                    Arguments = stub.Arguments,

                    StandardInput = new string('p', 512 * 1024),

                    Timeout = TimeSpan.FromMinutes(2),

                },
                cts.Token));

    }

    /// <summary>
    /// The resolver is the one auditable place a Familiar's name becomes a path. Spawning the bare
    /// name when PATH has no answer hands resolution back to the OS, whose search order — CreateProcess
    /// on Windows, and .NET's deliberately matching walk on Unix — reaches the application directory
    /// and the caller's current directory <em>before</em> PATH. `arcanum serve` started inside a cloned
    /// repository that happens to contain a file called `claude` would then run that file, unscrubbed
    /// of the operator's privileges. When PATH cannot answer, the honest outcome is the one the
    /// operator can act on.
    /// </summary>
    [Fact]
    public async Task A_command_PATH_cannot_resolve_never_falls_through_to_the_current_directory()
    {

        using CurrentDirectoryExecutable planted = CurrentDirectoryExecutable.Plant();

        FamiliarProcessException failure = await Assert.ThrowsAsync<FamiliarProcessException>(
            async () =>
            {

                await foreach (string _ in _runner.RunLinesAsync(
                    new FamiliarProcessRequest { FileName = planted.Name },
                    CancellationToken.None))
                {
                }

            });

        Assert.Equal(FamiliarProcessFailure.NotInstalled, failure.Failure);

        Assert.False(planted.WasExecuted);

    }

    [Fact]
    public async Task Run_to_completion_also_refuses_a_command_PATH_cannot_resolve()
    {

        using CurrentDirectoryExecutable planted = CurrentDirectoryExecutable.Plant();

        FamiliarProcessOutput output = await _runner.RunToCompletionAsync(
            new FamiliarProcessRequest { FileName = planted.Name },
            CancellationToken.None);

        Assert.Equal(FamiliarProcessFailure.NotInstalled, output.Failure);

        Assert.False(planted.WasExecuted);

    }

    /// <summary>
    /// The probe has to tell "you have not installed it" apart from "it is installed but refused",
    /// so a missing binary is a classified outcome rather than an exception it must catch.
    /// </summary>
    [Fact]
    public async Task Run_to_completion_classifies_a_missing_binary_as_not_installed()
    {

        FamiliarProcessOutput output = await _runner.RunToCompletionAsync(
            new FamiliarProcessRequest { FileName = StubFamiliarCli.MissingExecutablePath() },
            CancellationToken.None);

        Assert.Equal(FamiliarProcessFailure.NotInstalled, output.Failure);

    }

    private async Task<List<string>> CollectAsync(
        StubFamiliarCli stub,
        IReadOnlyList<string>? arguments = null,
        string? standardInput = null,
        IReadOnlyList<string>? deniedEnvironmentVariables = null)
    {

        List<string> lines = [];

        await foreach (string line in RunAsync(
            stub,
            arguments,
            standardInput,
            deniedEnvironmentVariables))
        {
            lines.Add(line);
        }

        return lines;

    }

    private IAsyncEnumerable<string> RunAsync(
        StubFamiliarCli stub,
        IReadOnlyList<string>? arguments = null,
        string? standardInput = null,
        IReadOnlyList<string>? deniedEnvironmentVariables = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {

        return _runner.RunLinesAsync(
            new FamiliarProcessRequest
            {

                FileName = stub.FileName,

                Arguments = [.. stub.Arguments, .. arguments ?? []],

                StandardInput = standardInput,

                DeniedEnvironmentVariables = deniedEnvironmentVariables ?? [],

                Timeout = timeout ?? TimeSpan.FromMinutes(2),

            },
            cancellationToken);

    }

    /// <summary>
    /// Plants a runnable file in the host's current directory under a name PATH cannot resolve, and
    /// reports whether anything executed it. Stands in for the repository an operator happened to
    /// start Arcanum from — the directory the OS would reach before PATH.
    /// </summary>
    private sealed class CurrentDirectoryExecutable : IDisposable
    {

        private readonly string _path;

        private readonly string _markerPath;

        private CurrentDirectoryExecutable(string name, string path, string markerPath)
        {

            Name = name;

            _path = path;

            _markerPath = markerPath;

        }

        /// <summary>The bare command name — deliberately absent from PATH.</summary>
        public string Name { get; }

        public bool WasExecuted => File.Exists(_markerPath);

        public static CurrentDirectoryExecutable Plant()
        {

            string name = "arcanum-familiar-cwd-" + Guid.NewGuid().ToString("N");

            string path = Path.Combine(Directory.GetCurrentDirectory(), name);

            string markerPath = path + ".ran";

            File.WriteAllText(path, $"#!/bin/sh\necho ran > '{markerPath}'\n");

            if (!OperatingSystem.IsWindows())
            {

                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            }

            return new CurrentDirectoryExecutable(name, path, markerPath);

        }

        public void Dispose()
        {

            Delete(_path);

            Delete(_markerPath);

        }

        private static void Delete(string path)
        {

            try
            {

                File.Delete(path);

            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {

                // A leftover temp file is not worth failing a test over.

            }

        }

    }

    /// <summary>Sets process environment variables and restores them, so tests stay independent.</summary>
    private sealed class EnvironmentVariableScope : IDisposable
    {

        private readonly List<(string Name, string? Original)> _saved = [];

        public void Set(string name, string? value)
        {

            _saved.Add((name, System.Environment.GetEnvironmentVariable(name)));

            System.Environment.SetEnvironmentVariable(name, value);

        }

        public void Dispose()
        {

            foreach ((string name, string? original) in _saved)
            {
                System.Environment.SetEnvironmentVariable(name, original);
            }

        }

    }

}
