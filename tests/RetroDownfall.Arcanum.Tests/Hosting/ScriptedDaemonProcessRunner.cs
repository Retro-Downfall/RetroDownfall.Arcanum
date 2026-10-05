using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>
/// Stands in for the OS service manager: records every helper invocation and answers it from a script,
/// so a daemon manager can be driven without touching the host's real launchd, systemd or SCM.
/// </summary>
internal sealed class ScriptedDaemonProcessRunner(
    Func<string, IReadOnlyList<string>, DaemonProcessOutcome> respond) : IDaemonProcessRunner
{
    /// <summary>
    /// Each call as one line, the file name followed by its space-joined arguments.
    /// </summary>
    internal List<string> Calls { get; } = [];

    /// <summary>
    /// Each call with its arguments kept apart, so a test can assert on argument boundaries, and the token it ran on.
    /// </summary>
    internal List<ScriptedInvocation> Invocations { get; } = [];

    public Task<DaemonProcessOutcome> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Calls.Add(fileName + " " + string.Join(' ', arguments));

        Invocations.Add(new ScriptedInvocation(fileName, [.. arguments], cancellationToken));

        return Task.FromResult(respond(fileName, arguments));
    }

    internal static DaemonProcessOutcome Exit(int exitCode, string stdout = "", string stderr = "") =>
        new(exitCode, stdout, stderr, null);
}

internal sealed record ScriptedInvocation(string FileName, string[] Arguments, CancellationToken Token);
