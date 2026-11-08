using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildWorkspaceLeaseStore
{
    internal static Task<Result<GuildWorkspaceLeaseRecord>> RetainRecoveryWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildWorkspaceLeaseRecord row, CancellationToken cancellationToken) =>
        row.State == GuildWorkspaceLeaseState.Released
            || row.InFlightMutationCount <= 0 && row.State != GuildWorkspaceLeaseState.ReconciliationRequired
            ? Task.FromResult(Result<GuildWorkspaceLeaseRecord>.Failure(Conflict))
            : UpdateWithinAsync(connection, transaction, row, row with
            {
                State = GuildWorkspaceLeaseState.ReconciliationRequired,
                ReasonCode = ErrorCodes.Guild.ReconciliationRequired,
            }, cancellationToken);
}
