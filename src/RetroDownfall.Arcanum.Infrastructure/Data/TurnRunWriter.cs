using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Raw-SQL <see cref="ITurnRunWriter"/> for InferenceRuns / BillableOperations.
/// </summary>
internal sealed class TurnRunWriter(ArcanumDbContext db) : ITurnRunWriter
{

    public Task<Guid> StartRunAsync(InferenceRunStart start, CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    INSERT INTO "InferenceRuns"
                        ("Id", "RequestId", "SessionId", "Surface", "Purpose", "StartedAt", "CompletedAt", "Status", "IdempotencyClaimId")
                    VALUES
                        (@id, @requestId, @sessionId, @surface, @purpose, @startedAt, NULL, @status, @claimId)
                    """;

                AddParameter(cmd, "@id", id.ToString("N"));
                AddParameter(cmd, "@requestId", start.RequestId);
                AddParameter(cmd, "@sessionId", start.SessionId is Guid sid ? sid.ToString("N") : DBNull.Value);
                AddParameter(cmd, "@surface", start.Surface);
                AddParameter(cmd, "@purpose", start.Purpose);
                AddParameter(cmd, "@startedAt", start.StartedAt.ToString("o", CultureInfo.InvariantCulture));
                AddParameter(cmd, "@status", (int)InferenceRunStatus.Running);
                AddParameter(cmd, "@claimId", start.IdempotencyClaimId is Guid cid ? cid.ToString("N") : DBNull.Value);

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                return id;
            },
            cancellationToken);
    }

    public Task CompleteRunAsync(Guid runId, InferenceRunStatus status, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "InferenceRuns"
                    SET "Status" = @status, "CompletedAt" = @completedAt
                    WHERE "Id" = @id
                    """;

                AddParameter(cmd, "@id", runId.ToString("N"));
                AddParameter(cmd, "@status", (int)status);
                AddParameter(cmd, "@completedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<bool> TryAbandonRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                // The Running guard is what makes repeated recovery safe: a run that reached Completed or
                // Failed before the crash landed keeps that status instead of being rewritten (#40).
                cmd.CommandText =
                    """
                    UPDATE "InferenceRuns"
                    SET "Status" = @abandoned, "CompletedAt" = @completedAt
                    WHERE "Id" = @id AND "Status" = @running
                    """;

                AddParameter(cmd, "@id", runId.ToString("N"));
                AddParameter(cmd, "@abandoned", (int)InferenceRunStatus.Abandoned);
                AddParameter(cmd, "@running", (int)InferenceRunStatus.Running);
                AddParameter(cmd, "@completedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

                return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
            },
            cancellationToken);
    }

    public Task<Guid> RecordBillableOperationAsync(BillableOperationRecord operation, CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    INSERT INTO "BillableOperations"
                        ("Id", "RunId", "OperationType", "Provider", "Model", "Purpose", "StartedAt", "CompletedAt",
                         "InputTokens", "OutputTokens", "ReasoningTokens", "CachedTokens", "PricingSnapshotJson", "ActualCostUsd", "Status", "ProviderRequestId")
                    VALUES
                        (@id, @runId, @opType, @provider, @model, @purpose, @startedAt, @completedAt,
                         @input, @output, @reasoning, @cached, @pricing, @cost, @status, @providerRequestId)
                    """;

                AddParameter(cmd, "@id", id.ToString("N"));
                AddParameter(cmd, "@runId", operation.RunId.ToString("N"));
                AddParameter(cmd, "@opType", (int)operation.OperationType);
                AddParameter(cmd, "@provider", operation.Provider);
                AddParameter(cmd, "@model", operation.Model);
                AddParameter(cmd, "@purpose", operation.Purpose);
                AddParameter(cmd, "@startedAt", operation.StartedAt.ToString("o", CultureInfo.InvariantCulture));
                AddParameter(cmd, "@completedAt", operation.CompletedAt.ToString("o", CultureInfo.InvariantCulture));
                AddParameter(cmd, "@input", operation.InputTokens);
                AddParameter(cmd, "@output", operation.OutputTokens);
                AddParameter(cmd, "@reasoning", operation.ReasoningTokens);
                AddParameter(cmd, "@cached", operation.CachedTokens);
                AddParameter(cmd, "@pricing", operation.PricingSnapshotJson);
                AddParameter(cmd, "@cost", operation.ActualCostUsd);
                AddParameter(cmd, "@status", (int)operation.Status);
                AddParameter(cmd, "@providerRequestId", (object?)operation.ProviderRequestId ?? DBNull.Value);

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                return id;
            },
            cancellationToken);
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

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

}
