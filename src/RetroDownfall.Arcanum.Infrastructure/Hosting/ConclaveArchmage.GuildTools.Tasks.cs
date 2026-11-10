using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ConclaveArchmage
{
    private async Task<Result<GuildLocalChildToolResult>> DispatchTaskManagementAsync(GuildLocalChildToolCall call, CancellationToken ct)
    {
        JsonElement arguments = call.Arguments;

        GuildChildToolAdmission admission = call.Admission;

        GuildCallerContext caller = admission.Registry.Parent.Driver.Caller!;

        if (call.Kind == GuildLocalChildToolKind.ListAgents)
        {
            if (!GuildTaskArguments.Closed(arguments, "source", "skill", "cursor", "pageSize")
                || !GuildTaskArguments.OptionalText(arguments, "skill", out string? skill)
                || !GuildTaskArguments.OptionalText(arguments, "cursor", out string? cursor)) return GuildTaskArguments.Invalid;

            GuildAgentSourceKind? source = null;

            if (arguments.TryGetProperty("source", out JsonElement selected))
            {
                try { source = selected.Deserialize(GuildJsonContext.Default.GuildAgentSourceKind); }
                catch (JsonException) { return GuildTaskArguments.Invalid; }
            }

            int size = 25;

            if (arguments.TryGetProperty("pageSize", out JsonElement pageSize)
                && (pageSize.ValueKind != JsonValueKind.Number || !pageSize.TryGetInt32(out size))) return GuildTaskArguments.Invalid;

            var page = await quests!.ListTaskAgentsAsync(admission, new(source, skill, cursor, size), ct).ConfigureAwait(false);

            if (page.IsFailure) return page.Error;

            admission.RecordCommitted(call.ParentQuest);

            return new GuildLocalChildToolResult(call.ParentQuest,
                Projection: JsonSerializer.SerializeToElement(page.Value, GuildJsonContext.Default.GuildAgentCardPageDto));
        }

        if (!GuildTaskArguments.Identity(arguments, "questId", out Guid questId)) return GuildTaskArguments.Invalid;

        bool relationship = call.Kind == GuildLocalChildToolKind.SendAgentMessage;

        var target = await quests!.ReadTaskTargetAsync(admission, questId, relationship, ct).ConfigureAwait(false);

        if (target.IsFailure) return target.Error;

        if (call.Kind == GuildLocalChildToolKind.GetAgentTask)
        {
            if (!GuildTaskArguments.Closed(arguments, "questId")) return GuildTaskArguments.Invalid;

            admission.RecordCommitted(target.Value.Quest);

            return new GuildLocalChildToolResult(target.Value.Quest,
                Projection: JsonSerializer.SerializeToElement(target.Value.Quest, GuildJsonContext.Default.GuildQuestDto));
        }

        var mutation = GuildTaskArguments.Mutation(arguments, admission.LogicalCallId);

        if (mutation.IsFailure) return mutation.Error;

        if (call.Kind == GuildLocalChildToolKind.CancelAgentTask)
        {
            if (!GuildTaskArguments.Closed(arguments, "questId", "expectedRevision", "idempotencyKey")
                || !arguments.TryGetProperty("expectedRevision", out JsonElement revision)
                || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out long expected) || expected < 1) return GuildTaskArguments.Invalid;

            GuildCancelQuestRequest request = new(mutation.Value, expected, GuildCancellationReason.OperatorRequested);

            var intent = GuildQuestIntent.Compute(questId, request);

            if (intent.IsFailure) return intent.Error;

            var canceled = await quests.RequestTaskCancellationAsync(admission, new(mutation.Value, intent.Value), questId, request, ct).ConfigureAwait(false);

            if (canceled.IsFailure) return canceled.Error;

            guildScheduler!.Schedule(target.Value.Quest);

            admission.RecordCommitted(target.Value.Quest);

            return new GuildLocalChildToolResult(target.Value.Quest,
                Projection: JsonSerializer.SerializeToElement(canceled.Value, GuildJsonContext.Default.GuildCancellationDto));
        }

        if (call.Kind != GuildLocalChildToolKind.SendAgentMessage
            || !GuildTaskArguments.Closed(arguments, "questId", "kind", "contentType", "body", "idempotencyKey", "expectedRevision")
            || !GuildTaskArguments.Text(arguments, "kind", out string? kind) || kind is not ("message" or "continuation")
            || !GuildTaskArguments.Text(arguments, "contentType", out string? contentType) || contentType is not ("text/plain" or "application/json")
            || !GuildTaskArguments.Text(arguments, "body", out string? body) || body is null) return GuildTaskArguments.Invalid;

        if (kind == "message")
        {
            if (arguments.TryGetProperty("expectedRevision", out _)) return GuildTaskArguments.Invalid;

            var receipt = await quests.PublishTaskMessageAsync(admission, target.Value.Quest,
                mutation.Value, contentType, body, ct).ConfigureAwait(false);

            if (receipt.IsFailure) return receipt.Error;

            admission.RecordCommitted(target.Value.Quest);

            return new GuildLocalChildToolResult(target.Value.Quest,
                Projection: JsonSerializer.SerializeToElement(receipt.Value, GuildJsonContext.Default.GuildMessageReceipt));
        }

        if (!arguments.TryGetProperty("expectedRevision", out JsonElement expectedRevision)
            || expectedRevision.ValueKind != JsonValueKind.Number || !expectedRevision.TryGetInt64(out long expectedContinuation)
            || expectedContinuation < 1) return GuildTaskArguments.Invalid;

        var continuing = await quests.ReadTaskContinuationAsync(admission, target.Value.Quest, mutation.Value,
            expectedContinuation, contentType, body, ct).ConfigureAwait(false);

        if (continuing.IsFailure) return continuing.Error;

        var canonical = GuildQuestIntent.Compute(questId, continuing.Value);

        if (canonical.IsFailure) return canonical.Error;

        if (!admission.BindIntent(canonical.Value)) return new Error(ErrorCodes.Guild.IdempotencyConflict, "This tool call binds different continuation intent.");

        var resumed = await quests.AcceptContinuationAsync(caller, questId, continuing.Value, ct, toolAdmission: admission).ConfigureAwait(false);

        if (resumed.IsFailure) return resumed.Error;

        guildScheduler!.Schedule(resumed.Value.Quest);

        admission.RecordCommitted(resumed.Value.Quest);

        return new GuildLocalChildToolResult(resumed.Value.Quest,
            Projection: JsonSerializer.SerializeToElement(resumed.Value.Quest, GuildJsonContext.Default.GuildQuestDto));
    }
}
