namespace RetroDownfall.TheForge.Ux.Services.Git;

/// <summary>Outcome of a desktop-local <c>git</c> process invocation (The Ledger).</summary>
public sealed record GitProcessResult
{

    public int? ExitCode { get; init; }

    public string Stdout { get; init; } = string.Empty;

    public string Stderr { get; init; } = string.Empty;

    public bool Cancelled { get; init; }

    public bool FailedToStart { get; init; }

    public string? ErrorMessage { get; init; }

    public bool StdoutTruncated { get; init; }

    public bool StderrTruncated { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public static GitProcessResult Completed(
        int exitCode,
        string stdout,
        string stderr,
        IReadOnlyList<string> arguments,
        bool stdoutTruncated = false,
        bool stderrTruncated = false) =>
        new()
        {

            ExitCode = exitCode,

            Stdout = stdout,

            Stderr = stderr,

            Arguments = arguments,

            StdoutTruncated = stdoutTruncated,

            StderrTruncated = stderrTruncated,

        };

    public static GitProcessResult CancelledResult(IReadOnlyList<string> arguments) =>
        new()
        {

            Cancelled = true,

            Arguments = arguments,

        };

    public static GitProcessResult Failed(string message, IReadOnlyList<string> arguments) =>
        new()
        {

            FailedToStart = true,

            ErrorMessage = message,

            Arguments = arguments,

        };

}
