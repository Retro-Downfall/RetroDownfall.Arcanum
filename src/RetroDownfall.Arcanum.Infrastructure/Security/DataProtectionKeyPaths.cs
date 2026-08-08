using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

internal static class DataProtectionKeyPaths
{

    /// <summary>
    /// The key-ring location, without creating it. Diagnostics need the path but must not
    /// materialize the directory they are reporting on, and they must not recompute it — the ring
    /// lives under the Grimoire directory, which on macOS and Windows is a different root from the
    /// secret store beside it.
    /// </summary>
    public static string Directory => System.IO.Path.Combine(ArcanumPaths.GrimoireDirectory, "keys");

    public static DirectoryInfo EnsureDirectory()
    {

        string path = Directory;

        DirectoryInfo directory = System.IO.Directory.CreateDirectory(path);

        SecureFilePermissions.ApplyOwnerOnlyDirectory(directory.FullName);

        return directory;

    }

}
