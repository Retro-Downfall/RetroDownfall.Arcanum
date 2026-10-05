using System.ComponentModel;
using System.Diagnostics;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// The one bounded runner the three daemon managers share. A service-manager helper normally answers in
/// well under a second, so a child that has not finished by <see cref="DefaultTimeout"/> is hung: the
/// runner kills its whole process tree and reports <see cref="TimeoutErrorCode"/> instead of leaving
/// <c>arcanum daemon</c> waiting for it forever. Cancellation kills the tree the same way.
/// </summary>
internal sealed class DaemonProcessRunner(TimeSpan timeout) : IDaemonProcessRunner
{
    internal const string StartErrorCode = "DaemonProcessStart";

    internal const string TimeoutErrorCode = "DaemonProcessTimeout";

    /// <summary>
    /// Code-owned bound for one helper invocation.
    /// </summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    internal static DaemonProcessRunner Default { get; } = new(DefaultTimeout);

    public async Task<DaemonProcessOutcome> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new();

        process.StartInfo = startInfo;

        try
        {
            _ = process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        {
            return StartFailure(fileName, ex);
        }

        using CancellationTokenSource deadline = new(timeout);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);

        // The three awaitables are started together and joined by the very first statement of the protected
        // block that follows, which is the shape the hosted-producer inventory accepts for a process boundary
        // reached from a retained admission.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);

        Task<string> stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

        Task exitTask = process.WaitForExitAsync(linked.Token);

        try
        {
            await Task.WhenAll(exitTask, stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessTreeKiller.TryKillEntireTree(process, logger: null, context: fileName);

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new DaemonProcessOutcome(
                -1,
                string.Empty,
                string.Empty,
                new Error(
                    TimeoutErrorCode,
                    $"'{fileName}' did not finish within {timeout.TotalSeconds:0.###} seconds and was stopped."));
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            // The OS refused the caller access while the helper was running (Windows service control).
            ProcessTreeKiller.TryKillEntireTree(process, logger: null, context: fileName);

            return StartFailure(fileName, ex);
        }

        return new DaemonProcessOutcome(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false),
            null);
    }

    private static DaemonProcessOutcome StartFailure(string fileName, Exception exception) =>
        new(
            -1,
            string.Empty,
            string.Empty,
            new Error(StartErrorCode, $"Could not start '{fileName}'. {exception.Message}"),
            AccessDenied: IsAccessDenied(exception));

    private static bool IsAccessDenied(Exception exception) =>
        exception is UnauthorizedAccessException
        || OperatingSystem.IsWindows() && exception is Win32Exception { NativeErrorCode: ErrorAccessDenied };

    private const int ErrorAccessDenied = 5;
}
