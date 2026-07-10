namespace RetroDownfall.Arcanum.Core.Storage;

public static class ArcanumPaths
{

    public static string GrimoireDirectory =>
        Path.Combine(
            global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.UserProfile),
            ".config",
            "arcanum");

    public static string GrimoireDatabaseFile => Path.Combine(GrimoireDirectory, "arcanum.db");

    /// <summary>
    /// Directory holding the Data Protection secret store files: <c>~/.config/arcanum/</c> on Unix,
    /// <c>%APPDATA%/arcanum/</c> on Windows. Distinct from <see cref="GrimoireDirectory"/> (which lives
    /// under <c>~/.config/arcanum/</c> on Unix but under <c>%APPDATA%/arcanum/</c> on Windows).
    /// </summary>
    public static string SecretStoreDirectory =>
        Path.Combine(
            global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.ApplicationData),
            "arcanum");

    /// <summary>
    /// Data Protection-encrypted API key store: <c>security.dat</c> under <see cref="SecretStoreDirectory"/>.
    /// </summary>
    public static string ApiKeyStoreFile => Path.Combine(SecretStoreDirectory, "security.dat");

    /// <summary>
    /// Data Protection-encrypted Grimoire database key store: <c>grimoire-key.dat</c> under
    /// <see cref="SecretStoreDirectory"/>.
    /// </summary>
    public static string GrimoireKeyStoreFile => Path.Combine(SecretStoreDirectory, "grimoire-key.dat");

    /// <summary>
    /// Built-in (global) spell catalog root: <c>~/.config/arcanum/spells/</c>.
    /// Distinct from <see cref="GrimoireDirectory"/> (config/DB) and from per-project workspace roots.
    /// </summary>
    public static string GlobalSpellsDirectory => Path.Combine(GrimoireDirectory, "spells");

    /// <summary>
    /// Local GGUF model cache root: <c>~/.config/arcanum/models/</c>.
    /// </summary>
    public static string ModelCacheDirectory => Path.Combine(GrimoireDirectory, "models");

    /// <summary>
    /// <c>POST /v1/files</c> upload storage root: <c>~/.config/arcanum/files/</c>. Each uploaded
    /// file is stored under its own GUID-named path (see <c>UploadedFileRepository.ResolvePath</c>) —
    /// never the client-supplied filename — so path traversal and filename collisions are structurally
    /// impossible; the original filename is retained only as row metadata.
    /// </summary>
    public static string FilesDirectory => Path.Combine(GrimoireDirectory, "files");

    /// <summary>
    /// HTTPS certificate storage root: <c>~/.config/arcanum/certs/</c>. Holds locally generated
    /// self-signed development certificates (and their PFX bundles) written owner-only.
    /// </summary>
    public static string CertificatesDirectory => Path.Combine(GrimoireDirectory, "certs");

}
