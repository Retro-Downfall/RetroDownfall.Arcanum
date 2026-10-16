using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using RetroDownfall.Arcanum.Infrastructure.Security;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// The environment every child process of the Long Rest qualification runs in: one private temporary root, a synthetic home
/// beneath the credential policy's <c>arcanum-tests</c> boundary, the in-memory credential store the testing guard enables
/// there (so no keychain item is read, written or prompted for), and nothing inherited from the operator's shell but the
/// executable search path.
/// </summary>
internal sealed class LongRestQualificationEnvironment(PublishedTestProcessIsolation isolation, string workspace)
{
    private const string TestCredentialOptInVariable = "ARCANUM_TEST_IN_MEMORY_CREDENTIALS";

    public PublishedTestProcessIsolation Isolation { get; } = isolation;

    public string Workspace { get; } = workspace;

    public string TestHome => Isolation.TestHome;

    public string ConfigurationDirectory => Path.Combine(TestHome, ".config", "arcanum");

    public string ConfigurationPath => Path.Combine(ConfigurationDirectory, "arcanum.json");

    public string? MasterApiKey { get; set; }

    public ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments)
    {
        ProcessStartInfo start = new(executable)
        {
            WorkingDirectory = Workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        string? systemRoot = global::System.Environment.GetEnvironmentVariable("SystemRoot");

        string? comSpec = global::System.Environment.GetEnvironmentVariable("ComSpec");

        string? path = global::System.Environment.GetEnvironmentVariable("PATH");

        start.Environment.Clear();

        CopyIfPresent(start, "SystemRoot", systemRoot);

        CopyIfPresent(start, "ComSpec", comSpec);

        CopyIfPresent(start, "PATH", path);

        start.Environment["DOTNET_ENVIRONMENT"] = "Testing";

        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";

        start.Environment["ARCANUM_TEST_HOME"] = TestHome;

        start.Environment[TestCredentialOptInVariable] = "1";

        start.Environment[LongRestQualificationConfiguration.ChatCredentialVariable] =
            LongRestQualificationConfiguration.ChatCredentialPlaceholder;

        start.Environment[LongRestQualificationConfiguration.EmbeddingCredentialVariable] =
            LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder;

        start.Environment["ARCANUM_NO_AUTO_SERVE"] = "1";

        start.Environment["NO_COLOR"] = "1";

        start.Environment["HOME"] = TestHome;

        start.Environment["USERPROFILE"] = TestHome;

        start.Environment["DOTNET_CLI_HOME"] = TestHome;

        start.Environment["APPDATA"] = Path.Combine(TestHome, "AppData", "Roaming");

        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(TestHome, ".config");

        start.Environment["XDG_DATA_HOME"] = Path.Combine(TestHome, ".local", "share");

        start.Environment["XDG_CACHE_HOME"] = Path.Combine(TestHome, ".cache");

        Isolation.ApplyTemporaryEnvironment(start);

        AssertInMemoryCredentialBackend(start);

        return start;
    }

    /// <summary>
    /// Proves, from the environment a child is about to be given, that the product's own testing gate will select the in-memory credential
    /// store, so that the published host cannot reach the operating-system credential store (and prompt for the keychain) when it first
    /// writes a secret. The gate falls back to the real store whenever any of its conditions fails, and nothing else would notice. The
    /// failure names the variables and never their values.
    /// </summary>
    internal static void AssertInMemoryCredentialBackend(ProcessStartInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);

        _ = start.Environment.TryGetValue("DOTNET_ENVIRONMENT", out string? dotnetEnvironment);

        _ = start.Environment.TryGetValue("ASPNETCORE_ENVIRONMENT", out string? aspNetCoreEnvironment);

        _ = start.Environment.TryGetValue("ARCANUM_TEST_HOME", out string? testHome);

        _ = start.Environment.TryGetValue(TestCredentialOptInVariable, out string? optIn);

        _ = start.Environment.TryGetValue(OperatingSystem.IsWindows() ? "TMP" : "TMPDIR", out string? temporaryDirectory);

        if (!string.Equals(dotnetEnvironment, aspNetCoreEnvironment, StringComparison.Ordinal)
            || !TestCredentialStorePolicy.IsEnabled(dotnetEnvironment, testHome, optIn, temporaryDirectory))
        {
            throw new InvalidOperationException(
                "The isolated installation would not select the in-memory credential store: the product's testing gate "
                + $"(DOTNET_ENVIRONMENT and ASPNETCORE_ENVIRONMENT agreeing on Testing, ARCANUM_TEST_HOME beneath the rebased temporary directory, and {TestCredentialOptInVariable}) did not hold, "
                + "so the published host could reach the operating-system credential store. It is not started.");
        }
    }

    public string Redact(string value) =>
        LocalOllamaQualificationGuards.Redact(
            value,
            LongRestQualificationConfiguration.ChatCredentialPlaceholder,
            MasterApiKey,
            LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder);

    public InvalidOperationException CreateSanitizedFailure(string context, Exception failure) =>
        LocalOllamaQualificationGuards.CreateSanitizedFailure(
            context,
            failure,
            LongRestQualificationConfiguration.ChatCredentialPlaceholder,
            MasterApiKey,
            LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder);

    private static void CopyIfPresent(ProcessStartInfo start, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            start.Environment[name] = value;
        }
    }
}

