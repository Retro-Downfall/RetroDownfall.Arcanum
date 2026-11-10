using System.Collections.Immutable;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ConclaveArchmage
{
    public bool SupportsRemote => remoteResolver is not null && settings.CurrentValue.Features.Guild
        && settings.CurrentValue.ResolveConclave().Enabled && settings.CurrentValue.ResolveA2A().ClientEnabled;

    private async Task<Result<GuildLocalChildToolResult>> DispatchRemoteToolAsync(GuildLocalChildToolCall call, CancellationToken ct)
    {
        if (!SupportsRemote) return UnavailableGuild();

        GuildChildToolAdmission admission = call.Admission;

        GuildCallerContext caller = admission.Registry.Parent.Driver.Caller!;

        GuildQuestDto child;

        GuildRemoteDispatchMode mode;

        if (call.Kind == GuildLocalChildToolKind.ContinueSending)
        {
            var parsed = RemoteContinuation(call);

            if (parsed.IsFailure) return parsed.Error;

            var target = await quests!.ReadToolRemoteChildAsync(admission, parsed.Value.QuestId,
                parsed.Value.Destination, ct).ConfigureAwait(false);

            if (target.IsFailure) return target.Error;

            var prior = await quests.ReadToolContinuationRequestAsync(admission, target.Value,
                parsed.Value.Body, parsed.Value.Options, ct).ConfigureAwait(false);

            if (prior.IsFailure) return prior.Error;

            if (prior.Value is null && target.Value.Quest.AwaitingInput is null) return InvalidRemoteTool;

            GuildContinueRequest request = prior.Value ?? new(admission.LogicalCallId, target.Value.Quest.Revision,
                target.Value.Quest.AwaitingInput!.ContinuationId, "text/plain", parsed.Value.Body, parsed.Value.Options);

            var intent = GuildQuestIntent.Compute(target.Value.Quest.Id, request);

            if (intent.IsFailure) return intent.Error;

            if (!admission.BindIntent(intent.Value)) return new Error(ErrorCodes.Guild.IdempotencyConflict, "This tool call already accepted different input.");

            var accepted = await quests.AcceptContinuationAsync(caller, target.Value.Quest.Id, request, ct,
                toolAdmission: admission).ConfigureAwait(false);

            if (accepted.IsFailure) return accepted.Error;

            child = accepted.Value.Quest;

            admission.RecordCommitted(child);

            mode = parsed.Value.Options.Mode;
        }
        else
        {
            var parsed = RemoteIssue(call);

            if (parsed.IsFailure) return parsed.Error;

            var prior = await quests!.ReadToolRemoteTargetAsync(admission, parsed.Value.Destination, ct).ConfigureAwait(false);

            if (prior.IsFailure) return prior.Error;

            Guid agentId;

            if (prior.Value is { } acceptedAgent) agentId = acceptedAgent;
            else
            {
                var resolved = await remoteResolver!.ResolveAsync(caller, parsed.Value.Destination, ct).ConfigureAwait(false);

                if (resolved.IsFailure) return resolved.Error;

                agentId = resolved.Value.AgentId;
            }

            GuildIssueChildRequest request = parsed.Value.Request with { AgentId = agentId };

            GuildIssuanceIdentity issuance = new(caller.InstallationGeneration, call.ParentQuest.Id, null,
                admission.IssuingStepId, admission.LogicalCallId);

            var accepted = await quests.IssueToolChildAsync(admission, issuance, request, ct).ConfigureAwait(false);

            if (accepted.IsFailure) return accepted.Error;

            child = accepted.Value.Quest;

            mode = request.RemoteOptions!.Mode;
        }

        // All three remote modes await an outcome. Callback mode releases the transport's slot;
        // a continuable wait alone may return an actionable question before terminal completion.
        await using IAsyncDisposable capacity = await admission.Registry.Parent.SuspendForChildAsync(ct).ConfigureAwait(false);

        guildScheduler!.Schedule(child);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var observed = await quests!.ReadLocalResultAsync(caller, child.Id, true, ct).ConfigureAwait(false);

            if (observed.IsFailure) return observed.Error;

            if (observed.Value.Receipt is { } receipt)
            {
                var parent = await quests.GetAsync(caller, call.ParentQuest.Id, ct).ConfigureAwait(false);

                if (parent.IsFailure) return parent.Error;

                var applied = await quests.ApplyResultAsync(caller,
                    new(new(receipt.ResultId, receipt.CanonicalContentDigest), parent.Value.Quest.Id, receipt.QuestId,
                        receipt.ResultId, receipt.CanonicalContentDigest, parent.Value.Quest.Revision), ct).ConfigureAwait(false);

                if (applied.IsFailure)
                {
                    if (applied.Error.Code == ErrorCodes.Guild.RevisionConflict) continue;

                    return applied.Error;
                }

                return new GuildLocalChildToolResult(observed.Value.Quest,
                    observed.Value.Body ?? $"Sending {receipt.Outcome}: {receipt.ReasonCode}");
            }

            if (mode == GuildRemoteDispatchMode.Continuable && observed.Value.Quest.State == QuestState.InputRequired)
            {
                var question = await quests.ReadLocalQuestionAsync(caller, child.Id, ct).ConfigureAwait(false);

                if (question.IsFailure) return question.Error;

                return new GuildLocalChildToolResult(observed.Value.Quest,
                    $"Sending requires input. Use task_id {child.Id:D} with continue_sending.\n{question.Value.Question}");
            }

            if (observed.Value.Quest.ReconciliationRequired)
                return new Error(ErrorCodes.Guild.ReconciliationRequired, "The existing Sending requires reconciliation; its work will not be repeated.");

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }
    }

    private static Result<(GuildIssueChildRequest Request, string Destination)> RemoteIssue(GuildLocalChildToolCall call)
    {
        JsonElement arguments = call.Admission.Arguments;

        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any(property =>
            property.Name is not ("goal" or "agent_url" or "name" or "continuable" or "callback" or "skill_id" or "accepted_output_modes")))
            return InvalidRemoteTool;

        try
        {
            DispatchSendingParams? parsed = JsonSerializer.Deserialize(arguments, McpJsonSerializerContext.Default.DispatchSendingParams);

            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Goal) || string.IsNullOrWhiteSpace(parsed.AgentUrl)) return InvalidRemoteTool;

            GuildRemoteInvocationOptions options = new(parsed.Callback == true ? GuildRemoteDispatchMode.Callback
                : parsed.Continuable == true ? GuildRemoteDispatchMode.Continuable : GuildRemoteDispatchMode.Blocking,
                parsed.AcceptedOutputModes is null ? default : parsed.AcceptedOutputModes.ToImmutableArray(), parsed.SkillId);

            GuildIssueChildRequest request = new(call.Admission.LogicalCallId, call.ParentQuest.Revision, null,
                parsed.Name, parsed.Goal.Trim(), new(Budget: new(true, call.ParentQuest.ResolvedGrant.Budget)), options);

            var valid = GuildQuestIntent.Compute(call.ParentQuest.Id, request);

            return valid.IsFailure ? valid.Error : (request, parsed.AgentUrl.Trim());
        }
        catch (JsonException)
        {
            return InvalidRemoteTool;
        }
    }

    private static Result<(Guid QuestId, string Destination, string Body, GuildRemoteInvocationOptions Options)> RemoteContinuation(GuildLocalChildToolCall call)
    {
        JsonElement arguments = call.Admission.Arguments;

        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any(property =>
            property.Name is not ("task_id" or "agent_url" or "message" or "continuable" or "skill_id" or "accepted_output_modes")))
            return InvalidRemoteTool;

        try
        {
            ContinueSendingParams? parsed = JsonSerializer.Deserialize(arguments, McpJsonSerializerContext.Default.ContinueSendingParams);

            if (parsed is null || !Guid.TryParse(parsed.TaskId, out Guid questId) || questId == Guid.Empty
                || string.IsNullOrWhiteSpace(parsed.AgentUrl) || string.IsNullOrWhiteSpace(parsed.Message)) return InvalidRemoteTool;

            GuildRemoteInvocationOptions options = new(parsed.Continuable == true ? GuildRemoteDispatchMode.Continuable : GuildRemoteDispatchMode.Blocking,
                parsed.AcceptedOutputModes is null ? default : parsed.AcceptedOutputModes.ToImmutableArray(), parsed.SkillId);

            return (questId, parsed.AgentUrl.Trim(), parsed.Message, options);
        }
        catch (JsonException)
        {
            return InvalidRemoteTool;
        }
    }

    private static Error InvalidRemoteTool => new(ErrorCodes.Guild.InvalidRequest,
        "The remote Sending requires a configured destination and supported explicit arguments.");
}
