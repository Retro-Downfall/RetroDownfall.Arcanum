using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Proves the exact shipped Covenant catalog and its healthy metadata before a factory erasure can
/// be admitted.
/// </summary>
/// <remarks>
/// The owning overload reads the live WAL-visible database through a distinct enrolled connection.
/// The borrowed overload is for an already-exclusive caller and stays inside that caller's exact
/// connection and transaction. Both reach the same proof and collapse every untrusted failure to
/// one content-free operator remedy.
/// </remarks>
/// <summary>
/// The manifest facts the healthy-catalog proof inspected: the version and installed-catalog
/// fingerprint of the canonical tier, and of the accelerator tier when one is installed.
/// </summary>
/// <remarks>
/// Content-free by construction: a schema version and a fingerprint of the catalog's shape, which are
/// exactly what <see cref="CovenantAvailability"/> already publishes for a tier that installed
/// healthily. They are carried out of the proof so that a stopped-host caller can publish what the
/// proof saw instead of re-deriving it from a second read.
/// </remarks>
internal sealed record CovenantInspectedCatalog(
    int CanonicalSchemaVersion,
    string CanonicalInstalledFingerprint,
    int? AcceleratorSchemaVersion,
    string? AcceleratorInstalledFingerprint);

internal sealed class CovenantHealthyCatalogErasureGuard(
    IGrimoireOrdinaryConnectionFactory connections,
    GrimoireSchemaManifestInspector inspector)
{
    private static readonly Error UnsafeCatalog = new(
        ErrorCodes.Covenant.IntegrityFailure,
        "Healthy-catalog Covenant erasure requires an intact catalog. Restore a known-good backup, "
            + "run Covenant-family reinitialize, or perform a full installation reset.");

    private readonly IGrimoireOrdinaryConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private readonly GrimoireSchemaManifestInspector _inspector =
        inspector ?? throw new ArgumentNullException(nameof(inspector));

    /// <summary>
    /// Owns one live-catalog snapshot from open through proof, then releases every resource before
    /// returning.
    /// </summary>
    internal async Task<Result> RequireHealthyAsync(CancellationToken cancellationToken)
    {
        IGrimoireOrdinaryConnectionLease? lease = null;

        SqliteTransaction? transaction = null;

        Result result = Result.Failure(UnsafeCatalog);

        try
        {
            Result<IGrimoireOrdinaryConnectionLease> acquired = await _connections
                .OpenFreshAsync(
                    GrimoireOrdinaryFreshConnectionKind.ReadOnly,
                    cancellationToken)
                .ConfigureAwait(false);

            if (acquired.IsFailure)
            {
                return Result.Failure(UnsafeCatalog);
            }

            lease = acquired.Value;

            SqliteConnection connection = lease.Connection;

            transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            Result<CovenantInspectedCatalog> proved = await ProveAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);

            result = proved.IsSuccess ? Result.Success() : Result.Failure(proved.Error);

            await GrimoireScopedConsumerTestSeam.PauseAsync(
                "CovenantHealthyCatalogErasureGuard.RequireHealthyAsync",
                GrimoireScopedConsumerFinalUseKind.ReaderMaterialized,
                result.IsSuccess ? 1 : 0,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cleanup below is deliberately not caller-cancellable. The original cancellation is
            // rethrown only after the direct handle has been released and unregistered.
        }
        catch
        {
            result = Result.Failure(UnsafeCatalog);
        }

        bool cleaned = await CleanupOwnedAsync(transaction, lease).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return cleaned ? result : Result.Failure(UnsafeCatalog);
    }

    /// <summary>
    /// Proves the catalog inside the caller's existing active snapshot without opening,
    /// initializing, committing, rolling back, closing, or enrolling anything.
    /// </summary>
    internal async Task<Result> RequireHealthyWithinAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        Result<CovenantInspectedCatalog> inspected = await InspectHealthyWithinAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);

        return inspected.IsSuccess ? Result.Success() : Result.Failure(inspected.Error);
    }

    /// <summary>
    /// The same proof as <see cref="RequireHealthyWithinAsync"/>, handing back the facts it inspected.
    /// </summary>
    /// <remarks>
    /// A caller that only needs the verdict discards the facts; a caller that publishes the tier's
    /// capability reads them from the same snapshot the verdict came from, so the version it publishes
    /// cannot disagree with the catalog that was proved.
    /// </remarks>
    internal async Task<Result<CovenantInspectedCatalog>> InspectHealthyWithinAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            if (connection is null
                || transaction is null
                || connection.State != ConnectionState.Open
                || !ReferenceEquals(transaction.Connection, connection))
            {
                return Result<CovenantInspectedCatalog>.Failure(UnsafeCatalog);
            }

            Result<CovenantInspectedCatalog> result = await ProveAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result<CovenantInspectedCatalog>.Failure(UnsafeCatalog);
        }
    }

    private async Task<Result<CovenantInspectedCatalog>> ProveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        GrimoireSchemaManifest canonical = GrimoireSchemaManifests.CovenantCanonical;

        GrimoireSchemaInspectionResult canonicalInspection = await _inspector
            .InspectAsync(connection, transaction, canonical, cancellationToken)
            .ConfigureAwait(false);

        if (!IsValidInspection(canonicalInspection)
            || !await HasExactHealthyMetadataAsync(
                connection,
                transaction,
                canonical,
                canonicalInspection.InstalledCatalogFingerprint!,
                cancellationToken).ConfigureAwait(false))
        {
            return Result<CovenantInspectedCatalog>.Failure(UnsafeCatalog);
        }

        GrimoireSchemaManifest accelerator = GrimoireSchemaManifests.CovenantAccelerator;

        int trustedAcceleratorObjects = await CountTrustedObjectsAsync(
            connection,
            transaction,
            accelerator,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TierMetadata> acceleratorMetadata = await ReadMetadataAsync(
            connection,
            transaction,
            accelerator,
            cancellationToken).ConfigureAwait(false);

        if (trustedAcceleratorObjects == 0 && acceleratorMetadata.Count == 0)
        {
            return Result<CovenantInspectedCatalog>.Success(
                new CovenantInspectedCatalog(
                    canonical.Version,
                    canonicalInspection.InstalledCatalogFingerprint!,
                    AcceleratorSchemaVersion: null,
                    AcceleratorInstalledFingerprint: null));
        }

        GrimoireSchemaInspectionResult acceleratorInspection = await _inspector
            .InspectAsync(connection, transaction, accelerator, cancellationToken)
            .ConfigureAwait(false);

        return IsValidInspection(acceleratorInspection)
            && IsExactHealthyMetadata(
                acceleratorMetadata,
                accelerator,
                acceleratorInspection.InstalledCatalogFingerprint!)
            ? Result<CovenantInspectedCatalog>.Success(
                new CovenantInspectedCatalog(
                    canonical.Version,
                    canonicalInspection.InstalledCatalogFingerprint!,
                    accelerator.Version,
                    acceleratorInspection.InstalledCatalogFingerprint!))
            : Result<CovenantInspectedCatalog>.Failure(UnsafeCatalog);
    }

    private static bool IsValidInspection(GrimoireSchemaInspectionResult inspection) =>
        inspection.IsValid
        && !string.IsNullOrEmpty(inspection.InstalledCatalogFingerprint);

    private static async Task<bool> HasExactHealthyMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaManifest manifest,
        string installedFingerprint,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TierMetadata> metadata = await ReadMetadataAsync(
            connection,
            transaction,
            manifest,
            cancellationToken).ConfigureAwait(false);

        return IsExactHealthyMetadata(metadata, manifest, installedFingerprint);
    }

    private static bool IsExactHealthyMetadata(
        IReadOnlyList<TierMetadata> metadata,
        GrimoireSchemaManifest manifest,
        string installedFingerprint) =>
        metadata.Count == 1
        && metadata[0].SchemaVersion == manifest.Version
        && string.Equals(
            metadata[0].SourceDefinitionFingerprint,
            manifest.SourceDefinitionFingerprint,
            StringComparison.Ordinal)
        && string.Equals(
            metadata[0].InstalledCatalogFingerprint,
            installedFingerprint,
            StringComparison.Ordinal)
        && metadata[0].HealthCode == 0
        && string.IsNullOrEmpty(metadata[0].HealthDetailCode);

    private static async Task<IReadOnlyList<TierMetadata>> ReadMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaManifest manifest,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT SchemaVersion, SourceDefinitionFingerprint, InstalledCatalogFingerprint,
                   HealthCode, HealthDetailCode
            FROM grimoire_feature_schemas
            WHERE FamilyCode = $family
              AND TransactionTierCode = $tier
              AND typeof(FamilyCode) = 'integer'
              AND typeof(TransactionTierCode) = 'integer'
              AND typeof(SchemaVersion) = 'integer'
              AND typeof(SourceDefinitionFingerprint) = 'text'
              AND typeof(InstalledCatalogFingerprint) = 'text'
              AND typeof(HealthCode) = 'integer'
              AND typeof(HealthDetailCode) IN ('null', 'text')
            LIMIT 2;
            """;

        _ = command.Parameters.AddWithValue("$family", (long)manifest.Family);

        _ = command.Parameters.AddWithValue("$tier", (long)manifest.TransactionTier);

        List<TierMetadata> rows = [];

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(
                new TierMetadata(
                    checked((int)reader.GetInt64(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    checked((int)reader.GetInt64(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    private static async Task<int> CountTrustedObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaManifest manifest,
        CancellationToken cancellationToken)
    {
        HashSet<string> trustedNames = new(StringComparer.Ordinal);

        foreach (GrimoireSchemaManifestEntry entry in manifest.Entries)
        {
            _ = trustedNames.Add(entry.Name);

            foreach (GrimoireExpectedIndex index in entry.Indexes)
            {
                _ = trustedNames.Add(index.Name);
            }
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE name NOT LIKE 'sqlite\_%' ESCAPE '\';
            """;

        int count = 0;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (trustedNames.Contains(reader.GetString(0)))
            {
                count++;
            }
        }

        return count;
    }

    private static async Task<bool> CleanupOwnedAsync(
        SqliteTransaction? transaction,
        IGrimoireOrdinaryConnectionLease? lease)
    {
        bool clean = true;

        if (transaction is not null)
        {
            try
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                clean = false;
            }
        }

        if (lease is not null)
        {
            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                clean = false;
            }
        }

        return clean;
    }

    private sealed record TierMetadata(
        int SchemaVersion,
        string SourceDefinitionFingerprint,
        string InstalledCatalogFingerprint,
        int HealthCode,
        string? HealthDetailCode);
}
