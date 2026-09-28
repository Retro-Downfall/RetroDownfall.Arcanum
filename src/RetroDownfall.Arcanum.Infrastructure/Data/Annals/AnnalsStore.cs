using System.Data.Common;

using System.Globalization;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Annals;

/// <summary>
/// Raw-SQL read access to the Annals over the scoped <see cref="ArcanumDbContext"/>'s connection.
/// </summary>
/// <remarks>
/// Annals tables and historical Lexicon provenance are outside the compiled EF model, so access goes through
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
                    (AnnalSubjectStore)ReadCode(reader, 1, 1, 2),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    (AnnalOperation)ReadCode(reader, 5, 1, 3),
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

                return await GrimoireCoreSchemaVersion.InSnapshotAsync(connection, async () =>
                {
                    await using DbCommand command = connection.CreateCommand();

                    // Inspect per call: a live reader may outlast the atomic v10-to-v11 transition.
                    bool hasFormat = await GrimoireCoreSchemaVersion.ReadAsync(connection, cancellationToken).ConfigureAwait(false) >= 11;

                    // The transaction-time end is derived here rather than stored, so there is one owner of
                    // the rule and no column an append-only guard would have to be relaxed for. The
                    // correlated subquery returns at most one row: a version has at most one successor,
                    // because a revision is unique within its claim and each names exactly one predecessor.
                    command.CommandText =
                        $"""
                        SELECT version.VersionId, version.ClaimId, version.Sequence, version.Revision,
                               version.OperationCode, version.OriginCode, version.ScopeKindCode,
                               version.CampaignId, version.SensitivityCode, version.ValidFromUtc,
                               version.ValidToUtc, version.RecordedAtUtc,
                               (SELECT successor.RecordedAtUtc
                                FROM annal_versions AS successor
                                WHERE successor.PredecessorVersionId = version.VersionId) AS RecordedUntilUtc,
                               version.PredecessorVersionId,
                               {(hasFormat ? "version.ContentHashFormatCode" : "1")} AS ContentHashFormatCode, version.ContentHash
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
                                (AnnalOperation)ReadCode(reader, 4, 1, 3),
                                (AnnalOrigin)ReadCode(reader, 5, 1, 4),
                                (SagaMemoryScopeKind)ReadCode(reader, 6, 0, 3),
                                reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
                                (ContentSensitivity)ReadCode(reader, 8, 0, 1),
                                (AnnalContentHashFormat)ReadCode(reader, 14, 1, 2),
                                reader.IsDBNull(15) ? null : (byte[])reader.GetValue(15),
                                ParseTimestamp(reader.GetString(9)),
                                reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10)),
                                ParseTimestamp(reader.GetString(11)),
                                reader.IsDBNull(12) ? null : ParseTimestamp(reader.GetString(12)),
                                reader.IsDBNull(13) ? null : reader.GetString(13)));
                    }

                    return (IReadOnlyList<AnnalClaimVersion>)versions;
                }, cancellationToken).ConfigureAwait(false);
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
                            (AnnalDependencyRelation)ReadCode(reader, 2, 1, 3),
                            reader.GetInt32(3)));
                }

                return (IReadOnlyList<AnnalDependencyEdge>)edges;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LexiconAnnalFactProvenance>> GetLexiconFactProvenanceAsync(
        string versionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(versionId);

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                return await GrimoireCoreSchemaVersion.InSnapshotAsync(connection, async () =>
                {
                    if (await GrimoireCoreSchemaVersion.ReadAsync(connection, cancellationToken).ConfigureAwait(false) < 11)
                    {
                        return (IReadOnlyList<LexiconAnnalFactProvenance>)[];
                    }

                    await using DbCommand command = connection.CreateCommand();

                    command.CommandText = """
                        SELECT AnnalVersionId, FactOrdinal, SessionId, AttachmentId, LogicalKey,
                               AttachmentVersion, AttachmentContentHash, MaterializedAt, SourceType
                        FROM lexicon_annal_fact_provenance
                        WHERE AnnalVersionId = @versionId
                        ORDER BY FactOrdinal
                        """;

                    AddParameter(command, "@versionId", versionId);

                    List<LexiconAnnalFactProvenance> provenance = [];

                    await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        provenance.Add(new LexiconAnnalFactProvenance(
                            reader.GetString(0),
                            reader.GetInt32(1),
                            Guid.Parse(reader.GetString(2)),
                            Guid.Parse(reader.GetString(3)),
                            reader.GetString(4),
                            reader.GetInt32(5),
                            reader.GetString(6),
                            ParseTimestamp(reader.GetString(7)),
                            reader.GetString(8)));
                    }

                    return (IReadOnlyList<LexiconAnnalFactProvenance>)provenance;
                }, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal static int ReadCode(DbDataReader reader, int ordinal, int minimum, int maximum)
    {
        if (reader.GetValue(ordinal) is not long code || code < minimum || code > maximum)
        {
            throw new InvalidDataException($"Invalid Annals {reader.GetName(ordinal)} code.");
        }

        return (int)code;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        UtcInstantText.Parse(value);

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
