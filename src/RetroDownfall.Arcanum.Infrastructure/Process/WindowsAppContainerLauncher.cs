using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Windows-only trusted broker. It waits for the host to place it in the invocation Job Object,
/// grants a fresh AppContainer SID only the declared roots, creates the target suspended with
/// PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES, then resumes it. ACLs and the profile are restored
/// in a finally block.
/// </summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage] // Windows kernel/ACL integration is exercised by Windows CI.
internal static partial class WindowsAppContainerLauncher
{
    private const uint CreateSuspended = 0x00000004;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint ProcThreadAttributeSecurityCapabilities = 0x00020005;
    private const uint ProcThreadAttributeHandleList = 0x00020002;
    private const int StartupInfoStdHandles = 0x00000100;
    private const int Infinite = -1;

    internal static int Run(SandboxExecHelperPayload payload)
    {
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(payload.WindowsProfileName)
            || string.IsNullOrWhiteSpace(payload.Target))
        {
            return 70;
        }

        nint sid = 0;
        List<(string Path, byte[] Descriptor)> aclBackups = [];
        try
        {
            if (!WaitUntilAssignedToJob(TimeSpan.FromSeconds(5)))
            {
                return 71;
            }

            int profileResult = CreateAppContainerProfile(
                payload.WindowsProfileName,
                payload.WindowsProfileName,
                "Arcanum tool child",
                0,
                0,
                out sid);
            if (profileResult != 0 || sid == 0)
            {
                return 72;
            }

            SecurityIdentifier identity = new(sid);
            foreach (string root in payload.ReadWriteRoots)
            {
                Grant(root, identity, FileSystemRights.Modify | FileSystemRights.ReadAndExecute, aclBackups);
            }
            foreach (string root in payload.ReadOnlyRoots.Concat(payload.ReadExecuteRoots))
            {
                Grant(root, identity, FileSystemRights.ReadAndExecute, aclBackups);
            }

            return LaunchSuspended(payload, sid);
        }
        catch (Exception)
        {
            return 73;
        }
        finally
        {
            for (int index = aclBackups.Count - 1; index >= 0; index--)
            {
                try
                {
                    (string path, byte[] descriptor) = aclBackups[index];
                    DirectorySecurity restored = new();
                    restored.SetSecurityDescriptorBinaryForm(descriptor);
                    new DirectoryInfo(path).SetAccessControl(restored);
                }
                catch (Exception)
                {
                    // Failures are intentionally not hidden from health: the next capability probe
                    // remains independent. A random per-run SID prevents reuse by later children.
                }
            }

            if (sid != 0)
            {
                FreeSid(sid);
            }
            if (!string.IsNullOrWhiteSpace(payload.WindowsProfileName))
            {
                _ = DeleteAppContainerProfile(payload.WindowsProfileName);
            }
        }
    }

    private static void Grant(
        string path,
        SecurityIdentifier identity,
        FileSystemRights rights,
        List<(string Path, byte[] Descriptor)> backups)
    {
        if (!WindowsAppContainerPolicy.IsSafeRoot(path)
            || !Directory.Exists(path)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Unsafe AppContainer root.");
        }

        DirectoryInfo directory = new(path);
        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);
        backups.Add((path, security.GetSecurityDescriptorBinaryForm()));
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private static bool WaitUntilAssignedToJob(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            if (IsProcessInJob(GetCurrentProcess(), 0, out bool inJob) && inJob)
            {
                return true;
            }
            Thread.Sleep(10);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    private static unsafe int LaunchSuspended(SandboxExecHelperPayload payload, nint sid)
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
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            using SafeFileHandle processHandle = new(process.Process, true);
            using SafeFileHandle threadHandle = new(process.Thread, true);
            if (ResumeThread(process.Thread) == uint.MaxValue)
            {
                _ = TerminateProcess(process.Process, 74);
                return 74;
            }
            _ = WaitForSingleObject(process.Process, Infinite);
            return GetExitCodeProcess(process.Process, out uint code) ? unchecked((int)code) : 75;
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
