using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Hosting;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Shared classification from a failed <see cref="Result"/>'s <see cref="Error"/> to the CLI exit-code
/// contract (W10-3). Only five command sites classified a <c>Connection.*</c> failure as
/// <see cref="CliExitCode.NetworkError"/> before this; every other failed <c>Result</c> returned the
/// generic exit code, so scripts could not tell "arcanum serve is down" (retryable) from a real domain
/// failure. Every other failure keeps <see cref="CliExitCode.GenericError"/> unchanged.
/// </summary>
internal static class CliFailureExit
{

    private const string ConnectionErrorPrefix = "Connection.";

    /// <summary>The exit code a failed <see cref="Result"/> should return, mirroring BudgetCommands.Show.</summary>
    public static int ExitCode(Error error) =>
        IsConnectionFailure(error)
            ? (int)CliExitCode.NetworkError
            : (int)CliExitCode.GenericError;

    /// <summary>
    /// Names the base address the client tried on a <c>Connection.*</c> failure, so an operator on a
    /// non-default <c>Arcanum:Host</c> can see which address was unreachable; every other error is
    /// returned unchanged.
    /// </summary>
    public static Error Annotate(Error error, HostSettings host) =>
        IsConnectionFailure(error)
            ? error with { Message = $"{error.Message} (tried {ArcanumLocalApiAddress.ResolveBaseUrl(host)})" }
            : error;

    private static bool IsConnectionFailure(Error error) =>
        error.Code.StartsWith(ConnectionErrorPrefix, StringComparison.Ordinal);

}
