using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Infrastructure.Familiars;

/// <summary>
/// The one place Arcanum starts a Familiar.
/// </summary>
/// <remarks>
/// Deliberately not <c>CappedChildProcessRunner</c>. That runner exists for code the <em>model</em>
/// chose to run, so it wraps every spawn in a filesystem jail, OS resource limits, and a
/// process-group supervisor that rewrites the process image — and it has no stdin and no
/// incremental output. A Familiar is the opposite case: an operator-configured binary running an
/// Arcanum-authored argument list, which needs to reach the network and read its own auth store to
/// work at all, and which streams NDJSON that must be projected as it arrives. What the two share
/// is the security discipline, and that is reused directly: the same environment scrubber and the
/// same kill-tree teardown.
/// </remarks>
public sealed class FamiliarProcessRunner(ILogger<FamiliarProcessRunner>? logger = null) : IFamiliarProcessRunner
{

    public async IAsyncEnumerable<string> RunLinesAsync(
        FamiliarProcessRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        using Process process = CreateProcess(request);

        using CancellationTokenSource deadline = CreateDeadline(request, cancellationToken);

        Start(process, request);

        // Registered before the first read so a cancellation that lands mid-stream still reaps the
        // tree; a Familiar that ignores a closed stdout would otherwise outlive the turn.
        await using CancellationTokenRegistration teardown = deadline.Token.Register(
            static state => KillQuietly((Process)state!),
            process);

        StringBuilder standardError = new();

        Task errorPump = DrainStandardErrorAsync(process, standardError, deadline.Token);

        try
        {

            await WriteStandardInputOrTimeoutAsync(
                process,
                request,
                standardError,
                deadline.Token,
                cancellationToken).ConfigureAwait(false);

            while (true)
            {

                string? line;

                try
                {

                    line = await process.StandardOutput
                        .ReadLineAsync(deadline.Token)
                        .ConfigureAwait(false);

                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {

                    throw TimedOut(request, standardError);

                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                yield return Clamp(line, FamiliarProcessLimits.MaxLineCharacters);

            }

            try
            {

                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {

                throw TimedOut(request, standardError);

            }

            await AwaitErrorPumpAsync(errorPump).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {

                // Reported after the frames, not instead of them: a CLI that streams a partial answer
                // and then fails must never read as a clean short completion.
                throw NonZeroExit(request, process.ExitCode, standardError);

            }

        }
        finally
        {

            // A consumer that stops enumerating early — a client disconnect, a projection that has
            // what it needs — leaves a CLI with nowhere to write. Reaping here means no Familiar can
            // outlive the turn that asked for it, whether the stream ended, faulted, or was dropped.
            KillQuietly(process);

        }

    }

    public async Task<FamiliarProcessOutput> RunToCompletionAsync(
        FamiliarProcessRequest request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        using Process process = CreateProcess(request);

        using CancellationTokenSource deadline = CreateDeadline(request, cancellationToken);

        try
        {

            Start(process, request);

        }
        catch (FamiliarProcessException ex)
        {

            return new FamiliarProcessOutput(ex.Failure, ExitCode: 0, string.Empty, ex.Message);

        }

        await using CancellationTokenRegistration teardown = deadline.Token.Register(
            static state => KillQuietly((Process)state!),
            process);

        StringBuilder standardError = new();

        Task errorPump = DrainStandardErrorAsync(process, standardError, deadline.Token);

        await WriteStandardInputAsync(process, request.StandardInput, deadline.Token).ConfigureAwait(false);

        StringBuilder standardOutput = new();

        try
        {

            char[] buffer = new char[4096];

            while (true)
            {

                int read = await process.StandardOutput
                    .ReadAsync(buffer.AsMemory(), deadline.Token)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                Append(standardOutput, buffer.AsSpan(0, read), FamiliarProcessLimits.MaxBufferedStandardOutputCharacters);

            }

            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {

            return new FamiliarProcessOutput(
                FamiliarProcessFailure.TimedOut,
                ExitCode: 0,
                standardOutput.ToString(),
                ReadTail(standardError));

        }

        await AwaitErrorPumpAsync(errorPump).ConfigureAwait(false);

        return new FamiliarProcessOutput(
            process.ExitCode == 0 ? FamiliarProcessFailure.None : FamiliarProcessFailure.NonZeroExit,
            process.ExitCode,
            standardOutput.ToString(),
            ReadTail(standardError));

    }

    private static Process CreateProcess(FamiliarProcessRequest request)
    {

        // ArgumentList only. A single command string would let any value Arcanum interpolates —
        // a model name, a path — be re-parsed as further arguments by the OS or a shell.
        // Spawn the file resolution found, not the bare name. On Windows the resolver applies
        // PATHEXT, so a CLI installed as `claude.cmd` resolves as installed but would never start
        // from a bare `claude` — the probe would report ready for something that cannot run.
        string fileName = FamiliarExecutableResolver.TryResolve(request.FileName, out string? resolved)
            ? resolved!
            : request.FileName;

        ProcessStartInfo startInfo = new()
        {

            FileName = fileName,

            UseShellExecute = false,

            RedirectStandardInput = true,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            CreateNoWindow = true,

        };

        foreach (string argument in request.Arguments ?? [])
        {

            if (argument is not null)
            {
                startInfo.ArgumentList.Add(argument);
            }

        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        ChildProcessEnvironmentScrubber.ApplyProfile(startInfo, ChildProcessEnvironmentProfile.Familiar);

        // Arcanum's own provider keys are Arcanum's to hold. The CLI's vendor credentials are the
        // operator's own configuration and are deliberately left alone — Arcanum invokes a Familiar,
        // it does not manage how that Familiar authenticates.
        foreach (string name in request.DeniedEnvironmentVariables ?? [])
        {

            if (!string.IsNullOrWhiteSpace(name))
            {
                _ = startInfo.Environment.Remove(name);
            }

        }

        return new Process { StartInfo = startInfo };

    }

    private static CancellationTokenSource CreateDeadline(
        FamiliarProcessRequest request,
        CancellationToken cancellationToken)
    {

        CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        TimeSpan timeout = request.Timeout > TimeSpan.Zero
            ? request.Timeout
            : FamiliarProcessLimits.DefaultTimeout;

        deadline.CancelAfter(timeout);

        return deadline;

    }

    private void Start(Process process, FamiliarProcessRequest request)
    {

        try
        {

            _ = process.Start();

        }
        catch (Win32Exception ex)
        {

            // The distinction matters to the operator: "install it" and "your OS refused" have
            // different fixes, and neither is "check your Arcanum configuration".
            throw ex.NativeErrorCode is 2 or 3
                ? NotInstalled(request, ex)
                : new FamiliarProcessException(
                    FamiliarProcessFailure.StartFailed,
                    $"'{request.FileName}' could not be started ({ex.NativeErrorCode}). Check that it is executable.");

        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {

            logger?.LogError(
                "Familiar '{FileName}' failed to start ({ExceptionType}).",
                request.FileName,
                ex.GetType().Name);

            throw new FamiliarProcessException(
                FamiliarProcessFailure.StartFailed,
                $"'{request.FileName}' could not be started on this host.");

        }

    }

    private static FamiliarProcessException NotInstalled(FamiliarProcessRequest request, Exception? cause = null)
    {

        _ = cause;

        return new FamiliarProcessException(
            FamiliarProcessFailure.NotInstalled,
            $"'{request.FileName}' was not found. Arcanum never installs a Familiar — install the CLI yourself, or set the provider's `command` to its full path.");

    }

    private static FamiliarProcessException TimedOut(
        FamiliarProcessRequest request,
        StringBuilder standardError) =>
        new(
            FamiliarProcessFailure.TimedOut,
            $"'{request.FileName}' did not finish within {request.Timeout.TotalSeconds:0} seconds and its process tree was terminated.",
            standardError: ReadTail(standardError));

    private static FamiliarProcessException NonZeroExit(
        FamiliarProcessRequest request,
        int exitCode,
        StringBuilder standardError)
    {

        string tail = ReadTail(standardError);

        string detail = tail.Length > 0
            ? $" {tail.Trim()}"
            : string.Empty;

        return new FamiliarProcessException(
            FamiliarProcessFailure.NonZeroExit,
            $"'{request.FileName}' exited with code {exitCode}.{detail}",
            exitCode,
            tail);

    }

    private static async Task WriteStandardInputAsync(
        Process process,
        string? standardInput,
        CancellationToken cancellationToken)
    {

        try
        {


            if (!string.IsNullOrEmpty(standardInput))
            {

                await process.StandardInput
                    .WriteAsync(standardInput.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);

            }

            // Closed either way: a Familiar reading its prompt from stdin waits forever otherwise.
            process.StandardInput.Close();

        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {

            // A CLI that rejected the invocation before reading its prompt closes the pipe first.
            // Its exit code and stderr are the real diagnostic; a broken pipe here is noise.

        }

    }

    /// <summary>
    /// Writes the prompt, turning a deadline that lands mid-write into the same typed timeout every
    /// other stage produces. A raw <see cref="OperationCanceledException"/> escaping here would slip
    /// past the adapter's typed catch and reach the operator as a cancellation rather than a
    /// Familiar that never answered.
    /// </summary>
    private static async Task WriteStandardInputOrTimeoutAsync(
        Process process,
        FamiliarProcessRequest request,
        StringBuilder standardError,
        CancellationToken deadlineToken,
        CancellationToken callerToken)
    {

        try
        {

            await WriteStandardInputAsync(process, request.StandardInput, deadlineToken)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {

            throw TimedOut(request, standardError);

        }

    }

    private static async Task DrainStandardErrorAsync(
        Process process,
        StringBuilder sink,
        CancellationToken cancellationToken)
    {

        try
        {

            char[] buffer = new char[1024];

            while (true)
            {

                int read = await process.StandardError
                    .ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    return;
                }

                lock (sink)
                {
                    Append(sink, buffer.AsSpan(0, read), FamiliarProcessLimits.MaxStandardErrorCharacters);
                }

            }

        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {

            // stderr is a diagnostic, never the outcome — losing its tail must not fail the turn.

        }

    }

    private static async Task AwaitErrorPumpAsync(Task errorPump)
    {

        try
        {

            await errorPump.ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }

    }

    /// <summary>
    /// Snapshots the stderr tail under the pump's lock. On the timeout path the pump is still
    /// running, and <see cref="StringBuilder"/> is not safe to read while another thread appends.
    /// </summary>
    private static string ReadTail(StringBuilder sink)
    {

        lock (sink)
        {
            return sink.ToString();
        }

    }

    private static void Append(StringBuilder sink, ReadOnlySpan<char> chunk, int limit)
    {

        int remaining = limit - sink.Length;

        if (remaining <= 0)
        {
            return;
        }

        _ = sink.Append(chunk.Length <= remaining ? chunk : chunk[..remaining]);

    }

    private static string Clamp(string line, int limit) =>
        line.Length <= limit ? line : line[..limit];

    private static void KillQuietly(Process process)
    {

        try
        {

            if (!process.HasExited)
            {
                ProcessTreeKiller.TryKillEntireTree(process, context: "familiar");
            }

        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {

            // Already gone, or the OS will not say. Either way there is nothing left to reap.

        }

    }

}
