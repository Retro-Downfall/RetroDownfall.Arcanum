namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ServerSettings
{

    public string? PidFilePath { get; init; } = DefaultPidFilePath;

    private static string DefaultPidFilePath =>
        Path.Combine(
            global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.UserProfile),
            ".arcanum",
            "arcanum.pid");

}
