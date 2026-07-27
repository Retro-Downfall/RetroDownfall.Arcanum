using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckLaunchFileSnapshot(
    string Path,
    FileHandleIdentity Identity,
    long Length,
    long LastWriteUtcTicks);

internal sealed record WorkspaceCheckLaunchChainSnapshot(
    WorkspaceCheckLaunchFileSnapshot Shell,
    WorkspaceCheckLaunchFileSnapshot SandboxExec);

internal static class WorkspaceCheckLaunchChainPolicy
{
    internal static WorkspaceCheckLaunchChainSnapshot? Capture()
    {
        WorkspaceCheckLaunchFileSnapshot? shell =
            CaptureRootOwned("/bin/sh");
        WorkspaceCheckLaunchFileSnapshot? sandbox =
            CaptureRootOwned("/usr/bin/sandbox-exec");

        return shell is not null && sandbox is not null
            ? new WorkspaceCheckLaunchChainSnapshot(shell, sandbox)
            : null;
    }

    internal static bool Revalidate(
        WorkspaceCheckLaunchChainSnapshot snapshot) =>
        Matches(snapshot.Shell)
        && Matches(snapshot.SandboxExec);

    private static bool Matches(
        WorkspaceCheckLaunchFileSnapshot expected)
    {
        WorkspaceCheckLaunchFileSnapshot? current =
            CaptureRootOwned(expected.Path);

        return current is not null
            && FileHandleIdentity.IdentitiesMatch(
                expected.Identity,
                current.Identity)
            && expected.Length == current.Length
            && expected.LastWriteUtcTicks
                == current.LastWriteUtcTicks;
    }

    private static WorkspaceCheckLaunchFileSnapshot? CaptureRootOwned(
        string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            FileInfo file = new(path);

            if (!file.Exists)
            {
                return null;
            }

            string canonical = Path.GetFullPath(
                file.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? file.FullName);

            if (!FileHandleIdentityInterop.TryGetUnixOwnerUserId(
                    canonical,
                    out uint owner)
                || owner != 0
                || (File.GetUnixFileMode(canonical)
                    & (UnixFileMode.GroupWrite
                       | UnixFileMode.OtherWrite)) != 0
                || !FileHandleIdentityInterop.TryGetPathMetadata(
                    canonical,
                    out FileHandleMetadata metadata))
            {
                return null;
            }

            FileInfo canonicalFile = new(canonical);
            return new WorkspaceCheckLaunchFileSnapshot(
                canonical,
                metadata.Identity,
                canonicalFile.Length,
                canonicalFile.LastWriteTimeUtc.Ticks);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException
                or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
