using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ServerSettings
{

    public string? PidFilePath { get; set; } = DefaultPidFilePath;

    private static string DefaultPidFilePath =>
        Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            "arcanum.pid");

}
