using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestDiscoveryStore
{
    /// <summary>Settles only reported transport state; an interrupted remote return has unknown billing.</summary>
    private static async Task SettleInterruptedSendsAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration original, CancellationToken ct)
    {
        string? cursor = null;

        while (true)
        {
            List<InterruptedSend> page = [];

            await using (SqliteCommand command = Command(connection, transaction, """
                SELECT unit.WorkItemId, unit.KindCode, unit.ItemId, unit.SourceDigest, unit.RecipeDigest, unit.WorkDigest,
                    effect.WorkDigest, effect.AttemptId, effect.PayloadKindCode, effect.PhysicalOrdinal, effect.Revision,
                    effect.PayloadDigest, effect.DestinationDigest, effect.DisclosureReceiptDigest, effect.CompletionJson, effect.FailureCount, unit.OperationId, unit.DispositionCode,
                    effect.OriginalWorkJson
                FROM long_rest_provider_effects effect JOIN long_rest_work_items unit ON unit.WorkItemId = effect.WorkItemId
                WHERE unit.GenerationId = $generation
                    AND effect.StateCode = 3 AND ($cursor IS NULL OR unit.WorkItemId > $cursor)
                ORDER BY unit.WorkItemId LIMIT 128;
                """, ("$generation", Format(original.GenerationId)), ("$cursor", cursor)))
            {
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    LongRestWorkIdentity work = new(original.GenerationId, original.OperationId, reader.GetString(2), (LongRestWorkKind)reader.GetInt32(1),
                        original.Scope, original.Fence, new((byte[])reader[3]), new((byte[])reader[4]));

                    var hash = LongRestDiscoveryHash.Work(work);

                    LongRestProviderPayloadKind kind = (LongRestProviderPayloadKind)reader.GetInt32(8);

                    CovenantDigest destination = new((byte[])reader[12]);

                    if (reader.GetString(16) != original.OperationId.ToString("N") || reader.GetInt32(17) != 1
                        || hash.IsFailure || new CovenantDigest((byte[])reader[5]) != hash.Value || new CovenantDigest((byte[])reader[6]) != hash.Value
                        || work.RecipeDigest != LongRestDiscoveryHash.Recipe(original.Recipe).Value || Guid.Parse(reader.GetString(7)) == Guid.Empty
                        || reader.GetInt64(9) < 1 || reader.GetInt64(10) < 0 || !new CovenantDigest((byte[])reader[11]).IsValid
                        || !reader.IsDBNull(14) || (original.Scope.ProtectedPartitionDigest is not null && reader.IsDBNull(13))
                        || (kind == LongRestProviderPayloadKind.Adjudication ? work.Kind != LongRestWorkKind.Anchor : work.Kind != LongRestWorkKind.HierarchyFold)
                        || destination != (kind == LongRestProviderPayloadKind.Embedding ? original.Recipe.EmbeddingDestinationDigest : original.Recipe.SummaryDestinationDigest))
                    {
                        throw new InvalidDataException();
                    }

                    if (LongRestAttemptStore.ReadOriginalWork(reader.GetString(18), new((byte[])reader[6])) != work) throw new InvalidDataException();

                    // The caller has just re-proved the whole corpus in this transaction, so each send is a step.
                    await ValidateAttemptSourceMetadataAsync(connection, transaction, work, reader.GetString(0), LongRestVerificationDepth.Step, ct).ConfigureAwait(false);

                    page.Add(new(reader.GetString(0), hash.Value, reader.GetString(7), reader.GetInt64(9), reader.GetInt64(10), checked(reader.GetInt64(15) + 1)));
                }
            }

            if (page.Count == 0)
            {
                return;
            }

            foreach (InterruptedSend interrupted in page)
            {
                // The same settlement predicate the in-frame Unknown-Usage Settlement writes, under the reboot's own failure code.
                if (await LongRestAttemptStore.SettleSentAsync(connection, transaction, interrupted.Unit, interrupted.Work.Bytes, interrupted.Attempt,
                    interrupted.Ordinal, interrupted.Revision, interrupted.FailureCount, ErrorCodes.LongRest.InterruptedTransport, null, ct).ConfigureAwait(false) != 1)
                {
                    throw new InvalidDataException();
                }
            }

            cursor = page[^1].Unit;
        }
    }

    private sealed record InterruptedSend(string Unit, CovenantDigest Work, string Attempt, long Ordinal, long Revision, long FailureCount);
}
