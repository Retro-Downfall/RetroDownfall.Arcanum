namespace RetroDownfall.Arcanum.Core.Storage;

public static class ArcanumPaths
{

    private const string TestHomeEnvironmentVariable = "ARCANUM_TEST_HOME";

    public static string GrimoireDirectory
    {
        get
        {

            string profile = TestingHome
                ?? global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.UserProfile);

            return Path.Combine(profile, ".config", "arcanum");

        }
    }

    public static string GrimoireDatabaseFile => Path.Combine(GrimoireDirectory, "arcanum.db");

    public static string GlobalMcpConfigFile => Path.Combine(GrimoireDirectory, "mcp.json");

    public static string LogDirectory => Path.Combine(SecretStoreDirectory, "logs");

    /// <summary>
    /// Directory holding the Data Protection secret store files: <c>~/.config/arcanum/</c> on Unix,
    /// <c>%APPDATA%/arcanum/</c> on Windows. Distinct from <see cref="GrimoireDirectory"/> (which lives
    /// under <c>~/.config/arcanum/</c> on Unix but under <c>%APPDATA%/arcanum/</c> on Windows).
    /// </summary>
    public static string SecretStoreDirectory
    {
        get
        {

            string? testingHome = TestingHome;

            return testingHome is null
                ? Path.Combine(
                    global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.ApplicationData),
                    "arcanum")
                : Path.Combine(testingHome, ".config", "arcanum");

        }
    }

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
    /// <c>POST /v1/files</c> upload storage root: <c>~/.config/arcanum/files/</c>. Each uploaded
    /// file is stored under its own GUID-named path (see <c>UploadedFileRepository.ResolvePath</c>) —
    /// never the client-supplied filename — so path traversal and filename collisions are structurally
    /// impossible; the original filename is retained only as row metadata.
    /// </summary>
    public static string FilesDirectory => Path.Combine(GrimoireDirectory, "files");

    /// <summary>
    /// Session attachment storage root: <c>~/.config/arcanum/attachments/</c>. Holds text and
    /// Scrying image bytes keyed by session (or pending turn), with Grimoire metadata rows pointing
    /// at relative paths under this directory.
    /// </summary>
    public static string AttachmentsDirectory => Path.Combine(GrimoireDirectory, "attachments");

    /// <summary>
    /// HTTPS certificate storage root: <c>~/.config/arcanum/certs/</c>. Holds locally generated
    /// self-signed development certificates (and their PFX bundles) written owner-only.
    /// </summary>
    public static string CertificatesDirectory => Path.Combine(GrimoireDirectory, "certs");

    /// <summary>
    /// Explicit test-only root used by in-process hosts. On Windows, changing <c>HOME</c>,
    /// <c>USERPROFILE</c>, or <c>APPDATA</c> after process start does not change
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>, so the testing host
    /// needs a guarded override to avoid reading or writing a developer profile.
    /// </summary>
    private static string? TestingHome
    {
        get
        {

            string? dotnetEnvironment =
                global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

            string? aspNetCoreEnvironment =
                global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

            bool isTesting =
                string.Equals(dotnetEnvironment, "Testing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(aspNetCoreEnvironment, "Testing", StringComparison.OrdinalIgnoreCase);

            if (!isTesting)
            {

                return null;

            }

            string? configuredHome =
                global::System.Environment.GetEnvironmentVariable(TestHomeEnvironmentVariable);

            return string.IsNullOrWhiteSpace(configuredHome)
                ? null
                : Path.GetFullPath(configuredHome);

        }
    }

}
