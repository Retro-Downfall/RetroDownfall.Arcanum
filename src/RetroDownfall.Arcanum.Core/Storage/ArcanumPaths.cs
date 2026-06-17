namespace RetroDownfall.Arcanum.Core.Storage;

public static class ArcanumPaths
{
    public static string GrimoireDirectory =>
        Path.Combine(
            global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.UserProfile),
            ".config",
            "arcanum");

    public static string GrimoireDatabaseFile =>
        Path.Combine(GrimoireDirectory, "arcanum.db");

    /// <summary>
    /// Built-in (global) spell catalog root: <c>~/.config/arcanum/spells/</c>.
    /// Distinct from <see cref="GrimoireDirectory"/> (config/DB) and from per-project workspace roots.
    /// </summary>
    public static string GlobalSpellsDirectory =>
        Path.Combine(GrimoireDirectory, "spells");

    /// <summary>
    /// Local GGUF model cache root: <c>~/.config/arcanum/models/</c>.
    /// </summary>
    public static string ModelCacheDirectory =>
        Path.Combine(GrimoireDirectory, "models");

}
