using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Resolves accepted local inputs only inside a new Quest's issuance transaction.</summary>
internal interface IGuildLocalExecutionAdmission
{
    Task<Result<GuildLocalExecutionInput>> ResolveWithinAsync(GuildCallerContext caller, GuildQuestStored quest,
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken);

    Task<Result<GuildLocalExecutionInput>> ResolveToolWithinAsync(GuildCallerContext caller, GuildQuestStored quest,
        SqliteConnection connection, SqliteTransaction transaction, GuildChildToolAdmission admission, CancellationToken cancellationToken)
        => ResolveWithinAsync(caller, quest, connection, transaction, cancellationToken);
}
