using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ConclaveArchmage
{
    public async Task<Result<GuildQuestDto>> IssueAsync(GuildCallerContext caller, GuildMutationIdentity mutation,
        GuildIssueRootRequest request, CancellationToken cancellationToken)
    {
        Result<string> intent = GuildQuestIntent.Compute(request);

        if (intent.IsFailure) return intent.Error;

        if (!BindsIntent(caller, mutation, request.IdempotencyKey, intent.Value)) return InvalidGuildIntent;

        if (!CanIssueGuild()) return UnavailableGuild();

        GuildIssuanceIdentity issuance = new(caller.InstallationGeneration, null, caller.InvocationId,
            caller.InvocationId, request.IdempotencyKey);

        Result<GuildQuestStored> accepted = await quests!.IssueAsync(caller, issuance, request, cancellationToken).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        guildScheduler!.Schedule(accepted.Value.Quest);

        return accepted.Value.Quest;
    }

    public async Task<Result<GuildQuestDto>> IssueAsync(GuildCallerContext caller, GuildMutationIdentity mutation,
        Guid parentQuestId, GuildIssueChildRequest request, CancellationToken cancellationToken)
    {
        Result<string> intent = GuildQuestIntent.Compute(parentQuestId, request);

        if (intent.IsFailure) return intent.Error;

        if (!BindsIntent(caller, mutation, request.IdempotencyKey, intent.Value)) return InvalidGuildIntent;

        if (!CanIssueGuild()) return UnavailableGuild();

        GuildIssuanceIdentity issuance = new(caller.InstallationGeneration, parentQuestId, null,
            caller.InvocationId, request.IdempotencyKey);

        Result<GuildQuestStored> accepted = await quests!.IssueAsync(caller, issuance, parentQuestId, request, cancellationToken).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        guildScheduler!.Schedule(accepted.Value.Quest);

        return accepted.Value.Quest;
    }

    public async Task<Result<GuildQuestDto>> ContinueAsync(GuildCallerContext caller, GuildMutationIdentity mutation,
        Guid questId, GuildContinueRequest request, CancellationToken cancellationToken)
    {
        Result<string> intent = GuildQuestIntent.Compute(questId, request);

        if (intent.IsFailure) return intent.Error;

        if (!BindsIntent(caller, mutation, request.IdempotencyKey, intent.Value)) return InvalidGuildIntent;

        if (!CanIssueGuild()) return UnavailableGuild();

        Result<GuildQuestStored> accepted = await quests!.AcceptContinuationAsync(caller, questId, request, cancellationToken).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        guildScheduler!.Schedule(accepted.Value.Quest);

        return accepted.Value.Quest;
    }

    public async Task<Result<GuildCancellationDto>> RequestCancellationAsync(GuildCallerContext caller, GuildMutationIdentity mutation,
        Guid questId, GuildCancelQuestRequest request, CancellationToken cancellationToken)
    {
        Result<string> intent = GuildQuestIntent.Compute(questId, request);

        if (intent.IsFailure) return intent.Error;

        if (!BindsIntent(caller, mutation, request.IdempotencyKey, intent.Value)) return InvalidGuildIntent;

        if (quests is null || guildScheduler is null) return UnavailableGuild();

        Result<GuildCancellationDto> accepted = await quests.RequestCancellationAsync(caller, mutation, questId, request, cancellationToken).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        Result<GuildQuestStored> current = await quests.GetAsync(caller, questId, cancellationToken).ConfigureAwait(false);

        if (current.IsFailure) return current.Error;

        guildScheduler.Schedule(current.Value.Quest);

        return accepted.Value;
    }

    private bool CanIssueGuild() => settings.CurrentValue.Features.Guild && quests is { HasLocalExecutionAdmission: true }
        && guildScheduler is { IsAvailable: true };

    private static bool BindsIntent(GuildCallerContext caller, GuildMutationIdentity mutation, Guid key, string digest) =>
        caller is not null && mutation is not null && key != Guid.Empty && mutation.IdempotencyKey == key
        && mutation.CanonicalRequestDigest == digest;

    private static readonly Error InvalidGuildIntent = new(ErrorCodes.Guild.AuthorityMismatch, "The Guild mutation does not bind the caller's exact request.");

    private static Error UnavailableGuild() => new(ErrorCodes.Guild.Unavailable, "Qualified local Guild execution is unavailable.");
}
