using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<Guid?>> ReadToolRemoteTargetAsync(GuildChildToolAdmission admission, string destination, CancellationToken ct) =>
        authority.InQuestTransactionAsync<Guid?>(admission.Registry.Parent.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var allowed = await RequireRemoteToolWithinAsync(connection, transaction, signets, admission, destination, token).ConfigureAwait(false);

                if (allowed.IsFailure) return allowed.Error;

                GuildIssuanceIdentity identity = new(policy.InstallationGeneration, allowed.Value.Quest.Id, null,
                    admission.IssuingStepId, admission.LogicalCallId);

                Guid? existing = await FindIssueAsync(connection, transaction, identity, token).ConfigureAwait(false);

                if (existing is not { } id) return Result<Guid?>.Success(null);

                var child = await RequireRemoteToolChildWithinAsync(connection, transaction, admission, id, destination, token).ConfigureAwait(false);

                return child.IsFailure ? child.Error : Result<Guid?>.Success(child.Value.Quest.AgentId);
            }, ct);

    internal Task<Result<GuildQuestStored>> ReadToolRemoteChildAsync(GuildChildToolAdmission admission,
        Guid childId, string destination, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildQuestStored>(admission.Registry.Parent.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var allowed = await RequireRemoteToolWithinAsync(connection, transaction, signets, admission, destination, token).ConfigureAwait(false);

                return allowed.IsFailure ? allowed.Error
                    : await RequireRemoteToolChildWithinAsync(connection, transaction, admission, childId, destination, token).ConfigureAwait(false);
            }, ct);

    internal Task<Result<GuildContinueRequest?>> ReadToolContinuationRequestAsync(GuildChildToolAdmission admission,
        GuildQuestStored child, string body, GuildRemoteInvocationOptions options, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildContinueRequest?>(admission.Registry.Parent.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                string destination = admission.Arguments.GetProperty("agent_url").GetString()!;

                var allowed = await RequireRemoteToolWithinAsync(connection, transaction, signets, admission, destination, token).ConfigureAwait(false);

                if (allowed.IsFailure) return allowed.Error;

                var current = await RequireRemoteToolChildWithinAsync(connection, transaction, admission, child.Quest.Id, destination, token).ConfigureAwait(false);

                if (current.IsFailure) return current.Error;

                await using SqliteCommand prior = Command(connection, transaction, """
                    SELECT CASE WHEN typeof(ContinuationId)='text' AND length(CAST(ContinuationId AS BLOB))=36 THEN ContinuationId END,
                        CASE WHEN typeof(AcceptedRevision)='integer' AND AcceptedRevision BETWEEN 2 AND $revision THEN AcceptedRevision END
                    FROM guild_continuations WHERE QuestId=$quest AND MutationId=$mutation
                    """);

                Add(prior, "$quest", Id(child.Quest.Id));

                Add(prior, "$mutation", Id(admission.LogicalCallId));

                Add(prior, "$revision", current.Value.Quest.Revision);

                await using SqliteDataReader reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (!await reader.ReadAsync(token).ConfigureAwait(false)) return Result<GuildContinueRequest?>.Success(null);

                if (reader.IsDBNull(0) || reader.IsDBNull(1) || !Guid.TryParseExact(reader.GetString(0), "D", out Guid continuation)
                    || continuation == Guid.Empty) return Unavailable;

                return Result<GuildContinueRequest?>.Success(new(admission.LogicalCallId, reader.GetInt64(1) - 1,
                    continuation, "text/plain", body, options));
            }, ct);

    private async Task<Result<GuildQuestStored>> RequireRemoteToolWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, SignetAuthority.QuestTransaction signets, GuildChildToolAdmission admission,
        string destination, CancellationToken ct)
    {
        if (admission.Kind is not (GuildLocalChildToolKind.DispatchSending or GuildLocalChildToolKind.ContinueSending)
            || !admission.Registry.Owns(admission) || !ReferenceEquals(admission.Registry.Parent.Store, this)
            || policy is not GuildLocalHostPolicy host || !host.IsConfiguredRemoteDestination(destination))
            return new Error(ErrorCodes.Guild.DestinationChanged, "The Sending requires its current configured destination.");

        return await RequireLocalToolWithinAsync(connection, transaction, signets, admission.Registry,
            admission.Tool, admission.Eligibility, ct).ConfigureAwait(false);
    }

    private async Task<Result<GuildQuestStored>> RequireRemoteToolChildWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildChildToolAdmission admission, Guid childId, string destination, CancellationToken ct)
    {
        var child = await ReadWithinAsync(connection, transaction, childId, ct).ConfigureAwait(false);

        if (child.IsFailure) return child.Error;

        GuildQuestStored row = child.Value;

        GuildExecutionHandle execution = admission.Registry.Parent.Driver.Execution;

        if (row.Quest.ParentQuestId != execution.QuestId || row.Quest.RootQuestId != execution.RootQuestId
            || row.Issuance.InstallationGeneration != policy.InstallationGeneration)
            return new Error(ErrorCodes.Guild.Forbidden, "Only the current parent may continue its exact Sending.");

        Result visible = await RequireVisibleAsync(connection, transaction, admission.Registry.Parent.Driver.Caller!, row, ct).ConfigureAwait(false);

        if (visible.IsFailure) return visible.Error;

        var target = await GuildRemoteExecutionStore.ReadRecoveryWithinAsync(connection, transaction, childId, ct).ConfigureAwait(false);

        if (target.IsFailure) return target.Error;

        return Uri.TryCreate(destination.Trim(), UriKind.Absolute, out Uri? selected)
            && string.Equals(selected.AbsoluteUri, target.Value.DiscoveryEndpoint, StringComparison.Ordinal)
            ? row : new Error(ErrorCodes.Guild.DestinationChanged, "The Sending destination differs from the accepted remote child.");
    }

    private async Task<Result> ValidateToolContinuationWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, GuildQuestStored child,
        GuildChildToolAdmission admission, string intent, CancellationToken ct)
    {
        if (admission.Kind != GuildLocalChildToolKind.ContinueSending || !admission.Registry.Owns(admission)
            || !ReferenceEquals(admission.Registry.Parent.Driver.Caller, caller) || !admission.MatchesIntent(intent)) return InvalidReceipt;

        string? destination = admission.Arguments.GetProperty("agent_url").GetString();

        if (string.IsNullOrWhiteSpace(destination)) return InvalidReceipt;

        var allowed = await RequireRemoteToolWithinAsync(connection, transaction, signets, admission, destination, ct).ConfigureAwait(false);

        if (allowed.IsFailure) return allowed.Error;

        var exactChild = await RequireRemoteToolChildWithinAsync(connection, transaction, admission, child.Quest.Id, destination, ct).ConfigureAwait(false);

        return exactChild.IsFailure ? exactChild.Error : Result.Success();
    }
}
