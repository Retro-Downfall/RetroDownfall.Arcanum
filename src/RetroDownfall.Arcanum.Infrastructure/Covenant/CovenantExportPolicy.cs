using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The one implementation of the plaintext-export policy.
/// </summary>
/// <remarks>
/// A Session decision is one indexed <c>COUNT(*)</c> over the label ledger and one projection lookup; a
/// Campaign inventory is two indexed <c>COUNT(*)</c>s. No read of any kind opens an artifact. That is the
/// whole point of doing this before the export graph: a refusal that had to open the artifacts to
/// describe them would already have pulled Covenant-derived bytes into the process it is refusing to
/// send them out of (§10.19.11).
///
/// <para>The tables are read directly, with no existence probe first. The label ledger and the Session
/// projection are core tables the schema installer always creates, and the canonical entries exist
/// whenever this policy holds a lease, so one that is missing is a damaged installation rather than an
/// empty one. It is reported as <see cref="ErrorCodes.Covenant.ManualRecoveryRequired"/>, because the
/// one answer a plaintext export must never take from evidence it could not read is "clean".</para>
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

        Result<CovenantSessionExportSensitivity> sensitivity = await ReadSessionSensitivityAsync(
            connection,
            sessionId,
            cancellationToken).ConfigureAwait(false);

        if (sensitivity.IsFailure)
        {
            return sensitivity;
        }

        Result revalidated = await readLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        return revalidated.IsFailure ? revalidated.Error : sensitivity;
    }

    public async Task<Result<CovenantSessionExportSensitivity>> InspectSessionWithoutLeaseAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "A plaintext export inspection requires the Session it is about.");
        }

        // The core door on purpose. Both tables are core tables that no Covenant capability owns, and
        // the canonical door latches the one-way process residence that closes the offline host-tools
        // transition; a gate-off installation that merely exported a Session has held no Covenant
        // material and must not lose that transition for it.
        SqliteConnection connection = await connections
            .GetOpenCoreConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await ReadSessionSensitivityAsync(connection, sessionId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Result<CovenantSessionExportSensitivity>> ReadSessionSensitivityAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
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
        catch (SqliteException failure) when (IsMissingTable(failure))
        {
            return LedgerUnreadable();
        }
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

        long entries;

        long tainted;

        try
        {
            entries = await CountAsync(
                connection,
                "covenant_entries",
                "CampaignId",
                CanonicalIdentity(campaignId),
                cancellationToken).ConfigureAwait(false);

            tainted = await CountAsync(
                connection,
                "artifact_sensitivity",
                "CampaignId",
                LedgerIdentity(campaignId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException failure) when (IsMissingTable(failure))
        {
            return LedgerUnreadable();
        }

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
    /// would mean this decision had to invent values it never looks at.
    /// </remarks>
    private readonly record struct ProjectedTaint(long TaintedArtifactCount, ContentSensitivity MaximumSensitivity);

    /// <summary>
    /// Reads the Session's conservative projection through its single owner.
    /// </summary>
    /// <remarks>
    /// Routed through <see cref="ArtifactSensitivityLedger"/> rather than repeating its SQL, so the
    /// projection has one reader and one shape. A second copy here would be the place the two drift. A
    /// missing table surfaces from the read itself and is reported by the caller as a damaged
    /// installation: the schema installer always creates it.
    /// </remarks>
    private static async Task<Result<ProjectedTaint>> ReadProjectedTaintAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
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
    /// A missing table throws rather than counting zero: the callers report it as a damaged installation,
    /// because "zero rows" is the one answer a refusal must not take from a table it could not read. The
    /// table and column names are compile-time constants from this file only; nothing caller-supplied
    /// reaches the command text.
    /// </remarks>
    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string table,
        string ownerColumn,
        string ownerIdentity,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {ownerColumn} = $owner;";

        _ = command.Parameters.AddWithValue("$owner", ownerIdentity);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Whether SQLite refused a statement because a table it names does not exist.</summary>
    private static bool IsMissingTable(SqliteException failure) =>
        failure.SqliteErrorCode == 1
        && failure.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The refusal for a ledger table that is not there. Content-free by rule: it names no table,
    /// Session, or Campaign, because it travels through logs and refusal messages.
    /// </summary>
    private static Error LedgerUnreadable() =>
        new(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "The Covenant ledger this export is checked against is missing a table the core schema always creates, so nothing can be proved about what it would carry. The installation needs recovery.");
}
