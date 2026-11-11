using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    private Task<Result<T>> InCohortToolTransactionAsync<T>(Guid rootQuestId, GuildChildToolAdmission? toolAdmission,
        JsonTypeInfo<T> typeInfo,
        Func<SqliteConnection, SqliteTransaction, SignetAuthority.QuestTransaction, CancellationToken, Task<Result<T>>> action,
        CancellationToken ct) => authority.InQuestTransactionAsync<T>(rootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result<T> result = await action(connection, transaction, signets, token).ConfigureAwait(false);

                if (result.IsFailure || toolAdmission is null) return result;

                Result projection = toolAdmission.ValidateProjectionAllocation(JsonSerializer.SerializeToElement(result.Value, typeInfo));

                // A refused result rolls back the same transaction as funding, membership,
                // cancellation intent, events and its acceptance receipt.
                return projection.IsFailure ? projection.Error : result;
            }, ct);
}
