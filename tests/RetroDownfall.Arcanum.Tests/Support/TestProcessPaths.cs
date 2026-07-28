using System.Runtime.CompilerServices;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static class TestProcessPaths
{
    internal static string OriginalUserProfile { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void CaptureProcessStartPaths()
    {
        OriginalUserProfile = global::System.Environment.GetFolderPath(
            global::System.Environment.SpecialFolder.UserProfile);
    }
}
