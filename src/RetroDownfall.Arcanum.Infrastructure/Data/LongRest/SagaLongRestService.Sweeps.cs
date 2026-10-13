using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class SagaLongRestService
{
    internal static Task<Result<LongRestReceipt>?> ReadSweepReceiptAsync(SqliteConnection connection, SqliteTransaction transaction,
        string receipt, CancellationToken ct) => ReadReceiptAsync(connection, transaction, receipt, ct);

    internal async Task<Result<LongRestReceipt>> ApplyInTransactionAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestRequest request, LongRestProtectedEvidence? protection, CancellationToken cancellationToken)
    {
        if (protection is not null)
        {
            return Refuse(ErrorCodes.LongRest.InvalidRequest, "Protected application requires exact current work and retained input evidence.");
        }

        List<LoadedSnapshot> loaded = [];

        foreach (LongRestTarget target in request.Targets)
        {
            Result<LoadedSnapshot> snapshot = await ReadSnapshotAsync(connection, transaction, target, cancellationToken).ConfigureAwait(false);

            if (snapshot.IsFailure)
            {
                return Result<LongRestReceipt>.Failure(snapshot.Error);
            }

            loaded.Add(snapshot.Value);
        }

        LongRestSnapshot[] snapshots = loaded.Select(item => item.Snapshot).ToArray();

        // A successor can add new incoming dependency edges to an old exact input.
        // That changes today's context, but cannot invalidate the already applied decision.
        Result<LongRestReceipt>? historical = await ReadHistoricalAppliedReceiptAsync(
            connection, transaction, request, snapshots, cancellationToken).ConfigureAwait(false);

        if (historical is not null)
        {
            return historical;
        }

        Result<string> identity = LongRestPolicy.Identify(request, snapshots);

        if (identity.IsFailure)
        {
            return Result<LongRestReceipt>.Failure(identity.Error);
        }

        // Replay is historical evidence, even after an input's head changes. No receipt,
        // projection or memory write takes place on this path.
        Result<LongRestReceipt>? replay = await ReadReceiptAsync(connection, transaction, identity.Value, cancellationToken).ConfigureAwait(false);

        if (replay is not null)
        {
            return replay;
        }

        foreach (LoadedSnapshot input in loaded)
        {
            if (input.Content is { } content
                && !CryptographicOperations.FixedTimeEquals(
                    AnnalContentDigest.ForSagaMemory(content), Convert.FromHexString(input.Snapshot.ContentHash)))
            {
                return Refuse(ErrorCodes.LongRest.StaleInput, "The stored content no longer matches its exact observed version.");
            }
        }

        Result<LongRestReceipt> decision = LongRestPolicy.Evaluate(request, snapshots);

        if (decision.IsFailure)
        {
            return decision;
        }

        await WriteReceiptAsync(connection, transaction, request, decision.Value, snapshots, cancellationToken).ConfigureAwait(false);

        return decision;
    }
}
