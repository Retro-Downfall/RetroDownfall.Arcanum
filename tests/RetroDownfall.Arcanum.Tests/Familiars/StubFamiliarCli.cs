using System.Globalization;
using System.Text;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Familiars;

/// <summary>
/// A throwaway executable that stands in for an installed <c>claude</c> or <c>codex</c> binary.
/// Tests never touch a live subscription: the stub replays recorded NDJSON, and its exit code and
/// stderr are scripted so the negative paths (missing binary, non-zero exit, truncated stream) are
/// exercised for real through <see cref="System.Diagnostics.Process"/> rather than mocked away.
/// </summary>
internal sealed class StubFamiliarCli : IDisposable
{

    private readonly string _directory;

    private string? _startupReleasePath;

    private string? _startupReadyPath;

    private string? _errorReleasePath;

    private string? _errorHolderPidPath;

    private global::System.Diagnostics.Process? _errorHolder;

    private bool _errorHolderJoined;

    private int _disposed;

    private StubFamiliarCli(string directory, string fileName, IReadOnlyList<string> arguments)
    {

        _directory = directory;

        FileName = fileName;

        Arguments = arguments;

    }

    /// <summary>The binary to spawn — the script itself on Unix, the shell host on Windows.</summary>
    public string FileName { get; }

    /// <summary>Arguments that must precede the caller's own on Windows; empty on Unix.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Where the stub records the argv and environment it was spawned with.</summary>
    public string ArgvLogPath => Path.Combine(_directory, "argv.log");

    public string EnvironmentLogPath => Path.Combine(_directory, "env.log");

    public string StandardInputLogPath => Path.Combine(_directory, "stdin.log");

    /// <summary>
    /// Writes a stub that echoes <paramref name="stdoutLines"/>, then <paramref name="stderr"/>,
    /// then exits with <paramref name="exitCode"/>.
    /// </summary>
    public static StubFamiliarCli Create(
        IEnumerable<string> stdoutLines,
        string stderr = "",
        int exitCode = 0,
        int perLineDelayMilliseconds = 0)
    {

        string directory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-familiar-stub-" + Guid.NewGuid().ToString("N"));

        _ = Directory.CreateDirectory(directory);

        string payloadPath = Path.Combine(directory, "payload.ndjson");

        File.WriteAllText(payloadPath, string.Join('\n', stdoutLines) + "\n");

        return OperatingSystem.IsWindows()
            ? CreateWindows(directory, payloadPath, stderr, exitCode, perLineDelayMilliseconds)
            : CreateUnix(directory, payloadPath, stderr, exitCode, perLineDelayMilliseconds);

    }

