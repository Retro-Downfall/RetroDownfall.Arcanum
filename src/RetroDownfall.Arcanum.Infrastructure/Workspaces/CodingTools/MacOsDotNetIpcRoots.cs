using System.Runtime.Versioning;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

/// <summary>
/// Resolves the shared .NET PAL inter-process roots a sandboxed workspace-check child needs on macOS.
/// </summary>
/// <remarks>
/// <para>
/// The .NET runtime places named-mutex shared memory and lock files under a hard-coded
/// <c>/tmp/.dotnet/{shm,lockfiles}</c> on Unix — it does not honour <c>TMPDIR</c>. The SDK reaches that
/// path on every invocation (NuGet's migration runner takes a named <c>Mutex</c> before any command
/// runs), so a child with no writable PAL root cannot start at all.
/// </para>
/// <para>
/// Arcanum previously redirected the PAL to a private per-run
/// <c>~/Library/Group Containers/&lt;id&gt;</c> via <c>DOTNET_SANDBOX_APPLICATION_GROUP_ID</c>. macOS now
/// restricts that directory to processes carrying the matching app-group entitlement, so creating one
/// fails with <c>EPERM</c> even unsandboxed and every workspace check failed before it began. The
/// supported arrangement is therefore the shared PAL roots, granted to the jail as two explicit
/// subpaths rather than a broad <c>/tmp</c> write.
/// </para>
/// <para>
/// The roots are host-owned and shared with the operator's other .NET processes: they are created
/// once if absent, never deleted with the run, and never treated as per-run state. The child can
/// create and hold PAL sync objects there — the same access every unsandboxed .NET process on the
/// machine already has — but gains no read access to any other process's files or memory.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal static class MacOsDotNetIpcRoots
{

    /// <summary>Directory the runtime uses for named-mutex shared memory.</summary>
    private const string SharedMemoryRoot = "/private/tmp/.dotnet/shm";

    /// <summary>Directory the runtime uses for named-mutex lock files.</summary>
    private const string LockFileRoot = "/private/tmp/.dotnet/lockfiles";

    /// <summary>
    /// The runtime creates these roots world-writable so every user's processes can share them.
    /// <c>/private/tmp</c> carries the sticky bit, so a foreign entry cannot be unlinked by another user.
    /// </summary>
    private const UnixFileMode SharedMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// Ensures both PAL roots exist and returns them. Existing directories are left exactly as the
    /// runtime created them — no permission or ownership is rewritten.
    /// </summary>
    /// <exception cref="IOException">The roots are absent and could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The roots are absent and could not be created.</exception>
    internal static IReadOnlyList<string> Ensure()
    {

        EnsureRoot(SharedMemoryRoot);

        EnsureRoot(LockFileRoot);

        return [SharedMemoryRoot, LockFileRoot];
    }

    /// <summary>
    /// True when both roots are present or can be created. Used by the capability probe so the tool is
    /// never advertised on a host where every invocation would fail.
    /// </summary>
    internal static bool AreAvailable()
    {

        try
        {

            _ = Ensure();

            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException)
        {

            return false;
        }
    }

    private static void EnsureRoot(string path)
    {

        if (Directory.Exists(path))
        {

            return;
        }

        _ = Directory.CreateDirectory(path, SharedMode);
    }
}
