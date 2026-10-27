using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// What one inspection observed about the Covenant tiers, without changing anything.
/// </summary>
/// <remarks>
/// The digest answers whether this is still the catalog the journal described. Equality is necessary
/// for a pure original dispatch-refusal proof, but cannot establish that an interrupted action had no
/// data effects. Recovery never re-executes an unresolved Prepared repair.
/// </remarks>
internal sealed record CovenantSchemaRepairInspection(
    CovenantDigest CatalogDigest,
    string CatalogFingerprint,
    bool CanonicalObjectsPresent,
    bool CanonicalValid,
    bool AcceleratorValid,
    IReadOnlyList<string> MissingOrdinaryIndexes,
    string? BlockingCode);

/// <summary>
/// The three things schema repair is allowed to do, and the inspection that decides whether it may.
/// </summary>
/// <remarks>
/// A seam rather than a direct installer dependency, because "what may be repaired" is a policy
/// question and "how is a tier installed" is a mechanism the bootstrap already owns. Splitting them
/// keeps the policy testable without a real catalog and keeps the mechanism single-owner.
/// </remarks>
internal interface ICovenantSchemaRepairExecutor
{
    Task<CovenantSchemaRepairResult<CovenantSchemaRepairInspection>> InspectAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken);

    /// <summary>
    /// Performs the one permitted repair, and reports positive no-mutation evidence, completed mutation, or unknown effects.
    /// </summary>
    Task<CovenantSchemaRepairExecution> RepairAsync(
        SqliteConnection connection,
        CovenantSchemaRepairAction action,
        CovenantSchemaRepairInspection inspected,
        CancellationToken cancellationToken);
}