/// <summary>The captured result of a completed published CLI command.</summary>
internal sealed record LongRestCliResult(string StandardOutput, string StandardError);

/// <summary>
/// One <c>arcanum serve</c> process of the published apphost. It can be asked to quit through the API, or killed with
/// SIGKILL, which is how the qualification interrupts a sweep in flight.
/// </summary>
internal sealed class LongRestPublishedHost
{
    private static readonly TimeSpan CleanupAttemptBudget = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueue<string> _safeLines = new();

    private readonly DiagnosticsProcess _process;

    private readonly LongRestQualificationEnvironment _environment;

    private readonly string? _knownMasterApiKey;

    private readonly TaskCompletionSource<string> _masterApiKey =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Task _standardOutput;

    private readonly Task _standardError;

    private int _cleanupStarted;

    private LongRestPublishedHost(DiagnosticsProcess process, string? knownMasterApiKey, LongRestQualificationEnvironment environment)
    {
        _process = process;

        _knownMasterApiKey = knownMasterApiKey;

        _environment = environment;

        _standardOutput = PumpAsync(process.StandardOutput, observeStartup: true);

        _standardError = PumpAsync(process.StandardError, observeStartup: false);
    }

    public string? DisclosedMasterApiKey { get; private set; }

    public int ExitCode => _process.ExitCode;

    public bool HasExited => _process.HasExited;

    public static LongRestPublishedHost Start(string executable, LongRestQualificationEnvironment environment, string? knownMasterApiKey)
    {
        ProcessStartInfo start = environment.CreateStartInfo(executable, ["serve", "--plain"]);

        DiagnosticsProcess process = DiagnosticsProcess.Start(start)
            ?? throw new InvalidOperationException("The published Arcanum host did not start.");

        return new LongRestPublishedHost(process, knownMasterApiKey, environment);
    }

    public async Task<string> WaitForInitialReadyAsync(TimeSpan timeout)
    {
        using CancellationTokenSource deadline = new(timeout);

        string key = await _masterApiKey.Task.WaitAsync(deadline.Token);

        await _ready.Task.WaitAsync(deadline.Token);

        return key;
    }

    public async Task WaitForRestartReadyAsync(TimeSpan timeout)
    {
        using CancellationTokenSource deadline = new(timeout);

        await _ready.Task.WaitAsync(deadline.Token);
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken);

