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

    private const string TransitionsSegment = "Transitions";

    private static readonly Lazy<IReadOnlyList<GrimoireSchemaObject>> LoadedObjects =
        new(LoadOrderedObjects, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyList<GrimoireSchemaTransitionStatementResource>> LoadedTransitions =
        new(LoadOrderedTransitions, LazyThreadSafetyMode.ExecutionAndPublication);

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

    private static readonly Lazy<string> LoadedCoreFingerprint =
        new(
            () => ComputeSourceFingerprint(LoadedCoreObjects.Value),
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
    /// Every transition statement in the tree, ordered by tier, then target version, then ordinal.
    /// That order is the install order of a version step.
    /// </summary>
    /// <remarks>
    /// Deliberately outside <see cref="AllObjects"/>, outside every per-tier object list, and outside
    /// every source fingerprint. A transition resource that entered a tier's fingerprint would change
    /// the value recorded for the version it upgrades <i>from</i>, so authoring the very step that
    /// leaves version 1 would make every installation at version 1 refuse with
    /// <c>SourceDefinitionMismatch</c> before that step could run. The feature would break itself on
    /// its first use.
    ///
    /// <para>Empty today: no tier has left version 1. The loader runs in production and finds
    /// nothing, which is the cheapest state in which to watch it run.</para>
    /// </remarks>
    public static IReadOnlyList<GrimoireSchemaTransitionStatementResource> TransitionStatements =>
        LoadedTransitions.Value;

    /// <summary>
    /// Stable hash of the whole canonical schema source, every tier, in install order. It identifies
    /// the <i>definitions</i>, not an installed database — <see cref="GrimoireSchemaIdentity"/> and
    /// <see cref="GrimoireSchemaManifestInspector"/> do the latter. Tests use it to decide whether a
    /// cached template database is still current.
    ///
    /// <para>It is deliberately not any tier's identity: it moves when any resource in any tier
    /// changes, so recording it against a tier would make an edit to one capability read as a change
    /// to another. Each tier records its own scoped value instead.</para>
    ///
    /// <para>Uppercase 64-character SHA-256 with no prefix. Installed-catalog fingerprints use the
    /// separate lowercase <c>sha256-</c> form and are never compared with this value.</para>
    /// </summary>
    public static string CanonicalSchemaFingerprint => LoadedFingerprint.Value;

    /// <summary>
    /// The same hash restricted to core resources, which is the durable schema's source identity in
    /// <c>grimoire_feature_schemas</c>. Core is the one tier whose refusal aborts startup, so its
    /// identity must not move for a change it does not contain: a comment-only edit under
    /// <c>Capabilities/Covenant/</c> would otherwise read as a changed core schema and take the host
    /// and every CLI verb that opens the Grimoire down with it.
    /// </summary>
    public static string CoreSchemaFingerprint => LoadedCoreFingerprint.Value;

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
    /// Decodes a transition resource path, or reports that the path names an object instead.
    /// </summary>
    /// <remarks>
    /// Returning <see langword="false"/> means "this is not a transition", which is the ordinary
    /// answer for every object in the tree. A path that <i>is</i> under a <c>Transitions</c> folder
    /// and is malformed throws instead: declining it would hand it to
    /// <see cref="ParseResourcePath"/> and produce a failure naming the wrong mistake.
    /// </remarks>
    internal static bool TryParseTransitionResourcePath(
        string relativePath,
        out GrimoireSchemaTransitionResourcePath? path)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string[] segments = relativePath.Split('.');

        if (segments.Length == 3
            && string.Equals(segments[0], TransitionsSegment, StringComparison.Ordinal))
        {

            path = BuildTransitionPath(
                GrimoireSchemaFamily.Core,
                GrimoireSchemaTransactionTier.Core,
                segments[1],
                segments[2],
                relativePath);

            return true;

        }

        if (segments.Length == 6
            && string.Equals(segments[0], CapabilitiesSegment, StringComparison.Ordinal)
            && string.Equals(segments[3], TransitionsSegment, StringComparison.Ordinal))
        {

            (GrimoireSchemaFamily family, GrimoireSchemaTransactionTier tier) =
                ParseCapability(segments[1], segments[2], relativePath);

            path = BuildTransitionPath(family, tier, segments[4], segments[5], relativePath);

            return true;

        }

        path = null;

        return false;

    }

    /// <summary>
    /// Decodes the <c>V&lt;n&gt;</c> folder and the <c>&lt;ordinal&gt;_&lt;name&gt;</c> file name of
    /// one transition resource.
    /// </summary>
    /// <remarks>
    /// Version 1 is refused along with 0 and every non-numeric folder: version 1 is what the head
    /// tree installs directly, so it can never be a transition <i>target</i>, and a folder claiming
    /// otherwise is an authoring mistake rather than a step to run.
    /// </remarks>
    private static GrimoireSchemaTransitionResourcePath BuildTransitionPath(
        GrimoireSchemaFamily family,
        GrimoireSchemaTransactionTier tier,
        string versionFolder,
        string fileName,
        string relativePath)
    {

        if (versionFolder.Length < 2
            || versionFolder[0] != 'V'
            || !int.TryParse(
                versionFolder.AsSpan(1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int toVersion)
            || toVersion < 2)
        {

            throw new InvalidOperationException(
                $"Embedded Grimoire transition resource '{relativePath}.sql' is not in a 'V<n>' folder "
                + "with n of 2 or more; version 1 is installed by the head tree and is never a transition target.");

        }

        int underscore = fileName.IndexOf('_', StringComparison.Ordinal);

        if (underscore <= 0
            || underscore == fileName.Length - 1
            || !int.TryParse(
                fileName.AsSpan(0, underscore),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int ordinal))
        {

            throw new InvalidOperationException(
                $"Embedded Grimoire transition resource '{relativePath}.sql' is not named "
                + "'<ordinal>_<name>' with a numeric ordinal and a non-empty name.");

        }

        return new GrimoireSchemaTransitionResourcePath(family, tier, toVersion, ordinal, fileName[(underscore + 1)..]);

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

            string relative = resourceName[ResourcePrefix.Length..^ResourceSuffix.Length];

            // A transition statement lives in the same embedded tree and is loaded separately. It is
            // never a head object: nothing converges it, and it must not reach any source fingerprint.
            if (TryParseTransitionResourcePath(relative, out GrimoireSchemaTransitionResourcePath? _))
            {

                continue;

            }

            GrimoireSchemaObject definition = ReadObject(assembly, resourceName, relative);

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

    /// <summary>
    /// Reads one head object. The relative path is supplied rather than recomputed, because the
    /// caller has already decoded it once to rule the resource out as a transition.
    /// </summary>
    private static GrimoireSchemaObject ReadObject(Assembly assembly, string resourceName, string relative)
    {

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
    /// Loads every transition statement in the tree, in the order a version step installs them.
    /// </summary>
    /// <remarks>
    /// Duplicate ordinals within one tier and target version throw rather than being ordered
    /// arbitrarily: the ordinal <i>is</i> the install order, and two statements sharing one have no
    /// defined order at all.
    /// </remarks>
    private static IReadOnlyList<GrimoireSchemaTransitionStatementResource> LoadOrderedTransitions()
    {

        Assembly assembly = typeof(GrimoireSchemaCatalog).Assembly;

        List<GrimoireSchemaTransitionStatementResource> statements = [];

        HashSet<(GrimoireSchemaTransactionTier Tier, int ToVersion, int Ordinal)> seen = [];

        foreach (string resourceName in assembly.GetManifestResourceNames())
        {

            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !resourceName.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            {

                continue;

            }

            string relative = resourceName[ResourcePrefix.Length..^ResourceSuffix.Length];

            if (!TryParseTransitionResourcePath(relative, out GrimoireSchemaTransitionResourcePath? path))
            {

                continue;

            }

            if (!seen.Add((path!.TransactionTier, path.ToVersion, path.Ordinal)))
            {

                throw new InvalidOperationException(
                    $"Embedded Grimoire transition resource '{relative}.sql' duplicates ordinal "
                    + $"{path.Ordinal} already declared for the same tier and target version; two "
                    + "statements sharing one ordinal have no defined install order.");

            }

            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded Grimoire transition resource not found: {resourceName}");

            using StreamReader reader = new(stream, Encoding.UTF8);

            statements.Add(
                new GrimoireSchemaTransitionStatementResource(
                    path.Family,
                    path.TransactionTier,
                    path.ToVersion,
                    path.Ordinal,
                    path.Name,
                    relative,
                    reader.ReadToEnd()));

        }

        return
        [
            .. statements
                .OrderBy(static statement => (int)statement.TransactionTier)
                .ThenBy(static statement => statement.ToVersion)
                .ThenBy(static statement => statement.Ordinal),
        ];

    }

    /// <summary>
    /// Frames each object with family, tier, category, resource path, and name before its exact
    /// unresolved SQL, so two objects cannot hash alike by moving between tiers or folders.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so a test can prove every published fingerprint is computed over
    /// head objects alone. A test-only wrapper would be a second name for one algorithm.
    /// </remarks>
    internal static string ComputeSourceFingerprint(IReadOnlyList<GrimoireSchemaObject> definitions)
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
