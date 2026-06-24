using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

internal static class DataProtectionKeyPaths
{

    public static DirectoryInfo EnsureDirectory()
    {

        string path = Path.Combine(ArcanumPaths.GrimoireDirectory, "keys");

        DirectoryInfo directory = Directory.CreateDirectory(path);

        SecureFilePermissions.ApplyOwnerOnlyDirectory(directory.FullName);

        return directory;

    }

}
