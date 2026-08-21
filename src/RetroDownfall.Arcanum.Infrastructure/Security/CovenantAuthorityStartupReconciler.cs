using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Publishes the committed authority row and takes the envelope master material, once, at startup.
/// </summary>
/// <remarks>
/// Runs after the three install transactions commit and before readiness opens. That ordering is the
/// whole contract: publishing earlier would let a rolled-back transaction leave the process asserting
/// an authority generation the database never recorded, and taking key material later would put a
/// credential read on a request path (§10.12).
///
/// <para>The dataset-keyed envelope purposes are established only when the canonical tier is healthy.
/// An unhealthy tier leaves those three families unkeyed, so every cursor, preflight, and Ward token
/// fails closed while the recovery families — which are keyed by installation identity — keep working.
/// That is exactly the asymmetry an operator needs in order to repair the tier at all.</para>
///
/// <para>Failure here never aborts startup. Authority stays unpublished, which every consumer already
/// reads as "no operator authority", and the rest of Arcanum keeps working.</para>
///
/// <para>The host-tools runtime policy is consulted first and is not advisory. A process the startup
/// gate told is tainted, blocked, or simply unclassified must never derive an envelope key or publish
/// an authority snapshot, because both are the material that makes Covenant readable — and on a
/// tainted installation arbitrary same-identity code can read whatever this process holds.</para>
/// </remarks>
internal static class CovenantAuthorityStartupReconciler
{

    internal static async Task ReconcileAsync(
        SqliteConnection installConnection,
        CovenantRuntimeGenerationProvider runtime,
        CovenantEnvelopeMasterKeyProvider keyProvider,
        CovenantAvailabilitySnapshot availability,
        IHostProcessToolsRuntimePolicy hostToolsPolicy,
        string masterApiKey,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(installConnection);

        ArgumentNullException.ThrowIfNull(runtime);

        ArgumentNullException.ThrowIfNull(keyProvider);

        ArgumentNullException.ThrowIfNull(availability);

        ArgumentNullException.ThrowIfNull(hostToolsPolicy);

        if (!hostToolsPolicy.CovenantPermitted)
        {

            Log.Warning(
                "Covenant authority stays unavailable: the host-process-tools startup gate has not permitted it ({Disposition}).",
                hostToolsPolicy.Disposition?.ToString() ?? "Unclassified");

            return;

        }

        CovenantRuntimeGenerationState expected = runtime.Current;

        try
        {

            CovenantAuthoritySnapshot? snapshot = await ReadAuthorityAsync(
                installConnection,
                cancellationToken).ConfigureAwait(false);

            if (snapshot is null)
            {

                Log.Warning(
                    "Covenant authority state is absent after schema installation; operator authority stays unavailable.");

                return;

            }

            CovenantEnvelopeStateRow? envelope = availability.Canonical is CovenantCapabilityState.Healthy
                ? await CovenantEnvelopeStateStore.ReadAsync(installConnection, transaction: null, cancellationToken)
                    .ConfigureAwait(false)
                : null;

            // A canonical row whose recorded master version disagrees with the core row means the two
            // tiers converged against different keys. Refusing the dataset-keyed families is the only
            // safe answer: deriving from either version would authenticate tokens the other rejects.
            if (envelope is not null && envelope.MasterKeyVersion != snapshot.MasterKeyVersion)
            {

                Log.Warning(
                    "Covenant canonical envelope state records a different master key version than core authority; dataset-keyed envelopes stay unavailable.");

                envelope = null;

            }

            byte[] material = Encoding.UTF8.GetBytes(masterApiKey);

            Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keyProvider.PrepareInitial(
                material,
                new CovenantEnvelopeBootstrapKeyInput(
                    snapshot.InstallationIdentity,
                    snapshot.MasterKeyVersion,
                    envelope?.EnvelopeKeyEpoch ?? 1,
                    snapshot.RecoveryEnvelopeEpoch,
                    envelope?.DatasetGeneration));

            if (!prepared.IsSuccess)
            {

                Log.Warning(
                    "Covenant envelope key derivation failed with {ErrorCode}; opaque Covenant tokens stay unavailable.",
                    prepared.Error.Code);

                return;

            }

            using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

            Result initialized = runtime.Initialize(expected, owned, snapshot, availability);

            if (!initialized.IsSuccess)
            {

                Log.Warning(
                    "Covenant runtime authority initialization failed with {ErrorCode}; authority and opaque tokens stay unavailable.",
                    initialized.Error.Code);

            }

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            Log.Warning(ex, "Covenant authority reconciliation failed; operator authority stays unavailable.");

        }

    }

    private static async Task<CovenantAuthoritySnapshot?> ReadAuthorityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT InstallationIdentity,
                   AuthorityEpoch,
                   CurrentMasterKeyVersion,
                   RecoveryEnvelopeEpoch,
                   HostToolsStateCode,
                   TransitionId
            FROM covenant_authority_state
            WHERE StateKey = 1;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new CovenantAuthoritySnapshot(
            RuntimeAuthorityGeneration: 1,
            reader.GetString(0),
            reader.GetInt64(1),
            checked((uint)reader.GetInt64(2)),
            reader.GetInt64(3),
            (CovenantHostToolsState)reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));

    }

}
