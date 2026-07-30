using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Hidden mode: <c>arcanum __sandbox-exec --config &lt;owner-only.json&gt;</c>.
/// Applies Landlock (Linux) then <c>execv</c>s the target. Never shell-concatenates argv.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: Linux-only Landlock + execv helper; covered by integration tests on Linux CI.
internal static class SandboxExecHelper
{

    /// <summary>
    /// When <paramref name="args"/> is a <c>__sandbox-exec</c> invocation, runs the helper and never returns
    /// (process image replaced) or exits the process with a non-zero code. Returns <c>false</c> when this
    /// is a normal Arcanum CLI/host launch.
    /// </summary>
    internal static bool TryHandle(string[] args)
    {

        if (args.Length < 1
            || !string.Equals(args[0], ChildProcessFilesystemJail.HelperArg, StringComparison.Ordinal))
        {

            return false;

        }

        // Not advertised in help; intentional silent fail-closed for malformed helper argv.
        if (args.Length < 3
            || !string.Equals(args[1], "--config", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(args[2]))
        {

            Console.Error.WriteLine("sandbox-exec: malformed arguments.");

            Environment.Exit(64);

            return true;

        }

        string configPath = args[2];

        SandboxExecHelperPayload? payload;

        try
        {

            string json = File.ReadAllText(configPath);

            payload = JsonSerializer.Deserialize(json, SandboxExecJsonContext.Default.SandboxExecHelperPayload);

        }
        catch (Exception ex)
        {

            Console.Error.WriteLine($"sandbox-exec: failed to read config: {ex.GetType().Name}");

            Environment.Exit(65);

            return true;

        }

        if (payload is null
            || string.IsNullOrWhiteSpace(payload.Target))
        {

            Console.Error.WriteLine("sandbox-exec: invalid config payload.");

            Environment.Exit(65);

            return true;

        }

        if (OperatingSystem.IsWindows())
        {
            Environment.Exit(WindowsAppContainerLauncher.Run(payload));
            return true;
        }

        if (!OperatingSystem.IsLinux())
        {

            Console.Error.WriteLine("sandbox-exec: Landlock helper is Linux-only.");

            Environment.Exit(66);

            return true;

        }

        if (!LinuxLandlock.TryRestrict(
                payload.ReadWriteRoots ?? [],
                payload.ReadExecuteRoots ?? [],
                out string? landlockError))
        {

            Console.Error.WriteLine($"sandbox-exec: Landlock failed: {landlockError}");

            Environment.Exit(67);

            return true;

        }

        if (!string.IsNullOrWhiteSpace(payload.WorkingDirectory))
        {

            try
            {

                Directory.SetCurrentDirectory(payload.WorkingDirectory);

            }
            catch (Exception)
            {

                Console.Error.WriteLine("sandbox-exec: failed to set working directory.");

                Environment.Exit(68);

                return true;

            }

        }

        string[] argv = new string[(payload.Arguments?.Length ?? 0) + 1];

        argv[0] = payload.Target;

        if (payload.Arguments is { Length: > 0 })
        {

            Array.Copy(payload.Arguments, 0, argv, 1, payload.Arguments.Length);

        }

        LinuxLandlock.ExecvOrExit(payload.Target, argv);

        return true;

    }

}

/// <summary>Minimal Landlock ABI via <c>syscall(2)</c> for filesystem path-beneath rules.</summary>
[ExcludeFromCodeCoverage]
internal static partial class LinuxLandlock
{

    // asm-generic unistd numbers (x86_64 and aarch64).
    private const long SysLandlockCreateRuleset = 444;

    private const long SysLandlockAddRule = 445;

    private const long SysLandlockRestrictSelf = 446;

    private const int LandlockRulePathBeneath = 1;

    private const int PrSetNoNewPrivs = 38;

    private const ulong AccessFsExecute = 1UL << 0;

    private const ulong AccessFsWriteFile = 1UL << 1;

    private const ulong AccessFsReadFile = 1UL << 2;

    private const ulong AccessFsReadDir = 1UL << 3;

    private const ulong AccessFsRemoveDir = 1UL << 4;

    private const ulong AccessFsRemoveFile = 1UL << 5;

    private const ulong AccessFsMakeChar = 1UL << 6;

    private const ulong AccessFsMakeDir = 1UL << 7;

    private const ulong AccessFsMakeReg = 1UL << 8;

    private const ulong AccessFsMakeSock = 1UL << 9;

    private const ulong AccessFsMakeFifo = 1UL << 10;

    private const ulong AccessFsMakeBlock = 1UL << 11;

    private const ulong AccessFsMakeSym = 1UL << 12;

    private const ulong AccessFsRefer = 1UL << 13;

    private const ulong AccessFsTruncate = 1UL << 14;

    private const ulong HandledAccessFs =
        AccessFsExecute
        | AccessFsWriteFile
        | AccessFsReadFile
        | AccessFsReadDir
        | AccessFsRemoveDir
        | AccessFsRemoveFile
        | AccessFsMakeChar
        | AccessFsMakeDir
        | AccessFsMakeReg
        | AccessFsMakeSock
        | AccessFsMakeFifo
        | AccessFsMakeBlock
        | AccessFsMakeSym
        | AccessFsRefer
        | AccessFsTruncate;

    private const ulong ReadExecuteAccess =
        AccessFsExecute | AccessFsReadFile | AccessFsReadDir | AccessFsRefer;

