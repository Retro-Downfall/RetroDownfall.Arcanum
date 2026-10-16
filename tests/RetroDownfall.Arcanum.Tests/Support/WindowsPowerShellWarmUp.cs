using System.Diagnostics;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Runs Windows PowerShell 5.1 once per test process, outside every measured run, so a test that spawns it
/// proves what it set out to prove and not how fast a cold PowerShell starts. A cold start loads the engine,
/// has its assemblies scanned and auto-loads the modules the first cmdlet needs, and on a loaded
/// <c>windows-latest</c> runner that has taken longer than ten seconds, which is longer than several tests'
/// deadlines. After one run the same work is in the file cache and a start is a second or two.
/// </summary>
/// <remarks>
/// The warm-up is not a measurement and nothing asserts on it: its output and exit status are ignored, and the
/// only way it fails a test is a PowerShell that does not finish at all, which the test would have hung on
/// anyway. It runs the cmdlets and .NET calls the tests' scripts use, once as a <c>-Command</c> and once as a
/// <c>-File</c> script (the shape <c>StubFamiliarCli</c> uses). Awaiting it is a no-op everywhere but Windows.
/// </remarks>
internal static class WindowsPowerShellWarmUp
{
    /// <summary>
    /// How long one warm-up run may take. It is not a deadline any test measures; it only keeps a PowerShell that
    /// never answers from hanging the suite.
    /// </summary>
    private static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(2);

    private static readonly Lazy<Task> Warmed = new(WarmAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Completes once Windows PowerShell has run its warm-up in this process. Every caller shares the one run.
    /// </summary>
    public static Task EnsureAsync() =>
        OperatingSystem.IsWindows()
            ? Warmed.Value
            : Task.CompletedTask;

    private static async Task WarmAsync()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-powershell-warm-up-" + Guid.NewGuid().ToString("N"));

        _ = Directory.CreateDirectory(directory);

        try
        {
            string scratch = Path.Combine(directory, "scratch.txt");

            string scriptPath = Path.Combine(directory, "warm-up.ps1");

            string script = WarmUpScript(scratch);

            await File.WriteAllTextAsync(scriptPath, script);

            await RunAsync(["-NoProfile", "-NonInteractive", "-Command", script]);

            await RunAsync(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath]);
        }
        finally
        {
            _ = TestDirectoryCleanup.TryDelete(directory, nameof(WindowsPowerShellWarmUp));
        }
    }

    /// <summary>
    /// The cmdlets and .NET calls the tests' PowerShell children use: pipeline output, <c>Start-Sleep</c>,
    /// <c>Set-Content</c> and <c>Get-Content</c>, an <c>env:</c> listing, a read of stdin to its end (the
    /// warm-up closes stdin, so it returns at once) and a write to stdout.
    /// </summary>
    private static string WarmUpScript(string scratch)
    {
        string path = scratch.Replace("'", "''", StringComparison.Ordinal);

        return $"Set-Content -LiteralPath '{path}' -Value 'warm'; "
            + $"Get-Content -LiteralPath '{path}' | ForEach-Object {{ $_; Start-Sleep -Milliseconds 1 }}; "
            + "Get-ChildItem env: | ForEach-Object { '{0}={1}' -f $_.Name, $_.Value } | Out-Null; "
            + $"[System.IO.File]::WriteAllText('{path}', [Console]::In.ReadToEnd(), (New-Object System.Text.UTF8Encoding $false)); "
            + "[Console]::Out.Write(('x' * 16)); "
            + "Write-Output $env:SystemRoot";
    }

    private static async Task RunAsync(string[] arguments)
    {
        string powerShell = Path.Combine(
            global::System.Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        ProcessStartInfo startInfo = new(powerShell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using global::System.Diagnostics.Process warmUp = global::System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows PowerShell did not start for its warm-up.");

        warmUp.StandardInput.Close();

        Task<string> standardOutput = warmUp.StandardOutput.ReadToEndAsync();

        Task<string> standardError = warmUp.StandardError.ReadToEndAsync();

        using CancellationTokenSource limit = new(RunLimit);

        try
        {
            await warmUp.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            warmUp.Kill(entireProcessTree: true);

            throw new InvalidOperationException($"Windows PowerShell did not finish its warm-up within {RunLimit}.");
        }

        _ = await Task.WhenAll(standardOutput, standardError);
    }
}
