using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<bool>> VerifyTaskGrantAsync(GuildChildToolAdmission admission, GuildRequestedGrant? requested, CancellationToken ct) =>
        authority.InQuestTransactionAsync<bool>(admission.Registry.Parent.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var allowed = await RequireLocalToolWithinAsync(connection, transaction, signets, admission.Registry,
                    admission.Tool, admission.Eligibility, token).ConfigureAwait(false);

                if (allowed.IsFailure) return allowed.Error;

                if (policy is not GuildLocalHostPolicy host) return GuildLocalToolRegistry.Unavailable;

                if (GuildTaskArguments.IsRemote(admission.Arguments) && requested is { Budget.IsSpecified: true, Budget.Value: { } budget }
                    && (budget.Usd.Mode != GuildBudgetMode.Unlimited || budget.Tokens.Mode != GuildBudgetMode.Unlimited))
                    return new Error(ErrorCodes.Guild.BudgetCapabilityUnavailable, "The remote transport cannot enforce finite requested spending authority.");

                var grant = await host.ValidateTaskGrantWithinAsync(connection, transaction,
                    allowed.Value.Quest.ResolvedGrant, requested, token).ConfigureAwait(false);

                return grant.IsFailure ? grant.Error : true;
            }, ct);

    internal async Task<Result<GuildQuestStored>> ReadTaskTargetAsync(GuildChildToolAdmission admission, Guid target,
        bool relationship, CancellationToken ct) => await authority.InQuestTransactionAsync<GuildQuestStored>(
        admission.Registry.Parent.Driver.Execution.RootQuestId, async (connection, transaction, signets, token) =>
        {
            Result allowed = await AuthorizeTaskToolWithinAsync(connection, transaction, signets, admission,
                target, relationship, token).ConfigureAwait(false);

            return allowed.IsFailure ? allowed.Error
                : await ReadTaskTargetWithinAsync(connection, transaction, admission, target, relationship, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    internal async Task<Result> AuthorizeTaskToolWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildChildToolAdmission admission, Guid target, bool relationship, CancellationToken ct)
    {
        if (!admission.Registry.Owns(admission) || !ReferenceEquals(admission.Registry.Parent.Store, this)) return InvalidReceipt;

        var live = await RequireLocalToolWithinAsync(connection, transaction, signets, admission.Registry,
            admission.Tool, admission.Eligibility, ct).ConfigureAwait(false);

        if (live.IsFailure) return live.Error;

        var visible = await ReadTaskTargetWithinAsync(connection, transaction, admission, target, relationship, ct).ConfigureAwait(false);

        return visible.IsFailure ? visible.Error : Result.Success();
    }

    private async Task<Result<GuildQuestStored>> ReadTaskTargetWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildChildToolAdmission admission, Guid target, bool relationship, CancellationToken ct)
    {
        var found = await ReadWithinAsync(connection, transaction, target, ct).ConfigureAwait(false);

        if (found.IsFailure) return TaskVisibilityError(found.Error);

        var issuer = await ReadWithinAsync(connection, transaction, admission.Registry.Parent.Driver.Execution.QuestId, ct).ConfigureAwait(false);

        if (issuer.IsFailure) return issuer.Error;

        GuildQuestDto parent = issuer.Value.Quest;

        if (found.Value.Retired || found.Value.Issuance.InstallationGeneration != policy.InstallationGeneration
            || found.Value.Quest.RootQuestId != parent.RootQuestId) return TaskNotFound;

        if (relationship && found.Value.Quest.ParentQuestId != parent.Id && parent.ParentQuestId != target) return TaskNotFound;

        if (relationship && parent.ParentQuestId == target)
        {
            Result owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, found.Value.Quest.SessionId,
                found.Value.Quest.CampaignId, ct).ConfigureAwait(false);

            return owner.IsFailure ? TaskVisibilityError(owner.Error) : found;
        }

        Result visible = await RequireVisibleAsync(connection, transaction, admission.Registry.Parent.Driver.Caller!, found.Value, ct).ConfigureAwait(false);

        return visible.IsFailure ? TaskVisibilityError(visible.Error) : found;
    }

    internal Task<Result<GuildAgentCardPageDto>> ListTaskAgentsAsync(GuildChildToolAdmission admission,
        GuildListAgentsQuery query, CancellationToken ct) => authority.InQuestTransactionAsync<GuildAgentCardPageDto>(
        admission.Registry.Parent.Driver.Execution.RootQuestId, async (connection, transaction, signets, token) =>
        {
            if (query.PageSize is < 1 or > 100 || query.Source is { } source && !Enum.IsDefined(source)) return GuildTaskArguments.Invalid;

            var allowed = await RequireLocalToolWithinAsync(connection, transaction, signets, admission.Registry,
                admission.Tool, admission.Eligibility, token).ConfigureAwait(false);

            if (allowed.IsFailure) return allowed.Error;

            GuildCallerContext caller = admission.Registry.Parent.Driver.Caller!;

            GuildQuestDto owner = allowed.Value.Quest;

            string callerKey = TreeCallerKey(caller);

            string scope = "agents:" + query.PageSize.ToString(CultureInfo.InvariantCulture) + ":"
                + query.Source?.ToString() + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(query.Skill ?? ""))) + ":";

            var epoch = await GuildSignetStore.EpochAsync(connection, transaction, token).ConfigureAwait(false);

            if (epoch.IsFailure) return epoch.Error;

            TreeCheckpoint? checkpoint = null;

            if (query.Cursor is { } cursor)
            {
                var saved = await ReadTreeCursorWithinAsync(connection, transaction, callerKey, scope, cursor, epoch.Value, token).ConfigureAwait(false);

                if (saved.IsFailure) return saved.Error;

                checkpoint = saved.Value;

                if (checkpoint.Root != owner.RootQuestId || checkpoint.OwnerKey != TreeOwnerKey(owner)) return TreeCursorGone;
            }

            string? root = null;

            if (owner.WorkspaceId is { } workspace)
            {
                await using SqliteCommand registered = Command(connection, transaction, "SELECT Path FROM Campaigns WHERE Id=$id");

                Add(registered, "$id", Id(workspace));

                root = await registered.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            }

            string? after = checkpoint is null ? null : checkpoint.After.ToString(CultureInfo.InvariantCulture) + ":" + Id(checkpoint.Last);

            var page = await guild.ListWithinAsync(connection, transaction,
                new(owner.WorkspaceId, owner.SessionId, owner.CampaignId, WorkspaceRoot: root), query.Source, query.Skill,
                after, query.PageSize, token, checkpoint?.Snapshot, admission.ResponseAllocationBytes).ConfigureAwait(false);

            if (page.IsFailure) return page.Error;

            string? next = null;

            if (page.Value.NextCardId is { } storageCursor)
            {
                string[] parts = storageCursor.Split(':');

                next = await SaveTreeCursorWithinAsync(connection, transaction, callerKey, TreeOwnerKey(owner), scope,
                    owner.RootQuestId, epoch.Value, page.Value.SnapshotSequence, long.Parse(parts[0], CultureInfo.InvariantCulture),
                    Guid.Parse(parts[1]), token).ConfigureAwait(false);
            }

            return new GuildAgentCardPageDto([.. page.Value.Items.Select(item =>
            {
                GuildAgentCard card = item.Payload.Card;

                GuildAgentSourceDto source = card.Source;

                bool available = source.Available && (source.Kind != GuildAgentSourceKind.Remote
                    || policy is GuildLocalHostPolicy host && item.Payload.DiscoveryEndpoint is { } endpoint
                        && host.IsConfiguredRemoteDestination(endpoint));

                return new GuildAgentCardDto(card.AgentId, card.CardId, card.Revision, card.Name, card.Description,
                    card.Version, card.Skills, card.DefaultInputModes, card.DefaultOutputModes, card.Capabilities,
                    source with { Available = available, ReasonCode = available ? null : source.ReasonCode ?? ErrorCodes.Guild.Unavailable });
            })], next, page.Value.SnapshotSequence);
        }, ct);

    internal Task<Result<GuildMessageReceipt>> PublishTaskMessageAsync(GuildChildToolAdmission admission,
        GuildQuestDto target, Guid mutation, string mimeType, string body, CancellationToken ct)
    {
        GuildCircleAddress address = new(target.ParentQuestId == admission.Registry.Parent.Driver.Execution.QuestId
            ? GuildCircleAddressKind.Child : GuildCircleAddressKind.Parent, target.Id, null, null);

        return new GuildPortalStore(this, authority, policy, time).PublishAsync(admission.Registry.Parent.Driver.Caller!,
            admission.Registry.Parent.Driver.Execution.QuestId,
            new(mutation, address, GuildEnvelopeKind.Progress, mimeType, body, null), ct, admission);
    }

    internal Task<Result<GuildContinueRequest>> ReadTaskContinuationAsync(GuildChildToolAdmission admission, GuildQuestDto target,
        Guid mutation, long expected, string mimeType, string body, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildContinueRequest>(admission.Registry.Parent.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result allowed = await AuthorizeTaskToolWithinAsync(connection, transaction, signets, admission, target.Id, true, token).ConfigureAwait(false);

                if (allowed.IsFailure) return allowed.Error;

                if (target.ParentQuestId != admission.Registry.Parent.Driver.Execution.QuestId) return TaskNotFound;

                Guid? continuation = target.AwaitingInput?.ContinuationId;

                await using SqliteCommand prior = Command(connection, transaction,
                    "SELECT ContinuationId FROM guild_continuations WHERE QuestId=$quest AND MutationId=$mutation");

                Add(prior, "$quest", Id(target.Id));

                Add(prior, "$mutation", Id(mutation));

                if (await prior.ExecuteScalarAsync(token).ConfigureAwait(false) is string saved && Guid.TryParseExact(saved, "D", out Guid existing))
                    continuation = existing;

                return continuation is { } id
                    ? new GuildContinueRequest(mutation, expected, id, mimeType, body)
                    : new Error(ErrorCodes.Guild.RevisionConflict, "The task has no current continuation request.");
            }, ct);

    private static Error TaskVisibilityError(Error error) => error.Code is ErrorCodes.Guild.Forbidden
        or ErrorCodes.Guild.OwnerUnavailable or ErrorCodes.Guild.NotFound ? TaskNotFound : error;

    private static Error TaskNotFound => new(ErrorCodes.Guild.NotFound, "The task is unavailable.");
}
