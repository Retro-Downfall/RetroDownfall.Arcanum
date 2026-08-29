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

    /// <summary>
    /// Both CLIs speak UTF-8 on every platform, so the transport pins it rather than inheriting the
    /// host's console code page — which on Windows is CP437 on a default console and CP_ACP with no
    /// console at all, and would silently mangle every non-ASCII prompt and answer. No preamble: a
    /// byte-order mark is three stray bytes in front of the prompt.
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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

        // Started rather than awaited, for the same reason stderr is pumped: until this returns the
        // child's stdout is unread, and a folded system prompt is orders of magnitude larger than
        // any OS pipe buffer. A CLI that emits a frame before it drains its prompt would fill its
        // own pipe, stop reading, and leave both sides waiting for the other until the deadline.
        Task standardInputWrite = WriteStandardInputOrTimeoutAsync(
            process,
            request,
            standardError,
            deadline.Token,
            cancellationToken);

        FamiliarStdoutLineReader reader = new(
            process.StandardOutput,
            FamiliarProcessLimits.MaxLineCharacters);

        try
        {

            while (true)
            {

                FamiliarStdoutLine? frame;

                try
                {

                    frame = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);

                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {

                    throw TimedOut(request, standardError);

                }

                if (frame is not { } line)
                {
                    break;
                }

                if (line.Exceeded)
                {

                    // Dropped whole rather than passed on cut at a character offset: the fragment is
                    // not valid JSON, so projection would discard it silently. Said out loud instead,
                    // and a turn whose terminal frame went this way still fails closed on the
                    // missing-terminal-frame check rather than reading as a short answer.
                    logger?.LogWarning(
                        "Familiar '{FileName}' emitted a frame longer than {Limit} characters; the frame was dropped.",
                        request.FileName,
                        FamiliarProcessLimits.MaxLineCharacters);

                    continue;

                }

                if (string.IsNullOrWhiteSpace(line.Text))
                {
                    continue;
                }

                yield return line.Text;

            }

            // Awaited before the exit code is read so a prompt the deadline cut short is reported as
            // this turn's timeout rather than as whatever the half-fed child happened to exit with.
            await standardInputWrite.ConfigureAwait(false);

            try
            {

                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {

                throw TimedOut(request, standardError);

            }

            await AwaitErrorPumpAsync(errorPump).ConfigureAwait(false);

            // The verdict is read off the deadline rather than inferred from an exception, because on
            // one supported platform no exception arrives. A pending Windows pipe read is not
            // cancelled: the registration above kills the tree, the read ends at EOF instead of
            // throwing, and WaitForExitAsync then returns without consulting the token because the
            // child has already gone — so control reaches here holding the killed child's exit code,
            // and the operator is told the Familiar failed when it in fact ran out of time. That is
            // the one distinction a deadline exists to draw. Off Windows the same run raises
            // OperationCanceledException from the read and the arms above answer first; both routes
            // have to give the same answer, so this one depends on neither.
            // The caller's own token is asked first: the deadline is linked to it and reads as
            // cancelled for either cause, and a caller that stopped the turn itself is owed its
            // cancellation rather than this turn's timeout classification.
            cancellationToken.ThrowIfCancellationRequested();

            if (deadline.IsCancellationRequested)
            {
                throw TimedOut(request, standardError);
            }

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

            await AwaitStandardInputWriteAsync(standardInputWrite).ConfigureAwait(false);

        }

    }

    public async Task<FamiliarProcessOutput> RunToCompletionAsync(
        FamiliarProcessRequest request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        Process created;

        try
        {

            // A command PATH cannot resolve fails here rather than at the spawn, so the probe still
            // gets a classification instead of an exception it would have to catch.
            created = CreateProcess(request);

        }
        catch (FamiliarProcessException ex)
        {

            return new FamiliarProcessOutput(ex.Failure, ExitCode: 0, string.Empty, ex.Message);

        }

        using Process process = created;

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

        // Overlapped with the drain below for the same reason as the streaming path: the probe sends
        // no stdin today, but any future caller that does would otherwise inherit the same wedge.
        Task standardInputWrite = WriteStandardInputAsync(process, request.StandardInput, deadline.Token);

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

            await AwaitErrorPumpAsync(errorPump).ConfigureAwait(false);

            // Decided from the deadline's own state, for the reason the streaming path states at
            // length: the read that ends at EOF instead of cancelling is the ordinary Windows case,
            // and this path is the one the status probe reads, where a Familiar that never answered
            // must not be classified as one that answered badly.
            cancellationToken.ThrowIfCancellationRequested();

            if (deadline.IsCancellationRequested)
            {
                return TimedOutOutput(standardOutput, standardError);
            }

            return new FamiliarProcessOutput(
                process.ExitCode == 0 ? FamiliarProcessFailure.None : FamiliarProcessFailure.NonZeroExit,
                process.ExitCode,
                standardOutput.ToString(),
                ReadTail(standardError));

        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {

            return TimedOutOutput(standardOutput, standardError);

        }
        finally
        {

            // In the finally rather than on each exit, exactly as the streaming path does it. The two
            // returns above are not the only ways out: a caller that cancels its own token fails the
            // `when` filter, so the read loop's OperationCanceledException propagates instead — and the
            // prompt write, whose pipe the teardown registration has just killed the process under,
            // would be left to surface later as an unobserved faulted task rather than as this turn's
            // outcome. Cheap on the ordinary path, where the write has long since completed.
            await AwaitStandardInputWriteAsync(standardInputWrite).ConfigureAwait(false);

        }

    }

    private static Process CreateProcess(FamiliarProcessRequest request) =>
        new() { StartInfo = BuildStartInfo(request) };

    /// <summary>
    /// Builds the spawn exactly as <see cref="CreateProcess"/> will run it. Internal so the spawn
    /// boundary can be asserted directly: the properties that matter here — argv never becoming a
    /// command string, the streams never being decoded with the host's code page — are properties of
    /// the <see cref="ProcessStartInfo"/>, and on a Unix test host the defaults happen to be right,
    /// so only the start info itself pins them for every platform Arcanum ships on.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(FamiliarProcessRequest request)
    {

        // ArgumentList only. A single command string would let any value Arcanum interpolates —
        // a model name, a path — be re-parsed as further arguments by the OS or a shell.
        // Spawn the file resolution found, and nothing else. Falling back to the bare name would
        // hand resolution back to the OS, whose search order — CreateProcess on Windows, and .NET's
        // deliberately matching walk on Unix — reaches the application directory and the caller's
        // current directory before PATH, so a repository Arcanum happened to be started in could
        // supply the binary. A resolver miss means PATH has no answer, and the operator needs to
        // hear exactly that. On Windows the resolver applies PATHEXT, so a CLI installed as
        // `claude.cmd` starts from the path that resolved rather than from a bare `claude`.
        if (!FamiliarExecutableResolver.TryResolve(request.FileName, out string? fileName))
        {
            throw NotInstalled(request);
        }

        ProcessStartInfo startInfo = new()
        {

            FileName = fileName!,

            UseShellExecute = false,

            RedirectStandardInput = true,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            StandardInputEncoding = Utf8NoBom,

            StandardOutputEncoding = Utf8NoBom,

            StandardErrorEncoding = Utf8NoBom,

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

        return startInfo;

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

    /// <summary>
    /// The buffered path's timeout verdict, in one place because two routes reach it: the deadline
    /// state the run is judged by, and the cancelled read that only some platforms raise.
    /// </summary>
    private static FamiliarProcessOutput TimedOutOutput(
        StringBuilder standardOutput,
        StringBuilder standardError) =>
        new(
            FamiliarProcessFailure.TimedOut,
            ExitCode: 0,
            standardOutput.ToString(),
            ReadTail(standardError));

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
    /// Observes the prompt write on every exit path, including the one where the consumer abandons
    /// the stream and the teardown above breaks the pipe under it. Left unobserved, its view of that
    /// same teardown would surface later as an unobserved faulted task instead of as this turn's
    /// outcome — which the read loop has already decided by the time this runs.
    /// </summary>
    private static async Task AwaitStandardInputWriteAsync(Task standardInputWrite)
    {

        try
        {

            await standardInputWrite.ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is FamiliarProcessException or OperationCanceledException or IOException or ObjectDisposedException)
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

/// <summary>One NDJSON frame from a Familiar, and whether it outgrew the frame ceiling.</summary>
internal readonly record struct FamiliarStdoutLine(string Text, bool Exceeded);

/// <summary>
/// Reads NDJSON frames from a Familiar's stdout with the frame ceiling applied while reading.
/// </summary>
/// <remarks>
/// <see cref="StreamReader.ReadLineAsync(CancellationToken)"/> accumulates a whole line before
/// anything can clamp it, so a CLI that writes without a newline grows the host's heap until EOF or
/// the deadline — a quarter of an hour, by default, in which to OOM a host that is serving every
/// other request too. Reading fixed chunks and splitting them here makes
/// <see cref="FamiliarProcessLimits.MaxLineCharacters"/> the allocation ceiling it says it is: once
/// a frame passes it the rest is discarded as it arrives rather than accumulated, and the frame is
/// reported as oversize instead of being handed on as an unparseable fragment.
/// </remarks>
internal sealed class FamiliarStdoutLineReader(TextReader reader, int maxLineCharacters)
{

    private readonly char[] _buffer = new char[4096];

    private int _length;

    private int _index;

    /// <summary>The next frame, or null once the stream has ended.</summary>
    public async ValueTask<FamiliarStdoutLine?> ReadLineAsync(CancellationToken cancellationToken)
    {

        StringBuilder line = new();

        bool exceeded = false;

        while (true)
        {

            if (_index >= _length)
            {

                _length = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

                _index = 0;

                if (_length <= 0)
                {

                    return line.Length == 0 && !exceeded
                        ? null
                        : new FamiliarStdoutLine(Render(line), exceeded);

                }

            }

            int start = _index;

            int newline = Array.IndexOf(_buffer, '\n', start, _length - start);

            int segment = newline >= 0 ? newline - start : _length - start;

            int room = maxLineCharacters - line.Length;

            if (segment > room)
            {

                // Past the ceiling the remainder is read and thrown away rather than kept, so the
                // rest of this frame costs the pipe buffer and nothing more.
                exceeded = true;

            }

            _ = line.Append(_buffer, start, Math.Min(segment, room));

            _index = start + segment;

            if (newline >= 0)
            {

                _index++;

                return new FamiliarStdoutLine(Render(line), exceeded);

            }

        }

    }

    /// <summary>
    /// Drops the carriage return of a CRLF terminator, which is what
    /// <see cref="StreamReader.ReadLineAsync(CancellationToken)"/> did and what a Windows-hosted CLI
    /// still emits.
    /// </summary>
    private static string Render(StringBuilder line) =>
        line.Length > 0 && line[^1] == '\r'
            ? line.ToString(0, line.Length - 1)
            : line.ToString();

}
