using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildLocalHostPolicy
{
    private async Task<Result<T>> ExecuteAsync<T>(bool write,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<T>>> action, CancellationToken ct)
    {
        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? lease)) return Unavailable;

        await using IGrimoireWorkLease held = lease!;

        IGrimoireExternalEffectGroup? effect = null;

        if (write && !held.TryBeginExternalEffectGroup(out effect)) return Unavailable;

        await using IGrimoireExternalEffectGroup? heldEffect = effect;

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, held.MaintenanceRevocation);

        return await _store.ExecuteAsync(write, action, linked.Token).ConfigureAwait(false);
    }

    private static async Task<Result<Guid>> ReadGenerationAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            "SELECT InstallationIdentity FROM covenant_authority_state WHERE StateKey=1 AND typeof(InstallationIdentity)='text' AND length(CAST(InstallationIdentity AS BLOB))=36");

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string raw && Guid.TryParseExact(raw, "D", out Guid generation) && generation != Guid.Empty
            ? generation : Unavailable;
    }

    private async Task<Result> CurrentIdentityAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        Identity? identity = Volatile.Read(ref _identity);

        if (identity is null) return Unavailable;

        Result<Guid> generation = await ReadGenerationAsync(connection, transaction, ct).ConfigureAwait(false);

        if (generation.IsFailure || generation.Value != identity.Generation) return Unavailable;

        await using SqliteCommand command = Command(connection, transaction,
            "SELECT NativeOperatorPrincipalId FROM guild_workspace_authority_state WHERE StateKey=1 AND InstallationGeneration=$generation");

        Add(command, "$generation", Id(identity.Generation));

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string principal && principal == Id(identity.Principal)
            ? Result.Success() : Unavailable;
    }

    private static async Task<bool> OperationMatchesAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid questId, Guid operationId, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM guild_quests q JOIN LongRunningOperations o ON o.Id=q.OperationId WHERE q.Id=$quest AND q.OperationId=$operation AND o.RunId=q.RunId AND o.SessionId=q.SessionId AND q.Retired=0)");

        Add(command, "$quest", Id(questId));

        Add(command, "$operation", operationId.ToString("N"));

        return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static async Task<Result> ValidateOwnersAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid? session, Guid? campaign, Guid? workspace, CancellationToken ct)
    {
        if (workspace is not null && campaign != workspace) return Forbidden;

        Result owners = await GuildStore.RequireOwnerWithinAsync(connection, transaction, session, campaign, ct).ConfigureAwait(false);

        if (owners.IsFailure || session is null) return owners;

        await using SqliteCommand command = Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM session_campaign_bindings WHERE SessionId=$session AND CampaignId IS $campaign AND BindingKindCode=$kind)");

        Add(command, "$session", Id(session.Value));

        Add(command, "$campaign", campaign is { } id ? Id(id) : null);

        Add(command, "$kind", campaign is null ? 1 : 2);

        return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) ? Result.Success() : Forbidden;
    }

    private async Task<Result<GuildWorkspaceRegistration>> RegistrationAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid workspace, CancellationToken ct)
    {
        Result owners = await GuildStore.RequireOwnerWithinAsync(connection, transaction, null, workspace, ct).ConfigureAwait(false);

        if (owners.IsFailure) return owners.Error;

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT i.DisplayPath,i.PolicyVersion,i.Revision,i.PhysicalIdentityDigest
            FROM campaign_path_identities i JOIN Campaigns c ON c.Id=i.CampaignId AND c.Path=i.DisplayPath
            WHERE i.CampaignId=$workspace
            """);

        Add(command, "$workspace", Id(workspace));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt64(1) != CampaignPathIdentityPolicy.Version
            || reader.GetInt64(2) < 1 || reader.GetValue(3) is not byte[] { Length: 32 } digest) return Unavailable;

        string root = reader.GetString(0);

        CovenantDigest identity = new(digest);

        if (!Path.IsPathFullyQualified(root) || opener.IdentifyExact(root) != identity) return Unavailable;

        return new GuildWorkspaceRegistration(workspace, root, (uint)reader.GetInt64(1), reader.GetInt64(2), identity);
    }

    private async Task<Result<GuildGrantScopeEvidence>> ScopeAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildGrant parent, GuildGrant child, CancellationToken ct)
    {
        if (GuildGrantPolicy.Validate(parent).IsFailure || GuildGrantPolicy.Validate(child).IsFailure) return Forbidden;

        ImmutableArray<(GuildWorkspaceGrant Parent, GuildWorkspaceGrant Child)>.Builder workspaces = ImmutableArray.CreateBuilder<(GuildWorkspaceGrant, GuildWorkspaceGrant)>();

        ImmutableArray<(Guid CampaignId, Guid SessionId)>.Builder sessions = ImmutableArray.CreateBuilder<(Guid, Guid)>();

        foreach (Guid workspace in parent.Workspaces.Concat(child.Workspaces).Select(item => item.WorkspaceId).Distinct())
        {
            Result<GuildWorkspaceRegistration> registration = await RegistrationAsync(connection, transaction, workspace, ct).ConfigureAwait(false);

            if (registration.IsFailure) return registration.Error;

            GuildWorkspaceAuthority physical = new(registration.Value, opener, token => RegistrationAsync(connection, transaction, workspace, token));

            GuildGrant parentScope = parent with { Workspaces = [.. parent.Workspaces.Where(item => item.WorkspaceId == workspace)] };

            GuildGrant childScope = child with { Workspaces = [.. child.Workspaces.Where(item => item.WorkspaceId == workspace)] };

            Result<GuildGrantScopeEvidence> contained = await physical.ResolveContainmentAsync(parentScope, childScope, ct).ConfigureAwait(false);

            if (contained.IsFailure) return contained.Error;

            workspaces.AddRange(contained.Value.WorkspaceContainment);
        }

        foreach (GuildMemoryScope scope in parent.Memory.Concat(child.Memory).Select(item => item.Scope).Distinct())
        {
            if (scope.Kind == GuildMemoryScopeKind.Global) continue;

            Guid id = scope.Id!.Value;

            if (scope.Kind == GuildMemoryScopeKind.Campaign)
            {
                Result live = await GuildStore.RequireOwnerWithinAsync(connection, transaction, null, id, ct).ConfigureAwait(false);

                if (live.IsFailure) return live.Error;

                continue;
            }

            await using SqliteCommand command = Command(connection, transaction,
                "SELECT CampaignId FROM session_campaign_bindings WHERE SessionId=$session AND BindingKindCode IN(1,2)");

            Add(command, "$session", Id(id));

            object? owner = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

            if (owner is null) return Forbidden;

            Guid? campaign = owner is string text && Guid.TryParse(text, out Guid parsed) ? parsed : null;

            Result valid = await ValidateOwnersAsync(connection, transaction, id, campaign, null, ct).ConfigureAwait(false);

            if (valid.IsFailure) return valid.Error;

            if (campaign is { } ownerId) sessions.Add((ownerId, id));
        }

        return new GuildGrantScopeEvidence(workspaces.ToImmutable(), sessions.ToImmutable());
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Id(Guid value) => value.ToString("D").ToUpperInvariant();
}
