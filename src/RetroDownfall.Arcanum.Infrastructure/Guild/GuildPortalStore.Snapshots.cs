using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    internal async Task<Result<GuildTreeSnapshotDto>> CaptureTreeSnapshotAsync(GuildCallerContext caller,
        Guid subtree, CancellationToken ct)
    {
        if (caller is null || subtree == Guid.Empty) return Invalid;

        // This lookup only discovers immutable root linkage. Nothing from it is disclosed, and all
        // authorization and projection reads are repeated under the actual root transaction.
        Result<GuildQuestStored> located = await authority.InQuestTransactionAsync<GuildQuestStored>(subtree,
            (connection, transaction, _, token) => GuildQuestStore.ReadWithinAsync(connection, transaction, subtree, token), ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        Guid root = located.Value.Quest.RootQuestId;

        return await authority.InQuestTransactionAsync<GuildTreeSnapshotDto>(root, async (connection, transaction, signets, token) =>
        {
            Result<PortalReadBinding> binding = await ReadBindingWithinAsync(connection, transaction, caller, token, operatorMetadataOnly: true).ConfigureAwait(false);

            if (binding.IsFailure) return binding.Error;

            Result<PortalReadScope> scope = await RequireWatchScopeWithinAsync(connection, transaction, signets,
                caller, root, subtree, null, binding.Value, false, token).ConfigureAwait(false);

            if (scope.IsFailure) return scope.Error;

            Result<GuildQuestStored> summary = await GuildQuestStore.ReadWithinAsync(connection, transaction, subtree, token).ConfigureAwait(false);

            if (summary.IsFailure) return summary.Error;

            if (summary.Value.Quest.RootQuestId != root) return Unavailable;

            Result<GuildQuestPageDto> members = await quests.GetOperatorTreePageWithinAsync(connection, transaction,
                caller, subtree, new GuildPageQuery(), token).ConfigureAwait(false);

            if (members.IsFailure) return members.Error;

            Result<(long Last, long Retained)> head = await ReadEventHeadWithinAsync(connection, transaction, root, token).ConfigureAwait(false);

            if (head.IsFailure) return head.Error;

            string resume = await WriteCursorWithinAsync(connection, transaction, binding.Value, root,
                ScopedReplayKey(subtree, null), head.Value.Last, head.Value.Last, token).ConfigureAwait(false);

            return new GuildTreeSnapshotDto(summary.Value.Quest with { Receipt = null }, members.Value, head.Value.Last, resume);
        }, ct).ConfigureAwait(false);
    }

    internal async Task<Result<GuildCohortSnapshotDto>> CaptureCohortSnapshotAsync(GuildCallerContext caller,
        Guid cohortId, GuildCohortStore cohorts, CancellationToken ct)
    {
        if (caller is null || cohortId == Guid.Empty || cohorts is null) return Invalid;

        Result<Guid> located = await authority.InQuestTransactionAsync<Guid>(cohortId, async (connection, transaction, _, token) =>
        {
            await using var command = Command(connection, transaction, "SELECT RootQuestId FROM guild_cohorts WHERE Id=$cohort");

            Add(command, "$cohort", Id(cohortId));

            object? raw = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

            if (raw is null) return new Error(ErrorCodes.Guild.OwnerUnavailable, "The Cohort is unavailable.");

            return raw is string value && Guid.TryParseExact(value, "D", out Guid parsed) && parsed != Guid.Empty && Id(parsed) == value
                ? parsed : Unavailable;
        }, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        Guid root = located.Value;

        return await authority.InQuestTransactionAsync<GuildCohortSnapshotDto>(root, async (connection, transaction, signets, token) =>
        {
            Result<PortalReadBinding> binding = await ReadBindingWithinAsync(connection, transaction, caller, token, operatorMetadataOnly: true).ConfigureAwait(false);

            if (binding.IsFailure) return binding.Error;

            Result<PortalReadScope> scope = await RequireWatchScopeWithinAsync(connection, transaction, signets,
                caller, root, null, cohortId, binding.Value, false, token).ConfigureAwait(false);

            if (scope.IsFailure) return scope.Error.Code == ErrorCodes.Guild.Forbidden
                ? new Error(ErrorCodes.Guild.OwnerUnavailable, "The Cohort is unavailable.") : scope.Error;

            Result<GuildCohortStored> summary = await GuildCohortStore.ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

            if (summary.IsFailure) return summary.Error;

            if (summary.Value.Cohort.RootQuestId != root) return Unavailable;

            Result<CohortMemberPageDto> members = await cohorts.GetOperatorMemberPageWithinAsync(connection, transaction,
                caller, cohortId, new GuildPageQuery(), token).ConfigureAwait(false);

            if (members.IsFailure) return members.Error;

            Result<(long Last, long Retained)> head = await ReadEventHeadWithinAsync(connection, transaction, root, token).ConfigureAwait(false);

            if (head.IsFailure) return head.Error;

            string resume = await WriteCursorWithinAsync(connection, transaction, binding.Value, root,
                ScopedReplayKey(null, cohortId), head.Value.Last, head.Value.Last, token).ConfigureAwait(false);

            return new GuildCohortSnapshotDto(summary.Value.Cohort with { Receipt = null }, members.Value, head.Value.Last, resume);
        }, ct).ConfigureAwait(false);
    }

}
