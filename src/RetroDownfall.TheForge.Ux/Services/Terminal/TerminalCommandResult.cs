namespace RetroDownfall.TheForge.Ux.Services.Terminal;

/// <summary>Outcome of a Hearth shell command run.</summary>
public sealed record TerminalCommandResult
{

    public int? ExitCode { get; init; }

    public bool Cancelled { get; init; }

    public bool FailedToStart { get; init; }

    public string? ErrorMessage { get; init; }

    public static TerminalCommandResult Completed(int exitCode) => new()
    {

        ExitCode = exitCode,

    };

    public static TerminalCommandResult CancelledResult() => new()
    {

        Cancelled = true,

        ExitCode = null,

    };

    public static TerminalCommandResult Failed(string message) => new()
    {

        FailedToStart = true,

        ErrorMessage = message,

        ExitCode = null,

    };

}
