using System.Diagnostics.CodeAnalysis;

using System.Reflection;

using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The worker's exit codes: a small space the worker owns, so the host can tell what the worker said from what the
/// runtime or a shell said. Zero means a frame was written, and the frame carries the verdict about the document,
/// whatever it is; every other code means no frame was written. None is 126 (the resource-limit prelude's refusal) and
/// none is 128 or above (a shell's report of a signal, which is also how the runtime's own aborts surface).
/// </summary>
internal enum ComprehensionWorkerExitCode
{
    /// <summary>A frame was written to stdout. The document's verdict, whatever it is, is in the frame.</summary>
    FrameWritten = 0,

    /// <summary>
    /// The arguments were not exactly the mode and the argument of a worker kind this binary has an extractor for. Nothing
    /// was read or written.
    /// </summary>
    Usage = 2,

    /// <summary>Stdin carried more than the kind's input ceiling. Nothing was parsed or written.</summary>
    InputTooLarge = 3,

    /// <summary>Stdin could not be read. Nothing was parsed or written.</summary>
    InputUnreadable = 4,

    /// <summary>Reading and parsing outlived the kind's self-deadline. Nothing was written.</summary>
    Deadline = 5,

    /// <summary>
    /// Extraction or the frame failed in a way no frame describes (a verdict over the kind's output bound included), or
    /// stdout could not be written.
    /// </summary>
    Fault = 6,
}

/// <summary>
/// The hidden worker mode <c>arcanum __comprehension-worker &lt;kind&gt;</c>, the same for every kind of isolated document
/// worker: it resolves the kind's <see cref="DocumentWorkerProfile"/> from its argument, reads one document from stdin up
/// to the profile's input ceiling, runs the kind's extractor (<see cref="ExtractorFor"/>) under the profile's
/// self-deadline, and writes one <see cref="ComprehensionWorkerFrame"/> within the profile's output bound to stdout. It
/// never reads configuration, the Grimoire, files or the network; the host runs it under the filesystem jail, with no
/// network, a cleared environment and code-owned process limits, so a hostile document can only end this process.
/// </summary>
internal static class ComprehensionWorkerEntry
{
    /// <summary>The first argument that selects the worker mode. Not advertised in help, completion or the command map.</summary>
    internal const string ModeArgument = "__comprehension-worker";

    private static Assembly? _declaredHost;

    private static DocumentWorkerLaunch? _hostLaunch;

    /// <summary>
    /// Whether this process is a host whose re-execution with the worker arguments reaches <see cref="TryHandle"/>: the
    /// same rule as <see cref="SandboxExecHelper.IsBrokerCapableHost"/>, re-decided on every read.
    /// </summary>
    internal static bool IsWorkerCapableHost => SandboxExecHelper.DeclarationBindsToThisProcess(_declaredHost);

    /// <summary>
    /// This host's own binary as captured when the host started, or <see langword="null"/> when this process cannot
    /// re-launch itself as the worker.
    /// </summary>
    internal static DocumentWorkerLaunch? HostLaunch => IsWorkerCapableHost ? _hostLaunch : null;

    /// <summary>
    /// The extractor the worker mode runs for <paramref name="format"/>, or <see langword="null"/> when this binary has none,
    /// which the worker mode refuses as a usage error before it reads anything. Each kind's extractor gives a verdict for any
    /// bytes and keeps its own output within the kind's <see cref="DocumentWorkerProfile.MaxOutputCharacters"/>.
    /// </summary>
    internal static Func<ReadOnlyMemory<byte>, CancellationToken, DocumentExtractionOutcome>? ExtractorFor(DocumentWorkerFormat format) => format switch
    {
        DocumentWorkerFormat.Pdf => static (bytes, cancellationToken) =>
            new PdfTextExtractor().Extract(bytes, PdfExtractionLimits.Default, cancellationToken),
        _ => null,
    };

    /// <summary>
    /// Runs the worker when <paramref name="args"/> select it and the calling host is this process's own entry point,
    /// and reports its exit code; otherwise records the declaration and returns <see langword="false"/> so the host
    /// continues its normal start. An embedding host (a test host running another program's entry point) never runs the
    /// worker and never becomes worker-capable.
    /// </summary>
    [ExcludeFromCodeCoverage] // Reason: process entry glue over the real stdin and stdout, reached only as the first statement of a host's Main; Run, which it calls, is covered in-process by ComprehensionWorkerEntryTests, and the real re-launch by the process worker tests through the Cli apphost.
    internal static bool TryHandle(string[] args, Type hostEntryPoint, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);

        ArgumentNullException.ThrowIfNull(hostEntryPoint);

        exitCode = 0;

        if (!SandboxExecHelper.DeclarationBindsToThisProcess(hostEntryPoint.Assembly))
        {
            return false;
        }

        if (args.Length == 0 || !string.Equals(args[0], ModeArgument, StringComparison.Ordinal))
        {
            // The binary is captured now, at host start, so a binary replaced later is told apart from it.
            _hostLaunch = DocumentWorkerLaunch.ForHostExecutable(Environment.ProcessPath);

            _declaredHost = hostEntryPoint.Assembly;

            return false;
        }

