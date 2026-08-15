using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Source-level guards on how SQLite is reached at all.
/// </summary>
/// <remarks>
/// The hermetic runtime's guarantees are only as good as the number of ways around them. A single
/// forgotten <c>Batteries_V2.Init()</c> would install a provider over an unverified bundle, and a
/// single <c>EnableExtensions</c> call would reintroduce the dynamic loading the build was compiled
/// to forbid. These are inventory assertions over production source rather than behavior tests,
/// because the failure they prevent is a new call site, not a wrong result.
/// </remarks>
public sealed class SqliteConnectionInitializationInventoryTests
{

    /// <summary>
    /// The one file allowed to mention extension loading, and the exact literal it is allowed to
    /// use: the validator proves at startup that <c>load_extension</c> cannot be invoked.
    /// </summary>
    private const string ValidatorFileName = "SqliteNativeRuntimeValidator.cs";

    private const string PermittedLoadExtensionLiteral = "SELECT load_extension('forbidden');";

    [Fact]
    public void No_Batteries_initialization_remains()
    {

        List<string> offenders =
        [
            .. ProductionSources()
                .Where(static source => source.Text.Contains("Batteries_V2", StringComparison.Ordinal))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            offenders.Count == 0,
            "Batteries_V2 installs a provider over the bundled native libraries Arcanum no longer "
            + "ships. Call SqliteNativeRuntime.Instance.Initialize() instead: "
            + string.Join(", ", offenders));

    }

    [Fact]
    public void No_dynamic_extension_loading_remains()
    {

        List<string> offenders = [];

        foreach ((string relativePath, string text) in ProductionSources())
        {

            bool mentionsEnable = text.Contains("EnableExtensions", StringComparison.Ordinal)
                || text.Contains("sqlite3_enable_load_extension", StringComparison.Ordinal)
                || text.Contains("sqlite3_load_extension", StringComparison.Ordinal);

            if (mentionsEnable)
            {

                offenders.Add(relativePath);

                continue;

            }

            if (!text.Contains("load_extension", StringComparison.Ordinal))
            {

                continue;

            }

            // The validator's refusal probe is the one permitted mention, and only as this exact
            // literal.
            bool isPermittedProbe = relativePath.EndsWith(ValidatorFileName, StringComparison.Ordinal)
                && text.Contains(PermittedLoadExtensionLiteral, StringComparison.Ordinal);

            if (!isPermittedProbe)
            {

                offenders.Add(relativePath);

            }

        }

        Assert.True(
            offenders.Count == 0,
            "The shipped SQLCipher library is compiled with SQLITE_OMIT_LOAD_EXTENSION, so dynamic "
            + "extension loading cannot work and must not be attempted: "
            + string.Join(", ", offenders));

    }

    [Fact]
    public void No_vec0_schema_resource_remains()
    {

        string schema = Path.Combine(
            NativeSqlCipherTestPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Infrastructure",
            "Data",
            "Schema");

        List<string> offenders =
        [
            .. Directory.EnumerateFiles(schema, "*.sql", SearchOption.AllDirectories)
                .Where(static file => File.ReadAllText(file).Contains("vec0", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .Select(static name => name!)
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            offenders.Count == 0,
            "A vec0 virtual table can only be created by a loadable extension, which this runtime "
            + "cannot load. Installing one would fail every bootstrap: "
            + string.Join(", ", offenders));

    }

    /// <summary>
    /// Every production path that opens a SQLCipher connection has to reach policy through the one
    /// initializer, so a new component cannot quietly open a connection with no busy timeout, no
    /// foreign keys, no secure delete, and no authorization functions registered.
    /// </summary>
    [Fact]
    public void No_component_applies_its_own_connection_pragmas()
    {

        List<string> offenders =
        [
            .. ProductionSources()
                .Where(static source =>
                    !source.RelativePath.EndsWith("CovenantSqliteConnectionInitializer.cs", StringComparison.Ordinal)
                    && !source.RelativePath.EndsWith("SqliteConnectionPragmas.cs", StringComparison.Ordinal)
                    && !source.RelativePath.EndsWith(ValidatorFileName, StringComparison.Ordinal)
                    && (source.Text.Contains("PRAGMA journal_mode=WAL", StringComparison.OrdinalIgnoreCase)
                        || source.Text.Contains("PRAGMA foreign_keys=ON", StringComparison.OrdinalIgnoreCase)))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            offenders.Count == 0,
            "Connection policy belongs to CovenantSqliteConnectionInitializer, which also verifies "
            + "that it took effect. Route these through it: "
            + string.Join(", ", offenders));

    }

    private static IReadOnlyList<(string RelativePath, string Text)> ProductionSources()
    {

        string root = Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), "src");

        List<(string, string)> sources = [];

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {

            // Generated intermediates are build output, not authored production code.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {

                continue;

            }

            sources.Add((
                Path.GetRelativePath(NativeSqlCipherTestPaths.RepositoryRoot(), file),
                WithoutComments(File.ReadAllText(file))));

        }

        Assert.NotEmpty(sources);

        return sources;

    }

    /// <summary>
    /// Removes comments so the inventory matches on code alone.
    /// </summary>
    /// <remarks>
    /// Without this, documenting why a construct is forbidden trips the very guard that forbids it —
    /// which would push the explanation out of the code and leave the next reader with a rule and no
    /// reason. Line-oriented on purpose: it drops whole comment lines and block-comment bodies, and
    /// deliberately does not attempt to parse trailing comments out of lines that also contain code,
    /// because under-stripping only risks a false positive that a human will read, never a miss.
    /// </remarks>
    private static string WithoutComments(string source)
    {

        System.Text.StringBuilder builder = new(source.Length);

        bool inBlockComment = false;

        foreach (string line in source.Split('\n'))
        {

            string trimmed = line.TrimStart();

            if (inBlockComment)
            {

                if (trimmed.Contains("*/", StringComparison.Ordinal))
                {

                    inBlockComment = false;

                }

                continue;

            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {

                inBlockComment = !trimmed.Contains("*/", StringComparison.Ordinal);

                continue;

            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {

                continue;

            }

            _ = builder.Append(line).Append('\n');

        }

        return builder.ToString();

    }

}
