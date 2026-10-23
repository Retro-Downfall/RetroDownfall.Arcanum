using System.Diagnostics.CodeAnalysis;

using System.Reflection;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

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

    /// <summary>The arguments were not exactly the mode and a known format. Nothing was read or written.</summary>
    Usage = 2,

    /// <summary>Stdin carried more than the intake ceiling. Nothing was parsed or written.</summary>
    InputTooLarge = 3,

    /// <summary>Stdin could not be read. Nothing was parsed or written.</summary>
    InputUnreadable = 4,

    /// <summary>Reading and parsing outlived the worker's own deadline. Nothing was written.</summary>
    Deadline = 5,

    /// <summary>Extraction or the frame failed in a way no frame describes, or stdout could not be written.</summary>
    Fault = 6,
}

/// <summary>
/// The hidden worker mode <c>arcanum __comprehension-worker pdf</c>: it reads one PDF from stdin, extracts its text with
/// the bounded <see cref="PdfTextExtractor"/>, and writes one <see cref="ComprehensionWorkerFrame"/> to stdout. It never
/// reads configuration, the Grimoire, files or the network; the host runs it under the filesystem jail, with no
/// network, a cleared environment and code-owned process limits, so a hostile document can only end this process.
/// </summary>
internal static class ComprehensionWorkerEntry
{
    /// <summary>The first argument that selects the worker mode. Not advertised in help, completion or the command map.</summary>
    internal const string ModeArgument = "__comprehension-worker";

    /// <summary>The second argument, naming the one format the worker reads.</summary>
    internal const string PdfFormatArgument = "pdf";

    /// <summary>The most bytes the worker reads from stdin: the attachment intake ceiling for a non-image file.</summary>
    internal const long InputCeilingBytes = ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes;

    /// <summary>
    /// The worker's own deadline over reading and parsing, under the host's wall clock so the worker ends on its own
    /// before the host has to kill it. The parser checks cancellation only between pages, so the deadline does not wait
    /// for it: the worker stops waiting, writes nothing and exits, and the process takes the parser with it.
    /// </summary>
    internal static readonly TimeSpan SelfDeadline = TimeSpan.FromSeconds(8);

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

        exitCode = (int)Run(args, input, output, SelfDeadline);

        return true;
    }

    /// <summary>
    /// The worker itself, over any streams. Reading and extraction run on a pool thread under <paramref name="deadline"/>;
    /// the frame is written only if they finish in time, so a late result is never written after the deadline was reported.
    /// </summary>
    /// <param name="extract">The extraction; the production <see cref="PdfTextExtractor"/> with its default limits when omitted.</param>
    internal static ComprehensionWorkerExitCode Run(
        IReadOnlyList<string> arguments,
        Stream input,
        Stream output,
        TimeSpan deadline,
        Func<ReadOnlyMemory<byte>, CancellationToken, PdfExtractionOutcome>? extract = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        ArgumentNullException.ThrowIfNull(input);

        ArgumentNullException.ThrowIfNull(output);

        if (arguments.Count != 2
            || !string.Equals(arguments[0], ModeArgument, StringComparison.Ordinal)
            || !string.Equals(arguments[1], PdfFormatArgument, StringComparison.Ordinal))
        {
            return ComprehensionWorkerExitCode.Usage;
        }

        extract ??= static (bytes, cancellationToken) =>
            new PdfTextExtractor().Extract(bytes, PdfExtractionLimits.Default, cancellationToken);

        using CancellationTokenSource expiry = new();

        Task<WorkerVerdict> work = Task.Run(() => ReadAndExtract(input, extract, expiry.Token), CancellationToken.None);

        bool finished;

        try
        {
            finished = work.Wait(deadline);
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
        Func<ReadOnlyMemory<byte>, CancellationToken, PdfExtractionOutcome> extract,
        CancellationToken cancellationToken)
    {
        // One byte past the ceiling proves the input too large, so no more than that is ever read.
        byte[] buffer = new byte[InputCeilingBytes + 1];

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

        if (length > InputCeilingBytes)
        {
            return new WorkerVerdict(ComprehensionWorkerExitCode.InputTooLarge, null);
        }

        try
        {
            PdfExtractionOutcome outcome = extract(buffer.AsMemory(0, length), cancellationToken);

            return new WorkerVerdict(ComprehensionWorkerExitCode.FrameWritten, ComprehensionWorkerFrame.Encode(outcome));
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
