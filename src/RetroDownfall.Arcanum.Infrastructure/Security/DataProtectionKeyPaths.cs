using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

internal static class DataProtectionKeyPaths
{

    public static DirectoryInfo EnsureDirectory()
    {

        string path = Path.Combine(ArcanumPaths.GrimoireDirectory, "keys");

        DirectoryInfo directory = Directory.CreateDirectory(path);

        if (!OperatingSystem.IsWindows())
        {

            try
            {

                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            }
            catch (Exception)
            {

                // Best effort — keys remain protected by OS user account isolation.

            }

        }

        return directory;

    }

}
