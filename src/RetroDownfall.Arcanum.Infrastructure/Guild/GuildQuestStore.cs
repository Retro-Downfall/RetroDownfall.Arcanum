using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Transactional Quest state, replay and admitted effect ownership for the local host.</summary>
internal sealed partial class GuildQuestStore(GuildStore guild, SignetAuthority authority,
    IGuildQuestHostPolicy policy, IGuildQuestAllocationParticipant allocations, TimeProvider time,
    IGuildLocalExecutionAdmission? localExecution = null, IGuildLocalHostReadiness? readiness = null)
{
    private Result RequireLocalExecutionReady() => readiness?.RequireExecutionReady() ?? GuildLocalHostReadiness.Unavailable;

    internal bool HasLocalExecutionAdmission => localExecution is not null;

    internal Task<Result<GuildQuestStored>> IssueAsync(GuildCallerContext caller, GuildIssuanceIdentity issuance,
        GuildIssueRootRequest request, CancellationToken ct) => IssueAsync(caller, issuance, request, null, null, ct);

    internal Task<Result<GuildQuestStored>> IssueAsync(GuildCallerContext caller, GuildIssuanceIdentity issuance,
        Guid parent, GuildIssueChildRequest request, CancellationToken ct) => IssueAsync(caller, issuance, null, parent, request, ct);

    internal Task<Result<GuildQuestStored>> IssueInboundAsync(GuildCallerContext caller, GuildIssuanceIdentity issuance,
        GuildIssueRootRequest request, GuildInboundIssue inbound, CancellationToken ct) =>
        IssueAsync(caller, issuance, request, null, null, ct, inbound: inbound);

    private Task<Result<GuildQuestStored>> IssueAsync(GuildCallerContext caller, GuildIssuanceIdentity issuance,
        GuildIssueRootRequest? root, Guid? parentId, GuildIssueChildRequest? child, CancellationToken ct,
        GuildChildToolAdmission? toolAdmission = null, GuildInboundIssue? inbound = null)
    {
        Result<string> digest = root is not null ? GuildQuestIntent.Compute(root)
            : toolAdmission is not null ? ToolIssueIntent(parentId.GetValueOrDefault(), child!)
            : GuildQuestIntent.Compute(parentId.GetValueOrDefault(), child!);

        if (digest.IsFailure) return Task.FromResult(Result<GuildQuestStored>.Failure(digest.Error));

        Guid key = root?.IdempotencyKey ?? child!.IdempotencyKey;

        if (caller is null || issuance is null || policy.InstallationGeneration == Guid.Empty
            || caller.InstallationGeneration != policy.InstallationGeneration || issuance.InstallationGeneration != policy.InstallationGeneration
            || key == Guid.Empty || issuance.LogicalCallId != key || issuance.IssuingStepId == Guid.Empty
            || (root is not null
                ? caller.Kind is not (GuildCallerKind.Operator or GuildCallerKind.Peer) || issuance.IssuerQuestId is not null || issuance.RootInvocationId != caller.InvocationId
                : caller.Kind != GuildCallerKind.Run || caller.Execution?.QuestId != parentId || issuance.IssuerQuestId != parentId
                    || issuance.RootInvocationId is not null || child!.ExpectedRevision < 1))
            return Task.FromResult(Result<GuildQuestStored>.Failure(OwnerUnavailable));

        Guid id = Guid.NewGuid();

        Guid rootId = caller.Execution?.RootQuestId ?? id;

        return authority.InQuestTransactionAsync<GuildQuestStored>(rootId, async (connection, transaction, signets, token) =>
        {
            Result authorized = await policy.AuthorizeLocateAsync(caller, connection, transaction, parentId, root?.SessionId, root?.CampaignId, token).ConfigureAwait(false);

            if (authorized.IsFailure) return authorized.Error;

            if (caller.Execution is { } execution)
            {
                Result<GuildGrant> live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

                if (live.IsFailure) return live.Error;
            }

            if (toolAdmission is not null)
            {
                if (root is not null || child is null) return InvalidReceipt;

                Result source = await ValidateToolIssueWithinAsync(connection, transaction, signets, caller,
                    digest.Value, toolAdmission, token).ConfigureAwait(false);

                if (source.IsFailure) return source.Error;
            }

            if (inbound is not null)
            {
                if (caller.Kind != GuildCallerKind.Peer || policy is not GuildLocalHostPolicy)
                    return OwnerUnavailable;

                var acceptedInbound = await GuildInboundTaskStore.ReadWithinAsync(connection, transaction,
                    inbound.Peer.PeerId, null, inbound.MessageId, token).ConfigureAwait(false);

                if (acceptedInbound.IsFailure) return acceptedInbound.Error;

                if (acceptedInbound.Value is { } receipt)
                {
                    var acceptedQuest = await ReadWithinAsync(connection, transaction, receipt.QuestId, token).ConfigureAwait(false);

                    if (acceptedQuest.IsFailure) return acceptedQuest.Error;

                    Result visible = await RequireVisibleAsync(connection, transaction, caller, acceptedQuest.Value, token).ConfigureAwait(false);

                    if (visible.IsFailure) return visible.Error;

                    return receipt.RequestDigest == inbound.RequestDigest && acceptedQuest.Value.IntentDigest == digest.Value
                        ? Replay(acceptedQuest.Value) : Conflict;
                }
            }

            Guid? existing = await FindIssueAsync(connection, transaction, issuance, token).ConfigureAwait(false);

            if (existing is { } acceptedId)
            {
                Result<GuildQuestStored> accepted = await ReadWithinAsync(connection, transaction, acceptedId, token).ConfigureAwait(false);

                if (accepted.IsFailure) return accepted.Error;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, accepted.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                if (accepted.Value.IntentDigest != digest.Value) return Conflict;

                if (toolAdmission is not null)
                {
                    Result mode = await ValidateToolChildWithinAsync(connection, transaction, accepted.Value, toolAdmission, token).ConfigureAwait(false);

                    if (mode.IsFailure) return mode.Error;
                }

                return Replay(accepted.Value);
            }

            GuildQuestStored? parent = null;

            if (parentId is { } issuer)
            {
                Result<GuildQuestStored> found = await ReadWithinAsync(connection, transaction, issuer, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                parent = found.Value;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, parent, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                if (parent.Quest.Revision != child!.ExpectedRevision) return RevisionConflict;

                if (parent.Quest.RootQuestId != rootId || await HasClosedAncestorAsync(connection, transaction, issuer, token).ConfigureAwait(false))
                    return new Error(ErrorCodes.Guild.Forbidden, "The issuing Quest is closed to new descendants.");

                if (toolAdmission is not null)
                {
                    Result<GuildIssueChildRequest> resolved = await ResolveToolBudgetWithinAsync(connection, transaction,
                        parent, child, token).ConfigureAwait(false);

                    if (resolved.IsFailure) return resolved.Error;

                    child = resolved.Value;
                }
            }

            Guid session = root?.SessionId ?? Guid.NewGuid();

            Guid? campaign = parent?.Quest.CampaignId ?? root?.CampaignId ?? root?.WorkspaceId;

            Guid? workspace = parent?.Quest.WorkspaceId ?? root?.WorkspaceId;

            if (root is { SessionId: { } suppliedSession, CampaignId: null })
            {
                await using SqliteCommand sessionOwner = Command(connection, transaction, "SELECT CampaignId FROM session_campaign_bindings WHERE SessionId=$session AND BindingKindCode IN(1,2)");

                Add(sessionOwner, "$session", Id(suppliedSession));

                campaign = await sessionOwner.ExecuteScalarAsync(token).ConfigureAwait(false) is string storedCampaign
                    ? Guid.Parse(storedCampaign) : null;
            }

            if (workspace is not null && campaign != workspace) return OwnerUnavailable;

            Result owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, root?.SessionId, campaign, token).ConfigureAwait(false);

            if (owner.IsFailure) return owner.Error;

            GuildMutationIdentity mutation = new(key, digest.Value);

            GuildQuestProposal proposal = new(id, Guid.NewGuid(), session, mutation, root, parentId, child, campaign, workspace);

            Result<GuildQuestAdmissionPlan> resolution = await policy.ResolveAsync(caller, proposal, connection, transaction, token).ConfigureAwait(false);

            if (resolution.IsFailure) return resolution.Error;

            GuildQuestAdmissionPlan plan = resolution.Value;

            using IDisposable? admissionScope = plan.Lifetime;

            if (!ReferenceEquals(plan.Admission.Caller, caller) || plan.Admission.Mutation != mutation
                || plan.Owners.SessionId != session || plan.Owners.CampaignId != campaign || plan.Owners.WorkspaceId != workspace
                || plan.OriginPrincipalId == Guid.Empty || GuildGrantPolicy.Validate(plan.Grant).IsFailure)
                return new Error(ErrorCodes.Guild.AuthorityMismatch, "Host resolution does not bind the proposed Quest.");

            if (root is not null)
            {
                Result<string> resolvedIntent = GuildQuestIntent.Compute(root with { Grant = plan.Grant });

                if (resolvedIntent.IsFailure || resolvedIntent.Value != digest.Value)
                {
                    return new Error(ErrorCodes.Guild.AuthorityMismatch, "Host resolution changed the explicit root grant.");
                }
            }

            DateTimeOffset now = time.GetUtcNow();

            if (root?.SessionId is null)
            {
                await using SqliteCommand create = Command(connection, transaction, "INSERT INTO Sessions(Id,CampaignId,CreatedAt,UpdatedAt) VALUES($id,$campaign,$now,$now)");

                Add(create, "$id", Id(session));

                Add(create, "$campaign", Id(campaign));

                Add(create, "$now", UtcInstantText.Format(now));

                _ = await create.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                create.CommandText = "INSERT INTO session_campaign_bindings(SessionId,BindingKindCode,CampaignId,BoundAtUtc) VALUES($id,$kind,$campaign,$now)";

                Add(create, "$kind", campaign is null ? 1 : 2);

                using CovenantSqliteAuthorizationScope bindingAuthorization = CovenantSqliteConnectionInitializer.Instance
                    .Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

                _ = await create.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            Result<(Guid AgentId, Guid CardId, long Revision)> destination = await ResolveDestinationAsync(connection, transaction,
                root?.AgentId ?? child?.AgentId, proposal, plan, token).ConfigureAwait(false);

            if (destination.IsFailure) return destination.Error;

            byte[] body = Encoding.UTF8.GetBytes(GuildQuestIntent.NormalizeGoal(root?.Goal ?? child!.Goal));

            Guid content = Guid.NewGuid();

            Result<GuildDataRef> goal = await GuildStore.WriteContentWithinAsync(connection, transaction,
                new(content, id, GuildContentKind.QuestGoal, 1, session, campaign, "text/plain", body, Sensitivity(content, session, campaign, body, plan.SourceLabel)), now, token).ConfigureAwait(false);

            if (goal.IsFailure) return goal.Error;

            Guid operationId = Guid.NewGuid();

            _ = await LongRunningOperationStore.CreateWithinAsync(connection, transaction, operationId,
                new(LongRunningOperationKinds.GuildQuest, LongRunningOperationRecoveryPolicy.ReconcileAndComplete, "Guild Quest", now,
                    await RootOperationIdAsync(connection, transaction, parent, token).ConfigureAwait(false), parent?.Quest.OperationId, session, proposal.RunId),
                1, GuildQuestCheckpoint.Encode(id), Id(id), token).ConfigureAwait(false);

            GuildSignetBinding binding = new(Guid.NewGuid(), destination.Value.AgentId, proposal.RunId, id, rootId,
                caller.Execution?.SignetId, plan.Grant, now, plan.OriginPrincipalId);

            Result<GuildSignetRecord> signet = await signets.IssueAsync(plan.Admission, binding, caller.Execution,
                child?.RequestedGrant ?? (child is null ? null : new()), token).ConfigureAwait(false);

            if (signet.IsFailure) return signet.Error;

            Result<Guid> account = await allocations.AllocateAsync(connection, transaction, id, parent?.AllocationAccountId, plan.Grant, token).ConfigureAwait(false);

            if (account.IsFailure || account.Value == Guid.Empty) return account.IsFailure ? account.Error : Unavailable;

            await InsertAsync(connection, transaction, proposal, issuance, destination.Value, goal.Value, binding, operationId, account.Value,
                root?.Name ?? child?.Name, now, token).ConfigureAwait(false);

            Result<long> projected = await GuildPortalEvents.RecordSourceWithinAsync(connection, transaction, rootId, id,
                GuildReceiptSourceKind.QuestIssue, id, 1, now, token).ConfigureAwait(false);

            if (projected.IsFailure) return projected.Error;

            Result<GuildQuestStored> created = await ReadWithinAsync(connection, transaction, id, token).ConfigureAwait(false);

            if (created.IsFailure) return created;

            if (inbound is not null)
            {
                Result bound = await GuildInboundTaskStore.CreateWithinAsync(connection, transaction,
                    (GuildLocalHostPolicy)policy, created.Value, inbound, token).ConfigureAwait(false);

                if (bound.IsFailure) return bound.Error;
            }

            if (plan.RemoteTransport is { } remoteTransport)
            {
                var card = await GuildStore.ResolveQuestCardWithinAsync(connection, transaction, destination.Value.AgentId,
                    destination.Value.CardId, destination.Value.Revision, new(workspace, session, campaign), token).ConfigureAwait(false);

                if (card.IsFailure) return card.Error;

                if (card.Value.Payload.RemoteTransport is null || card.Value.Payload.DiscoveryEndpoint is not { } discovery
                    || card.Value.Payload.RemoteTransport.Endpoint != remoteTransport.Endpoint
                    || card.Value.Payload.Card.Source.Kind != GuildAgentSourceKind.Remote) return Unavailable;

                GuildRemoteInvocationOptions? options = root?.RemoteOptions ?? child?.RemoteOptions;

                var modes = options is { AcceptedOutputModes.IsDefaultOrEmpty: false } ? options.AcceptedOutputModes
                    : policy is GuildLocalHostPolicy host ? host.RemoteOutputModes : System.Collections.Immutable.ImmutableArray.Create("text/plain");

                var remote = await GuildRemoteExecutionStore.CreateWithinAsync(connection, transaction, created.Value,
                    new(1, discovery, remoteTransport, (RetroDownfall.Arcanum.Infrastructure.A2A.A2ADispatchMode)(options?.Mode ?? GuildRemoteDispatchMode.Blocking),
                        modes, options?.SkillId, toolAdmission?.Registry.AcceptedInput.DelegationChain ?? []), plan.SourceLabel, now, token).ConfigureAwait(false);

                return remote.IsFailure ? remote.Error : created;
            }

            if (root?.RemoteOptions is not null || child?.RemoteOptions is not null)
                return new Error(ErrorCodes.Guild.InvalidRequest, "Remote invocation options require a remote destination.");

            if (localExecution is null) return toolAdmission is null ? created : GuildLocalToolRegistry.Unavailable;

            Result<GuildLocalExecutionInput> input = toolAdmission is null
                ? await localExecution.ResolveWithinAsync(caller, created.Value, connection, transaction, token).ConfigureAwait(false)
                : await localExecution.ResolveToolWithinAsync(caller, created.Value, connection, transaction, toolAdmission, token).ConfigureAwait(false);

            if (input.IsFailure) return input.Error;

            if (inbound?.DelegationChain is { } inboundChain)
                input = input.Value with { DelegationChain = inboundChain };

            Result<GuildLocalExecutionBinding> local = await GuildLocalExecutionStore.CreateWithinAsync(
                connection, transaction, created.Value, input.Value, plan.SourceLabel, now, token).ConfigureAwait(false);

            if (local.IsFailure) return local.Error;

            if (toolAdmission is not null)
            {
                Result mode = await ValidateToolChildWithinAsync(connection, transaction, created.Value, toolAdmission, token).ConfigureAwait(false);

                if (mode.IsFailure) return mode.Error;
            }

            return created;
        }, ct);
    }

    private async Task<Result<(Guid AgentId, Guid CardId, long Revision)>> ResolveDestinationAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid? selectedAgent, GuildQuestProposal proposal, GuildQuestAdmissionPlan plan, CancellationToken ct)
    {
        if (selectedAgent is null)
        {
            if (plan.GeneratedCard is not { } template || template.Card.Source.Kind != GuildAgentSourceKind.Derived) return Unavailable;

            Result<GuildStoredCard> created = await guild.UpsertWithinAsync(connection, transaction,
                new("quest:" + Id(proposal.QuestId), null, proposal.SessionId, proposal.CampaignId, template,
                    proposal.Mutation.CanonicalRequestDigest, Label: plan.SourceLabel), ct).ConfigureAwait(false);

            return created.IsFailure ? created.Error : (created.Value.Payload.Card.AgentId, created.Value.Payload.Card.CardId, created.Value.Payload.Card.Revision);
        }

        return await GuildStore.SelectQuestCardWithinAsync(connection, transaction, selectedAgent.Value,
            new(proposal.WorkspaceId, proposal.SessionId, proposal.CampaignId), ct).ConfigureAwait(false);
    }

    private static async Task<Guid?> RootOperationIdAsync(SqliteConnection connection, SqliteTransaction transaction, GuildQuestStored? parent, CancellationToken ct)
    {
        if (parent is null) return null;

        await using SqliteCommand command = Command(connection, transaction, "SELECT OperationId FROM guild_quests WHERE Id=$id");

        Add(command, "$id", Id(parent.Quest.RootQuestId));

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string id ? Guid.Parse(id) : null;
    }

    private static DerivedArtifactWrite Sensitivity(Guid content, Guid? session, Guid? campaign, byte[] body, ArtifactSensitivityLabel? source) =>
        new(SensitiveArtifactKind.GuildContent, content, session, campaign, source?.TurnId, 1, new CovenantDigest(SHA256.HashData(body)),
            source?.Sensitivity ?? ContentSensitivity.None, source?.Provenance ?? GenerationProvenance.CreateExact([]),
            source?.ProducingPlanDigest, source?.ProducingAdmissionDigest, source?.ProducingMaintenanceReceiptDigest);

    private static async Task<Guid?> FindIssueAsync(SqliteConnection connection, SqliteTransaction transaction, GuildIssuanceIdentity issuance, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT Id FROM guild_quests WHERE InstallationGeneration=$generation AND IssuerKindCode=$kind
              AND IssuerIdentity=$issuer AND IssuingStepId=$step AND LogicalCallId=$call
            """);

        BindIdentity(command, issuance);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string id ? Guid.Parse(id) : null;
    }

    private static async Task<bool> HasClosedAncestorAsync(SqliteConnection connection, SqliteTransaction transaction, Guid parent, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            WITH RECURSIVE lineage(Id,ParentQuestId,CancellationRequested,Retired,OperationId) AS (
                SELECT Id,ParentQuestId,CancellationRequested,Retired,OperationId FROM guild_quests WHERE Id=$id
                UNION SELECT c.Id,c.ParentQuestId,c.CancellationRequested,c.Retired,c.OperationId FROM guild_quests c JOIN lineage p ON c.Id=p.ParentQuestId)
            SELECT EXISTS(SELECT 1 FROM lineage c JOIN LongRunningOperations o ON o.Id=c.OperationId
                WHERE c.CancellationRequested=1 OR c.Retired=1 OR o.State IN(3,4,5,6))
            """);

        Add(command, "$id", Id(parent));

        return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, GuildQuestProposal proposal,
        GuildIssuanceIdentity issuance, (Guid AgentId, Guid CardId, long Revision) card, GuildDataRef goal, GuildSignetBinding binding,
        Guid operationId, Guid accountId, string? name, DateTimeOffset now, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO guild_quests(Id,OperationId,InstallationGeneration,IssuerKindCode,IssuerIdentity,IssuingStepId,LogicalCallId,
              MutationId,IntentDigest,ResolutionDigest,RootQuestId,ParentQuestId,AgentId,CardId,CardRevision,RunId,SignetId,Name,
              SessionId,CampaignId,WorkspaceId,GoalContentId,GoalRevision,GoalDigest,GoalMimeType,GoalByteLength,AllocationAccountId,
              CreatedAtUtc,UpdatedAtUtc,IssuedSequence,Revision,LastNonterminalStateCode,CancellationRequested,CancellationConverged,AccountingStateCode,Retired)
            VALUES($id,$operation,$generation,$kind,$issuer,$step,$call,$mutation,$intent,$resolution,$root,$parent,$agent,$card,$cardRevision,$run,$signet,$name,
              $session,$campaign,$workspace,$goal,1,$goalDigest,$mime,$bytes,$account,$now,$now,(SELECT COALESCE(MAX(IssuedSequence),0)+1 FROM guild_quests),1,0,0,0,1,0);
            INSERT INTO guild_quest_start_intents(QuestId,CreatedAtUtc) VALUES($id,$now);
            """);

        BindIdentity(command, issuance);

        Add(command, "$id", Id(proposal.QuestId));

        Add(command, "$operation", operationId.ToString("N"));

        Add(command, "$mutation", Id(proposal.Mutation.IdempotencyKey));

        Add(command, "$intent", Convert.FromHexString(proposal.Mutation.CanonicalRequestDigest));
        using MemoryStream facts = new();
        using (Utf8JsonWriter writer = new(facts))
        {
            writer.WriteStartObject();

            writer.WriteString("operationId", operationId);

            writer.WriteString("cardId", card.CardId);

            writer.WriteNumber("cardRevision", card.Revision);

            writer.WriteString("sessionId", proposal.SessionId);

            writer.WriteString("campaignId", Id(proposal.CampaignId));

            writer.WriteString("workspaceId", Id(proposal.WorkspaceId));

            writer.WriteString("accountId", accountId);

            writer.WriteString("goalDigest", goal.Digest);

            writer.WritePropertyName("signetBinding");

            JsonSerializer.Serialize(writer, binding, GuildJsonContext.Default.GuildSignetBinding);

            writer.WriteEndObject();
        }

        Add(command, "$resolution", SHA256.HashData(facts.ToArray()));

        Add(command, "$root", Id(binding.RootQuestId));

        Add(command, "$parent", Id(proposal.ParentQuestId));

        Add(command, "$agent", Id(card.AgentId));

        Add(command, "$card", Id(card.CardId));

        Add(command, "$cardRevision", card.Revision);

        Add(command, "$run", Id(proposal.RunId));

        Add(command, "$signet", Id(binding.SignetId));

        Add(command, "$name", name);

        Add(command, "$session", Id(proposal.SessionId));

        Add(command, "$campaign", Id(proposal.CampaignId));

        Add(command, "$workspace", Id(proposal.WorkspaceId));

        Add(command, "$goal", Id(goal.ContentId));

        Add(command, "$goalDigest", Convert.FromHexString(goal.Digest));

        Add(command, "$mime", goal.MimeType);

        Add(command, "$bytes", goal.ByteLength);

        Add(command, "$account", Id(accountId));

        Add(command, "$now", UtcInstantText.Format(now));

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void BindIdentity(SqliteCommand command, GuildIssuanceIdentity issuance)
    {
        Add(command, "$generation", Id(issuance.InstallationGeneration));

        Add(command, "$kind", issuance.IssuerQuestId is null ? 0 : 1);

        Add(command, "$issuer", Id(issuance.IssuerQuestId ?? issuance.RootInvocationId));

        Add(command, "$step", Id(issuance.IssuingStepId));

        Add(command, "$call", Id(issuance.LogicalCallId));
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

    private static string? Id(Guid? value) => value is { } id ? Id(id) : null;

    private static GuildQuestStored Replay(GuildQuestStored row) => row with { Quest = row.Quest with { Receipt = row.Quest.Receipt! with { Replayed = true } } };

    private static Error Conflict => new(ErrorCodes.Guild.IdempotencyConflict, "The logical Quest identity records different caller intent.");

    private static Error RevisionConflict => new(ErrorCodes.Guild.RevisionConflict, "The Quest or operation revision changed.");

    private static Error OwnerUnavailable => new(ErrorCodes.Guild.OwnerUnavailable, "The Quest owner or installation generation is unavailable.");

    private static Error Unavailable => new(ErrorCodes.Guild.Unavailable, "The Quest facts are unavailable or inconsistent.");
}
