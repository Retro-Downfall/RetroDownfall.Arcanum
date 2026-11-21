using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildPortalMessageDeliveryReceipt>> AccessInboundPortalMessageAsync(GuildCallerContext caller,
        GuildInboundBinding located, GuildPortalMessageReceiptQuery query, GuildPortalMessageRequest? message, CancellationToken ct) =>
        new GuildPortalStore(this, authority, policy, time).AccessInboundAsync(caller, located, query, message, ct);
}
