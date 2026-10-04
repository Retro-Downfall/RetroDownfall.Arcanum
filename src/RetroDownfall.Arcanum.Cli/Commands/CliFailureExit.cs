using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Hosting;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Shared classification from a failed <see cref="Result"/>'s <see cref="Error"/> to the CLI exit-code
/// contract. Only five command sites classified a <c>Connection.*</c> failure as
/// <see cref="CliExitCode.NetworkError"/> before this; every other failed <c>Result</c> returned the
/// generic exit code, so scripts could not tell "arcanum serve is down" (retryable) from a real domain
/// failure. Every other failure keeps <see cref="CliExitCode.GenericError"/> unchanged.
/// </summary>
internal static class CliFailureExit
{
    private const string ConnectionErrorPrefix = "Connection.";

    /// <summary>The exit code a failed <see cref="Result"/> should return, mirroring BudgetCommands.Show.</summary>
    public static int ExitCode(Error error) =>
        ExitCode(error.Code);

    /// <summary>
    /// The exit code for a failure known only by its error code, such as the code a research stream's
    /// error frame carries.
    /// </summary>
    public static int ExitCode(string? errorCode) =>
        IsConnectionFailure(errorCode)
            ? (int)CliExitCode.NetworkError
            : (int)CliExitCode.GenericError;

    /// <summary>
    /// The exit code for a failure known by its error code where the calling command reports its own
    /// failures under a code other than <see cref="CliExitCode.GenericError"/> (a resolver that fails
    /// with <see cref="CliExitCode.ConfigurationError"/> when a name matches nothing, for one): a
    /// <c>Connection.*</c> code is still <see cref="CliExitCode.NetworkError"/>, because the host was
    /// never asked, and every other failure keeps <paramref name="fallback"/>.
    /// </summary>
    public static CliExitCode Classify(string? errorCode, CliExitCode fallback) =>
        IsConnectionFailure(errorCode)
            ? CliExitCode.NetworkError
            : fallback;

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
        IsConnectionFailure(error.Code);

    private static bool IsConnectionFailure(string? errorCode) =>
        errorCode?.StartsWith(ConnectionErrorPrefix, StringComparison.Ordinal) == true;
}
