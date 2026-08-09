using System.Text;

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

    public void Dispose()
    {

        try
        {

            Directory.Delete(_directory, recursive: true);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            // A leftover temp directory is not worth failing a test over.

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

        _ = script.Append(
            $"$input | Out-String | Set-Content -LiteralPath '{Path.Combine(directory, "stdin.log")}'\n");

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