/// <summary>
/// The production repair executor, over the shipped manifest inspector and tier installer.
/// </summary>
/// <remarks>
/// Nothing here repairs a missing trigger, drops an object, or converges a tier whose installed
/// version disagrees with this binary. Writes may have occurred while a trigger's invariant was
/// absent, so restoring the trigger would restore the guarantee without restoring the data it
/// protected — and a same-version definition mismatch means two builds disagree about what version 1
/// means, which no automatic action can resolve (§10.17).
/// </remarks>
internal sealed partial class CovenantSchemaRepairExecutor(
    GrimoireSchemaManifestInspector inspector,
    GrimoireSchemaInstaller installer,
    GrimoireSchemaInitializationContext initializationContext,
    int embeddingDimensions) : ICovenantSchemaRepairExecutor
{
    private readonly GrimoireSchemaManifestInspector _inspector =
        inspector ?? throw new ArgumentNullException(nameof(inspector));

    private readonly GrimoireSchemaInstaller _installer =
        installer ?? throw new ArgumentNullException(nameof(installer));

    private readonly GrimoireSchemaInitializationContext _context =
        initializationContext ?? throw new ArgumentNullException(nameof(initializationContext));

    // ASCII unit and record separators. Neither can appear in a SQLite object name or in the DDL text
    // SQLite stores, so no combination of catalog fields can be framed into a colliding digest input.
    private const char FieldSeparator = '\u001F';

    private const char RowSeparator = '\u001E';

    public async Task<CovenantSchemaRepairResult<CovenantSchemaRepairInspection>> InspectAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            return await InspectCoreAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException failure)
        {
            if ((failure.SqliteErrorCode & 0xff) == 9)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return CovenantSchemaRepairResult<CovenantSchemaRepairInspection>.Failure(
                new Error(ErrorCodes.Covenant.MaintenanceFailed,
                    "Covenant schema inspection could not be completed. Preserve the profile and inspect the safe storage diagnostic before recovery."),
                CovenantSchemaRepairStorageFault.From(failure));
        }
    }

    private async Task<Result<CovenantSchemaRepairInspection>> InspectCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        GrimoireSchemaInspectionResult canonical = await _inspector.InspectAsync(
            connection,
            transaction: null,
            GrimoireSchemaManifests.CovenantCanonical,
            cancellationToken).ConfigureAwait(false);

        GrimoireSchemaInspectionResult accelerator = await _inspector.InspectAsync(
            connection,
            transaction: null,
            GrimoireSchemaManifests.CovenantAccelerator,
            cancellationToken).ConfigureAwait(false);

        bool present = await AnyCanonicalObjectExistsAsync(connection, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> missingIndexes = await MissingOrdinaryIndexesAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        string fingerprint = canonical.InstalledCatalogFingerprint
            ?? accelerator.InstalledCatalogFingerprint
            ?? GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint;

        return Result<CovenantSchemaRepairInspection>.Success(
            new CovenantSchemaRepairInspection(
                await ComputeCatalogDigestAsync(connection, cancellationToken).ConfigureAwait(false),
                fingerprint,
                present,
                canonical.IsValid,
                accelerator.IsValid,
                missingIndexes,
                canonical.DiagnosticCode ?? accelerator.DiagnosticCode));
    }

    public async Task<CovenantSchemaRepairExecution> RepairAsync(
        SqliteConnection connection,
        CovenantSchemaRepairAction action,
        CovenantSchemaRepairInspection inspected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(inspected);

        if (CovenantSchemaRepairExecution.RefusesBeforeAnyMutator(action, inspected))
        {
            return CovenantSchemaRepairExecution.NoMutation(CovenantSchemaRepairFailure.ActionNotApplicable);
        }

        switch (action)
        {
            case CovenantSchemaRepairAction.RepairOrdinaryIndex when inspected.MissingOrdinaryIndexes.Count == 0:

                return CovenantSchemaRepairExecution.NoMutation();

            case CovenantSchemaRepairAction.RepairOrdinaryIndex:

                // The narrowest arm runs the narrowest statements. Handing it the whole installer
                // would let "recreate one missing index" also create an absent table, which is a
                // different repair than the operator asked for.
                return await RecreateOrdinaryIndexesAsync(
                    connection,
                    inspected.MissingOrdinaryIndexes,
                    cancellationToken).ConfigureAwait(false);

            case CovenantSchemaRepairAction.InstallAbsentCanonicalFamily:
            case CovenantSchemaRepairAction.RepairExistingFamily:

                break;

            default:

                return CovenantSchemaRepairExecution.NoMutation(CovenantSchemaRepairFailure.ActionNotApplicable);
        }

        try
        {
            GrimoireSchemaInstallResult installed = await _installer.InstallAsync(
                connection,
                embeddingDimensions,
                _context,
                cancellationToken).ConfigureAwait(false);

            return installed.CovenantCanonical.IsHealthy
                ? CovenantSchemaRepairExecution.MutationCompleted()
                : CovenantSchemaRepairExecution.Unknown(CovenantSchemaRepairFailure.CanonicalUnhealthy);
        }
        catch (SqliteException exception)
        {
            if ((exception.SqliteErrorCode & 0xff) == 9)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return CovenantSchemaRepairExecution.Unknown(CovenantSchemaRepairFailure.Storage, exception);
        }
        catch (GrimoireSchemaRefusedException exception)
        {
            return CovenantSchemaRepairExecution.Unknown(CovenantSchemaRepairFailure.SchemaRefused, exception);
        }
    }

    /// <summary>
    /// Executes only the <c>CREATE INDEX</c> statements the canonical catalog declares for the named
    /// missing indexes.
    /// </summary>
    /// <remarks>
    /// The statements come from embedded resources compiled into this binary, matched by the exact
    /// index names the inspector reported missing. Nothing read from the database reaches the
    /// statement text, and no other object type is eligible: a missing table, trigger, or virtual
    /// table is not an ordinary index and this action must not create one.
    /// </remarks>
    private static async Task<CovenantSchemaRepairExecution> RecreateOrdinaryIndexesAsync(
        SqliteConnection connection,
        IReadOnlyList<string> missing,
        CancellationToken cancellationToken)
    {
        HashSet<string> wanted = new(missing, StringComparer.Ordinal);

        bool changed = false;

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.CovenantCanonicalObjects)
        {
            if (definition.Category != GrimoireSchemaCategory.Tables)
            {
                continue;
            }

            string sql = GrimoireSchemaCatalog.Resolve(definition, embeddingDimensions: null);

            foreach (Match match in OrdinaryIndexStatement().Matches(sql))
            {
                if (!wanted.Contains(match.Groups["name"].Value))
                {
                    continue;
                }

                try
                {
                    await using SqliteCommand command = connection.CreateCommand();

                    command.CommandText = match.Value;

                    _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    changed = true;
                }
                catch (SqliteException exception)
                {
                    if ((exception.SqliteErrorCode & 0xff) == 9)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    return CovenantSchemaRepairExecution.Unknown(CovenantSchemaRepairFailure.Storage, exception);
                }
            }
        }

        return changed ? CovenantSchemaRepairExecution.MutationCompleted()
            : CovenantSchemaRepairExecution.NoMutation(CovenantSchemaRepairFailure.IndexDefinitionMissing);
    }

    [GeneratedRegex(
        @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+IF\s+NOT\s+EXISTS\s+(?<name>[A-Za-z0-9_]+)\b[^;]*;",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrdinaryIndexStatement();

    /// <summary>
    /// Hashes the whole installed Covenant catalog, so a repair can prove the schema has not moved.
    /// </summary>
    private static async Task<CovenantDigest> ComputeCatalogDigestAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT "type", "name", COALESCE("sql", '')
            FROM sqlite_master
            WHERE "name" LIKE 'covenant_%'
            ORDER BY "type", "name";
            """;

        StringBuilder catalog = new();

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = catalog
                .Append(reader.GetString(0))
                .Append(FieldSeparator)
                .Append(reader.GetString(1))
                .Append(FieldSeparator)
                .Append(reader.GetString(2))
                .Append(RowSeparator);
        }

        return new CovenantDigest(SHA256.HashData(Encoding.UTF8.GetBytes(catalog.ToString())));
    }

    /// <summary>
    /// The tables the canonical manifest declares, which are the only ones whose presence says anything
    /// about the canonical family.
    /// </summary>
    private static readonly string[] CanonicalTableNames =
    [
        .. GrimoireSchemaManifests.CovenantCanonical.Entries
            .Where(static entry => entry.Type is GrimoireSchemaObjectType.Table or GrimoireSchemaObjectType.VirtualTable)
            .Select(static entry => entry.Name),
    ];

    /// <summary>
    /// Whether any table of the canonical Covenant family is installed.
    /// </summary>
    /// <remarks>
    /// Matched against the names the manifest actually declares, never against a <c>covenant_</c> name
    /// prefix: Core owns two tables that share that prefix (<c>covenant_schema_repair_intents</c> and
    /// <c>covenant_authority_state</c>) and the core install runs before any repair pass, so a prefix
    /// match answers "present" on every installation that exists — including the one whose canonical
    /// family is genuinely absent, which is the single catalog
    /// <see cref="CovenantSchemaRepairAction.InstallAbsentCanonicalFamily"/> exists to repair.
    /// </remarks>
    private static async Task<bool> AnyCanonicalObjectExistsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (CanonicalTableNames.Length == 0)
        {
            return false;
        }

        await using SqliteCommand command = connection.CreateCommand();

        // Parameterized rather than interpolated: the names are code-owned, but a catalog identity is
        // exactly the kind of value that must never be concatenated into SQL.
        string placeholders = string.Join(", ", CanonicalTableNames.Select(static (_, index) => $"@name{index}"));

        command.CommandText =
            $"""SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE "type" = 'table' AND "name" IN ({placeholders}));""";

        for (int index = 0; index < CanonicalTableNames.Length; index++)
        {
            _ = command.Parameters.AddWithValue($"@name{index}", CanonicalTableNames[index]);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// The ordinary indexes the canonical manifest declares that the installed catalog does not have.
    /// </summary>
    /// <remarks>
    /// Only indexes. A missing table, trigger, or virtual table is not an ordinary index and is not
    /// repairable by this action, which is the entire safety property of the narrowest arm.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> MissingOrdinaryIndexesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        HashSet<string> installed = new(StringComparer.Ordinal);

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT "name" FROM sqlite_master WHERE "type" = 'index' AND "name" LIKE '%covenant%';
                """;

            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = installed.Add(reader.GetString(0));
            }
        }

        return
        [
            .. GrimoireSchemaManifests.CovenantCanonical.Entries
                .SelectMany(static entry => entry.Indexes)
                .Select(static index => index.Name)
                .Where(name => !installed.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }
}
