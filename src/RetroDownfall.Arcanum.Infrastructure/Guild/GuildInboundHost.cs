using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildInboundTaskPage(IReadOnlyList<GuildInboundTask> Items, string? NextTaskId);

internal sealed record GuildInboundTask(GuildInboundBinding Binding, GuildQuestDto Quest, string? Result, string? Question);

/// <summary>Inbound A2A is a projection over the sole local Quest lifecycle and its durable receipts.</summary>
internal sealed class GuildInboundHost(GuildLocalHostPolicy policy, GuildQuestStore quests,
    ConclaveArchmage coordinator)
{
    internal async Task<Result<T>> AdmitDisclosureAsync<T>(GuildInboundPeer peer, string taskId,
        Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (action is null || !GuildInboundTaskStore.ValidIdentifier(taskId)) return Invalid;

        var binding = await policy.ReadInboundBindingAsync(peer, taskId, null, ct).ConfigureAwait(false);

        if (binding.IsFailure) return binding.Error;

        if (binding.Value is not { } found) return Missing;

        var caller = await policy.CreatePeerCallerAsync(peer,
            GuildInboundTaskStore.Identity("disclosure", peer.PeerId, taskId), ct).ConfigureAwait(false);

        return caller.IsFailure ? caller.Error : await quests.AdmitInboundDisclosureAsync(caller.Value,
            found.QuestId, action, ct).ConfigureAwait(false);
    }

    internal Task<Result<bool>> IsGuildTaskAsync(string taskId, CancellationToken ct) => policy.IsInboundTaskAsync(taskId, ct);

    internal async Task<Result<GuildInboundTaskPage>> ListAsync(GuildInboundPeer peer, string? afterTaskId,
        int pageSize, bool materialize, CancellationToken ct)
    {
        if (afterTaskId is not null && !GuildInboundTaskStore.ValidIdentifier(afterTaskId)) return Invalid;

        int limit = Math.Clamp(pageSize, 1, 100);

        var ids = await policy.ReadInboundTaskIdsAsync(peer, afterTaskId, limit, ct).ConfigureAwait(false);

        if (ids.IsFailure) return ids.Error;

        List<GuildInboundTask> items = [];

        long bytes = 0;

        string? through = afterTaskId;

        bool byteBound = false;

        foreach (string id in ids.Value)
        {
            var current = await ReadAsync(peer, id, materialize, ct).ConfigureAwait(false);

            if (current.IsFailure)
            {
                // A lifecycle-denied row advances the principal's private scan without disclosure.
                if (current.Error.Code is not (ErrorCodes.Guild.Forbidden or ErrorCodes.Guild.AuthorityRevoked
                    or ErrorCodes.Guild.NotFound or ErrorCodes.Guild.OwnerUnavailable)) return current.Error;

                through = id;

                continue;
            }

            GuildInboundTask item = current.Value;

            long encoded = JsonSerializer.SerializeToUtf8Bytes(item.Quest, GuildJsonContext.Default.GuildQuestDto).LongLength
                + Encoding.UTF8.GetByteCount(item.Result ?? string.Empty) + Encoding.UTF8.GetByteCount(item.Question ?? string.Empty)
                + Encoding.UTF8.GetByteCount(item.Binding.TaskId) + Encoding.UTF8.GetByteCount(item.Binding.ContextId) + 1024L;

            if (bytes + encoded > 1_048_576)
            {
                if (items.Count == 0) return new Error(ErrorCodes.Guild.PayloadTooLarge, "The inbound task projection exceeds its page boundary.");

                byteBound = true;

                break;
            }

            items.Add(item);

            bytes += encoded;

            through = id;
        }

        return new GuildInboundTaskPage(items, byteBound || ids.Value.Length == limit ? through : null);
    }

    internal async Task<Result<GuildInboundTask>> IssueAsync(GuildInboundPeer peer, string messageId,
        string taskId, string? contextId, string goal, CancellationToken ct, string[]? delegationChain = null)
    {
        ImmutableArray<string> chain = (delegationChain ?? []).ToImmutableArray();

        if (chain.Any(string.IsNullOrWhiteSpace)) return Invalid;

        if (!GuildInboundTaskStore.ValidIdentifier(messageId) || !GuildInboundTaskStore.ValidIdentifier(taskId)
            || contextId is not null && !GuildInboundTaskStore.ValidIdentifier(contextId)) return Invalid;

        Guid invocation = GuildInboundTaskStore.Identity("issue", peer.PeerId, messageId);

        Result<GuildCallerContext> caller = await policy.CreatePeerCallerAsync(peer, invocation, ct).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        GuildIssueRootRequest request = new(invocation, null, "Inbound Sending", goal, peer.Grant,
            peer.SessionId, peer.CampaignId, peer.WorkspaceId);

        Result<string> intent = GuildQuestIntent.Compute(request);

        if (intent.IsFailure) return intent.Error;

        using MemoryStream facts = new();

        using (Utf8JsonWriter writer = new(facts))
        {
            writer.WriteStartObject();

            writer.WriteNumber("version", 1);

            writer.WriteString("intent", intent.Value);

            writer.WriteString("contextId", contextId);

            writer.WriteStartArray("delegationChain");

            foreach (string hop in chain) writer.WriteStringValue(hop);

            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        if (facts.Length > GuildStore.MaximumContentBytes)
            return new Error(ErrorCodes.Guild.PayloadTooLarge, "The inbound transport observations exceed their record boundary.");

        string digest = Convert.ToHexString(SHA256.HashData(facts.ToArray()));

        string context = contextId ?? GuildInboundTaskStore.Identity("context", peer.PeerId, messageId).ToString("N");

        GuildInboundIssue issue = new(peer, taskId, messageId, context, digest, chain);

        Result<GuildQuestDto> accepted = await coordinator.IssueInboundAsync(caller.Value, request, issue, ct).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        var binding = await policy.ReadInboundBindingAsync(peer, null, messageId, ct).ConfigureAwait(false);

        return binding.IsFailure ? binding.Error : binding.Value is null ? Invalid
            : new GuildInboundTask(binding.Value, accepted.Value, null, null);
    }

    internal async Task<Result<GuildInboundTask>> ReadAsync(GuildInboundPeer peer, string taskId, bool materialize, CancellationToken ct)
    {
        if (!GuildInboundTaskStore.ValidIdentifier(taskId)) return Invalid;

        var binding = await policy.ReadInboundBindingAsync(peer, taskId, null, ct).ConfigureAwait(false);

        if (binding.IsFailure) return binding.Error;

        if (binding.Value is not { } found) return Missing;

        Result<GuildCallerContext> caller = await policy.CreatePeerCallerAsync(peer,
            GuildInboundTaskStore.Identity("read", peer.PeerId, taskId), ct).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        var current = await quests.ReadLocalResultAsync(caller.Value, found.QuestId, materialize, ct).ConfigureAwait(false);

        if (current.IsFailure) return current.Error;

        string? question = null;

        if (materialize && current.Value.Quest.State == QuestState.InputRequired)
        {
            var input = await quests.ReadLocalQuestionAsync(caller.Value, found.QuestId, ct).ConfigureAwait(false);

            if (input.IsFailure) return input.Error;

            question = input.Value.Question;
        }

        return new GuildInboundTask(found, current.Value.Quest, current.Value.Body, question);
    }

    internal async Task<Result<GuildInboundTask>> ContinueAsync(GuildInboundPeer peer, string taskId,
        string messageId, string body, CancellationToken ct)
    {
        if (!GuildInboundTaskStore.ValidIdentifier(messageId)) return Invalid;

        var current = await ReadAsync(peer, taskId, false, ct).ConfigureAwait(false);

        if (current.IsFailure) return current.Error;

        GuildQuestDto quest = current.Value.Quest;

        Guid invocation = GuildInboundTaskStore.Identity("continue", peer.PeerId, messageId);

        var caller = await policy.CreatePeerCallerAsync(peer, invocation, ct).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        // A retry must retain the accepted question and CAS values even after the task advances.
        var prior = await quests.ReadInboundContinuationRequestAsync(caller.Value, quest.Id, invocation, body, ct).ConfigureAwait(false);

        if (prior.IsFailure) return prior.Error;

        if (prior.Value is null && quest.AwaitingInput is null) return Invalid;

        GuildContinueRequest request = prior.Value ?? new(invocation, quest.Revision, quest.AwaitingInput!.ContinuationId, "text/plain", body);

        Result<string> intent = GuildQuestIntent.Compute(quest.Id, request);

        if (intent.IsFailure) return intent.Error;

        Result<GuildQuestDto> accepted = await coordinator.ContinueAsync(caller.Value, new(invocation, intent.Value), quest.Id, request, ct).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        return new GuildInboundTask(current.Value.Binding, accepted.Value, null, null);
    }

    internal async Task<Result<GuildInboundTask>> CancelAsync(GuildInboundPeer peer, string taskId, CancellationToken ct)
    {
        var current = await ReadAsync(peer, taskId, false, ct).ConfigureAwait(false);

        if (current.IsFailure) return current.Error;

        GuildQuestDto quest = current.Value.Quest;

        Guid invocation = GuildInboundTaskStore.Identity("cancel", peer.PeerId, taskId);

        var caller = await policy.CreatePeerCallerAsync(peer, invocation, ct).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        var prior = await quests.ReadInboundCancellationRequestAsync(caller.Value, quest.Id, invocation, ct).ConfigureAwait(false);

        if (prior.IsFailure) return prior.Error;

        GuildCancelQuestRequest request = prior.Value ?? new(invocation, quest.Revision, GuildCancellationReason.OperatorRequested);

        Result<string> intent = GuildQuestIntent.Compute(quest.Id, request);

        if (intent.IsFailure) return intent.Error;

        var accepted = await coordinator.RequestCancellationAsync(caller.Value, new(invocation, intent.Value), quest.Id, request, ct).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        return await ReadAsync(peer, taskId, false, ct).ConfigureAwait(false);
    }

    private static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "The inbound Sending request is invalid.");

    private static Error Missing => new(ErrorCodes.Guild.NotFound, "The inbound task is unavailable.");
}
