namespace RetroDownfall.Arcanum.Core.Storage;

public static class ArcanumPaths
{

    public static string GrimoireDirectory =>
        Path.Combine(

            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),

            ".config",

            "arcanum");

    public static string GrimoireDatabaseFile =>
        Path.Combine(GrimoireDirectory, "arcanum.db");

}
