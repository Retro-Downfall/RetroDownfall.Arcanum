using System.Data.Common;

using System.Globalization;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Annals;

/// <summary>
/// Raw-SQL read access to the Annals over the scoped <see cref="ArcanumDbContext"/>'s connection.
/// </summary>
/// <remarks>
/// None of the four <c>annal_*</c> tables is part of the compiled EF model, so access goes through
/// <see cref="DbCommand"/> rather than LINQ, mirroring <see cref="SagaMemoryStore"/>.
/// </remarks>
internal sealed class AnnalsStore(ArcanumDbContext db) : IAnnalsStore
{

    public async Task<AnnalClaimHead?> GetClaimAsync(
        AnnalSubjectStore subjectStore,
        string subjectId,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(subjectId);

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand command = connection.CreateCommand();

                command.CommandText =
                    """
                    SELECT claim.ClaimId, claim.SubjectStoreCode, claim.SubjectId,
                           head.CurrentVersionId, head.CurrentRevision, head.CurrentOperationCode,
                           head.UpdatedAtUtc
                    FROM annal_claims AS claim
                    JOIN annal_heads AS head ON head.ClaimId = claim.ClaimId
                    WHERE claim.SubjectStoreCode = @storeCode AND claim.SubjectId = @subjectId
                    """;

                AddParameter(command, "@storeCode", (int)subjectStore);

                AddParameter(command, "@subjectId", subjectId);

                await using DbDataReader reader =
                    await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                // A durable row with no claim is a first-class state, not an error: it is what a memory
                // written while the Annals was disabled looks like, and what every row looks like before
                // the upgrade sweep drains.
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    return null;

                }

                return new AnnalClaimHead(
                    reader.GetString(0),
                    (AnnalSubjectStore)reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    (AnnalOperation)reader.GetInt32(5),
                    ParseTimestamp(reader.GetString(6)));

            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<IReadOnlyList<AnnalClaimVersion>> GetVersionsAsync(
        string claimId,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(claimId);

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand command = connection.CreateCommand();

                // The transaction-time end is derived here rather than stored, so there is one owner of
                // the rule and no column an append-only guard would have to be relaxed for. The
                // correlated subquery returns at most one row: a version has at most one successor,
                // because a revision is unique within its claim and each names exactly one predecessor.
                command.CommandText =
                    """
                    SELECT version.VersionId, version.ClaimId, version.Sequence, version.Revision,
                           version.OperationCode, version.OriginCode, version.ScopeKindCode,
                           version.CampaignId, version.SensitivityCode, version.ValidFromUtc,
                           version.ValidToUtc, version.RecordedAtUtc,
                           (SELECT successor.RecordedAtUtc
                            FROM annal_versions AS successor
                            WHERE successor.PredecessorVersionId = version.VersionId) AS RecordedUntilUtc,
                           version.PredecessorVersionId
                    FROM annal_versions AS version
                    WHERE version.ClaimId = @claimId
                    ORDER BY version.Revision
                    """;

                AddParameter(command, "@claimId", claimId);

                List<AnnalClaimVersion> versions = [];

                await using DbDataReader reader =
                    await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    versions.Add(
                        new AnnalClaimVersion(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetInt64(2),
                            reader.GetInt32(3),
                            (AnnalOperation)reader.GetInt32(4),
                            (AnnalOrigin)reader.GetInt32(5),
                            (SagaMemoryScopeKind)reader.GetInt32(6),
                            reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
                            (ContentSensitivity)reader.GetInt32(8),
                            ParseTimestamp(reader.GetString(9)),
                            reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10)),
                            ParseTimestamp(reader.GetString(11)),
                            reader.IsDBNull(12) ? null : ParseTimestamp(reader.GetString(12)),
                            reader.IsDBNull(13) ? null : reader.GetString(13)));

                }

                return (IReadOnlyList<AnnalClaimVersion>)versions;

            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<IReadOnlyList<AnnalDependencyEdge>> GetDependenciesAsync(
        string versionId,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(versionId);

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand command = connection.CreateCommand();

                command.CommandText =
                    """
                    SELECT DependentVersionId, DependencyVersionId, RelationCode, Ordinal
                    FROM annal_dependencies
                    WHERE DependentVersionId = @versionId
                    ORDER BY Ordinal
                    """;

                AddParameter(command, "@versionId", versionId);

                List<AnnalDependencyEdge> edges = [];

                await using DbDataReader reader =
                    await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    edges.Add(
                        new AnnalDependencyEdge(
                            reader.GetString(0),
                            reader.GetString(1),
                            (AnnalDependencyRelation)reader.GetInt32(2),
                            reader.GetInt32(3)));

                }

                return (IReadOnlyList<AnnalDependencyEdge>)edges;

            },
            cancellationToken).ConfigureAwait(false);

    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void AddParameter(DbCommand command, string name, object value)
    {

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        _ = command.Parameters.Add(parameter);

    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        }

        return connection;

    }

}
