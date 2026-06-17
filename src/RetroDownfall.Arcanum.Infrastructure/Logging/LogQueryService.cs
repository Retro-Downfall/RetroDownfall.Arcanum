using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

public sealed class LogQueryService(ILogRingBuffer buffer) : ILogQueryService
{

    public Task<LogQueryResult> QueryAsync(LogQueryRequest request, CancellationToken ct)
    {

        ct.ThrowIfCancellationRequested();

        IEnumerable<LogEntry> query = buffer.GetSnapshot().AsEnumerable();

        query = ApplyFilters(query, request);

        if (request.BeforeSequence is long beforeSequence)
        {

            query = query.Where(e => e.Sequence < beforeSequence);
        }

        int limit = ArcanumSettingClamps.LogQueryLimit(request.Limit ?? 100);

        LogEntry[] page = query
            .OrderByDescending(e => e.Sequence)
            .Take(limit + 1)
            .ToArray();

        bool hasMore = page.Length > limit;

        if (hasMore)
        {

            page = page[..limit];
        }

        long? nextBeforeSequence = hasMore && page.Length > 0 ? page[^1].Sequence : null;

        return Task.FromResult(new LogQueryResult(page, nextBeforeSequence, hasMore));
    }

    public async IAsyncEnumerable<LogEntry> StreamAsync(
        LogQueryRequest? request,
        [EnumeratorCancellation] CancellationToken ct)
    {

        await foreach (LogEntry entry in buffer.StreamAsync(ct).ConfigureAwait(false))
        {

            if (request is null || MatchesFilters(entry, request))
            {

                yield return entry;
            }
        }
    }

    private static IEnumerable<LogEntry> ApplyFilters(IEnumerable<LogEntry> entries, LogQueryRequest request) =>
        entries.Where(e => MatchesFilters(e, request));

    private static bool MatchesFilters(LogEntry entry, LogQueryRequest request)
    {

        if (request.MinLevel is LogLevel minLevel && entry.Level < minLevel)
        {

            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.Category)
            && !string.Equals(entry.Category, request.Category, StringComparison.OrdinalIgnoreCase))
        {

            return false;
        }

        if (request.From is DateTimeOffset from && entry.Timestamp < from)
        {

            return false;
        }

        if (request.To is DateTimeOffset to && entry.Timestamp > to)
        {

            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {

            string search = request.Search;

            bool matchesMessage = entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase);

            bool matchesCategory = entry.Category.Contains(search, StringComparison.OrdinalIgnoreCase);

            if (!matchesMessage && !matchesCategory)
            {

                return false;
            }
        }

        return true;
    }

}