        using Stream input = Console.OpenStandardInput();

        using Stream output = Console.OpenStandardOutput();

        exitCode = (int)Run(args, input, output);

        return true;
    }

    /// <summary>
    /// The worker mode as the host's binary runs it: <paramref name="arguments"/> must be exactly the mode and the argument
    /// of a kind that has a profile and an extractor here, or nothing is read; that kind's profile then bounds everything
    /// the worker does.
    /// </summary>
    /// <param name="extract">
    /// Replaces the kind's extractor for a test; the kind must still have one of its own, or the arguments are refused.
    /// </param>
    internal static ComprehensionWorkerExitCode Run(
        IReadOnlyList<string> arguments,
        Stream input,
        Stream output,
        Func<ReadOnlyMemory<byte>, CancellationToken, DocumentExtractionOutcome>? extract = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        ArgumentNullException.ThrowIfNull(input);

        ArgumentNullException.ThrowIfNull(output);

        if (arguments.Count != 2
            || !string.Equals(arguments[0], ModeArgument, StringComparison.Ordinal)
            || !DocumentWorkerProfile.TryForArgument(arguments[1], out DocumentWorkerProfile? profile)
            || ExtractorFor(profile.Format) is not { } extractor)
        {
            return ComprehensionWorkerExitCode.Usage;
        }

        return Run(profile, input, output, extract ?? extractor);
    }

    /// <summary>
    /// The worker itself, for one kind's profile, over any streams. Reading and extraction run on a pool thread under the
    /// profile's self-deadline; the frame is written only if they finish in time, so a late result is never written after
    /// the deadline was reported. The parser may check cancellation only now and then, so the deadline does not wait for
    /// it: the worker stops waiting, writes nothing and exits, and the process takes the parser with it.
    /// </summary>
    internal static ComprehensionWorkerExitCode Run(
        DocumentWorkerProfile profile,
        Stream input,
        Stream output,
        Func<ReadOnlyMemory<byte>, CancellationToken, DocumentExtractionOutcome> extract)
    {
        ArgumentNullException.ThrowIfNull(profile);

        ArgumentNullException.ThrowIfNull(input);

        ArgumentNullException.ThrowIfNull(output);

        ArgumentNullException.ThrowIfNull(extract);

        using CancellationTokenSource expiry = new();

        Task<WorkerVerdict> work = Task.Run(() => ReadAndExtract(input, profile, extract, expiry.Token), CancellationToken.None);

        bool finished;

        try
        {
            finished = work.Wait(profile.SelfDeadline);
        }
        catch (AggregateException)
        {
            return ComprehensionWorkerExitCode.Fault;
        }

        if (!finished)
        {
            // The parser may never look at the token; the process exit that follows ends it either way.
            expiry.Cancel();

            return ComprehensionWorkerExitCode.Deadline;
        }

        WorkerVerdict verdict = work.Result;

        if (verdict.Frame is null)
        {
            return verdict.Code;
        }

        try
        {
            output.Write(verdict.Frame);

            output.Flush();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or NotSupportedException)
        {
            return ComprehensionWorkerExitCode.Fault;
        }

        return ComprehensionWorkerExitCode.FrameWritten;
    }

    private static WorkerVerdict ReadAndExtract(
        Stream input,
        DocumentWorkerProfile profile,
        Func<ReadOnlyMemory<byte>, CancellationToken, DocumentExtractionOutcome> extract,
        CancellationToken cancellationToken)
    {
        // One byte past the ceiling proves the input too large, so no more than that is ever read.
        byte[] buffer = new byte[profile.InputCeilingBytes + 1];

        int length = 0;

        try
        {
            while (length < buffer.Length)
            {
                int read = input.Read(buffer, length, buffer.Length - length);

                if (read == 0)
                {
                    break;
                }

                length += read;
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or NotSupportedException)
        {
            return new WorkerVerdict(ComprehensionWorkerExitCode.InputUnreadable, null);
        }

        if (length > profile.InputCeilingBytes)
        {
            return new WorkerVerdict(ComprehensionWorkerExitCode.InputTooLarge, null);
        }

        try
        {
            DocumentExtractionOutcome outcome = extract(buffer.AsMemory(0, length), cancellationToken);

            return new WorkerVerdict(
                ComprehensionWorkerExitCode.FrameWritten,
                ComprehensionWorkerFrame.Encode(outcome, profile.MaxOutputCharacters));
        }
        catch (Exception)
        {
            // A cancellation lands here only after the deadline was already reported, so its verdict is never read.
            return new WorkerVerdict(ComprehensionWorkerExitCode.Fault, null);
        }
    }

    /// <summary>What reading and extraction concluded: a frame to write, or the code to exit with when there is none.</summary>
    private sealed record WorkerVerdict(ComprehensionWorkerExitCode Code, byte[]? Frame);
}
