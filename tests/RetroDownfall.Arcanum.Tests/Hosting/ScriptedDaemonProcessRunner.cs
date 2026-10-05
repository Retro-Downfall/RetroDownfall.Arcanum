using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>
/// Stands in for the OS service manager: records every helper invocation and answers it from a script,
/// so a daemon manager can be driven without touching the host's real launchd, systemd or SCM.
/// </summary>
internal sealed class ScriptedDaemonProcessRunner(
    Func<string, IReadOnlyList<string>, DaemonProcessOutcome> respond) : IDaemonProcessRunner
{
    internal List<string> Calls { get; } = [];

    public Task<DaemonProcessOutcome> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Calls.Add(fileName + " " + string.Join(' ', arguments));

        return Task.FromResult(respond(fileName, arguments));
    }

    internal static DaemonProcessOutcome Exit(int exitCode, string stdout = "", string stderr = "") =>
        new(exitCode, stdout, stderr, null);
}
