using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal async Task<Result<GuildQuestDriver>> CreateLocalCleanupDriverAsync(GuildCallerContext owner, Guid questId, CancellationToken ct,
        GuildQuestDriver? stoppedLease = null)
    {
        if (questId == Guid.Empty || policy is not GuildLocalHostPolicy hostPolicy
            || owner.Kind != GuildCallerKind.Operator || !hostPolicy.Authenticates(owner)) return OwnerUnavailable;

        // Owner erasure hides ordinary reads. This native-only lookup discloses one fixed
        // root identity; the actual cleanup credential is checked again under that root gate.
        var root = await authority.InQuestTransactionAsync<Guid>(questId, async (connection, transaction, _, token) =>
        {
            Result located = await hostPolicy.AuthorizeLocateAsync(owner, connection, transaction, questId, null, null, token).ConfigureAwait(false);

            if (located.IsFailure) return located.Error;

            await using SqliteCommand command = Command(connection, transaction, """
                SELECT CASE WHEN typeof(RootQuestId)='text' AND length(CAST(RootQuestId AS BLOB))=36 THEN RootQuestId END
                FROM guild_quests WHERE Id=$quest AND InstallationGeneration=$generation
                """);

            Add(command, "$quest", Id(questId));

            Add(command, "$generation", Id(policy.InstallationGeneration));

            object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

            return value is string text && Guid.TryParseExact(text, "D", out Guid id) && id != Guid.Empty
                && text == Id(id) ? Result<Guid>.Success(id) : Result<Guid>.Failure(Unavailable);
        }, ct).ConfigureAwait(false);

        if (root.IsFailure) return root.Error;

        return await authority.InQuestTransactionAsync<GuildQuestDriver>(root.Value,
            async (connection, transaction, signets, token) =>
            {
                var row = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (row.IsFailure) return row.Error;

                if (row.Value.Quest.RootQuestId != root.Value) return Unavailable;

                if (await IsRemoteWithinAsync(connection, transaction, questId, token).ConfigureAwait(false)
                    || policy is not GuildLocalHostPolicy host) return Unavailable;

                var binding = await GuildLocalExecutionStore.ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (binding.IsFailure) return binding.Error;

                var operation = await LongRunningOperationStore.ReadWithinAsync(connection, transaction, row.Value.Quest.OperationId, token).ConfigureAwait(false);

                if (operation is null || operation.Kind != LongRunningOperationKinds.GuildQuest
                    || operation.RunId != row.Value.Quest.RunId || binding.Value.RunId != row.Value.Quest.RunId) return Unavailable;

                if (stoppedLease is not null)
                {
                    // This path is entered only after the owning engine and its in-process work
                    // joined. It adopts the existing lease for settlement, never for another send.
                    if (stoppedLease.Caller is not { Kind: GuildCallerKind.Run } run
                        || !ReferenceEquals(run.Execution, stoppedLease.Execution)
                        || stoppedLease.Execution.QuestId != row.Value.Quest.Id || stoppedLease.Execution.RunId != row.Value.Quest.RunId
                        || stoppedLease.Execution.SignetId != row.Value.Quest.SignetId || stoppedLease.Execution.AgentId != row.Value.Quest.AgentId
                        || stoppedLease.Execution.RootQuestId != row.Value.Quest.RootQuestId || stoppedLease.OperationRevision < 1
                        || operation.Revision < stoppedLease.OperationRevision || operation.LeaseOwner != stoppedLease.LeaseOwner
                        || operation.LeaseExpiresAt is null || operation.LeaseExpiresAt <= time.GetUtcNow()
                        || operation.State is not (LongRunningOperationState.Running or LongRunningOperationState.Waiting or LongRunningOperationState.Cancelling))
                        return RevisionConflict;

                    if (!row.Value.Quest.CancellationRequested)
                    {
                        var live = await signets.VerifyAsync(stoppedLease.Execution, token).ConfigureAwait(false);

                        if (live.IsSuccess && host.IsExecutionEnabled) return ClosedDispatch;

                        GuildCancelQuestRequest request = new(Guid.NewGuid(), row.Value.Quest.Revision, GuildCancellationReason.OperatorRequested);

                        var intent = GuildQuestIntent.Compute(questId, request);

                        if (intent.IsFailure) return intent.Error;

                        var accepted = await RequestCancellationWithinAsync(connection, transaction, signets, owner,
                            questId, request, intent.Value, token).ConfigureAwait(false);

                        if (accepted.IsFailure) return accepted.Error;

                        row = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                        if (row.IsFailure) return row.Error;

                        operation = await LongRunningOperationStore.ReadWithinAsync(connection, transaction, row.Value.Quest.OperationId, token).ConfigureAwait(false);

                        if (operation is null) return Unavailable;
                    }
                }

                var cleanup = await host.CreateLocalCleanupWithinAsync(owner, row.Value, operation, connection, transaction, token).ConfigureAwait(false);

                return cleanup.IsFailure ? cleanup.Error : stoppedLease is null ? cleanup.Value
                    : cleanup.Value with { LeaseOwner = operation.LeaseOwner!, OperationRevision = operation.Revision };
            }, ct).ConfigureAwait(false);
    }

    internal Task<Result<GuildMutationIdentity>> ReadLocalCleanupStopIdentityAsync(GuildQuestDriver driver, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildMutationIdentity>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                if (driver.Caller is not { Kind: GuildCallerKind.HostReceipt, ReceiptSource: GuildLocalHostPolicy.LocalCleanupReceiptSource })
                    return ClosedDispatch;

                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

                // The host-only stop identity binds the immutable accepted Quest. Owner sealing
                // can set the durable cancellation flag without an ordinary caller receipt.
                return admitted.IsFailure ? admitted.Error
                    : new GuildMutationIdentity(admitted.Value.Row.MutationId, admitted.Value.Row.IntentDigest);
            }, ct);
}
