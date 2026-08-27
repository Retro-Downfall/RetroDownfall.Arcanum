using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class AttachmentMemoryProvenanceStore(
    ArcanumDbContext db) : IAttachmentMemoryProvenanceStore
{

    public Task RecordConsultationsAsync(
        Guid sourceEntryId,
        IReadOnlyList<AttachmentMemoryProvenance> provenance,
        CancellationToken cancellationToken)
    {

        if (provenance.Count == 0)
        {

            return Task.CompletedTask;

        }

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (AttachmentMemoryProvenance source in provenance)
                {

                    await using DbCommand command = connection.CreateCommand();

                    command.Transaction = transaction;

                    command.CommandText =
                        """
                        INSERT OR IGNORE INTO attachment_memory_consultations (
                            SourceEntryId, SessionId, AttachmentId, LogicalKey, Version, ContentHash,
                            MaterializedAt, SourceType)
                        VALUES (
                            @sourceEntryId, @sessionId, @attachmentId, @logicalKey, @version, @contentHash,
                            @materializedAt, @sourceType)
                        """;

                    AddParameter(
                        command,
                        "@sourceEntryId",
                        sourceEntryId.ToString().ToUpperInvariant());

                    AddParameter(
                        command,
                        "@sessionId",
                        source.SessionId.ToString().ToUpperInvariant());

                    AddParameter(command, "@attachmentId", source.AttachmentId.ToString().ToUpperInvariant());

                    AddParameter(command, "@logicalKey", source.LogicalKey);

                    AddParameter(command, "@version", source.Version);

                    AddParameter(command, "@contentHash", source.ContentHash);

                    AddParameter(
                        command,
                        "@materializedAt",
                        source.MaterializedAt.ToString("o", CultureInfo.InvariantCulture));

                    AddParameter(command, "@sourceType", source.SourceType);

                    _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    public async Task<IReadOnlyList<AttachmentMemoryProvenance>> ListConsultationsAsync(
        Guid sessionId,
        DateTimeOffset afterExclusive,
        DateTimeOffset throughInclusive,
        CancellationToken cancellationToken)
    {

        return await SqliteBusyRetry.ExecuteAsync<IReadOnlyList<AttachmentMemoryProvenance>>(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand command = connection.CreateCommand();

                command.CommandText =
                    """
                    SELECT c.SessionId, c.AttachmentId, c.LogicalKey, c.Version,
                           c.ContentHash, c.MaterializedAt, c.SourceType,
                           EXISTS(
                               SELECT 1 FROM "SessionAttachments" a
                               WHERE a."Id" = c.AttachmentId AND a."State" = 'Bound'
                           )
                    FROM attachment_memory_consultations c
                    INNER JOIN "Entries" e
                        ON e."Id" = c.SourceEntryId
                       AND e."SessionId" = c.SessionId
                    WHERE c.SessionId = @sessionId
                      AND julianday(e."CreatedAt") > julianday(@afterExclusive)
                      AND julianday(e."CreatedAt") <= julianday(@throughInclusive)
                    ORDER BY e."Sequence", c.MaterializedAt, c.AttachmentId
                    """;

                AddParameter(
                    command,
                    "@sessionId",
                    sessionId.ToString().ToUpperInvariant());

                AddParameter(
                    command,
                    "@afterExclusive",
                    afterExclusive.ToString("o", CultureInfo.InvariantCulture));

                AddParameter(
                    command,
                    "@throughInclusive",
                    throughInclusive.ToString("o", CultureInfo.InvariantCulture));

                List<AttachmentMemoryProvenance> results = [];

                await using DbDataReader reader = await command
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    results.Add(
                        new AttachmentMemoryProvenance(
                            Guid.Parse(reader.GetString(0)),
                            Guid.Parse(reader.GetString(1)),
                            reader.GetString(2),
                            reader.GetInt32(3),
                            reader.GetString(4),
                            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                            reader.GetString(6),
                            reader.GetInt32(7) == 1
                                ? AttachmentSourceAvailability.Available
                                : AttachmentSourceAvailability.Unavailable));

                }

                return results;

            },
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        }

        return connection;

    }

    private static void AddParameter(DbCommand command, string name, object value)
    {

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        command.Parameters.Add(parameter);

    }

}
