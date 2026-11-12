using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed class GuildNativeMessageService(GuildLocalHostPolicy policy, GuildPortalStore portal,
    GuildQuestStore quests, IGuildQuestCoordinator coordinator)
{
    internal async Task<Result<GuildMessageReceipt>> SendAsync(GuildCallerContext caller, Guid questId,
        GuildSendOperatorMessageRequest request, CancellationToken ct)
    {
        if (!policy.Authenticates(caller) || caller.Kind != GuildCallerKind.Operator)
            return new Error(ErrorCodes.Guild.NotFound, "The Guild resource was not found.");

        if (request.IdempotencyKey == Guid.Empty || request.ExpectedRevision < 1 || !Enum.IsDefined(request.Kind)
            || request.Body is null || request.MimeType is not ("text/plain" or "application/json"))
            return new Error(ErrorCodes.Guild.InvalidRequest, "The operator message is invalid.");

        if (request.Kind == GuildMessageKind.Message)
            return await portal.PublishAsync(caller, questId, new(request.IdempotencyKey,
                new(GuildCircleAddressKind.Child, questId, null, null), GuildEnvelopeKind.Intervention,
                request.MimeType, request.Body, request.ExpectedRevision), ct).ConfigureAwait(false);

        Result<GuildContinueRequest> continuation = await quests.ReadOperatorContinuationAsync(caller, questId, request, ct).ConfigureAwait(false);

        if (continuation.IsFailure) return continuation.Error;

        Result<string> intent = GuildQuestIntent.Compute(questId, continuation.Value);

        if (intent.IsFailure) return intent.Error;

        Result<GuildQuestDto> accepted = await coordinator.ContinueAsync(caller, new(request.IdempotencyKey, intent.Value),
            questId, continuation.Value, ct).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        GuildQuestDto quest = accepted.Value;

        return new GuildMessageReceipt(questId, continuation.Value.ContinuationId, quest.Receipt!.AcceptedRevision,
            quest.DeliveryState, quest.EventWatermark, quest.Receipt);
    }
}
