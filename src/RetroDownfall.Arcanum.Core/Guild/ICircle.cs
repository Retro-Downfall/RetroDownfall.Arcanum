using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Accepts intent with verified caller context. Claims are private effect capabilities, distinct from public page/event cursors. Acknowledgement commits the consumer effect and receipt atomically.</summary>
public interface ICircle
{
    Task<Result<GuildMessageReceipt>> PublishAsync(GuildCallerContext caller, GuildMutationIdentity mutation, GuildPublishRequest request, CancellationToken cancellationToken);

    Task<Result<GuildInboxClaim>> ClaimAsync(GuildVerifiedAuthority authority, Guid recipientCommissionId, CancellationToken cancellationToken);

    Task<Result<GuildCircleInboxItem>> ReadClaimAsync(GuildInboxClaim claim, CancellationToken cancellationToken);

    Task<Result<GuildCircleInboxPage>> ReadInboxAsync(GuildCallerContext caller, GuildCircleInboxQuery query, CancellationToken cancellationToken);

    Task<Result<GuildCircleAcknowledgement>> AcknowledgeAsync(GuildInboxClaim claim, GuildCircleAcknowledgeRequest request, CancellationToken cancellationToken);

    Task<Result<GuildEventPage>> ReadEventsAsync(GuildCallerContext caller, GuildEventQuery query, CancellationToken cancellationToken);
}
