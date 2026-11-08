using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using Microsoft.Data.Sqlite;
using System.Data.Common;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class SignetAuthority
{
    internal Task<Result<T>> AdmitMemoryAsync<T>(GuildExecutionHandle execution, GuildMemoryStore memoryStore,
        GuildMemoryOperation operation, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (execution is null || action is null || !Enum.IsDefined(memoryStore) || !Enum.IsDefined(operation))
        {
            return Task.FromResult(Result<T>.Failure(Mismatch));
        }

        if (!GuildMemoryInvocationAmbient.IsActive || GuildMemoryInvocationAmbient.Current is not { } invocation
            || !ReferenceEquals(invocation.Authority, this) || !ReferenceEquals(invocation.Execution, execution))
        {
            return Task.FromResult(Result<T>.Failure(Mismatch));
        }

        return UnderRootAsync(execution.RootQuestId, true, async ct =>
        {
            Result<Verified> live = await ReadLiveAsync(execution, ct).ConfigureAwait(false);

            if (live.IsFailure)
            {
                return Result<T>.Failure(live.Error);
            }

            if (!live.Value.Effective.Memory.Any(grant => grant.Store == memoryStore && grant.Operation == operation))
            {
                return Result<T>.Failure(new(ErrorCodes.Guild.MemoryNotGranted, "The memory operation is not granted to this Guild run."));
            }

            try
            {
                T result = await GuildMemoryAccess.WithinAsync(memoryStore, operation, live.Value.Effective,
                    live.Value.Lineage[0].Owners.CampaignId, live.Value.Lineage[0].Owners.SessionId, action, ct).ConfigureAwait(false);

                if (operation == GuildMemoryOperation.Read)
                {
                    Result<Verified> current = await ReadLiveAsync(execution, ct).ConfigureAwait(false);

                    if (current.IsFailure)
                    {
                        return Result<T>.Failure(current.Error);
                    }

                    if (live.Value.Effective.Memory.Where(grant => grant.Store == memoryStore && grant.Operation == operation)
                        .Any(grant => !current.Value.Effective.Memory.Contains(grant)))
                    {
                        return Result<T>.Failure(new(ErrorCodes.Guild.MemoryNotGranted, "Memory authority narrowed before disclosure."));
                    }
                }

                return Result<T>.Success(result);
            }
            catch (GuildMemoryAccessException ex)
            {
                return Result<T>.Failure(ex.Error);
            }
        }, cancellationToken);
    }

    internal async Task<Result<GuildGrant>> RevalidateMemoryAsync(GuildMemoryInvocationContext invocation,
        DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (!GuildMemoryInvocationAmbient.IsActive || !ReferenceEquals(GuildMemoryInvocationAmbient.Current, invocation)
            || !ReferenceEquals(invocation.Authority, this)
            || connection is not SqliteConnection sqlite
            || transaction is not null && (transaction is not SqliteTransaction sqliteTransaction
                || !ReferenceEquals(sqliteTransaction.Connection, sqlite)))
        {
            return Mismatch;
        }

        // Lexicon's connection-owned BEGIN IMMEDIATE has no managed transaction wrapper.
        Result<Verified> current = await LiveAsync(sqlite, (SqliteTransaction)transaction!, invocation.Execution, cancellationToken).ConfigureAwait(false);

        return current.IsSuccess ? current.Value.Effective : Result<GuildGrant>.Failure(current.Error);
    }
}