    /// <summary>
    /// Writes a stub that emits <paramref name="stdoutLines"/> <em>before</em> it drains stdin. Both
    /// shipped CLIs happen to read their whole prompt first, which is exactly why this ordering is
    /// unpinned: a caller that finishes its stdin write before it starts reading stdout wedges both
    /// pipes against a child in this shape, and nothing else in the suite would notice.
    /// </summary>
    public static StubFamiliarCli CreateEmittingBeforeReadingStandardInput(IEnumerable<string> stdoutLines)
    {

        string directory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-familiar-stub-" + Guid.NewGuid().ToString("N"));

        _ = Directory.CreateDirectory(directory);

        string payloadPath = Path.Combine(directory, "payload.ndjson");

        File.WriteAllText(payloadPath, string.Join('\n', stdoutLines) + "\n");

        string stdinLogPath = Path.Combine(directory, "stdin.log");

        if (OperatingSystem.IsWindows())
        {

            string windowsScriptPath = Path.Combine(directory, "familiar-stub.ps1");

            File.WriteAllText(
                windowsScriptPath,
                $"Get-Content -LiteralPath '{payloadPath}'\n"
                + $"[System.IO.File]::WriteAllText('{stdinLogPath}', [Console]::In.ReadToEnd(), (New-Object System.Text.UTF8Encoding $false))\n"
                + "exit 0\n");

            return new StubFamiliarCli(
                directory,
                "powershell.exe",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", windowsScriptPath]);

        }

        string scriptPath = Path.Combine(directory, "familiar-stub");

        File.WriteAllText(
            scriptPath,
            "#!/bin/sh\n"
            + $"cat '{payloadPath}'\n"
            + $"cat > '{stdinLogPath}'\n"
            + "exit 0\n");

        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return new StubFamiliarCli(directory, scriptPath, []);

    }

    /// <summary>
    /// Writes a stub that finishes — its frames, then <paramref name="exitCode"/> — while a
    /// background grandchild holds the stderr pipe open behind it until the test releases it.
    /// That shape reproduces on a POSIX host what Windows
    /// does to every Familiar it kills: by the time the deadline fires the child has already exited
    /// and stdout has already ended at EOF, so no read is left pending to observe the cancellation and
    /// the runner reaches its verdict with a real exit code in hand and nothing thrown. Unix only, and
    /// only because Windows needs no help producing that sequence.
    /// </summary>
    public static StubFamiliarCli CreateExitingBehindAHeldOpenErrorStream(
        IEnumerable<string> stdoutLines,
        int exitCode,
        bool holdStartupUntilReleased = false)
    {

        string directory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-familiar-stub-" + Guid.NewGuid().ToString("N"));

        _ = Directory.CreateDirectory(directory);

        string payloadPath = Path.Combine(directory, "payload.ndjson");

        File.WriteAllText(payloadPath, string.Join('\n', stdoutLines) + "\n");

        string scriptPath = Path.Combine(directory, "familiar-stub");

        string startupReleasePath = Path.Combine(directory, "startup.release");

        string startupReadyPath = Path.Combine(directory, "startup.ready");

        string errorReleasePath = Path.Combine(directory, "stderr.release");

        string errorHolderPidPath = Path.Combine(directory, "stderr-holder.pid");

        // The grandchild inherits stderr and nothing else — its own stdout goes to /dev/null — so the
        // runner still sees end of stream the moment this script exits, and is left waiting on the
        // one pipe that has a writer alive past the deadline.
        File.WriteAllText(
            scriptPath,
            "#!/bin/sh\n"
            + (holdStartupUntilReleased
                ? $"printf ready > '{startupReadyPath}'\nwhile [ ! -f '{startupReleasePath}' ]; do sleep 0.01; done\n"
                : string.Empty)
            + $"cat '{payloadPath}'\n"
            + $"/bin/sh -c 'while [ ! -f \"$1\" ]; do sleep 0.01; done' held-stderr '{errorReleasePath}' >/dev/null &\n"
            + $"printf '%s\\n' \"$!\" > '{errorHolderPidPath}'\n"
            + $"exit {exitCode}\n");

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }

        return new StubFamiliarCli(directory, scriptPath, [])
        {
            _startupReleasePath = holdStartupUntilReleased ? startupReleasePath : null,
            _startupReadyPath = holdStartupUntilReleased ? startupReadyPath : null,
            _errorReleasePath = errorReleasePath,
            _errorHolderPidPath = errorHolderPidPath,
        };

    }

    /// <summary>A path that does not exist, for the "operator has not installed it" path.</summary>
    public static string MissingExecutablePath() =>
        Path.Combine(
            Path.GetTempPath(),
            "arcanum-familiar-absent-" + Guid.NewGuid().ToString("N"));

    public IReadOnlyList<string> ReadRecordedArgv() =>
        File.Exists(ArgvLogPath)
            ? File.ReadAllLines(ArgvLogPath)
            : [];

    public IReadOnlyList<string> ReadRecordedEnvironment() =>
        File.Exists(EnvironmentLogPath)
            ? File.ReadAllLines(EnvironmentLogPath)
            : [];

    public string ReadRecordedStandardInput() =>
        File.Exists(StandardInputLogPath)
            ? File.ReadAllText(StandardInputLogPath)
            : string.Empty;

    internal void ReleaseStartup()
    {
        if (_startupReleasePath is not null)
        {
            File.WriteAllText(_startupReleasePath, string.Empty);
        }
    }

