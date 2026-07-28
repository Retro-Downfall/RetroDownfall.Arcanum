using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>Code-owned server lifecycle paths; this is not a public configuration root.</summary>
public sealed record ServerSettings
{

    public string? PidFilePath { get; set; } = DefaultPidFilePath;

    private static string DefaultPidFilePath =>
        Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            "arcanum.pid");

}
