using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Windows-only trusted broker. It waits until the host confirms it assigned this process to the
/// invocation Job Object, grants a fresh AppContainer SID only the declared roots, creates the target
/// (already resolved to an absolute path by the host) suspended with
/// PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES, then resumes it. The SID's ACEs and the profile are
/// removed in a finally block, and every one of those undo steps is journaled to an owner-only
/// host-owned file first: a timeout, cancellation, or Job Object kill terminates this process with
/// TerminateProcess, which does not run the finally, and the host replays the journal instead.
/// </summary>
[SupportedOSPlatform("windows")]
// Windows kernel/ACL integration. Its only end-to-end coverage is the Windows-lane
// WindowsAppContainerBrokerTests (whose smoke needs a published apphost) and
// WindowsAppContainerAclTests, which have not yet run in CI; the pure parts it relies on
// (WindowsAppContainerBrokerExit, WindowsAppContainerRootLockBudget, the journal and the resolver)
// are covered on every host.
[ExcludeFromCodeCoverage]
internal static partial class WindowsAppContainerLauncher
{
    private static readonly TimeSpan HostJobConfirmationTimeout = TimeSpan.FromSeconds(5);

    private const uint CreateSuspended = 0x00000004;

    private const uint ExtendedStartupInfoPresent = 0x00080000;

    private const uint ProcThreadAttributeSecurityCapabilities = 0x00020009;

    private const uint ProcThreadAttributeHandleList = 0x00020002;

    private const int StartupInfoStdHandles = 0x00000100;
    private const int Infinite = -1;

    internal static int Run(SandboxExecHelperPayload payload) => Run(payload, Console.Error);

