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
/// <item>One file per table, FTS5 virtual table, trigger, and view, named after the object. The
/// declared name and the file name must match, so a rename cannot leave a stale object installed
/// under its old name.</item>
/// <item>Indexes are co-located in their owning table's file rather than getting files of their own,
/// so a table and everything that constrains it read as one definition.</item>
/// <item>Every statement is <c>CREATE ... IF NOT EXISTS</c>, so an install against an already
/// installed database is a no-op instead of an error.</item>
/// <item>The folder path selects family, transaction tier, and category. Core objects sit directly
/// under a category folder; a capability's objects sit under
/// <c>Capabilities/&lt;Family&gt;/&lt;Tier&gt;/&lt;Category&gt;/</c>. An unrecognized path is a
/// build-time authoring mistake and throws rather than being silently skipped.</item>
/// </list>
///
/// There is no migration chain and no <c>__EFMigrationsHistory</c>: the schema is installed fresh
/// and per-tier metadata in <c>grimoire_feature_schemas</c> records what was installed, so drift and
/// an unsupported newer version fail closed instead of being guessed at.
/// </summary>
internal static class GrimoireSchemaCatalog
{

    /// <summary>
    /// Placeholder resolved at install time from <c>Arcanum:Integrations:Embeddings:Dimensions</c>.
    /// No shipped object uses it today; the mechanism is retained for a statically linked vector
    /// accelerator, whose column width is not knowable at authoring time.
    /// </summary>
    internal const string EmbeddingDimensionsToken = "{{EmbeddingDimensions}}";

    private const string ResourcePrefix = "RetroDownfall.Arcanum.Infrastructure.Data.Schema.";

    private const string ResourceSuffix = ".sql";

    private const string CapabilitiesSegment = "Capabilities";

