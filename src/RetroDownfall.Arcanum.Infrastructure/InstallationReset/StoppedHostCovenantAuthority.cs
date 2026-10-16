using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>
/// Puts the Covenant runtime of a stopped-host process in place, or proves it is still the one a
/// previous tenure of the same process put there.
/// </summary>
/// <remarks>
/// A factory reset that measures the Covenant family has to take the gate's installation read, and the
/// gate grants that only against a published authority, a healthy canonical tier and a dataset
/// generation. A stopped-host command deliberately never bootstraps (it must not create or repair state
/// while it decides whether to reset), so none of those is published. This is the read-only way to get
/// them: the same primitives the host's pre-bootstrap recovery already runs before it bootstraps, over
/// the query-only lease the plan already holds, and nothing that writes.
///
/// <para>The first tenure of a process proves the host-tools classification into a fresh provisional
/// policy (the process-wide one stays unpublished, as it does for a recovery pass), proves the canonical
/// catalog inside a read transaction, publishes what that proof inspected, publishes the persisted
/// dataset position, reads the master key and prepares the envelope generation. It never adopts a
/// recovery owner and never publishes readiness, so the gate can lease reads and, later, the exclusive
/// erasure lease, and nothing else changes.</para>
///
/// <para>Every later tenure of the same process (the apply-time replan, then the nested erasure) never
/// initializes again. It re-reads the five facts the erasure's launch binds, installation identity,
/// authority epoch, master-key version, dataset generation and canonical sequence, and requires each to
/// equal what the runtime holds. Anything else is a catalog that moved between the plan the operator
/// confirmed and the tenure that would act on it, and it is refused before any effect.</para>
///
/// <para>Each refusal is typed by its cause, so the operator is told one thing: an outage of the
/// marker, the catalog or the credential store is <c>Covenant.Unavailable</c>; a host-tools state that
/// is not a settled clean pair is <c>Covenant.HostToolsTransitionRequired</c>; a catalog that is not
/// intact is <c>Covenant.IntegrityFailure</c>; a missing master key is
/// <c>Covenant.OperatorAuthorityUnavailable</c>; and a missing state or authority row is
/// <c>Data.InventoryUnavailable</c>.</para>
/// </remarks>
internal sealed class StoppedHostCovenantAuthority(
    CovenantRuntimeGenerationProvider runtime,
    CovenantAvailability availability,
    CovenantEnvelopeMasterKeyProvider keys,
    IHostProcessToolsRecoveryStartupClassifier classifier,
    CovenantHealthyCatalogErasureGuard catalog,
    ISecretStore secrets)
{
    internal const string HostToolsRemedy =
        "Covenant stays closed for this installation until the host-process-tools state is a settled clean "
        + "pair. Clear the ARCANUM_ALLOW_HOST_PROCESS_TOOLS environment variable, or resolve the pending or "
        + "mismatched transition, and retry.";

    internal const string OutageRemedy =
        "Unlock or reconnect the operating-system credential store, or wait for the Grimoire database to be "
        + "free, and retry.";

    internal const string IntegrityRemedy =
        "The Covenant catalog is not intact at the version this build expects. Start the host once with "
        + "`arcanum serve`, stop it with `arcanum serve quit`, and retry; if the catalog is still not intact, "
        + "follow the \"Local Grimoire reinstall\" procedure in docs/Arcanum.Engineering.md.";

    internal const string MasterKeyRemedy =
        "The master API key could not be found in the operating-system credential store, so the Covenant "
        + "inventory cannot be measured. Restore the key (`arcanum key`) or start the host once to recover "
        + "it, and retry.";

    internal const string StateRemedy =
        "The Grimoire holds no Covenant state row, so the Covenant inventory cannot be measured. Start the "
        + "host once with `arcanum serve` so the schema converges, stop it with `arcanum serve quit`, and retry.";

    /// <summary>
    /// Samples the independent host-tools marker. Taken before the database lease opens, so the two
    /// halves of the classification are never read on opposite sides of an operating-system change.
    /// </summary>
    internal Result<HostProcessToolsRecoveryMarkerSnapshot> CaptureMarker()
    {
        Result<HostProcessToolsRecoveryMarkerSnapshot> captured = classifier.CaptureMarker();

        return captured.IsSuccess
            ? captured
            : Result<HostProcessToolsRecoveryMarkerSnapshot>.Failure(
                captured.Error.Code == ErrorCodes.Covenant.Unavailable
                    ? new Error(
                        ErrorCodes.Covenant.Unavailable,
                        "The host-process-tools marker could not be read right now. " + OutageRemedy)
                    : new Error(
                        ErrorCodes.Covenant.HostToolsTransitionRequired,
                        "The host-process-tools marker is malformed. " + HostToolsRemedy));
    }

    /// <summary>
    /// Establishes the runtime on the first tenure of this process and verifies it on every later one.
    /// </summary>
    /// <param name="leaseConnection">The open query-only connection the plan holds.</param>
    /// <param name="marker">The marker snapshot taken before the lease opened.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<Result> EnsureAsync(
        SqliteConnection leaseConnection,
        HostProcessToolsRecoveryMarkerSnapshot marker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leaseConnection);

        ArgumentNullException.ThrowIfNull(marker);

        try
        {
            Result<StoppedHostCovenantFacts> read = await ReadFactsAsync(leaseConnection, cancellationToken)
                .ConfigureAwait(false);

            if (read.IsFailure)
            {
                return Result.Failure(read.Error);
            }

            return runtime.Current.AuthoritySlot is null
                ? await EstablishAsync(leaseConnection, marker, read.Value, cancellationToken)
                    .ConfigureAwait(false)
                : Verify(read.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            return Result.Failure(new Error(
                ErrorCodes.Covenant.Unavailable,
                "The Covenant state could not be read right now. " + OutageRemedy));
        }
    }

    private async Task<Result> EstablishAsync(
        SqliteConnection connection,
        HostProcessToolsRecoveryMarkerSnapshot marker,
        StoppedHostCovenantFacts facts,
        CancellationToken cancellationToken)
    {
        // The host-tools classification first, into a policy of its own. Nothing here publishes the
        // process-wide one: a stopped-host reset is not a start, and a later start classifies afresh.
        Result<IHostProcessToolsRuntimePolicy> classified;

        classified = await classifier
            .ClassifyAsync(connection, facts.InstallationId, marker, cancellationToken)
            .ConfigureAwait(false);

        if (classified.IsFailure)
        {
            return Result.Failure(
                classified.Error.Code == ErrorCodes.Covenant.Unavailable
                    ? new Error(
                        ErrorCodes.Covenant.Unavailable,
                        "The host-process-tools state could not be read right now. " + OutageRemedy)
                    : new Error(
                        ErrorCodes.Covenant.HostToolsTransitionRequired,
                        "The host-process-tools state is not a settled clean pair. " + HostToolsRemedy));
        }

        IHostProcessToolsRuntimePolicy provisional = classified.Value;

        if (!provisional.IsPublished || !provisional.CovenantPermitted)
        {
            return Result.Failure(new Error(
                ErrorCodes.Covenant.HostToolsTransitionRequired,
                "The host-process-tools state does not permit Covenant. " + HostToolsRemedy));
        }

        // The canonical catalog, proved inside one read transaction on the lease, and the facts it
        // inspected carried out so the capability published below is the one the proof saw.
        Result<CovenantInspectedCatalog> inspected;

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            inspected = await catalog
                .InspectHealthyWithinAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        if (inspected.IsFailure)
        {
            return Result.Failure(new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The Covenant catalog could not be proved intact. " + IntegrityRemedy));
        }

        _ = availability.PublishInspectedSchema(inspected.Value, CovenantHealthTransition.Bootstrap);

        if (!await CovenantPersistedAvailabilityPublisher.PublishAsync(
                availability,
                connection,
                acceleratorHealthy: false,
                CovenantHealthTransition.Bootstrap,
                cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(new Error(
                ErrorCodes.Data.InventoryUnavailable,
                "The Grimoire holds no Covenant state row. " + StateRemedy));
        }

        string? masterApiKey;

        try
        {
            masterApiKey = await secrets.GetApiKeyAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result.Failure(new Error(
                ErrorCodes.Covenant.Unavailable,
                "The master API key could not be read from the operating-system credential store. "
                + OutageRemedy));
        }

        if (string.IsNullOrWhiteSpace(masterApiKey))
        {
            return Result.Failure(new Error(
                ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                MasterKeyRemedy));
        }

        if (!await CovenantAuthorityStartupReconciler.ReconcileAsync(
                connection,
                runtime,
                keys,
                availability.Current,
                provisional,
                masterApiKey,
                cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(new Error(
                ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                "The Covenant operator authority could not be established from the Grimoire's authority "
                + "row and the master API key. " + MasterKeyRemedy));
        }

        return Result.Success();
    }

    /// <summary>
    /// Requires what this process published to be exactly what the catalog still says.
    /// </summary>
    private Result Verify(StoppedHostCovenantFacts facts)
    {
        CovenantRuntimeGenerationState state = runtime.Current;

        CovenantAuthoritySnapshot? authority = state.ActiveAuthority;

        if (authority is null)
        {
            return Result.Failure(new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "The Covenant runtime of this process is not available for a further tenure."));
        }

        CovenantAvailabilitySnapshot published = state.Availability;

        if (!string.Equals(
                authority.InstallationIdentity,
                facts.InstallationIdentityText,
                StringComparison.Ordinal)
            || authority.AuthorityEpoch != facts.AuthorityEpoch
            || authority.MasterKeyVersion != facts.MasterKeyVersion
            || published.DatasetGeneration != facts.DatasetGeneration
            || published.CanonicalSequence != facts.CanonicalSequence)
        {
            return Result.Failure(new Error(
                ErrorCodes.Data.PlanChanged,
                "The Covenant catalog changed after the plan was measured; request a new dry-run before applying."));
        }

        return Result.Success();
    }

    private static async Task<Result<StoppedHostCovenantFacts>> ReadFactsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        Result<Guid> identity = await InstallationResetDatabaseIdentityReader
            .ReadOpenConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        if (identity.IsFailure)
        {
            return Result<StoppedHostCovenantFacts>.Failure(new Error(
                ErrorCodes.Data.InventoryUnavailable,
                "The Grimoire holds no authenticated installation identity. " + StateRemedy));
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT a.InstallationIdentity,
                   a.AuthorityEpoch,
                   a.CurrentMasterKeyVersion,
                   s.DatasetGeneration,
                   s.CanonicalSearchSequence
            FROM covenant_authority_state a
            JOIN covenant_state s ON s.StateKey = 1
            WHERE a.StateKey = 1;
            """;

        try
        {
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.IsDBNull(3))
            {
                return Result<StoppedHostCovenantFacts>.Failure(new Error(
                    ErrorCodes.Data.InventoryUnavailable,
                    "The Grimoire holds no Covenant state row. " + StateRemedy));
            }

            byte[] generation = new byte[16];

            if (reader.GetBytes(3, 0, generation, 0, generation.Length) != generation.Length)
            {
                return Result<StoppedHostCovenantFacts>.Failure(new Error(
                    ErrorCodes.Data.InventoryUnavailable,
                    "The Grimoire's Covenant dataset generation is not readable. " + StateRemedy));
            }

            return Result<StoppedHostCovenantFacts>.Success(
                new StoppedHostCovenantFacts(
                    identity.Value,
                    reader.GetString(0),
                    reader.GetInt64(1),
                    checked((uint)reader.GetInt64(2)),
                    new Guid(generation),
                    reader.GetInt64(4)));
        }
        catch (SqliteException)
        {
            // The state tables do not exist: the canonical tier was never installed at this version.
            return Result<StoppedHostCovenantFacts>.Failure(new Error(
                ErrorCodes.Data.InventoryUnavailable,
                "The Grimoire holds no Covenant state tables. " + StateRemedy));
        }
    }

    private sealed record StoppedHostCovenantFacts(
        Guid InstallationId,
        string InstallationIdentityText,
        long AuthorityEpoch,
        uint MasterKeyVersion,
        Guid DatasetGeneration,
        long CanonicalSequence);
}
