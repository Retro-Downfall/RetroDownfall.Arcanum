using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Explicit membership, receipt-driven convergence and distribution through the same coordinator and Circle.</summary>
public interface IGuildCohortService
{
    Task<Result<CohortDto>> CreateAsync(GuildCallerContext caller, GuildMutationIdentity mutation, CreateCohortRequest request, CancellationToken cancellationToken);

    Task<Result<CohortDto>> GetAsync(GuildCallerContext caller, Guid cohortId, CancellationToken cancellationToken);

    Task<Result<CohortPageDto>> ListAsync(GuildCallerContext caller, Guid? rootQuestId, GuildCohortState? state, GuildPageQuery query, CancellationToken cancellationToken);

    Task<Result<CohortMemberDto>> JoinAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid cohortId, AdmitCohortMemberRequest request, CancellationToken cancellationToken);

    Task<Result<CohortMemberDto>> RemoveAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid cohortId, Guid memberId, GuildRevisionRequest request, CancellationToken cancellationToken);

    Task<Result<CohortMemberPageDto>> ListMembersAsync(GuildCallerContext caller, Guid cohortId, GuildPageQuery query, CancellationToken cancellationToken);

    Task<Result<CohortMemberQuestPageDto>> ListMemberQuestsAsync(GuildCallerContext caller, Guid cohortId, Guid memberId, GuildPageQuery query, CancellationToken cancellationToken);

    Task<Result<CohortDto>> CloseAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid cohortId, GuildRevisionRequest request, CancellationToken cancellationToken);

    Task<Result<CohortDto>> RequestCancellationAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid cohortId, GuildRevisionRequest request, CancellationToken cancellationToken);

    Task<Result<CohortContributionDto>> AcceptContributionAsync(GuildVerifiedAuthority authority, GuildCohortContributionRequest request, CancellationToken cancellationToken);

    Task<Result<CohortContributionPageDto>> ListContributionsAsync(GuildCallerContext caller, Guid cohortId, bool? selected, GuildPageQuery query, CancellationToken cancellationToken);

    Task<Result<CohortConvergenceDto>> GetConvergenceAsync(GuildCallerContext caller, Guid cohortId, CancellationToken cancellationToken);

    Task<Result<CohortConvergenceDto>> ReconcileAsync(GuildRecoveryContext recovery, Guid cohortId, CancellationToken cancellationToken);

    Task<Result<WorkOfferDto>> PostOfferAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid cohortId, PostWorkOfferRequest request, CancellationToken cancellationToken);

    Task<Result<WorkOfferDto>> GetOfferAsync(GuildCallerContext caller, Guid offerId, CancellationToken cancellationToken);

    Task<Result<WorkOfferPageDto>> ListOffersAsync(GuildCallerContext caller, Guid cohortId, GuildWorkOfferState? state, GuildPageQuery query, CancellationToken cancellationToken);

    Task<Result<WorkOfferDto>> WithdrawOfferAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid offerId, GuildRevisionRequest request, CancellationToken cancellationToken);

    Task<Result<WorkBidDto>> SubmitBidAsync(GuildExecutionHandle execution, GuildMutationIdentity mutation, Guid offerId, SubmitBidRequest request, CancellationToken cancellationToken);

    Task<Result<WorkBidDto>> WithdrawBidAsync(GuildExecutionHandle execution, GuildMutationIdentity mutation, Guid bidId, GuildRevisionRequest request, CancellationToken cancellationToken);

    Task<Result<WorkBidPageDto>> ListBidsAsync(GuildCallerContext caller, Guid offerId, GuildPageQuery query, CancellationToken cancellationToken);

    Task<Result<WorkAwardDto>> AwardAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid offerId, AwardWorkRequest request, CancellationToken cancellationToken);

    Task<Result<SharedNoteDto>> AppendNoteAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid cohortId, AppendSharedNoteRequest request, CancellationToken cancellationToken);

    Task<Result<SharedNotePageDto>> ReadNotesAsync(GuildCallerContext caller, Guid cohortId, GuildPageQuery query, CancellationToken cancellationToken);
}
