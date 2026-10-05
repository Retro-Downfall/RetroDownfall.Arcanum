using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Policy and capability probe for the per-invocation Windows AppContainer jail.
/// Native activation is performed by the hidden sandbox broker.
/// </summary>
internal static partial class WindowsAppContainerPolicy
{
    private const int AppContainerNameMax = 64;

    internal static string CreateProfileName()
    {
        string name = "RetroDownfall.Arcanum.Tool." + Guid.NewGuid().ToString("N");
        return name.Length <= AppContainerNameMax ? name : name[..AppContainerNameMax];
    }

    internal static bool IsSafeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return false;
        }

        // Reject alternate data streams while retaining the drive-designator colon.
        int firstColon = path.IndexOf(':');
        if (firstColon != 1 || path.IndexOf(':', firstColon + 1) >= 0)
        {
            return false;
        }

        string[] components = path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(static component => component is "." or ".."))
        {
            return false;
        }

        return path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');
    }

    internal static bool IsSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            int result = DeriveAppContainerSidFromAppContainerName(
                "RetroDownfall.Arcanum.CapabilityProbe",
                out nint sid);
            if (sid != 0)
            {
                FreeSid(sid);
            }

            // ERROR_NOT_FOUND means the API is present and the probe profile simply does not exist.
            return result is 0 or 1168;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeriveAppContainerSidFromAppContainerName(
        string appContainerName,
        out nint appContainerSid);

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll")]
    private static partial nint FreeSid(nint sid);
}

/// <summary>
/// The exit codes the Windows broker returns for its own failures, and the one-line
/// <c>sandbox-exec:</c> diagnostic it writes to the child's stderr for each, so a refused or failed run
/// never reaches the model as a bare number. Cross-platform so every host pins the wording; only the
/// Windows broker calls it.
/// </summary>
internal static class WindowsAppContainerBrokerExit
{
    /// <summary>The payload lacks a member the broker needs: nothing was attempted.</summary>
    internal const int InvalidPayload = 70;

    /// <summary>The host never confirmed this broker's Job Object assignment.</summary>
    internal const int HostJobNotConfirmed = 71;

    /// <summary>The AppContainer profile could not be created.</summary>
    internal const int ProfileCreationFailed = 72;

    /// <summary>Granting a root or creating the target failed.</summary>
    internal const int SetupFailed = 73;

    /// <summary>The target was created suspended but could not be resumed, so it was terminated unrun.</summary>
    internal const int ResumeFailed = 74;

    /// <summary>The target ran, but its exit code could not be read.</summary>
    internal const int TargetExitCodeUnavailable = 75;

    private const string DiagnosticPrefix = "sandbox-exec: ";

    private const int ErrorAccessDenied = 5;

    /// <summary>The first member the broker cannot run without, or <c>null</c> when none is missing.</summary>
    internal static string? MissingPayloadMember(SandboxExecHelperPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return string.IsNullOrWhiteSpace(payload.Target) ? nameof(SandboxExecHelperPayload.Target)
            : string.IsNullOrWhiteSpace(payload.WindowsProfileName) ? nameof(SandboxExecHelperPayload.WindowsProfileName)
            : string.IsNullOrWhiteSpace(payload.WindowsRestoreJournalPath) ? nameof(SandboxExecHelperPayload.WindowsRestoreJournalPath)
            : string.IsNullOrWhiteSpace(payload.WindowsJobAssignedSignalPath) ? nameof(SandboxExecHelperPayload.WindowsJobAssignedSignalPath)
            : null;
    }

    internal static string InvalidPayloadMessage(string missingMember) =>
        $"the broker payload has no {missingMember}; the command was not started.";

    /// <summary>
    /// <c>CreateProcessW</c> failed. Access denied is what an AppContainer gets for an image it cannot
    /// read: only the Windows and Program Files directories, the run's granted roots and PATH
    /// directories inside the user profile are reachable, and the broker deliberately grants nothing
    /// else, so the line names that set instead of a bare "Access is denied".
    /// </summary>
    internal static string LaunchFailedMessage(string target, int win32Error) =>
        win32Error == ErrorAccessDenied
            ? $"the sandbox cannot read '{target}': an AppContainer can read only the Windows and Program Files directories, "
                + "the run's granted roots, and PATH directories inside the user profile, so a tool installed elsewhere "
                + "(for example under ProgramData or on another drive) cannot run sandboxed; the command was not started."
            : $"the sandboxed command '{target}' could not be created (Win32 error {win32Error}); the command was not started.";

    internal static string ResumeFailedMessage(int win32Error) =>
        $"the sandboxed command was created suspended but could not be resumed (Win32 error {win32Error}); it was terminated before it ran.";

    internal static string ExitCodeUnavailableMessage(int win32Error) =>
        $"the sandboxed command ran, but its exit code could not be read (Win32 error {win32Error}); its result is unknown.";

    /// <summary>
    /// Writes <c>sandbox-exec: </c><paramref name="message"/> to <paramref name="error"/> and returns
    /// <paramref name="exitCode"/>. A closed stderr never turns a refusal into a different outcome.
    /// </summary>
    internal static int Fail(TextWriter error, int exitCode, string message)
    {
        try
        {
            error.WriteLine(DiagnosticPrefix + message);
        }
        catch (Exception)
        {
        }

        return exitCode;
    }
}

/// <summary>
/// One deadline shared by every per-root ACL lock wait in a single broker run, or in a single host
/// replay of an undo log. The lock is held only for one DACL read and write, so a long wait means
/// another run is stuck holding it; giving each wait its own timeout let several contended roots, and a
/// grant phase followed by a removal phase, block for a multiple of it. Each wait is offered only what
/// is left, and a spent budget still tries the lock once without blocking, so an uncontended root is
/// always released.
/// </summary>
internal sealed class WindowsAppContainerRootLockBudget
{
    internal static readonly TimeSpan PerRun = TimeSpan.FromSeconds(30);

    private readonly DateTime _deadline;

    private readonly Func<DateTime> _utcNow;

    internal WindowsAppContainerRootLockBudget(TimeSpan total, Func<DateTime> utcNow)
    {
        ArgumentNullException.ThrowIfNull(utcNow);

        _utcNow = utcNow;
        _deadline = utcNow() + total;
    }

    internal static WindowsAppContainerRootLockBudget StartPerRun() =>
        new(PerRun, static () => DateTime.UtcNow);

    /// <summary>Waits through <paramref name="waitOne"/> for at most what is left of the deadline.</summary>
    internal bool TryAcquire(Func<TimeSpan, bool> waitOne)
    {
        ArgumentNullException.ThrowIfNull(waitOne);

        TimeSpan remaining = _deadline - _utcNow();
        return waitOne(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }
}
