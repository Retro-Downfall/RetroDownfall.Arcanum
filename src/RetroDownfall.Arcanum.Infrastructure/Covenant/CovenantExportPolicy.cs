using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The one implementation of the plaintext-export policy.
/// </summary>
/// <remarks>
/// Two indexed <c>COUNT(*)</c> reads and one projection lookup, and no content read of any kind. That
/// is the whole point of doing this before the export graph: a refusal that had to open the artifacts
/// to describe them would already have pulled Covenant-derived bytes into the process it is refusing
/// to send them out of (§10.19.11).
///
/// <para>Coverage is validated before SQL rather than after. An under-scoped lease is refused rather
/// than supplemented with a second acquisition, because a nested acquisition would take its own
/// snapshot — and the export would then be answering from two.</para>
/// </remarks>
internal sealed class CovenantExportPolicy(
    ICovenantAvailability availability,
    ICovenantOperationGate gate,
    ICovenantConnectionSource connections) : ICovenantExportPolicy
{

    public async ValueTask<Result<CovenantExportAdmission>> AcquireConditionalReadAsync(
        CovenantOperationScope? scope,
        CancellationToken cancellationToken)
    {

        // Read once. Two reads of the availability snapshot could straddle a disable, and the arm a
        // response is written under has to be the arm its first decision was made under.
        if (!availability.Current.FeatureEnabled)
        {

            return Result<CovenantExportAdmission>.Success(CovenantExportAdmission.Absent);

        }

        if (scope is not { } exact)
        {

            Result<CovenantInstallationReadLease> installation = await gate
                .AcquireInstallationReadAsync(cancellationToken)
                .ConfigureAwait(false);

            return installation.IsFailure
                ? Result<CovenantExportAdmission>.Failure(installation.Error)
                : Result<CovenantExportAdmission>.Success(new CovenantExportAdmission(installation.Value));

        }

        Result<CovenantReadLease> scoped = await gate
            .AcquireReadAsync(exact, cancellationToken)
            .ConfigureAwait(false);

        return scoped.IsFailure
            ? Result<CovenantExportAdmission>.Failure(scoped.Error)
            : Result<CovenantExportAdmission>.Success(new CovenantExportAdmission(scoped.Value));

    }

    public async Task<Result<CovenantSessionExportSensitivity>> InspectSessionAsync(
        Guid sessionId,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(readLease);

        if (sessionId == Guid.Empty)
        {

            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "A plaintext export inspection requires the Session it is about.");

        }

        // A Session's labels may name any Campaign, or none, so nothing narrower than an installation
        // read covers the question this answers.
        if (readLease.Snapshot.Coverage is not CovenantLeaseCoverage.Installation)
        {

            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "A Session export inspection requires an installation-wide Covenant read lease.");

        }

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        long labelled = await CountAsync(
            connection,
            "artifact_sensitivity",
            "SessionId",
            LedgerIdentity(sessionId),
            cancellationToken).ConfigureAwait(false);

        Result<ProjectedTaint> projected = await ReadProjectedTaintAsync(
            connection,
            sessionId,
            cancellationToken).ConfigureAwait(false);

        if (projected.IsFailure)
        {

            return projected.Error;

        }

        Result revalidated = await readLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {

            return revalidated.Error;

        }

        // The maximum of the two, in both fields. The label rows are the live evidence and the
        // projection is the conservative one; taking the smaller of them anywhere would let a purge
        // that removed the artifacts turn a Session that held Covenant content into an exportable one.
        return new CovenantSessionExportSensitivity(
            sessionId,
            Math.Max(labelled, projected.Value.TaintedArtifactCount),
            labelled > 0
                ? ContentSensitivityAlgebra.Maximum(
                    ContentSensitivity.CovenantDerived,
                    projected.Value.MaximumSensitivity)
                : projected.Value.MaximumSensitivity);

    }

    public async Task<Result<CovenantCampaignExportExclusions>> InventoryCampaignExclusionsAsync(
        Guid campaignId,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(readLease);

        if (campaignId == Guid.Empty)
        {

            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "A Campaign export inventory requires the Campaign it is about.");

        }

        if (!CoversCampaign(readLease.Snapshot, campaignId))
        {

            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "A Campaign export inventory requires a lease over that exact Campaign or the installation.");

        }

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        long entries = await CountAsync(
            connection,
            "covenant_entries",
            "CampaignId",
            CanonicalIdentity(campaignId),
            cancellationToken).ConfigureAwait(false);

        long tainted = await CountAsync(
            connection,
            "artifact_sensitivity",
            "CampaignId",
            LedgerIdentity(campaignId),
            cancellationToken).ConfigureAwait(false);

        Result revalidated = await readLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        return revalidated.IsFailure
            ? revalidated.Error
            : new CovenantCampaignExportExclusions(entries, tainted);

    }

    /// <summary>
    /// Whether this lease's coverage includes the named Campaign.
    /// </summary>
    /// <remarks>
    /// An installation read covers every Campaign, so it covers one. A scoped lease covers exactly the
    /// Campaign it named — a Global scope is not a superset here, because Global Covenant memory is a
    /// different partition rather than a parent of the Campaign ones.
    /// </remarks>
    private static bool CoversCampaign(CovenantOperationLeaseSnapshot snapshot, Guid campaignId) =>
        snapshot.Coverage is CovenantLeaseCoverage.Installation
        || (snapshot.Scope is { IsInitialized: true } scope
            && scope.Kind is CovenantScope.Campaign
            && scope.CampaignId == campaignId);

    /// <summary>The two projection fields this decision reads, and nothing else.</summary>
    /// <remarks>
    /// Narrower than <see cref="Core.Storage.SessionSensitivityProjection"/> on purpose. The refusal
    /// needs a count and a sensitivity; carrying the provenance digest and revision alongside them
    /// would mean the absent-table arm had to invent values it never looks at.
    /// </remarks>
    private readonly record struct ProjectedTaint(long TaintedArtifactCount, ContentSensitivity MaximumSensitivity);

    /// <summary>
    /// Reads the Session's conservative projection through its single owner.
    /// </summary>
    /// <remarks>
    /// Routed through <see cref="ArtifactSensitivityLedger"/> rather than repeating its SQL, so the
    /// projection has one reader and one shape. A second copy here would be the place the two drift.
    /// An absent table is clean for the same reason an absent count is zero: a database whose core
    /// support tables predate the ledger genuinely records no taint.
    /// </remarks>
    private static async Task<Result<ProjectedTaint>> ReadProjectedTaintAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(connection, "session_sensitivity_state", cancellationToken)
                .ConfigureAwait(false))
        {

            return new ProjectedTaint(TaintedArtifactCount: 0, ContentSensitivity.None);

        }

        Result<Core.Storage.SessionSensitivityProjection> projection = await ArtifactSensitivityLedger
            .ReadProjectionWithinAsync(connection, transaction: null, sessionId, cancellationToken)
            .ConfigureAwait(false);

        return projection.IsFailure
            ? projection.Error
            : new ProjectedTaint(
                projection.Value.TaintedArtifactCount,
                projection.Value.MaximumSensitivity);

    }

    /// <summary>
    /// How the information-flow ledger spells an owner identity: uppercase, as
    /// <see cref="ArtifactSensitivityLedger"/> writes it.
    /// </summary>
    /// <remarks>
    /// The two tables this policy reads genuinely disagree about case — the canonical family writes
    /// <c>ToString("D")</c> and the ledger writes it uppercased — and SQLite compares TEXT byte for
    /// byte. One shared formatter would therefore silently return zero for one of them, which for a
    /// refusal that fails closed is the one direction that must never be wrong.
    /// </remarks>
    private static string LedgerIdentity(Guid value) => value.ToString().ToUpperInvariant();

    /// <summary>How the canonical Covenant family spells an owner identity: <c>ToString("D")</c>.</summary>
    private static string CanonicalIdentity(Guid value) => value.ToString("D");

    /// <summary>
    /// Counts rows of one table owned by one identity.
    /// </summary>
    /// <remarks>
    /// A missing table is zero rather than a failure. An installation whose Covenant tier was never
    /// installed genuinely holds none of these rows, and turning that into a refusal would break an
    /// export that has always been allowed. The table and column names are compile-time constants from
    /// this file only; nothing caller-supplied reaches the command text.
    /// </remarks>
    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string table,
        string ownerColumn,
        string ownerIdentity,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(connection, table, cancellationToken)
                .ConfigureAwait(false))
        {

            return 0;

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {ownerColumn} = $owner;";

        _ = command.Parameters.AddWithValue("$owner", ownerIdentity);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

}
