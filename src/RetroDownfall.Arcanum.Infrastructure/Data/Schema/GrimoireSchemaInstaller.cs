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
    GrimoireSchemaVersionChainSet chains,
    TimeProvider timeProvider,
    ILogger<GrimoireSchemaInstaller>? logger = null)
{

    private readonly GrimoireSchemaManifestInspector _inspector =
        inspector ?? throw new ArgumentNullException(nameof(inspector));

    private readonly GrimoireSchemaDataInitializers _initializers =
        initializers ?? throw new ArgumentNullException(nameof(initializers));

    private readonly GrimoireSchemaVersionChainSet _chains =
        chains ?? throw new ArgumentNullException(nameof(chains));

    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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

        GrimoireSchemaManifest manifest = _chains.ForTier(tier).HeadManifest;

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
    /// The single tier algorithm: classify what is recorded, refuse anything this binary cannot
    /// honor, then either install the head shape or walk the declared version chain toward it.
    /// </summary>
    private async Task<GrimoireSchemaTierInstallResult> InstallTierAsync(
        SqliteConnection connection,
        GrimoireSchemaTransactionTier tier,
        int? embeddingDimensions,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        GrimoireSchemaVersionChain chain = _chains.ForTier(tier);

        GrimoireSchemaRecordedTier? recorded = await ReadRecordedTierAsync(connection, chain, cancellationToken)
            .ConfigureAwait(false);

        GrimoireSchemaTransitionJournalRow? journal = await ReadJournalAsync(connection, tier, cancellationToken)
            .ConfigureAwait(false);

        bool anyObjectPresent = recorded is null
            && await AnyManifestObjectExistsAsync(connection, chain.HeadManifest, cancellationToken)
                .ConfigureAwait(false);

        GrimoireSchemaEvolutionDecision decision =
            GrimoireSchemaEvolutionPlanner.Decide(chain, recorded, anyObjectPresent, journal);

        switch (decision.Action)
        {

            case GrimoireSchemaEvolutionAction.Refuse:

                return Refuse(chain, tier, decision.Refusal!.Value);

            case GrimoireSchemaEvolutionAction.FreshInstall:
            case GrimoireSchemaEvolutionAction.Converge:

                return await InstallHeadAsync(
                    connection,
                    chain,
                    embeddingDimensions,
                    context,
                    cancellationToken).ConfigureAwait(false);

            case GrimoireSchemaEvolutionAction.ResumeRun when decision.PendingBackfillName is not null:

                // That step's DDL is already committed and its sweep has not drained. Running the
                // statements again would throw on the first non-idempotent one, and on Core that
                // failure would abort startup and leave the sweep unrunnable by the only process
                // able to run it.
                return Incomplete(chain, recorded!.SchemaVersion);

            case GrimoireSchemaEvolutionAction.BeginRun:

                return await BeginRunAsync(
                    connection,
                    chain,
                    tier,
                    recorded!.SchemaVersion,
                    embeddingDimensions,
                    context,
                    cancellationToken).ConfigureAwait(false);

            case GrimoireSchemaEvolutionAction.ResumeRun:

                return await RunStepsAsync(
                    connection,
                    chain,
                    journal,
                    recorded!.SchemaVersion,
                    decision.ResumeFromVersion,
                    embeddingDimensions,
                    context,
                    cancellationToken).ConfigureAwait(false);

            default:

                throw new InvalidOperationException($"Unhandled schema evolution action {decision.Action}.");

        }

    }

    /// <summary>
    /// Installs or converges the head shape in one transaction, which is what a fresh database and an
    /// already-current one both need.
    /// </summary>
    private async Task<GrimoireSchemaTierInstallResult> InstallHeadAsync(
        SqliteConnection connection,
        GrimoireSchemaVersionChain chain,
        int? embeddingDimensions,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            foreach (GrimoireSchemaObject definition in chain.HeadObjects)
            {

                cancellationToken.ThrowIfCancellationRequested();

                await ExecuteAsync(
                    connection,
                    transaction,
                    GrimoireSchemaCatalog.Resolve(definition, embeddingDimensions),
                    cancellationToken).ConfigureAwait(false);

            }

            GrimoireSchemaTierInstallResult result = await FinalizeRunAsync(
                connection,
                transaction,
                chain,
                journal: null,
                context,
                cancellationToken).ConfigureAwait(false);

            if (result.IsHealthy)
            {

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            }
            else
            {

                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                if (chain.TransactionTier == GrimoireSchemaTransactionTier.Core)
                {

                    throw new GrimoireSchemaRefusedException(chain.TransactionTier, result.Health);

                }

            }

            return result;

        }
        catch
        {

            await TryRollbackAsync(transaction).ConfigureAwait(false);

            throw;

        }

    }

    /// <summary>
    /// Opens a version run, after proving the catalog has not already been advanced behind the
    /// metadata's back.
    /// </summary>
    /// <remarks>
    /// The probe is the whole of <see cref="GrimoireSchemaTierHealth.MixedCatalogVersions"/>. A
    /// database whose objects already validate at head while its metadata names an older version was
    /// changed by something other than this engine - a restore, or a hand edit - and nothing proves
    /// the sweeps those skipped versions depend on were ever run. Recording head there would be
    /// exactly the silent advance past uncommitted work this design exists to prevent.
    /// </remarks>
    private async Task<GrimoireSchemaTierInstallResult> BeginRunAsync(
        SqliteConnection connection,
        GrimoireSchemaVersionChain chain,
        GrimoireSchemaTransactionTier tier,
        int recordedVersion,
        int? embeddingDimensions,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        GrimoireSchemaInspectionResult probe = await _inspector
            .InspectAsync(connection, transaction: null, chain.HeadManifest, cancellationToken)
            .ConfigureAwait(false);

        return probe.IsValid
            ? Refuse(chain, tier, GrimoireSchemaTierHealth.MixedCatalogVersions)
            : await RunStepsAsync(
                connection,
                chain,
                journal: null,
                recordedVersion,
                recordedVersion,
                embeddingDimensions,
                context,
                cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Walks the chain from <paramref name="fromVersion"/> toward head, one step per transaction.
    /// </summary>
    /// <remarks>
    /// A step's statements, the journal write that records the step, and - for the last step - the
    /// metadata write all commit together, so a step either fully applies or leaves nothing behind.
    /// That is what makes a step's DDL free to be non-idempotent, and it is why nothing here has to
    /// reason about a half-applied step.
    ///
    /// <para>The journal row is written only when the run will <i>not</i> finish in this transaction.
    /// A run that completes in one transaction needs no record of progress between transactions, and
    /// a row saying the run is finished is a row the table's own CHECK forbids.</para>
    /// </remarks>
    private async Task<GrimoireSchemaTierInstallResult> RunStepsAsync(
        SqliteConnection connection,
        GrimoireSchemaVersionChain chain,
        GrimoireSchemaTransitionJournalRow? journal,
        int recordedVersion,
        int fromVersion,
        int? embeddingDimensions,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        GrimoireSchemaTransitionJournalRow? row = journal;

        int through = fromVersion;

        while (chain.TryGetStep(through, out GrimoireSchemaVersionStep step))
        {

            cancellationToken.ThrowIfCancellationRequested();

            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {

                foreach (GrimoireSchemaTransitionStatement statement in step.Statements.OrderBy(
                    static candidate => candidate.Ordinal))
                {

                    await ExecuteAsync(
                        connection,
                        transaction,
                        ResolveStatement(statement, embeddingDimensions),
                        cancellationToken).ConfigureAwait(false);

                }

                if (step.Backfill is not null)
                {

                    row = await OpenOrAdvanceAsync(
                        connection,
                        transaction,
                        chain,
                        row,
                        recordedVersion,
                        through,
                        step.Backfill.Name,
                        cancellationToken).ConfigureAwait(false);

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return Incomplete(chain, recordedVersion);

                }

                if (step.ToVersion == chain.HeadVersion)
                {

                    GrimoireSchemaTierInstallResult result = await FinalizeRunAsync(
                        connection,
                        transaction,
                        chain,
                        row,
                        context,
                        cancellationToken).ConfigureAwait(false);

                    if (result.IsHealthy)
                    {

                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    }
                    else
                    {

                        // The journal row is left exactly as it was, so the run is retried rather
                        // than half-recorded.
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                    }

                    return result;

                }

                row = await OpenOrAdvanceAsync(
                    connection,
                    transaction,
                    chain,
                    row,
                    recordedVersion,
                    step.ToVersion,
                    backfillName: null,
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                through = step.ToVersion;

            }
            catch
            {

                await TryRollbackAsync(transaction).ConfigureAwait(false);

                throw;

            }

        }

        // Reached only when the chain has no step leaving this version, which the planner already
        // refuses. Fail closed rather than reporting a health nothing established.
        return Incomplete(chain, recordedVersion);

    }

    /// <summary>
    /// Ends a run inside the caller's transaction: seed, validate against the head manifest, record
    /// the head version, and close the journal.
    /// </summary>
    /// <remarks>
    /// Shared by the head install, by the last backfill-free step, and by the backfill runner's final
    /// batch, on the division the Covenant maintenance sweeps already keep: the driver owns the
    /// transaction, this owns what finishing means. Two copies would be two ideas of when a version is
    /// installed, and the journal deliberately has no completion flag for them to disagree through.
    ///
    /// <para>The caller commits on a healthy result and rolls back on any other, which is what leaves
    /// a failed validation retryable instead of half-recorded.</para>
    /// </remarks>
    internal async Task<GrimoireSchemaTierInstallResult> FinalizeRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaVersionChain chain,
        GrimoireSchemaTransitionJournalRow? journal,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(chain);

        ArgumentNullException.ThrowIfNull(context);

        await _initializers.For(chain.TransactionTier)
            .InitializeAsync(connection, transaction, context, cancellationToken)
            .ConfigureAwait(false);

        GrimoireSchemaInspectionResult inspection = await _inspector
            .InspectAsync(connection, transaction, chain.HeadManifest, cancellationToken)
            .ConfigureAwait(false);

        if (!inspection.IsValid)
        {

            return Failed(
                chain.HeadManifest,
                inspection.Failure == GrimoireSchemaInspectionFailure.CatalogReadFailed
                    ? GrimoireSchemaTierHealth.Unavailable
                    : GrimoireSchemaTierHealth.InstalledCatalogDrift,
                inspection.DiagnosticCode);

        }

        await WriteMetadataAsync(
            connection,
            transaction,
            chain.HeadManifest,
            chain.HeadVersion,
            inspection.InstalledCatalogFingerprint!,
            context,
            cancellationToken).ConfigureAwait(false);

        if (journal is not null
            && !await GrimoireSchemaTransitionJournal
                .DeleteAsync(connection, transaction, journal, cancellationToken)
                .ConfigureAwait(false))
        {

            throw new InvalidOperationException(
                $"The {chain.TransactionTier} schema transition journal moved while this run was finishing.");

        }

        return new GrimoireSchemaTierInstallResult(
            chain.TransactionTier,
            chain.HeadVersion,
            GrimoireSchemaTierHealth.Healthy,
            chain.HeadManifest.SourceDefinitionFingerprint,
            inspection.InstalledCatalogFingerprint,
            DiagnosticCode: null);

    }

    /// <summary>
    /// Writes the journal row on the first step of a run, or advances it on every step after.
    /// </summary>
    private async Task<GrimoireSchemaTransitionJournalRow> OpenOrAdvanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaVersionChain chain,
        GrimoireSchemaTransitionJournalRow? row,
        int recordedVersion,
        int completedThroughVersion,
        string? backfillName,
        CancellationToken cancellationToken)
    {

        DateTimeOffset now = _time.GetUtcNow();

        if (row is null)
        {

            GrimoireSchemaTransitionJournalRow opened = new(
                chain.Family,
                chain.TransactionTier,
                recordedVersion,
                chain.HeadVersion,
                completedThroughVersion,
                chain.HeadManifest.SourceDefinitionFingerprint,
                backfillName,
                BackfillCursor: null,
                BackfillRowsProcessed: 0,
                Revision: 0);

            await GrimoireSchemaTransitionJournal
                .InsertAsync(connection, transaction, opened, now, cancellationToken)
                .ConfigureAwait(false);

            return opened;

        }

        if (!await GrimoireSchemaTransitionJournal.AdvanceAsync(
                connection,
                transaction,
                row,
                completedThroughVersion,
                backfillName,
                backfillCursor: null,
                row.BackfillRowsProcessed,
                now,
                cancellationToken).ConfigureAwait(false))
        {

            throw new InvalidOperationException(
                $"The {chain.TransactionTier} schema transition journal moved while this run was advancing.");

        }

        return row with
        {

            CompletedThroughVersion = completedThroughVersion,

            BackfillName = backfillName,

            BackfillCursor = null,

            Revision = row.Revision + 1,

        };

    }

    /// <summary>
    /// Reads the two metadata fields that decide what may happen next, or null when nothing is
    /// recorded.
    /// </summary>
    private static async Task<GrimoireSchemaRecordedTier?> ReadRecordedTierAsync(
        SqliteConnection connection,
        GrimoireSchemaVersionChain chain,
        CancellationToken cancellationToken)
    {

        if (!await ObjectExistsAsync(connection, "grimoire_feature_schemas", cancellationToken)
            .ConfigureAwait(false))
        {

            return null;

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT SchemaVersion, SourceDefinitionFingerprint
            FROM grimoire_feature_schemas
            WHERE FamilyCode = $familyCode AND TransactionTierCode = $tierCode;
            """;

        _ = command.Parameters.AddWithValue("$familyCode", (long)chain.Family);

        _ = command.Parameters.AddWithValue("$tierCode", (long)chain.TransactionTier);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new GrimoireSchemaRecordedTier(reader.GetInt32(0), reader.GetString(1))
            : null;

    }

    /// <summary>
    /// Reads this tier's in-flight run, tolerating a database old enough not to have the journal at
    /// all - which is every database before the first install of this build.
    /// </summary>
    private static async Task<GrimoireSchemaTransitionJournalRow?> ReadJournalAsync(
        SqliteConnection connection,
        GrimoireSchemaTransactionTier tier,
        CancellationToken cancellationToken) =>
        await ObjectExistsAsync(connection, "grimoire_schema_transitions", cancellationToken)
            .ConfigureAwait(false)
            ? await GrimoireSchemaTransitionJournal
                .ReadAsync(connection, transaction: null, tier, cancellationToken)
                .ConfigureAwait(false)
            : null;

    /// <summary>
    /// Substitutes install-time template values into a step statement and refuses to return one that
    /// still carries an unresolved placeholder, exactly as a head object is resolved.
    /// </summary>
    private static string ResolveStatement(GrimoireSchemaTransitionStatement statement, int? embeddingDimensions)
    {

        string resolved = embeddingDimensions is int width
            ? statement.Sql.Replace(
                GrimoireSchemaCatalog.EmbeddingDimensionsToken,
                width.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            : statement.Sql;

        return resolved.Contains("{{", StringComparison.Ordinal)
            ? throw new InvalidOperationException(
                $"Grimoire transition statement '{statement.ResourcePath}.sql' contains an unresolved template placeholder.")
            : resolved;

    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task TryRollbackAsync(SqliteTransaction transaction)
    {

        try
        {

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

        }
        catch (Exception)
        {

            // Best-effort: disposal rolls an uncommitted transaction back anyway, and keeping the
            // original failure is worth more than reporting the rollback's.

        }

    }

    /// <summary>
    /// Turns a refusal into a result, throwing for the one tier whose refusal aborts startup.
    /// </summary>
    /// <remarks>
    /// <see cref="GrimoireSchemaTierHealth.TransitionIncomplete"/> never arrives here, and that is
    /// the point: an unfinished run is a state to resume, not a refusal, so Core reports it and keeps
    /// running.
    /// </remarks>
    private static GrimoireSchemaTierInstallResult Refuse(
        GrimoireSchemaVersionChain chain,
        GrimoireSchemaTransactionTier tier,
        GrimoireSchemaTierHealth health) =>
        tier == GrimoireSchemaTransactionTier.Core
            ? throw new GrimoireSchemaRefusedException(tier, health)
            : Failed(chain.HeadManifest, health);

    /// <summary>
    /// A tier whose DDL has reached a version its recorded metadata deliberately does not yet claim.
    /// </summary>
    /// <remarks>
    /// The version reported is the recorded one rather than head, because that is the version whose
    /// promises have actually been kept.
    /// </remarks>
    private static GrimoireSchemaTierInstallResult Incomplete(
        GrimoireSchemaVersionChain chain,
        int recordedVersion) =>
        new(
            chain.TransactionTier,
            recordedVersion,
            GrimoireSchemaTierHealth.TransitionIncomplete,
            chain.HeadManifest.SourceDefinitionFingerprint,
            InstalledCatalogFingerprint: null,
            $"Grimoire.Schema.{GrimoireSchemaTierHealth.TransitionIncomplete}");

    private static async Task WriteMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaManifest manifest,
        int schemaVersion,
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

        _ = command.Parameters.AddWithValue("$version", schemaVersion);

        _ = command.Parameters.AddWithValue("$source", manifest.SourceDefinitionFingerprint);

        _ = command.Parameters.AddWithValue("$installed", installedFingerprint);

        _ = command.Parameters.AddWithValue(
            "$installedAt",
            context.InstalledAtUtc.ToString("o", CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

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
    /// Resynchronizes the Lexicon's FTS5 external-content index with <c>lexicon_entries</c>, but only
    /// while doing so is free. Best-effort: a failure narrows Lexicon search until the next rebuild,
    /// it does not break the database.
    /// </summary>
    /// <remarks>
    /// FTS5's <c>rebuild</c> has no incremental mode - it drops the whole index and re-tokenizes every
    /// content row - so running it unconditionally would re-index the entire corpus on every start,
    /// before readiness opens and on every CLI verb that bootstraps the Grimoire, growing with a
    /// Lexicon that only ever accumulates. That is exactly what this design already refuses to do
    /// inline for the Covenant accelerator index.
    ///
    /// <para>Nothing is lost by declining. <c>lexicon_entries_ai</c>/<c>_au</c>/<c>_ad</c> maintain the
    /// index through the external-content <c>'delete'</c> idiom, so ordinary writes keep it exactly in
    /// step. The one desync that does occur - a factory reset emptying <c>lexicon_fts</c> while its
    /// content rows are still present, then deleting those rows and leaving delete markers behind -
    /// ends with an empty content table, which is precisely the case still repaired here.</para>
    /// </remarks>
    private async Task TryRebuildLexiconFtsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        try
        {

            if (!await LexiconCorpusIsEmptyAsync(connection, cancellationToken).ConfigureAwait(false))
            {

                return;

            }

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
    /// Whether the Lexicon holds no entities, answered by an existence probe rather than a count so
    /// the guard costs one index seek on a corpus the rebuild it guards would have read in full.
    /// </summary>
    private static async Task<bool> LexiconCorpusIsEmptyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT 1 FROM lexicon_entries LIMIT 1;";

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null || result == DBNull.Value;

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
        $"The {tier} Grimoire schema tier was refused: {health}. "
        + "This build carries an existing database forward only through a version step it declares; a "
        + "database that disagrees with it in any other way is repaired deliberately rather than "
        + "guessed at, because nothing records what its shape was or what would have to be rewritten. "
        + "Restore a .arcbackup generation taken by this build with 'arcanum backup restore', or start "
        + "fresh by moving arcanum.db and arcanum.db.kdf aside under ~/.config/arcanum/ — session data "
        + "in the old file is not readable by this build either way.")
{

    public GrimoireSchemaTransactionTier Tier { get; } = tier;

    public GrimoireSchemaTierHealth Health { get; } = health;

}
