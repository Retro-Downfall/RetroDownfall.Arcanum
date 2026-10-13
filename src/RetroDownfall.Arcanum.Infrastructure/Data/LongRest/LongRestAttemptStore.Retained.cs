using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestAttemptStore
{
    // The unit and immutable artifacts were independently reconstructed by the owning store transaction.
    internal static async Task<LongRestWorkIdentity?> ValidateRetainedEffectAsync(SqliteConnection connection, SqliteTransaction transaction,
        string unit, LongRestWorkIdentity work, IReadOnlyList<LongRestPaidEvidence> retained, CancellationToken ct)
    {
        Effect? effect = await ReadAsync(connection, transaction, unit, work, ct).ConfigureAwait(false);

        if (effect is null)
        {
            if (retained.Count != 0) throw new InvalidDataException();

            return null;
        }

        if (effect.Attempt.State is LongRestDispatchAttemptState.Prepared or LongRestDispatchAttemptState.Disclosed or LongRestDispatchAttemptState.Sent
            || effect.Attempt.DisclosureReceiptDigest is not null || effect.Attempt.AttemptId == Guid.Empty
            || effect.Attempt.PhysicalOrdinal == 0 || effect.Sent < 0 || effect.Paid < 0 || effect.Invalid < 0 || effect.Failed < 0
            || effect.Usage.InputTokens < 0 || effect.Usage.OutputTokens < 0 || effect.Usage.TotalTokens < 0
            || effect.Usage.CachedInputTokens < 0 || effect.Usage.ReasoningTokens < 0 || effect.Cost < 0
            || effect.Sent < 1 || (ulong)effect.Sent > effect.Attempt.PhysicalOrdinal || effect.Paid > effect.Sent
            || effect.Invalid > effect.Paid || effect.Failed > effect.Sent || effect.Invalid > effect.Sent - effect.Failed) throw new InvalidDataException();

        if (effect.Attempt.State == LongRestDispatchAttemptState.Accepted)
        {
            LongRestPaidEvidence paid = retained.SingleOrDefault(value =>
                value.Attempt with { Work = work } == effect.Attempt && LongRestDiscoveryHash.Work(value.Attempt.Work).Value == effect.WorkDigest)
                ?? throw new InvalidDataException();

            if (effect.OriginalWork != paid.Attempt.Work) throw new InvalidDataException();

            await LongRestDiscoveryStore.ValidateStoredWorkAssociationAsync(connection, transaction, effect.OriginalWork, work, ct).ConfigureAwait(false);

            LongRestDispatchCompletion completion = effect.Completion is { } json
                ? JsonSerializer.Deserialize(json, LongRestJsonContext.Default.LongRestDispatchCompletion) ?? throw new InvalidDataException()
                : throw new InvalidDataException();

            ValidateCompletion(completion);

            if (completion.State != LongRestDispatchAttemptState.Accepted || completion.Usage.InputTokens != paid.InputTokens
                || completion.Usage.OutputTokens != paid.OutputTokens || completion.CostUsd != paid.CostUsd || effect.Paid < retained.Count
                || effect.Usage.InputTokens < paid.InputTokens || effect.Usage.OutputTokens < paid.OutputTokens) throw new InvalidDataException();
        }
        else
        {
            await LongRestDiscoveryStore.ValidateStoredWorkAssociationAsync(connection, transaction, effect.OriginalWork, work, ct).ConfigureAwait(false);

            if (effect.Attempt.State is not (LongRestDispatchAttemptState.Invalid or LongRestDispatchAttemptState.Failed)
                || (effect.Attempt.State == LongRestDispatchAttemptState.Invalid && (effect.Invalid < 1 || effect.Paid < 1))
                || (effect.Attempt.State == LongRestDispatchAttemptState.Failed && effect.Failed < 1)) throw new InvalidDataException();

            if (effect.Completion is { } json)
            {
                LongRestDispatchCompletion completion = JsonSerializer.Deserialize(json, LongRestJsonContext.Default.LongRestDispatchCompletion)
                    ?? throw new InvalidDataException();

                ValidateCompletion(completion);

                if (completion.State != effect.Attempt.State || effect.Usage.InputTokens < completion.Usage.InputTokens
                    || effect.Usage.OutputTokens < completion.Usage.OutputTokens || effect.Usage.TotalTokens < completion.Usage.TotalTokens
                    || effect.Usage.CachedInputTokens < completion.Usage.CachedInputTokens || effect.Usage.ReasoningTokens < completion.Usage.ReasoningTokens
                    || (effect.Cost is { } cost && completion.CostUsd is { } added && cost < added)) throw new InvalidDataException();
            }
            else if (effect.Attempt.State != LongRestDispatchAttemptState.Failed) throw new InvalidDataException();
        }

        return effect.Attempt.State is LongRestDispatchAttemptState.Invalid or LongRestDispatchAttemptState.Failed ? effect.OriginalWork : null;
    }
}
