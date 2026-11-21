using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal enum GuildRemotePortalWorkKind
{
    Send,
    Observe,
}

internal sealed record GuildRemotePortalWork(Guid InboxId, long Revision, GuildRemotePortalWorkKind Kind);

internal sealed record GuildRemotePortalObservation(GuildRemotePortalWork Work, GuildRemoteRecoveryTarget Target);

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildRemotePortalWork?>> ReadRemotePortalWorkAsync(GuildQuestDriver driver, CancellationToken ct,
        GuildRemotePortalWorkKind? preferred = null) =>
        new GuildPortalStore(this, authority, policy, time).ReadRemotePortalWorkAsync(driver, preferred, ct);

    internal Task<Result<GuildPortalMessageDeliveryReceipt>> SendRemotePortalMessageAsync(GuildQuestDriver driver,
        GuildRemotePortalWork work, Func<GuildPortalMessageRequest, CancellationToken, Task<Result<GuildPortalMessageDeliveryReceipt>>> send,
        CancellationToken ct) => new GuildPortalStore(this, authority, policy, time).SendRemotePortalMessageAsync(driver, work, send, ct);

    internal Task<Result<GuildPortalMessageDeliveryReceipt>> ObserveRemotePortalMessageAsync(GuildQuestDriver driver,
        GuildRemotePortalWork work, Func<GuildPortalMessageReceiptQuery, CancellationToken, Task<Result<GuildPortalMessageDeliveryReceipt>>> read,
        CancellationToken ct) => new GuildPortalStore(this, authority, policy, time).ObserveRemotePortalMessageAsync(driver, work, read, ct);

    internal Task<Result<GuildRemotePortalObservation?>> ReadTerminalRemotePortalObservationAsync(GuildCallerContext owner,
        Guid questId, CancellationToken ct) =>
        new GuildPortalStore(this, authority, policy, time).ReadTerminalRemotePortalObservationAsync(owner, questId, ct);

    internal Task<Result<GuildPortalMessageDeliveryReceipt>> ObserveTerminalRemotePortalMessageAsync(GuildCallerContext owner,
        Guid questId, GuildRemotePortalWork work, Func<GuildPortalMessageReceiptQuery, CancellationToken, Task<Result<GuildPortalMessageDeliveryReceipt>>> read,
        CancellationToken ct) => new GuildPortalStore(this, authority, policy, time).ObserveTerminalRemotePortalMessageAsync(owner, questId, work, read, ct);
}
