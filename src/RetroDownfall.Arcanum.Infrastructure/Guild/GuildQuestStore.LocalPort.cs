using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildLocalPortState(GuildQuestStored Quest, LongRunningOperation Operation,
    GuildLocalExecutionBinding Binding, GuildQuestDispatchRecord? Dispatch, bool HadPhysicalDispatch);

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildLocalPortState>> ReadLocalPortAsync(GuildQuestDriver driver, Guid dispatchId, CancellationToken ct)
    {
        if (!DriverShape(driver) || dispatchId == Guid.Empty)
            return Task.FromResult(Result<GuildLocalPortState>.Failure(InvalidReceipt));

        return authority.InQuestTransactionAsync<GuildLocalPortState>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                var binding = await GuildLocalExecutionStore.ReadWithinAsync(connection, transaction, driver.Execution.QuestId, token).ConfigureAwait(false);

                if (binding.IsFailure) return binding.Error;

                var dispatch = await ReadDispatchWithinAsync(connection, transaction, dispatchId, token).ConfigureAwait(false);

                if (dispatch.IsFailure) return dispatch.Error;

                if (dispatch.Value is { } found && (found.QuestId != binding.Value.QuestId || found.LocalChildId != binding.Value.ApprenticeId))
                    return Conflict;

                await using SqliteCommand attempted = Command(connection, transaction,
                    "SELECT EXISTS(SELECT 1 FROM guild_dispatches WHERE QuestId=$quest AND (EvidenceCode IN(2,3) OR RemoteTaskId IS NOT NULL OR RemoteContextId IS NOT NULL))");

                Add(attempted, "$quest", Id(driver.Execution.QuestId));

                bool physical = Equals(1L, await attempted.ExecuteScalarAsync(token).ConfigureAwait(false));

                return new GuildLocalPortState(admitted.Value.Row, admitted.Value.Operation, binding.Value, dispatch.Value, physical);
            }, ct);
    }

    internal Task<Result<GuildLocalExecutionInput>> ReadLocalInputAsync(GuildQuestDriver driver, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildLocalExecutionInput>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, true, false, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                return await GuildLocalExecutionStore.ReadInputWithinAsync(connection, transaction, driver.Execution.QuestId, null, token).ConfigureAwait(false);
            }, ct);

    internal Task<Result<GuildQuestDispatchRecord>> ResolveLocalDispatchAsync(GuildQuestDriver driver, Guid dispatchId, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildQuestDispatchRecord>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, true, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                var dispatch = await ReadDispatchWithinAsync(connection, transaction, dispatchId, token).ConfigureAwait(false);

                if (dispatch.IsFailure) return dispatch.Error;

                if (dispatch.Value is not { } current || current.QuestId != driver.Execution.QuestId || current.LocalChildId is null) return Conflict;

                // The runner's return is not accounting evidence. Every physical attempt must have
                // an applied receipt before its enclosing local dispatch can be confirmed.
                await using SqliteCommand evidence = Command(connection, transaction, """
                    SELECT EXISTS(SELECT 1 FROM guild_budget_effects e WHERE e.QuestId=$quest
                        AND (e.StateCode IN(0,1)
                            OR NOT EXISTS(SELECT 1 FROM guild_budget_evidence r WHERE r.EffectId=e.EffectId AND r.AppliedRevision=e.Revision)))
                        OR EXISTS(SELECT 1 FROM BudgetReservations r WHERE lower(replace(r.RunId,'-',''))=$run AND r.Status NOT IN(1,2))
                    """);

                Add(evidence, "$quest", Id(driver.Execution.QuestId));

                Add(evidence, "$run", driver.Execution.RunId.ToString("N"));

                if (!Equals(0L, await evidence.ExecuteScalarAsync(token).ConfigureAwait(false))) return UncertainDispatch;

                GuildDispatchEvidence resolved = current.Evidence == GuildDispatchEvidence.Prepared
                    ? GuildDispatchEvidence.ResolvedNoDispatch : GuildDispatchEvidence.Confirmed;

                if (current.Evidence is GuildDispatchEvidence.Confirmed or GuildDispatchEvidence.ResolvedNoDispatch) return current;

                if (current.Revision == long.MaxValue) return ExhaustedReceipt;

                await using SqliteCommand update = Command(connection, transaction,
                    "UPDATE guild_dispatches SET EvidenceCode=$evidence,Revision=Revision+1,UpdatedAtUtc=$now WHERE DispatchId=$id AND Revision=$revision");

                Add(update, "$evidence", (int)resolved);

                Add(update, "$now", RetroDownfall.Arcanum.Infrastructure.Data.UtcInstantText.Format(time.GetUtcNow()));

                Add(update, "$id", Id(dispatchId));

                Add(update, "$revision", current.Revision);

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

                var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, driver.Execution.RootQuestId,
                    driver.Execution.QuestId, GuildEventKind.QuestChanged, dispatchId, current.Revision + 1, null,
                    time.GetUtcNow(), token, GuildPortalEventSource.Dispatch).ConfigureAwait(false);

                return projected.IsFailure ? projected.Error : current with { Evidence = resolved, Revision = current.Revision + 1 };
            }, ct);
}
