namespace RetroDownfall.TheForge.Core.IO;

/// <summary>
/// Owner-only Unix file/directory mode helpers for The Forge-local persistence.
/// Does not depend on <c>RetroDownfall.Arcanum.Infrastructure</c>.
/// </summary>
public static class TheForgeOwnerOnlyPermissions
{

    public static void TrySetFile(string path)
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        try
        {

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

    }

    public static void TrySetDirectory(string path)
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        try
        {

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

    }

}