    /// <summary>
    /// Every exit the broker takes for its own failure writes one <c>sandbox-exec:</c> line to
    /// <paramref name="error"/>, the stderr the target shares, so the runner hands the model a reason
    /// along with the exit code.
    /// </summary>
    internal static int Run(SandboxExecHelperPayload payload, TextWriter error)
    {
        string? missing = WindowsAppContainerBrokerExit.MissingPayloadMember(payload);
        if (missing is not null)
        {
            return WindowsAppContainerBrokerExit.Fail(
                error,
                WindowsAppContainerBrokerExit.InvalidPayload,
                WindowsAppContainerBrokerExit.InvalidPayloadMessage(missing));
        }

        string profileName = payload.WindowsProfileName!;
        string journalPath = payload.WindowsRestoreJournalPath!;
        string signalPath = payload.WindowsJobAssignedSignalPath!;
        nint sid = 0;
        string? sidValue = null;
        bool profileCreated = false;
        List<string> grantedRoots = [];
        WindowsAppContainerRootLockBudget? lockBudget = null;
        try
        {
            // "In a job" is not enough: a host that itself runs inside a job (a CI agent, a service
            // wrapper) makes every child a job member from birth. Only the host's confirmation that it
            // assigned this broker to the run's own job lets the target start under that job's limits.
            if (!WindowsAppContainerJobAssignment.WaitForHostAssignment(
                    () => WindowsAppContainerJobAssignment.IsConfirmed(signalPath),
                    IsInAnyJob,
                    HostJobConfirmationTimeout,
                    static () => DateTime.UtcNow,
                    static delay => Thread.Sleep(delay)))
            {
                return WindowsAppContainerBrokerExit.Fail(
                    error,
                    WindowsAppContainerBrokerExit.HostJobNotConfirmed,
                    WindowsAppContainerJobAssignment.HostJobTimeoutMessage);
            }

            // Journal before creating, not after: a kill in that window would otherwise strand a
            // registered profile that nothing remembers the name of.
            WindowsAppContainerRestoreJournal.RecordProfile(journalPath, profileName);
            int profileResult = CreateAppContainerProfile(
                profileName,
                profileName,
                "Arcanum tool child",
                0,
                0,
                out sid);
            profileCreated = profileResult == 0 && sid != 0;
            if (!profileCreated)
            {
                return WindowsAppContainerBrokerExit.Fail(
                    error,
                    WindowsAppContainerBrokerExit.ProfileCreationFailed,
                    $"the per-run AppContainer profile could not be created (HRESULT 0x{profileResult:X8}); the command was not started.");
            }

            SecurityIdentifier identity = new(sid);
            sidValue = identity.Value;

            // One deadline for every root-lock wait from here on, grants and removals alike, so several
            // contended roots cannot each stack a full timeout.
            lockBudget = WindowsAppContainerRootLockBudget.StartPerRun();
            foreach (string root in payload.ReadWriteRoots)
            {
                Grant(journalPath, root, identity, FileSystemRights.Modify | FileSystemRights.ReadAndExecute, lockBudget, grantedRoots);
            }

            foreach (string root in payload.ReadOnlyRoots.Concat(payload.ReadExecuteRoots))
            {
                Grant(journalPath, root, identity, FileSystemRights.ReadAndExecute, lockBudget, grantedRoots);
            }

            return LaunchSuspended(payload, sid, error);
        }
        catch (Exception ex)
        {
            return WindowsAppContainerBrokerExit.Fail(
                error,
                WindowsAppContainerBrokerExit.SetupFailed,
                $"AppContainer setup failed ({ex.GetType().Name}: {ex.Message}); the command was not started.");
        }
        finally
        {
            bool undone = true;
            WindowsAppContainerRootLockBudget removalBudget = lockBudget ?? WindowsAppContainerRootLockBudget.StartPerRun();
            for (int index = grantedRoots.Count - 1; index >= 0; index--)
            {
                try
                {
                    undone &= RemoveGrant(grantedRoots[index], sidValue!, removalBudget);
                }
                catch (Exception)
                {
                    // Failures are intentionally not hidden from health: the next capability probe
                    // remains independent. A random per-run SID prevents reuse by later children.
                    undone = false;
                }
            }

            if (sid != 0)
            {
                FreeSid(sid);
            }

            if (profileCreated)
            {
                undone &= DeleteProfile(profileName);
            }

            // Only a complete self-restore retires the journal. Anything left is the host's to undo.
            if (undone)
            {
                try
                {
                    WindowsAppContainerRestoreJournal.Clear(journalPath);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    /// <summary>
    /// Removes every explicit ACE for <paramref name="sid"/> from the directory's <b>current</b> DACL,
    /// leaving every other entry — including another concurrent run's grant on the same root — as it
    /// is now. Restoring a snapshot instead would delete that run's live ACE and later resurrect this
    /// run's dead SID. A directory that no longer exists carries no ACE to remove. Host replay uses
    /// this too. Only a per-run AppContainer SID is ever purged: anything broader would strip access
    /// no run granted. The root-lock wait draws on <paramref name="lockBudget"/>, shared by the whole
    /// broker run or host replay.
    /// </summary>
    internal static bool RemoveGrant(string path, string sid, WindowsAppContainerRootLockBudget lockBudget)
    {
        if (!WindowsAppContainerRestoreJournal.IsAppContainerSidString(sid))
        {
            return false;
        }

        if (!Directory.Exists(path))
        {
            return !File.Exists(path);
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            // The root was a plain directory when granted; never follow a link planted since.
            return false;
        }

        SecurityIdentifier identity = new(sid);
        return WithRootLock(path, lockBudget, () =>
        {
            DirectoryInfo directory = new(path);
            DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);

            security.PurgeAccessRules(identity);
            directory.SetAccessControl(security);
            return true;
        });
    }

    /// <summary>Removes a per-run AppContainer profile. Host replay uses this too.</summary>
    internal static bool DeleteProfile(string profileName) =>
        DeleteAppContainerProfile(profileName) == 0;

    /// <summary>
    /// Adds an inheritable Allow ACE for <paramref name="identity"/> to <paramref name="path"/>, after
    /// journaling the root and SID so a killed broker's grant can still be removed by the host. The
    /// root-lock wait draws on <paramref name="lockBudget"/>, shared by the whole broker run.
    /// </summary>
    internal static void Grant(
        string journalPath,
        string path,
        SecurityIdentifier identity,
        FileSystemRights rights,
        WindowsAppContainerRootLockBudget lockBudget,
        List<string>? grantedRoots = null)
    {
        if (!WindowsAppContainerPolicy.IsSafeRoot(path)
            || !Directory.Exists(path)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Unsafe AppContainer root.");
        }

        // The mutation below outlives this process when it is terminated, so the undo record has to
        // reach disk first; a failure here throws and fails the run closed rather than granting an
        // ACE nothing can take back. Removing a SID that was never added is a no-op, so recording
        // first is always safe to replay.
        WindowsAppContainerRestoreJournal.RecordGrant(journalPath, path, identity.Value);
        grantedRoots?.Add(path);
        _ = WithRootLock(path, lockBudget, () =>
        {
            DirectoryInfo directory = new(path);
            DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            directory.SetAccessControl(security);
            return true;
        });
    }

    /// <summary>
    /// Serialises the read-modify-write of one root's DACL across concurrent brokers and host replays,
    /// so two runs granting or removing on a shared root at the same moment cannot lose each other's
    /// update. The lock is held only for that one read and write, never for the run, and the wait for
    /// it is bounded by what is left of <paramref name="lockBudget"/>.
    /// </summary>
    private static bool WithRootLock(string path, WindowsAppContainerRootLockBudget lockBudget, Func<bool> update)
    {
        using Mutex mutex = new(initiallyOwned: false, RootLockName(path));
        bool acquired;
        try
        {
            acquired = lockBudget.TryAcquire(timeout => mutex.WaitOne(timeout));
        }
        catch (AbandonedMutexException)
        {
            // A broker killed mid-update abandoned it; ownership passes to this caller, and the DACL it
            // was writing is read fresh below either way.
            acquired = true;
        }

        if (!acquired)
        {
            throw new TimeoutException("Timed out waiting for another sandboxed run to finish updating a root's ACL.");
        }

        try
        {
            return update();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>The session-local mutex name serialising one root's DACL updates.</summary>
    internal static string RootLockName(string path)
    {
        byte[] key = System.Text.Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant());
        return @"Local\RetroDownfall.Arcanum.AclRoot."
            + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(key));
    }

    private static bool IsInAnyJob() =>
        IsProcessInJob(GetCurrentProcess(), 0, out bool inJob) && inJob;

    private static unsafe int LaunchSuspended(SandboxExecHelperPayload payload, nint sid, TextWriter error)
    {
        nuint size = 0;
        _ = InitializeProcThreadAttributeList(0, 2, 0, ref size);
        nint attributes = Marshal.AllocHGlobal((nint)size);
        nint capabilitiesPtr = 0;
        nint handlesPtr = 0;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            SecurityCapabilities capabilities = new()
            {
                AppContainerSid = sid,
                Capabilities = 0,
                CapabilityCount = 0,
                Reserved = 0,
            };
            capabilitiesPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(capabilities, capabilitiesPtr, false);
            if (!UpdateProcThreadAttribute(
                    attributes, 0, ProcThreadAttributeSecurityCapabilities,
                    capabilitiesPtr, (nuint)Marshal.SizeOf<SecurityCapabilities>(), 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            nint[] inheritedHandles =
            [
                GetStdHandle(-10),
                GetStdHandle(-11),
                GetStdHandle(-12),
            ];
            handlesPtr = Marshal.AllocHGlobal(nint.Size * inheritedHandles.Length);
            Marshal.Copy(inheritedHandles, 0, handlesPtr, inheritedHandles.Length);
            if (!UpdateProcThreadAttribute(
                    attributes, 0, ProcThreadAttributeHandleList,
                    handlesPtr, (nuint)(nint.Size * inheritedHandles.Length), 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            StartupInfoEx startup = new();
            startup.StartupInfo.Cb = Marshal.SizeOf<StartupInfoEx>();
            startup.StartupInfo.Flags = StartupInfoStdHandles;
            startup.StartupInfo.StdInput = GetStdHandle(-10);
            startup.StartupInfo.StdOutput = GetStdHandle(-11);
            startup.StartupInfo.StdError = GetStdHandle(-12);
            startup.AttributeList = attributes;

            string commandLine = BuildCommandLine(payload.Target, payload.Arguments);
            if (!CreateProcessW(
                    payload.Target, commandLine, 0, 0, true,
                    CreateSuspended | ExtendedStartupInfoPresent, 0,
                    payload.WorkingDirectory, ref startup, out ProcessInformation process))
            {
                // Access denied here is the AppContainer failing to read the image: a tool outside the
                // directories it can reach. The broker grants nothing more; the line says so.
                return WindowsAppContainerBrokerExit.Fail(
                    error,
                    WindowsAppContainerBrokerExit.SetupFailed,
                    WindowsAppContainerBrokerExit.LaunchFailedMessage(payload.Target, Marshal.GetLastPInvokeError()));
            }

            using SafeFileHandle processHandle = new(process.Process, true);
            using SafeFileHandle threadHandle = new(process.Thread, true);
            if (ResumeThread(process.Thread) == uint.MaxValue)
            {
                int resumeError = Marshal.GetLastPInvokeError();
                _ = TerminateProcess(process.Process, WindowsAppContainerBrokerExit.ResumeFailed);
                return WindowsAppContainerBrokerExit.Fail(
                    error,
                    WindowsAppContainerBrokerExit.ResumeFailed,
                    WindowsAppContainerBrokerExit.ResumeFailedMessage(resumeError));
            }
            _ = WaitForSingleObject(process.Process, Infinite);
            return GetExitCodeProcess(process.Process, out uint code)
                ? unchecked((int)code)
                : WindowsAppContainerBrokerExit.Fail(
                    error,
                    WindowsAppContainerBrokerExit.TargetExitCodeUnavailable,
                    WindowsAppContainerBrokerExit.ExitCodeUnavailableMessage(Marshal.GetLastPInvokeError()));
        }
        finally
        {
            if (attributes != 0)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (capabilitiesPtr != 0)
            {
                Marshal.FreeHGlobal(capabilitiesPtr);
            }

            if (handlesPtr != 0)
            {
                Marshal.FreeHGlobal(handlesPtr);
            }
        }
    }

    internal static string BuildCommandLine(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));

    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(static c => char.IsWhiteSpace(c) || c == '"'))
        {
            return value;
        }

        System.Text.StringBuilder result = new(value.Length + 2);
        result.Append('"');
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', (backslashes * 2) + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities { internal nint AppContainerSid; internal nint Capabilities; internal uint CapabilityCount; internal uint Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo { internal int Cb; internal nint Reserved; internal nint Desktop; internal nint Title; internal int X; internal int Y; internal int XSize; internal int YSize; internal int XCountChars; internal int YCountChars; internal int FillAttribute; internal int Flags; internal short ShowWindow; internal short Reserved2; internal nint Reserved2Ptr; internal nint StdInput; internal nint StdOutput; internal nint StdError; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { internal StartupInfo StartupInfo; internal nint AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { internal nint Process; internal nint Thread; internal uint ProcessId; internal uint ThreadId; }

    [SupportedOSPlatform("windows")][LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int CreateAppContainerProfile(string name, string displayName, string description, nint capabilities, uint capabilityCount, out nint sid);
    [SupportedOSPlatform("windows")][LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int DeleteAppContainerProfile(string name);
    [SupportedOSPlatform("windows")][LibraryImport("advapi32.dll")] private static partial nint FreeSid(nint sid);
    [SupportedOSPlatform("windows")][LibraryImport("kernel32.dll")] private static partial nint GetCurrentProcess();
    [SupportedOSPlatform("windows")][LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [SupportedOSPlatform("windows")][LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [SupportedOSPlatform("windows")][LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnSize);
    [SupportedOSPlatform("windows")][LibraryImport("kernel32.dll")] private static partial void DeleteProcThreadAttributeList(nint list);
    [SupportedOSPlatform("windows")][LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool CreateProcessW(string? application, [MarshalAs(UnmanagedType.LPWStr)] string commandLine, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? currentDirectory, ref StartupInfoEx startup, out ProcessInformation process);
    [SupportedOSPlatform("windows")][LibraryImport("kernel32.dll")] private static partial nint GetStdHandle(int handle);
    [SupportedOSPlatform("windows")][LibraryImport("kernel32.dll", SetLastError = true)] private static partial uint ResumeThread(nint thread);
    [SupportedOSPlatform("windows")][LibraryImport("kernel32.dll", SetLastError = true)] private static partial uint WaitForSingleObject(nint handle, int milliseconds);
    [SupportedOSPlatform("windows")][LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetExitCodeProcess(nint process, out uint exitCode);
    [SupportedOSPlatform("windows")][LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool TerminateProcess(nint process, uint exitCode);
}
