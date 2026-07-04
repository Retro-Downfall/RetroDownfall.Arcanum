using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace RetroDownfall.Arcanum.Infrastructure.Platform;

/// <summary>
/// AOT-safe (<c>LibraryImport</c>) P/Invoke declaration for macOS <c>setrlimit(2)</c>, documenting the
/// kernel primitive that the <c>ulimit</c> shell builtin invokes on our behalf inside the child process
/// prelude built by <see cref="ProcessResourceLimiter"/>.
/// </summary>
/// <remarks>
/// This is intentionally not called from <see cref="ProcessResourceLimiter"/>'s main enforcement path:
/// calling <c>setrlimit</c> directly in the parent (.NET host) process before <see cref="System.Diagnostics.Process.Start()"/>
/// would apply the limit to the host itself (there is no fork/pre-exec hook in .NET to scope it to only
/// the child), which could make the subsequent <c>Start()</c> call fail or destabilize the host process.
/// It exists to (a) document the exact kernel call the <c>ulimit</c> prelude relies on and (b) give unit
/// tests a way to verify the P/Invoke signature and struct marshaling are correct in isolation, using a
/// disposable helper process rather than the host.
/// </remarks>
[ExcludeFromCodeCoverage] // Reason: macOS-only P/Invoke interop, documentation/test-signature-verification only.
internal static partial class MacOsRlimitInterop
{

    /// <summary>Darwin <c>RLIMIT_CPU</c> — CPU time in seconds; exceeding it delivers <c>SIGXCPU</c>.</summary>
    internal const int RlimitCpu = 0;

    /// <summary>Darwin <c>RLIMIT_AS</c> — virtual address space in bytes (not physical RSS).</summary>
    internal const int RlimitAs = 5;

    /// <summary>Darwin <c>RLIMIT_NOFILE</c> — maximum open file descriptor count.</summary>
    internal const int RlimitNofile = 8;

    /// <summary>Sentinel for "unlimited" (<c>RLIM_INFINITY</c> on Darwin).</summary>
    internal const ulong RlimInfinity = ulong.MaxValue;

    /// <summary>
    /// Test-only wrapper so tests can invoke <c>setrlimit</c> against the *current* process (the test
    /// runner) for a harmless, easily-restored resource (e.g. raising <see cref="RlimitNofile"/>) to
    /// verify the interop signature and struct layout, without affecting the Arcanum host process's
    /// real tool-execution limits.
    /// </summary>
    internal static int TrySetRlimitForCurrentProcess(int resource, ulong softLimit, ulong hardLimit)
    {

        Rlimit limit = new() { Current = softLimit, Maximum = hardLimit };

        return setrlimit(resource, ref limit);

    }

    [LibraryImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
    private static partial int setrlimit(int resource, ref Rlimit rlim);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rlimit
    {

        public ulong Current;

        public ulong Maximum;

    }

}
