using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Installs the Grimoire schema from <see cref="GrimoireSchemaCatalog"/> as three independently
/// validated transaction tiers - AOT-safe, no <c>Database.MigrateAsync</c>, no filesystem access, no
/// migration bookkeeping.
///
/// <para>The three tiers exist because they must fail differently:</para>
///
/// <list type="bullet">
/// <item><b>Core</b> installs in one transaction and rethrows. Nothing else can be trusted without
/// it, so its failure aborts startup.</item>
/// <item><b>Covenant canonical</b> installs in its own transaction after Core is healthy. Its
/// failure is caught at its boundary and leaves Covenant unavailable while status, diagnosis,
/// offline repair, and the rest of Arcanum keep working.</item>
/// <item><b>Covenant accelerator</b> installs in its own transaction after canonical is healthy. Its
/// failure degrades inspection search to the canonical fallback and nothing else.</item>
/// </list>
///
/// <para>Cancellation always propagates. A cancelled install is the operator stopping the host, not
/// a damaged tier, and recording it as a health state would make the next start refuse a database
/// that is perfectly fine.</para>
/// </summary>
internal sealed class GrimoireSchemaInstaller(
    GrimoireSchemaManifestInspector inspector,
    GrimoireSchemaDataInitializers initializers,
    ILogger<GrimoireSchemaInstaller>? logger = null)
{

    private readonly GrimoireSchemaManifestInspector _inspector =
        inspector ?? throw new ArgumentNullException(nameof(inspector));

    private readonly GrimoireSchemaDataInitializers _initializers =
        initializers ?? throw new ArgumentNullException(nameof(initializers));

    /// <summary>
    /// Installs or converges all three tiers.
    /// </summary>
    /// <param name="embeddingDimensions">
    /// Configured embedding width, used only by the post-commit dimension-mismatch diagnostic and by
    /// any templated object. <see cref="InstallCoreOnlyAsync"/> has no configuration to read and so
    /// does not take it.
    /// </param>
    public async Task<GrimoireSchemaInstallResult> InstallAsync(
        SqliteConnection connection,
        int embeddingDimensions,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(context);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(embeddingDimensions);

        GrimoireSchemaTierInstallResult core = await InstallCoreAsync(
            connection,
            embeddingDimensions,
            context,
            cancellationToken).ConfigureAwait(false);

        GrimoireSchemaTierInstallResult canonical = await InstallOptionalTierAsync(
            connection,
            GrimoireSchemaTransactionTier.CovenantCanonical,
            embeddingDimensions,
            context,
            dependencyHealthy: core.IsHealthy,
            cancellationToken).ConfigureAwait(false);

        GrimoireSchemaTierInstallResult accelerator = await InstallOptionalTierAsync(
            connection,
            GrimoireSchemaTransactionTier.CovenantAccelerator,
            embeddingDimensions,
            context,
            dependencyHealthy: canonical.IsHealthy,
            cancellationToken).ConfigureAwait(false);

        await TryRebuildLexiconFtsAsync(connection, cancellationToken).ConfigureAwait(false);

        await WarnOnDimensionMismatchAsync(connection, embeddingDimensions, cancellationToken)
            .ConfigureAwait(false);

        return new GrimoireSchemaInstallResult(core, canonical, accelerator);

    }

    /// <summary>
    /// Installs or converges the Core tier alone, without inspecting, creating, attaching, or
    /// initializing any Covenant object.
    /// </summary>
    /// <remarks>
    /// A new-install startup gate calls this on one non-pooled connection, reads the seeded authority
    /// row, closes, and only then decides whether optional services may initialize. It delegates to
    /// the same helper <see cref="InstallAsync"/> uses, so there is exactly one Core installation
    /// algorithm.
    /// </remarks>
    public Task<GrimoireSchemaTierInstallResult> InstallCoreOnlyAsync(
        SqliteConnection connection,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(context);

        return InstallCoreAsync(connection, embeddingDimensions: null, context, cancellationToken);

    }

    private async Task<GrimoireSchemaTierInstallResult> InstallCoreAsync(
        SqliteConnection connection,
        int? embeddingDimensions,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        if (connection.State != System.Data.ConnectionState.Open)
        {

            throw new InvalidOperationException("Grimoire schema installation requires an open SQLite connection.");

        }

        // The core schema contains guard triggers that call the arcanum_*_authorized functions, and
        // SQLite resolves a function when it prepares a statement — so a seed that merely touches a
        // guarded table fails with "no such function" on a connection that never registered them.
        // Installation owns the schema those triggers belong to, so it guarantees them here rather
        // than relying on every caller to have applied connection policy first.
        Covenant.CovenantSqliteConnectionInitializer.Instance.EnsureAuthorizationFunctions(connection);

        return await SqliteBusyRetry.ExecuteAsync(
            () => InstallTierAsync(
                connection,
                GrimoireSchemaTransactionTier.Core,
                embeddingDimensions,
                context,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<GrimoireSchemaTierInstallResult> InstallOptionalTierAsync(
        SqliteConnection connection,
        GrimoireSchemaTransactionTier tier,
        int? embeddingDimensions,
        GrimoireSchemaInitializationContext context,
        bool dependencyHealthy,
        CancellationToken cancellationToken)
    {

        GrimoireSchemaManifest manifest = GrimoireSchemaManifests.ForTier(tier);

        if (!dependencyHealthy)
        {

            return Failed(manifest, GrimoireSchemaTierHealth.DependencyUnavailable);

        }

        try
        {

            return await SqliteBusyRetry.ExecuteAsync(
                () => InstallTierAsync(connection, tier, embeddingDimensions, context, cancellationToken),
                cancellationToken).ConfigureAwait(false);

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            // The whole point of the tier boundary. Whatever went wrong is confined to this
            // capability, and the exception is logged rather than surfaced, because the public
            // result carries only a closed content-free code.
            logger?.LogWarning(
                exception,
                "The {Tier} schema tier failed to install; that capability is unavailable for this process.",
                tier);

            return Failed(manifest, GrimoireSchemaTierHealth.Unavailable);

        }

    }

    /// <summary>
    /// The single tier algorithm: inspect what is already recorded, refuse anything this binary
    /// cannot honor, then install DDL, seed data, re-inspect, and record metadata in one transaction.
    /// </summary>
    private async Task<GrimoireSchemaTierInstallResult> InstallTierAsync(
        SqliteConnection connection,
        GrimoireSchemaTransactionTier tier,
        int? embeddingDimensions,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        GrimoireSchemaManifest manifest = GrimoireSchemaManifests.ForTier(tier);

        GrimoireSchemaTierHealth? refusal = await ClassifyExistingAsync(
            connection,
            manifest,
            cancellationToken).ConfigureAwait(false);

        if (refusal is GrimoireSchemaTierHealth reason)
        {

            if (tier == GrimoireSchemaTransactionTier.Core)
            {

                throw new GrimoireSchemaRefusedException(tier, reason);

            }

            return Failed(manifest, reason);

        }

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            foreach (GrimoireSchemaObject definition in ObjectsFor(tier))
            {

                cancellationToken.ThrowIfCancellationRequested();

                await using SqliteCommand command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = GrimoireSchemaCatalog.Resolve(definition, embeddingDimensions);

                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            }

            await _initializers.For(tier)
                .InitializeAsync(connection, transaction, context, cancellationToken)
                .ConfigureAwait(false);

            GrimoireSchemaInspectionResult inspection = await _inspector
                .InspectAsync(connection, transaction, manifest, cancellationToken)
                .ConfigureAwait(false);

            if (!inspection.IsValid)
            {

                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                GrimoireSchemaTierHealth health =
                    inspection.Failure == GrimoireSchemaInspectionFailure.CatalogReadFailed
                        ? GrimoireSchemaTierHealth.Unavailable
                        : GrimoireSchemaTierHealth.InstalledCatalogDrift;

                if (tier == GrimoireSchemaTransactionTier.Core)
                {

                    throw new GrimoireSchemaRefusedException(tier, health);

                }

                return Failed(manifest, health, inspection.DiagnosticCode);

            }

            await WriteMetadataAsync(
                connection,
                transaction,
                manifest,
                inspection.InstalledCatalogFingerprint!,
                context,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new GrimoireSchemaTierInstallResult(
                tier,
                manifest.Version,
                GrimoireSchemaTierHealth.Healthy,
                manifest.SourceDefinitionFingerprint,
                inspection.InstalledCatalogFingerprint,
                DiagnosticCode: null);

        }
        catch
        {

            try
            {

                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            }
            catch (Exception)
            {

                // Best-effort: disposal rolls an uncommitted transaction back anyway, and keeping
                // the original failure is worth more than reporting the rollback's.

            }

            throw;

        }

    }

    /// <summary>
    /// Decides whether an already-recorded tier may be converged, before any DDL runs.
    /// </summary>
    /// <remarks>
    /// Returning a value here means refuse; returning null means proceed. There is no legacy-upgrade
    /// arm: Arcanum has no installed base, so the only databases this can meet are ones some build
    /// of Arcanum itself wrote, and one that disagrees with this binary is repaired deliberately
    /// rather than migrated in place.
    /// </remarks>
    private static async Task<GrimoireSchemaTierHealth?> ClassifyExistingAsync(
        SqliteConnection connection,
        GrimoireSchemaManifest manifest,
        CancellationToken cancellationToken)
    {

        if (!await ObjectExistsAsync(connection, "grimoire_feature_schemas", cancellationToken)
            .ConfigureAwait(false))
        {

            return await AnyManifestObjectExistsAsync(connection, manifest, cancellationToken)
                .ConfigureAwait(false)
                ? GrimoireSchemaTierHealth.MetadataMissing
                : null;

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT SchemaVersion, SourceDefinitionFingerprint
            FROM grimoire_feature_schemas
            WHERE FamilyCode = $familyCode AND TransactionTierCode = $tierCode;
            """;

        _ = command.Parameters.AddWithValue("$familyCode", (long)manifest.Family);

        _ = command.Parameters.AddWithValue("$tierCode", (long)manifest.TransactionTier);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return await AnyManifestObjectExistsAsync(connection, manifest, cancellationToken)
                .ConfigureAwait(false)
                ? GrimoireSchemaTierHealth.MetadataMissing
                : null;

        }

        long installedVersion = reader.GetInt64(0);

        string installedSource = reader.GetString(1);

        if (installedVersion > manifest.Version)
        {

            return GrimoireSchemaTierHealth.IncompatibleNewerVersion;

        }

        return installedVersion == manifest.Version
            && !string.Equals(installedSource, manifest.SourceDefinitionFingerprint, StringComparison.Ordinal)
                ? GrimoireSchemaTierHealth.SourceDefinitionMismatch
                : null;

    }

    private static async Task WriteMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaManifest manifest,
        string installedFingerprint,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO grimoire_feature_schemas (
                FamilyCode, TransactionTierCode, SchemaVersion, SourceDefinitionFingerprint,
                InstalledCatalogFingerprint, InstalledAtUtc, HealthCode, HealthDetailCode)
            VALUES ($familyCode, $tierCode, $version, $source, $installed, $installedAt, 0, NULL)
            ON CONFLICT (FamilyCode, TransactionTierCode) DO UPDATE SET
                SchemaVersion = excluded.SchemaVersion,
                SourceDefinitionFingerprint = excluded.SourceDefinitionFingerprint,
                InstalledCatalogFingerprint = excluded.InstalledCatalogFingerprint,
                InstalledAtUtc = excluded.InstalledAtUtc,
                HealthCode = excluded.HealthCode,
                HealthDetailCode = NULL;
            """;

        _ = command.Parameters.AddWithValue("$familyCode", (long)manifest.Family);

        _ = command.Parameters.AddWithValue("$tierCode", (long)manifest.TransactionTier);

        _ = command.Parameters.AddWithValue("$version", manifest.Version);

        _ = command.Parameters.AddWithValue("$source", manifest.SourceDefinitionFingerprint);

        _ = command.Parameters.AddWithValue("$installed", installedFingerprint);

        _ = command.Parameters.AddWithValue(
            "$installedAt",
            context.InstalledAtUtc.ToString("o", CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static IReadOnlyList<GrimoireSchemaObject> ObjectsFor(GrimoireSchemaTransactionTier tier) =>
        tier switch
        {

            GrimoireSchemaTransactionTier.Core => GrimoireSchemaCatalog.CoreObjects,

            GrimoireSchemaTransactionTier.CovenantCanonical => GrimoireSchemaCatalog.CovenantCanonicalObjects,

            GrimoireSchemaTransactionTier.CovenantAccelerator => GrimoireSchemaCatalog.CovenantAcceleratorObjects,

            _ => throw new ArgumentOutOfRangeException(nameof(tier)),

        };

    private static GrimoireSchemaTierInstallResult Failed(
        GrimoireSchemaManifest manifest,
        GrimoireSchemaTierHealth health,
        string? diagnosticCode = null) =>
        new(
            manifest.TransactionTier,
            manifest.Version,
            health,
            manifest.SourceDefinitionFingerprint,
            InstalledCatalogFingerprint: null,
            diagnosticCode ?? $"Grimoire.Schema.{health}");

    private static async Task<bool> ObjectExistsAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """SELECT 1 FROM sqlite_master WHERE "name" = $name LIMIT 1;""";

        _ = command.Parameters.AddWithValue("$name", name);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is not null && result != DBNull.Value;

    }

    private static async Task<bool> AnyManifestObjectExistsAsync(
        SqliteConnection connection,
        GrimoireSchemaManifest manifest,
        CancellationToken cancellationToken)
    {

        foreach (GrimoireSchemaManifestEntry entry in manifest.Entries)
        {

            if (await ObjectExistsAsync(connection, entry.Name, cancellationToken).ConfigureAwait(false))
            {

                return true;

            }

        }

        return false;

    }

    /// <summary>
    /// Resynchronizes the Lexicon's FTS5 external-content index with <c>lexicon_entries</c>. A no-op
    /// on a fresh install and cheap insurance on a reopen. Best-effort: a failure narrows Lexicon
    /// search until the next rebuild, it does not break the database.
    /// </summary>
    private async Task TryRebuildLexiconFtsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        try
        {

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "INSERT INTO lexicon_fts(lexicon_fts) VALUES('rebuild');";

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger?.LogWarning(
                ex,
                "The Lexicon FTS rebuild after schema install failed; search may be incomplete until the next rebuild.");

        }

    }

    /// <summary>
    /// Detects a configured-dimension change against already-imprinted vectors and logs a warning for
    /// each affected BLOB source-of-truth table. Deliberately never truncates: the operator clears
    /// the affected scope and re-indexes.
    /// </summary>
    private async Task WarnOnDimensionMismatchAsync(
        SqliteConnection connection,
        int configuredDimensions,
        CancellationToken cancellationToken)
    {

        await WarnOnTableDimensionMismatchAsync(connection, "entry_embeddings", configuredDimensions, cancellationToken)
            .ConfigureAwait(false);

        await WarnOnTableDimensionMismatchAsync(connection, "saga_memory_embeddings", configuredDimensions, cancellationToken)
            .ConfigureAwait(false);

    }

    private async Task WarnOnTableDimensionMismatchAsync(
        SqliteConnection connection,
        string tableName,
        int configuredDimensions,
        CancellationToken cancellationToken)
    {

        try
        {

            await using SqliteCommand command = connection.CreateCommand();

            // tableName is one of the fixed internal constants passed above, never user input.
            command.CommandText = $"""SELECT "Dim" FROM "{tableName}" LIMIT 1;""";

            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (result is null or DBNull)
            {

                return;

            }

            long existingDimensions = Convert.ToInt64(result, CultureInfo.InvariantCulture);

            if (existingDimensions != configuredDimensions)
            {

                logger?.LogWarning(
                    "Embedding dimension changed from {OldDimensions} to {NewDimensions} in {TableName}. Existing embeddings are stale. Reset the affected embedding scope and re-index to use the new dimension.",
                    existingDimensions,
                    configuredDimensions,
                    tableName);

            }

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger?.LogWarning(
                ex,
                "The embedding dimension probe for {TableName} failed; skipping the dimension-mismatch check.",
                tableName);

        }

    }

}

/// <summary>
/// Thrown when the Core tier refuses to install against an existing database. Carries a closed
/// reason and no content, so it can be logged and surfaced without leaking schema detail.
/// </summary>
internal sealed class GrimoireSchemaRefusedException(
    GrimoireSchemaTransactionTier tier,
    GrimoireSchemaTierHealth health)
    : InvalidOperationException(
        $"The {tier} Grimoire schema tier was refused: {health}.")
{

    public GrimoireSchemaTransactionTier Tier { get; } = tier;

    public GrimoireSchemaTierHealth Health { get; } = health;

}