    internal async Task WaitUntilStartupHeldAsync()
    {
        ArgumentNullException.ThrowIfNull(_startupReadyPath);

        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));

        while (!File.Exists(_startupReadyPath))
        {
            await Task.Delay(10, guard.Token).ConfigureAwait(false);
        }
    }

    internal void CaptureErrorHolder()
    {
        if (!TryCaptureErrorHolder() || _errorHolder!.HasExited)
        {
            throw new InvalidOperationException("The stderr holder must still be alive after stdout EOF and process exit.");
        }
    }

    private bool TryCaptureErrorHolder()
    {
        if (_errorHolder is not null)
        {
            return true;
        }

        if (_errorHolderPidPath is null || !File.Exists(_errorHolderPidPath))
        {
            return false;
        }

        try
        {
            if (!int.TryParse(File.ReadAllText(_errorHolderPidPath), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
            {
                return false;
            }

            _errorHolder = global::System.Diagnostics.Process.GetProcessById(pid);

            return true;
        }
        catch (Exception exception) when (TestDescendantProcess.IsDescendantAlreadyGone(exception))
        {
            // A setup failure may have reaped the tree before it could publish a usable holder.
            return false;
        }
    }

    internal void ReleaseErrorStream()
    {
        if (_errorReleasePath is not null)
        {
            File.WriteAllText(_errorReleasePath, string.Empty);
        }
    }

    internal async Task JoinErrorHolderAsync()
    {
        _ = TryCaptureErrorHolder();

        ReleaseErrorStream();

        if (_errorHolder is not { } holder)
        {
            return;
        }

        try
        {
            await holder.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            _errorHolderJoined = true;
        }
        catch (TimeoutException)
        {
            TestDescendantProcess.KillTreeIfRunning(holder.Id);

            await holder.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            _errorHolderJoined = true;

            throw;
        }
        finally
        {
            holder.Dispose();

            _errorHolder = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            ReleaseStartup();

            ReleaseErrorStream();
        }
        finally
        {
            if (!_errorHolderJoined && _errorHolderPidPath is not null)
            {
                TestDescendantProcess.KillRecorded(_errorHolderPidPath);
            }

            _errorHolder?.Dispose();

            TestDirectoryCleanup.TryDelete(_directory, nameof(StubFamiliarCli));
        }
    }

    private static StubFamiliarCli CreateUnix(
        string directory,
        string payloadPath,
        string stderr,
        int exitCode,
        int perLineDelayMilliseconds)
    {

        string scriptPath = Path.Combine(directory, "familiar-stub");

        StringBuilder script = new();

        _ = script.Append("#!/bin/sh\n");

        _ = script.Append($"printf '%s\\n' \"$@\" > '{Path.Combine(directory, "argv.log")}'\n");

        _ = script.Append($"env > '{Path.Combine(directory, "env.log")}'\n");

        _ = script.Append($"cat > '{Path.Combine(directory, "stdin.log")}'\n");

        _ = perLineDelayMilliseconds > 0
            ? script.Append(
                $"while IFS= read -r line; do printf '%s\\n' \"$line\"; sleep {perLineDelayMilliseconds / 1000.0:0.###}; done < '{payloadPath}'\n")
            : script.Append($"cat '{payloadPath}'\n");

        if (stderr.Length > 0)
        {

            _ = script.Append($"printf '%s' '{stderr.Replace("'", "'\\''", StringComparison.Ordinal)}' >&2\n");

        }

        _ = script.Append($"exit {exitCode}\n");

        File.WriteAllText(scriptPath, script.ToString());

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }

        return new StubFamiliarCli(directory, scriptPath, []);

    }

    private static StubFamiliarCli CreateWindows(
        string directory,
        string payloadPath,
        string stderr,
        int exitCode,
        int perLineDelayMilliseconds)
    {

        string scriptPath = Path.Combine(directory, "familiar-stub.ps1");

        StringBuilder script = new();

        _ = script.Append($"$args | Set-Content -LiteralPath '{Path.Combine(directory, "argv.log")}'\n");

        _ = script.Append(
            $"Get-ChildItem env: | ForEach-Object {{ \"$($_.Name)=$($_.Value)\" }} | Set-Content -LiteralPath '{Path.Combine(directory, "env.log")}'\n");

        // Verbatim, the way `cat > stdin.log` is on the Unix stub. `$input | Out-String` was not:
        // $input splits stdin into lines, Out-String rejoins them with CRLF, and Set-Content adds a
        // trailing newline -- so the recording differed from what the runner actually wrote, and the
        // suite that exists to prove the prompt arrives unaltered was reading the harness's own
        // line endings back to itself. [Console]::In.ReadToEnd() does no line splitting, and
        // WriteAllText appends nothing; the encoding is named because the runner writes UTF-8
        // without a BOM and a recording in any other encoding would not compare equal.
        _ = script.Append(
            $"[System.IO.File]::WriteAllText('{Path.Combine(directory, "stdin.log")}', [Console]::In.ReadToEnd(), (New-Object System.Text.UTF8Encoding $false))\n");

        _ = perLineDelayMilliseconds > 0
            ? script.Append(
                $"Get-Content -LiteralPath '{payloadPath}' | ForEach-Object {{ $_; Start-Sleep -Milliseconds {perLineDelayMilliseconds} }}\n")
            : script.Append($"Get-Content -LiteralPath '{payloadPath}'\n");

        if (stderr.Length > 0)
        {

            _ = script.Append($"[Console]::Error.Write('{stderr.Replace("'", "''", StringComparison.Ordinal)}')\n");

        }

        _ = script.Append($"exit {exitCode}\n");

        File.WriteAllText(scriptPath, script.ToString());

        return new StubFamiliarCli(
            directory,
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath]);

    }

}
