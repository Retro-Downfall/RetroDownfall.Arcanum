using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildInboundHost
{
    internal Task<Result<GuildPortalMessageDeliveryReceipt>> AcceptPortalMessageAsync(GuildInboundPeer peer,
        GuildPortalMessageRequest message, CancellationToken ct)
    {
        Result<string> digest = GuildPortalMessageProtocol.ComputeIntentDigest(message);

        if (digest.IsFailure) return Task.FromResult(Result<GuildPortalMessageDeliveryReceipt>.Failure(digest.Error));

        if (message.IntentDigest != digest.Value) return Task.FromResult(Result<GuildPortalMessageDeliveryReceipt>.Failure(Invalid));

        return AccessPortalMessageAsync(peer, message.ReceiptQuery(), message, ct);
    }

    internal Task<Result<GuildPortalMessageDeliveryReceipt>> ReadPortalMessageReceiptAsync(GuildInboundPeer peer,
        GuildPortalMessageReceiptQuery query, CancellationToken ct) => GuildPortalMessageProtocol.ValidReceiptQuery(query)
        ? AccessPortalMessageAsync(peer, query, null, ct)
        : Task.FromResult(Result<GuildPortalMessageDeliveryReceipt>.Failure(Invalid));

    private async Task<Result<GuildPortalMessageDeliveryReceipt>> AccessPortalMessageAsync(GuildInboundPeer peer,
        GuildPortalMessageReceiptQuery query, GuildPortalMessageRequest? message, CancellationToken ct)
    {
        Result<GuildInboundBinding?> located = await policy.ReadInboundBindingAsync(peer, query.TaskId, null, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        if (located.Value is not { } binding || binding.ContextId != query.ContextId) return Missing;

        Result<GuildCallerContext> caller = await policy.CreatePeerCallerAsync(peer, query.MessageId, ct).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        return await quests.AccessInboundPortalMessageAsync(caller.Value, binding, query, message, ct).ConfigureAwait(false);
    }
}
