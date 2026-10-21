using System.Data.Common;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class SagaMemoryStore
{
    public Task<SagaMemoryCurationRow[]> ListCleanRecallRowsAsync(string query, MemoryScope scope,
        int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 512 || limit is < 1 or > MemoryRecallPager.MaximumCandidatesPerSource)
        {
            return Task.FromException<SagaMemoryCurationRow[]>(new ArgumentException("The clean recall window is invalid."));
        }

        return ReadLongRestSnapshotAsync(async (connection, longRest) =>
        {
            SagaRankingReader ranking = await SagaRankingReader.CreateAsync(connection, options.CurrentValue.Features.LongRest,
                (timeProvider ?? TimeProvider.System).GetUtcNow(), cancellationToken).ConfigureAwait(false);

            await using DbCommand command = connection.CreateCommand();

            BuildListCommand(command, query, null, scope, limit, 0, includeEmbeddingProbe: true, longRest, cleanRecall: true);

            List<SagaMemoryCurationRow> rows = new(limit);

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(1))
                {
                    throw new InvalidOperationException("A clean recall payload exceeds its supported bounds.");
                }

                SagaMemoryDto memory = ReadMemory(reader, ranking);

                rows.Add(new(memory, new(memory.RetiredAtUtc, memory.PinnedAtUtc), reader.GetInt32(18) == 1));
            }

            return rows.ToArray();
        }, cancellationToken);
    }
}
