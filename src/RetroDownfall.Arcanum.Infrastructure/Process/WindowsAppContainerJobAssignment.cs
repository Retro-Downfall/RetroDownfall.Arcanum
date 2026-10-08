namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Host-to-broker handshake proving the Windows broker sits in <b>this run's</b> Job Object before it
/// creates the untrusted target. <c>IsProcessInJob(process, NULL)</c> only answers "in some job", which
/// is already true from birth when the host itself runs inside a job (a CI agent, a service wrapper,
/// some terminals), so it cannot tell the run's job from an inherited one. The host writes the
/// confirmation to an owner-only file it created for the run, and only after
/// <c>AssignProcessToJobObject</c> succeeded for the started broker; the broker waits for that and for
/// job membership. Pure apart from the signal file, so the decision is pinned on every host.
/// </summary>
internal static class WindowsAppContainerJobAssignment
{
    /// <summary>The broker's stderr line when the host never confirmed the assignment (exit 71).</summary>
    internal const string HostJobTimeoutMessage =
        "the host did not confirm that this run was placed in its Sanctum Job Object, so its resource limits could not be guaranteed; the command was not started.";

    private const string ConfirmationToken = "assigned-to-run-job";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>Host side: records that the broker was assigned to the run's Job Object.</summary>
    internal static void Confirm(string signalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalPath);

        File.WriteAllText(signalPath, ConfirmationToken);
    }

    /// <summary>Broker side: whether the host has confirmed the assignment yet.</summary>
    internal static bool IsConfirmed(string signalPath)
    {
        try
        {
            return File.Exists(signalPath)
                && string.Equals(
                    File.ReadAllText(signalPath),
                    ConfirmationToken,
                    StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The host may be mid-write; the next poll reads the finished file.
            return false;
        }
    }

    /// <summary>
    /// Waits until the host has confirmed the assignment <b>and</b> this process is in a job, or the
    /// timeout passes. Membership alone never suffices: an inherited job would let the target start
    /// before — or entirely outside — the run's own limits.
    /// </summary>
    internal static bool WaitForHostAssignment(
        Func<bool> hostConfirmed,
        Func<bool> inAnyJob,
        TimeSpan timeout,
        Func<DateTime> utcNow,
        Action<TimeSpan> sleep)
    {
        ArgumentNullException.ThrowIfNull(hostConfirmed);
        ArgumentNullException.ThrowIfNull(inAnyJob);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(sleep);

        DateTime deadline = utcNow() + timeout;
        do
        {
            if (hostConfirmed() && inAnyJob())
            {
                return true;
            }

            sleep(PollInterval);
        } while (utcNow() < deadline);

        return false;
    }
}
