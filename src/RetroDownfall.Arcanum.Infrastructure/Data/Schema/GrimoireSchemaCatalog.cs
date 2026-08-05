using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// The Grimoire's declarative schema, read from the one-object-per-file tree under
/// <c>Data/Schema/</c>. The project embeds that tree by glob (<c>Data\Schema\**\*.sql</c>), so
/// adding an object is adding a file — there is no hand-maintained list to keep in sync and no
/// install-time filesystem dependency for the AOT host.
///
/// Conventions (see <c>docs/Arcanum.DESIGN.md</c> §5.4.5):
///
/// <list type="bullet">
/// <item>One file per table, FTS5 virtual table, trigger, and view, named after the object.</item>
/// <item>Indexes are co-located in their owning table's file rather than getting files of their own,
/// so a table and everything that constrains it read as one definition.</item>
/// <item>Every statement is <c>CREATE ... IF NOT EXISTS</c>, so an install against an already
/// installed database is a no-op instead of an error.</item>
/// <item>Folder name selects the <see cref="GrimoireSchemaCategory"/>, which selects the install
/// tier and order. An unrecognized folder is a build-time authoring mistake and throws rather than
/// being silently skipped.</item>
/// </list>
///
/// There is no migration chain and no <c>__EFMigrationsHistory</c>: Arcanum has no installed base,
/// so the schema is installed fresh and an incompatible local database is recreated
/// (<c>docs/Arcanum.README.md</c>, "Local Grimoire reinstall").
/// </summary>
internal static class GrimoireSchemaCatalog
{

    /// <summary>
    /// Placeholder resolved at install time from <c>Arcanum:Integrations:Embeddings:Dimensions</c>.
    /// Only the <c>vec0</c> accelerator objects declare a vector column width, and that width is not
    /// knowable at authoring time — this token is how a static object file expresses it.
    /// </summary>
    internal const string EmbeddingDimensionsToken = "{{EmbeddingDimensions}}";

    private const string ResourcePrefix = "RetroDownfall.Arcanum.Infrastructure.Data.Schema.";

    private const string ResourceSuffix = ".sql";

    private static readonly Lazy<IReadOnlyList<GrimoireSchemaObject>> LoadedObjects =
        new(LoadOrderedObjects, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string> LoadedFingerprint =
        new(ComputeCanonicalFingerprint, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Every object in install order, both tiers. Use <see cref="CoreObjects"/> /
    /// <see cref="AcceleratorObjects"/> to install; this is the authoring inventory.
    /// </summary>
    public static IReadOnlyList<GrimoireSchemaObject> AllObjects => LoadedObjects.Value;

    /// <summary>
    /// The durable schema: everything except the optional <c>vec0</c> accelerators. Installed in one
    /// transaction so a failure leaves no partial schema behind.
    /// </summary>
    public static IReadOnlyList<GrimoireSchemaObject> CoreObjects =>
        [.. LoadedObjects.Value.Where(static definition => definition.Category != GrimoireSchemaCategory.Accelerators)];

    /// <summary>
    /// The optional <c>vec0</c> virtual tables. Installed only when the sqlite-vec extension loads;
    /// their absence costs performance, never correctness, because the BLOB companion tables in
    /// <see cref="GrimoireSchemaCategory.Tables"/> remain the search source of truth (§21.2).
    /// </summary>
    public static IReadOnlyList<GrimoireSchemaObject> AcceleratorObjects =>
        [.. LoadedObjects.Value.Where(static definition => definition.Category == GrimoireSchemaCategory.Accelerators)];

    /// <summary>
    /// Stable hash of the canonical schema source (category, name, and unresolved SQL of every
    /// object, in install order). It identifies the <i>definitions</i>, not an installed database —
    /// <see cref="GrimoireSchemaIdentity"/> does the latter. Tests use it to decide whether a cached
    /// template database is still current.
    /// </summary>
    public static string CanonicalSchemaFingerprint => LoadedFingerprint.Value;

    /// <summary>
    /// Substitutes the install-time template values into an object definition and refuses to return
    /// a statement that still carries an unresolved placeholder — a typo in a token name must fail
    /// loudly rather than reach SQLite as invalid DDL.
    /// </summary>
    public static string Resolve(GrimoireSchemaObject definition, int embeddingDimensions)
    {

        ArgumentNullException.ThrowIfNull(definition);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(embeddingDimensions);

        string resolved = definition.Sql.Replace(
            EmbeddingDimensionsToken,
            embeddingDimensions.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

        if (resolved.Contains("{{", StringComparison.Ordinal))
        {

            throw new InvalidOperationException(
                $"Grimoire schema object '{definition.Category}/{definition.Name}.sql' contains an unresolved template placeholder.");

        }

        return resolved;

    }

    private static IReadOnlyList<GrimoireSchemaObject> LoadOrderedObjects()
    {

        Assembly assembly = typeof(GrimoireSchemaCatalog).Assembly;

        List<GrimoireSchemaObject> definitions = [];

        foreach (string resourceName in assembly.GetManifestResourceNames())
        {

            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !resourceName.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            {

                continue;

            }

            definitions.Add(ReadObject(assembly, resourceName));

        }

        if (definitions.Count == 0)
        {

            throw new InvalidOperationException(
                "No embedded Grimoire schema objects were found; the Data/Schema glob is not being embedded.");

        }

        return
        [
            .. definitions
                .OrderBy(static definition => (int)definition.Category)
                .ThenBy(static definition => definition.Name, StringComparer.Ordinal),
        ];

    }

    private static GrimoireSchemaObject ReadObject(Assembly assembly, string resourceName)
    {

        string relative = resourceName[ResourcePrefix.Length..^ResourceSuffix.Length];

        int separator = relative.IndexOf('.', StringComparison.Ordinal);

        if (separator <= 0 || separator == relative.Length - 1)
        {

            throw new InvalidOperationException(
                $"Embedded Grimoire schema resource '{resourceName}' is not under a Data/Schema category folder.");

        }

        GrimoireSchemaCategory category = ParseCategory(relative[..separator], resourceName);

        string name = relative[(separator + 1)..];

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Grimoire schema resource not found: {resourceName}");

        using StreamReader reader = new(stream, Encoding.UTF8);

        return new GrimoireSchemaObject(category, name, reader.ReadToEnd());

    }

    // Deliberately a switch rather than Enum.Parse: the folder names are a small closed authoring
    // vocabulary, and an unknown folder must name itself in the failure instead of silently
    // installing in an arbitrary tier.
    private static GrimoireSchemaCategory ParseCategory(string folder, string resourceName) =>
        folder switch
        {

            "Tables" => GrimoireSchemaCategory.Tables,

            "FullTextSearch" => GrimoireSchemaCategory.FullTextSearch,

            "Triggers" => GrimoireSchemaCategory.Triggers,

            "Views" => GrimoireSchemaCategory.Views,

            "Accelerators" => GrimoireSchemaCategory.Accelerators,

            _ => throw new InvalidOperationException(
                $"Embedded Grimoire schema resource '{resourceName}' is in unknown category folder '{folder}'."),

        };

    private static string ComputeCanonicalFingerprint()
    {

        StringBuilder combined = new();

        foreach (GrimoireSchemaObject definition in LoadedObjects.Value)
        {

            _ = combined
                .Append(definition.Category)
                .Append('/')
                .Append(definition.Name)
                .Append('\n')
                .Append(definition.Sql)
                .Append("\n---\n");

        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(combined.ToString())));

    }

}
