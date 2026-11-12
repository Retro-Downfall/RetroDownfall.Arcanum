using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildLocalHostPolicy
{
    /// <summary>
    /// Selects a current registered root for authenticated native Operator management. It does not
    /// create canonical Campaign identities or issue Run pins; the physical broker owns identity proof.
    /// </summary>
    public Task<Result<GuildOperatorWorkspaceHostEvidence>> ResolveOperatorAsync(Guid workspaceId, CancellationToken ct) =>
        ExecuteAsync<GuildOperatorWorkspaceHostEvidence>(false, async (connection, transaction, token) =>
        {
            Result current = await CurrentIdentityAsync(connection, transaction, token).ConfigureAwait(false);

            if (current.IsFailure) return current.Error;

            Result owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, null, workspaceId, token).ConfigureAwait(false);

            if (owner.IsFailure) return owner.Error;

            await using SqliteCommand command = Command(connection, transaction, """
                SELECT Path FROM Campaigns WHERE Id=$workspace AND typeof(Path)='text' AND length(CAST(Path AS BLOB))<=16384
                """);

            Add(command, "$workspace", Id(workspaceId));

            if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not string path)
                return new Error(ErrorCodes.Guild.WorkspaceIdentityUnavailable, "The current registered workspace root is unavailable.");

            Result<string> allowed = CampaignPathPolicy.ValidateAndNormalizePath(path, settings.CurrentValue);

            if (allowed.IsFailure)
                return new Error(ErrorCodes.Guild.WorkspaceIdentityUnavailable, "The current workspace root no longer satisfies host path policy.");

            return new GuildOperatorWorkspaceHostEvidence(allowed.Value, [new(".", GuildPathScopeKind.Subtree)]);
        }, ct);
}