        await Task.WhenAll(_standardOutput, _standardError).WaitAsync(cancellationToken);
    }

    /// <summary>SIGKILL without waiting, for a caller that must kill while a request of the host is in flight.</summary>
    public void KillNow()
    {
        if (!_process.HasExited)
        {
            _process.Kill();
        }
    }

    /// <summary>SIGKILL: the process gets no chance to settle anything, which is the point.</summary>
    public async Task KillAsync(TimeSpan timeout)
    {
        KillNow();

        using CancellationTokenSource deadline = new(timeout);

        await _process.WaitForExitAsync(deadline.Token);
    }

    public string SafeDiagnostics() =>
        string.Join(global::System.Environment.NewLine, _safeLines.Select(Redact));

    public async Task<IReadOnlyList<Exception>> CleanupAsync(string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
        {
            return [];
        }

        List<Exception> failures = [];

        Exception? terminationFailure = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }

                await _process.WaitForExitAsync().WaitAsync(CleanupAttemptBudget);

                terminationFailure = null;

                break;
            }
            catch (Exception exception)
            {
                terminationFailure = exception;
            }
        }

        if (terminationFailure is not null)
        {
            failures.Add(_environment.CreateSanitizedFailure(
                $"Long Rest qualification {phase} published-host termination did not complete after three bounded attempts.",
                terminationFailure));
        }

        Exception? drainFailure = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await Task.WhenAll(_standardOutput, _standardError).WaitAsync(CleanupAttemptBudget);

                drainFailure = null;

                break;
            }
            catch (Exception exception)
            {
                drainFailure = exception;
            }
        }

        if (drainFailure is not null)
        {
            failures.Add(_environment.CreateSanitizedFailure(
                $"Long Rest qualification {phase} published-host output cleanup did not complete after three bounded attempts."
                + global::System.Environment.NewLine
                + SafeDiagnostics(),
                drainFailure));
        }

        try
        {
            _process.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(_environment.CreateSanitizedFailure(
                $"Long Rest qualification {phase} published-host process-handle cleanup failed.",
                exception));
        }

        return failures;
    }

    private async Task PumpAsync(StreamReader reader, bool observeStartup)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (observeStartup && TryReadMasterApiKey(line, out string? key))
            {
                DisclosedMasterApiKey = key;

                _ = _masterApiKey.TrySetResult(key);

                Enqueue(LocalOllamaQualificationGuards.Redact(
                    line,
                    LongRestQualificationConfiguration.ChatCredentialPlaceholder,
                    key,
                    _knownMasterApiKey,
                    LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder));
            }
            else
            {
                Enqueue(Redact(line));
            }

            if (observeStartup && line.Contains("Listening on http://127.0.0.1:", StringComparison.Ordinal))
            {
                _ = _ready.TrySetResult();
            }
        }

        if (observeStartup)
        {
            string diagnostics = SafeDiagnostics();

            _ = _masterApiKey.TrySetException(new InvalidOperationException(
                "Published host exited before generating its initial master API key.\n" + diagnostics));

            _ = _ready.TrySetException(new InvalidOperationException(
                "Published host exited before reporting readiness.\n" + diagnostics));
        }
    }

    private string Redact(string line) =>
        LocalOllamaQualificationGuards.Redact(
            line,
            LongRestQualificationConfiguration.ChatCredentialPlaceholder,
            _knownMasterApiKey,
            DisclosedMasterApiKey,
            LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder);

    private void Enqueue(string line)
    {
        _safeLines.Enqueue(line);

        while (_safeLines.Count > 200)
        {
            _ = _safeLines.TryDequeue(out _);
        }
    }

    private static bool TryReadMasterApiKey(string line, out string key)
    {
        key = line.Trim();

        try
        {
            byte[] decoded = Convert.FromBase64String(key);

            bool isMasterApiKey = decoded.Length == 32;

            CryptographicOperations.ZeroMemory(decoded);

            return isMasterApiKey;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Runs one published CLI command to completion inside the isolated environment.</summary>
internal static class LongRestPublishedCli
{
    private static readonly TimeSpan CleanupAttemptBudget = TimeSpan.FromSeconds(5);

    public static async Task<LongRestCliResult> RunAsync(
        string executable,
        LongRestQualificationEnvironment environment,
        TimeSpan timeout,
        params string[] arguments)
    {
        ProcessStartInfo start = environment.CreateStartInfo(executable, arguments);

        DiagnosticsProcess process = DiagnosticsProcess.Start(start)
            ?? throw new InvalidOperationException("The published Arcanum CLI process did not start.");

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();

        Task<string> standardError = process.StandardError.ReadToEndAsync();

        using CancellationTokenSource deadline = new(timeout);

        Exception? primaryFailure = null;

        int? exitCode = null;

        try
        {
            await process.WaitForExitAsync(deadline.Token);

            exitCode = process.ExitCode;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            primaryFailure = environment.CreateSanitizedFailure(
                $"Published CLI command exceeded {timeout}: {environment.Redact(SafeCommand(arguments))}",
                new TimeoutException("The bounded command deadline elapsed."));
        }
        catch (Exception exception)
        {
            primaryFailure = environment.CreateSanitizedFailure(
                $"Published CLI command wait failed: {environment.Redact(SafeCommand(arguments))}",
                exception);
        }

        Exception? terminationFailure = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync().WaitAsync(CleanupAttemptBudget);

                terminationFailure = null;

                break;
            }
            catch (Exception exception)
            {
                terminationFailure = exception;
            }
        }

        string output = string.Empty;

        string error = string.Empty;

        try
        {
            await Task.WhenAll(standardOutput, standardError).WaitAsync(CleanupAttemptBudget);

            output = environment.Redact(standardOutput.Result);

            error = environment.Redact(standardError.Result);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            primaryFailure ??= environment.CreateSanitizedFailure("Published CLI output drain did not complete.", exception);
        }

        process.Dispose();

        if (primaryFailure is null && terminationFailure is not null)
        {
            primaryFailure = environment.CreateSanitizedFailure(
                "Published CLI process termination did not complete after three bounded attempts.",
                terminationFailure);
        }

        if (primaryFailure is null && exitCode is not 0)
        {
            primaryFailure = environment.CreateSanitizedFailure(
                $"Published CLI command exited {exitCode}: {environment.Redact(SafeCommand(arguments))}\nstdout:\n{output}\nstderr:\n{error}",
                new InvalidOperationException("The command returned a non-success exit code."));
        }

        if (primaryFailure is not null)
        {
            throw primaryFailure;
        }

        return new LongRestCliResult(output.Trim(), error.Trim());
    }

    private static string SafeCommand(IEnumerable<string> arguments) =>
        "arcanum " + string.Join(' ', arguments.Select(static argument =>
            argument.Contains(' ') ? "<prompt>" : argument));
}