    private static readonly Lazy<IReadOnlyList<GrimoireSchemaObject>> LoadedObjects =
        new(LoadOrderedObjects, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyList<GrimoireSchemaObject>> LoadedCoreObjects =
        new(() => FilterTier(GrimoireSchemaTransactionTier.Core), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyList<GrimoireSchemaObject>> LoadedCanonicalObjects =
        new(
            () => FilterTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyList<GrimoireSchemaObject>> LoadedAcceleratorObjects =
        new(
            () => FilterTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string> LoadedFingerprint =
        new(
            () => ComputeSourceFingerprint(LoadedObjects.Value),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string> LoadedCanonicalFingerprint =
        new(
            () => ComputeSourceFingerprint(LoadedCanonicalObjects.Value),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string> LoadedAcceleratorFingerprint =
        new(
            () => ComputeSourceFingerprint(LoadedAcceleratorObjects.Value),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Every object in install order, all three tiers. Use the per-tier catalogs to install; this is
    /// the authoring inventory and the input to the combined source fingerprint.
    /// </summary>
    public static IReadOnlyList<GrimoireSchemaObject> AllObjects => LoadedObjects.Value;

    /// <summary>
    /// The durable schema. Installed in one transaction so a failure leaves no partial schema
    /// behind, and so a Covenant failure can never abort startup.
    /// </summary>
    public static IReadOnlyList<GrimoireSchemaObject> CoreObjects => LoadedCoreObjects.Value;

    /// <summary>
    /// Covenant's authoritative tables. Installed in their own transaction after Core succeeds;
    /// failure leaves Covenant unavailable and everything else working.
    /// </summary>
    public static IReadOnlyList<GrimoireSchemaObject> CovenantCanonicalObjects =>
        LoadedCanonicalObjects.Value;

    /// <summary>
    /// Covenant's FTS5 inspection index. Installed in its own transaction after canonical succeeds;
    /// failure degrades inspection search to the canonical fallback and nothing else.
    /// </summary>
    public static IReadOnlyList<GrimoireSchemaObject> CovenantAcceleratorObjects =>
        LoadedAcceleratorObjects.Value;

    /// <summary>
    /// Stable hash of the whole canonical schema source, every tier, in install order. It identifies
    /// the <i>definitions</i>, not an installed database — <see cref="GrimoireSchemaIdentity"/> and
    /// <see cref="GrimoireSchemaManifestInspector"/> do the latter. Tests use it to decide whether a
    /// cached template database is still current.
    ///
    /// <para>Uppercase 64-character SHA-256 with no prefix. Installed-catalog fingerprints use the
    /// separate lowercase <c>sha256-</c> form and are never compared with this value.</para>
    /// </summary>
    public static string CanonicalSchemaFingerprint => LoadedFingerprint.Value;

    /// <summary>
    /// The same hash restricted to Covenant canonical resources, which is the capability's source
    /// identity in <c>grimoire_feature_schemas</c>. Kept separate from
    /// <see cref="CanonicalSchemaFingerprint"/> so an unrelated core edit does not read as a
    /// Covenant schema change and refuse to open an intact capability.
    /// </summary>
    public static string CovenantCanonicalSchemaFingerprint => LoadedCanonicalFingerprint.Value;

    /// <summary>
    /// The same hash restricted to Covenant accelerator resources. The accelerator has its own
    /// metadata row and its own failure domain, so it needs a source identity a canonical-only edit
    /// does not disturb.
    /// </summary>
    public static string CovenantAcceleratorSchemaFingerprint => LoadedAcceleratorFingerprint.Value;

    /// <summary>
    /// Substitutes the install-time template values into an object definition and refuses to return
    /// a statement that still carries an unresolved placeholder — a typo in a token name must fail
    /// loudly rather than reach SQLite as invalid DDL.
    /// </summary>
    /// <param name="embeddingDimensions">
    /// The configured embedding width, or <see langword="null"/> on an install path that has no
    /// configuration to read. A null width cannot resolve a templated object, so such an object
    /// fails closed rather than installing at a guessed width.
    /// </param>
    public static string Resolve(GrimoireSchemaObject definition, int? embeddingDimensions)
    {

        ArgumentNullException.ThrowIfNull(definition);

        if (embeddingDimensions is int dimensions)
        {

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);

        }

        string resolved = embeddingDimensions is int width
            ? definition.Sql.Replace(
                EmbeddingDimensionsToken,
                width.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            : definition.Sql;

        if (resolved.Contains("{{", StringComparison.Ordinal))
        {

            throw new InvalidOperationException(
                $"Grimoire schema object '{definition.ResourcePath}.sql' contains an unresolved template placeholder.");

        }

        return resolved;

    }

    /// <summary>
    /// Decodes the dotted resource path below <c>Data/Schema/</c> into family, transaction tier,
    /// category, and object name.
    ///
    /// <para>Exactly two shapes are accepted:</para>
    ///
    /// <code>
    /// &lt;Category&gt;.&lt;Name&gt;
    /// Capabilities.Covenant.{Canonical|Accelerator}.&lt;Category&gt;.&lt;Name&gt;
    /// </code>
    ///
    /// <para>Everything else throws. Deliberately a closed switch rather than
    /// <c>Enum.Parse</c>: these folder names are a small authoring vocabulary, and an unknown one
    /// must name itself in the failure instead of silently installing in an arbitrary tier — or,
    /// worse, landing a Covenant object in the startup-blocking core transaction.</para>
    /// </summary>
    internal static GrimoireSchemaResourcePath ParseResourcePath(string relativePath)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string[] segments = relativePath.Split('.');

        if (segments.Length == 2)
        {

            return new GrimoireSchemaResourcePath(
                GrimoireSchemaFamily.Core,
                GrimoireSchemaTransactionTier.Core,
                ParseCategory(segments[0], relativePath),
                segments[1]);

        }

        if (segments.Length == 5
            && string.Equals(segments[0], CapabilitiesSegment, StringComparison.Ordinal))
        {

            (GrimoireSchemaFamily family, GrimoireSchemaTransactionTier tier) =
                ParseCapability(segments[1], segments[2], relativePath);

            return new GrimoireSchemaResourcePath(
                family,
                tier,
                ParseCategory(segments[3], relativePath),
                segments[4]);

        }

        throw new InvalidOperationException(
            $"Embedded Grimoire schema resource '{relativePath}.sql' is not under a recognized "
            + "'<Category>' or 'Capabilities/<Family>/<Tier>/<Category>' folder.");

    }

    /// <summary>
    /// Extracts the object name from the first <c>CREATE ... IF NOT EXISTS</c> declaration, with
    /// optional double quoting removed. Used to prove the declaration and the file name agree.
    /// </summary>
    internal static string ReadDeclaredObjectName(string sql)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        const string marker = "IF NOT EXISTS";

        int start = sql.IndexOf(marker, StringComparison.Ordinal);

        if (start < 0)
        {

            throw new InvalidOperationException(
                "A Grimoire schema object declares no 'CREATE ... IF NOT EXISTS' statement.");

        }

        int index = start + marker.Length;

        while (index < sql.Length && char.IsWhiteSpace(sql[index]))
        {

            index++;

        }

        bool quoted = index < sql.Length && sql[index] == '"';

        if (quoted)
        {

            index++;

        }

        int nameStart = index;

        while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_'))
        {

            index++;

        }

        if (index == nameStart)
        {

            throw new InvalidOperationException(
                "A Grimoire schema object declares no object name after 'IF NOT EXISTS'.");

        }

        return sql[nameStart..index];

    }

    private static IReadOnlyList<GrimoireSchemaObject> FilterTier(GrimoireSchemaTransactionTier tier) =>
        [.. LoadedObjects.Value.Where(definition => definition.TransactionTier == tier)];

    private static IReadOnlyList<GrimoireSchemaObject> LoadOrderedObjects()
    {

        Assembly assembly = typeof(GrimoireSchemaCatalog).Assembly;

        List<GrimoireSchemaObject> definitions = [];

        HashSet<(GrimoireSchemaTransactionTier Tier, GrimoireSchemaCategory Category, string Name)> seen = [];

        foreach (string resourceName in assembly.GetManifestResourceNames())
        {

            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !resourceName.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            {

                continue;

            }

            GrimoireSchemaObject definition = ReadObject(assembly, resourceName);

            if (!seen.Add((definition.TransactionTier, definition.Category, definition.Name)))
            {

                throw new InvalidOperationException(
                    $"Embedded Grimoire schema resource '{definition.ResourcePath}.sql' duplicates an "
                    + "object already declared for the same transaction tier and category.");

            }

            definitions.Add(definition);

        }

        if (definitions.Count == 0)
        {

            throw new InvalidOperationException(
                "No embedded Grimoire schema objects were found; the Data/Schema glob is not being embedded.");

        }

        return
        [
            .. definitions
                .OrderBy(static definition => (int)definition.TransactionTier)
                .ThenBy(static definition => (int)definition.Category)
                .ThenBy(static definition => definition.Name, StringComparer.Ordinal),
        ];

    }

    private static GrimoireSchemaObject ReadObject(Assembly assembly, string resourceName)
    {

        string relative = resourceName[ResourcePrefix.Length..^ResourceSuffix.Length];

        GrimoireSchemaResourcePath path = ParseResourcePath(relative);

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Grimoire schema resource not found: {resourceName}");

        using StreamReader reader = new(stream, Encoding.UTF8);

        string sql = reader.ReadToEnd();

        string declared = ReadDeclaredObjectName(sql);

        if (!string.Equals(declared, path.Name, StringComparison.Ordinal))
        {

            throw new InvalidOperationException(
                $"Embedded Grimoire schema resource '{relative}.sql' declares object '{declared}', "
                + "which does not match its file name.");

        }

        return new GrimoireSchemaObject(
            path.Family,
            path.TransactionTier,
            path.Category,
            path.Name,
            relative,
            sql);

    }

    private static (GrimoireSchemaFamily Family, GrimoireSchemaTransactionTier Tier) ParseCapability(
        string family,
        string tier,
        string relativePath) =>
        (family, tier) switch
        {

            ("Covenant", "Canonical") =>
                (GrimoireSchemaFamily.Covenant, GrimoireSchemaTransactionTier.CovenantCanonical),

            ("Covenant", "Accelerator") =>
                (GrimoireSchemaFamily.Covenant, GrimoireSchemaTransactionTier.CovenantAccelerator),

            _ => throw new InvalidOperationException(
                $"Embedded Grimoire schema resource '{relativePath}.sql' names unknown capability "
                + $"family '{family}' and transaction tier '{tier}'."),

        };

    private static GrimoireSchemaCategory ParseCategory(string folder, string relativePath) =>
        folder switch
        {

            "Tables" => GrimoireSchemaCategory.Tables,

            "FullTextSearch" => GrimoireSchemaCategory.FullTextSearch,

            "Triggers" => GrimoireSchemaCategory.Triggers,

            "Views" => GrimoireSchemaCategory.Views,

            _ => throw new InvalidOperationException(
                $"Embedded Grimoire schema resource '{relativePath}.sql' is in unknown category folder '{folder}'."),

        };

    /// <summary>
    /// Frames each object with family, tier, category, resource path, and name before its exact
    /// unresolved SQL, so two objects cannot hash alike by moving between tiers or folders.
    /// </summary>
    private static string ComputeSourceFingerprint(IReadOnlyList<GrimoireSchemaObject> definitions)
    {

        StringBuilder combined = new();

        foreach (GrimoireSchemaObject definition in definitions)
        {

            _ = combined
                .Append(definition.Family)
                .Append('/')
                .Append(definition.TransactionTier)
                .Append('/')
                .Append(definition.Category)
                .Append('/')
                .Append(definition.ResourcePath)
                .Append('/')
                .Append(definition.Name)
                .Append('\n')
                .Append(definition.Sql)
                .Append("\n---\n");

        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(combined.ToString())));

    }

}