    private const ulong ReadWriteAccess =
        ReadExecuteAccess
        | AccessFsWriteFile
        | AccessFsRemoveDir
        | AccessFsRemoveFile
        | AccessFsMakeChar
        | AccessFsMakeDir
        | AccessFsMakeReg
        | AccessFsMakeSock
        | AccessFsMakeFifo
        | AccessFsMakeBlock
        | AccessFsMakeSym
        | AccessFsTruncate;

    private static readonly string[] DefaultSystemReadExecuteRoots =
    [
        "/usr",
        "/bin",
        "/sbin",
        "/lib",
        "/lib64",
        "/etc",
        "/dev",
        "/proc",
        "/sys",
        "/tmp",
        "/var/tmp",
        "/run",
    ];

    internal static bool TryRestrict(
        IReadOnlyList<string> readWriteRoots,
        IReadOnlyList<string> readExecuteRoots,
        out string? error)
    {

        error = null;

        LandlockRulesetAttr attr = new() { HandledAccessFs = HandledAccessFs };

        nint attrPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LandlockRulesetAttr>());

        try
        {

            Marshal.StructureToPtr(attr, attrPtr, fDeleteOld: false);

            long rulesetFd = syscall3(
                SysLandlockCreateRuleset,
                attrPtr,
                (nint)Marshal.SizeOf<LandlockRulesetAttr>(),
                0);

            if (rulesetFd < 0)
            {

                error =
                    $"landlock_create_ruleset failed (errno={Marshal.GetLastPInvokeError()}). Kernel may lack Landlock.";

                return false;

            }

            using SafeFileHandle ruleset = new((nint)rulesetFd, ownsHandle: true);

            foreach (string root in DefaultSystemReadExecuteRoots)
            {

                if (!TryAddPathRule(ruleset, root, ReadExecuteAccess, out error))
                {

                    return false;

                }

            }

            foreach (string root in readExecuteRoots)
            {

                if (!TryAddPathRule(ruleset, root, ReadExecuteAccess, out error))
                {

                    return false;

                }

            }

            foreach (string root in readWriteRoots)
            {

                if (!TryAddPathRule(ruleset, root, ReadWriteAccess, out error))
                {

                    return false;

                }

            }

            if (prctl(PrSetNoNewPrivs, 1, 0, 0, 0) != 0)
            {

                error = $"prctl(PR_SET_NO_NEW_PRIVS) failed (errno={Marshal.GetLastPInvokeError()}).";

                return false;

            }

            if (syscall2(SysLandlockRestrictSelf, (nint)rulesetFd, 0) != 0)
            {

                error = $"landlock_restrict_self failed (errno={Marshal.GetLastPInvokeError()}).";

                return false;

            }

            return true;

        }
        finally
        {

            Marshal.FreeHGlobal(attrPtr);

        }

    }

    internal static void ExecvOrExit(string path, string[] argv)
    {

        nint[] argvPtrs = new nint[argv.Length + 1];

        try
        {

            for (int i = 0; i < argv.Length; i++)
            {

                argvPtrs[i] = Marshal.StringToCoTaskMemUTF8(argv[i]);

            }

            argvPtrs[argv.Length] = nint.Zero;

            // execv uses the current process environment (already scrubbed by the parent).
            execv(path, argvPtrs);

            Console.Error.WriteLine($"sandbox-exec: execv failed (errno={Marshal.GetLastPInvokeError()}).");

            Environment.Exit(69);

        }
        finally
        {

            for (int i = 0; i < argv.Length; i++)
            {

                if (argvPtrs[i] != nint.Zero)
                {

                    Marshal.FreeCoTaskMem(argvPtrs[i]);

                }

            }

        }

    }

    private static bool TryAddPathRule(SafeFileHandle ruleset, string path, ulong access, out string? error)
    {

        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {

            return true;

        }

        string full;

        try
        {

            full = Path.GetFullPath(path.Trim());

        }
        catch (Exception)
        {

            return true;

        }

        const int OPath = 0x200000;

        const int OCloexec = 0x80000;

        int fd = open(full, OPath | OCloexec);

        if (fd < 0)
        {

            return true;

        }

        nint rulePtr = Marshal.AllocHGlobal(Marshal.SizeOf<LandlockPathBeneathAttr>());

        try
        {

            LandlockPathBeneathAttr rule = new()
            {
                AllowedAccess = access,

                ParentFd = fd,
            };

            Marshal.StructureToPtr(rule, rulePtr, fDeleteOld: false);

            long result = syscall4(
                SysLandlockAddRule,
                ruleset.DangerousGetHandle(),
                LandlockRulePathBeneath,
                rulePtr,
                0);

            if (result != 0)
            {

                error = $"landlock_add_rule failed for '{full}' (errno={Marshal.GetLastPInvokeError()}).";

                return false;

            }

            return true;

        }
        finally
        {

            Marshal.FreeHGlobal(rulePtr);

            close(fd);

        }

    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LandlockRulesetAttr
    {

        public ulong HandledAccessFs;

    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LandlockPathBeneathAttr
    {

        public ulong AllowedAccess;

        public int ParentFd;

    }

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long syscall2(long sysno, nint a1, nint a2);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long syscall3(long sysno, nint a1, nint a2, nint a3);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long syscall4(long sysno, nint a1, nint a2, nint a3, nint a4);

    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static partial int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string pathname, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", EntryPoint = "execv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int execv(string pathname, nint[] argv);

}
