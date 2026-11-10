using System.Data.Common;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Lexicon;

internal sealed partial class LexiconService
{
    public Task<Result<LexiconSearchRow[]>> SearchMemoryPageAsync(string query, LexiconScope scope, int limit,
        LexiconSearchPosition? after, CancellationToken cancellationToken = default, int contentByteLimit = 65536) =>
        GuildMemoryAccess.RunResultAsync(GuildMemoryStore.Lexicon, GuildMemoryOperation.Read, async ct =>
        {
            DbConnection connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

            bool curation = await HasCurationAsync(connection, ct).ConfigureAwait(false);

            await using DbCommand command = connection.CreateCommand();

            string granted = GuildMemoryAccess.SqlScope(command, GuildMemoryStore.Lexicon, GuildMemoryOperation.Read, "e.ScopeCampaignId");

            string shadowGranted = GuildMemoryAccess.SqlScope(command, GuildMemoryStore.Lexicon, GuildMemoryOperation.Read, "shadow.ScopeCampaignId");

            string seek = "";

            if (after is not null)
            {
                await using DbCommand resume = connection.CreateCommand();

                string resumeGranted = GuildMemoryAccess.SqlScope(resume, GuildMemoryStore.Lexicon, GuildMemoryOperation.Read, "r.ScopeCampaignId");

                string resumeShadowGranted = GuildMemoryAccess.SqlScope(resume, GuildMemoryStore.Lexicon, GuildMemoryOperation.Read, "shadow.ScopeCampaignId");

                resume.CommandText = $"""
                    SELECT r.NameNormalized FROM lexicon_entries r
                    WHERE r.Id=@after AND r.ScopeCampaignId=@afterScope AND r.ScopeCampaignId IN (@scope,'')
                      AND {resumeGranted} {(curation ? "AND r.RetiredAtUtc IS NULL" : "")}
                      {CleanRecallPredicate("r")}
                      AND NOT (r.ScopeCampaignId='' AND @scope<>'' AND EXISTS(
                        SELECT 1 FROM lexicon_entries shadow WHERE shadow.ScopeCampaignId=@scope
                          AND shadow.NameNormalized=r.NameNormalized AND {resumeShadowGranted}
                          {(curation ? "AND shadow.RetiredAtUtc IS NULL" : "")}
                          {CleanRecallPredicate("shadow")}))
                    LIMIT 1
                    """;

                AddParameter(resume, "@after", after.Id);

                AddParameter(resume, "@afterScope", after.ScopeKey);

                AddParameter(resume, "@scope", scope.Key);

                if (await resume.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string name)
                {
                    return new Error(ErrorCodes.Validation.InvalidBody, "The memory search continuation source is no longer eligible.");
                }

                // Only the currently admitted source may contribute its ordering key.
                seek = " AND (e.NameNormalized,e.ScopeCampaignId,e.Id) > (@afterName,@afterScope,@after)";

                AddParameter(command, "@afterName", name);

                AddParameter(command, "@after", after.Id);

                AddParameter(command, "@afterScope", after.ScopeKey);
            }

            string columns = RecallMatchColumns(curation, true, "e").Replace("THEN e.FactsJson ELSE NULL END",
                "THEN CASE WHEN octet_length(e.FactsJson)>@searchContentBytes THEN json_array(substr(e.FactsText,1,@searchContentChars)||@searchExcerptMarker) ELSE e.FactsJson END ELSE NULL END", StringComparison.Ordinal);

            AddParameter(command, "@searchContentBytes", contentByteLimit);

            AddParameter(command, "@searchContentChars", Math.Max(1, (contentByteLimit - 96) / 4));

            AddParameter(command, "@searchExcerptMarker", " [TRUNCATED excerpt; inspect the source for full content]");

            command.CommandText = $"""
                SELECT {columns}, e.NameNormalized
                FROM lexicon_entries e
                WHERE e.ScopeCampaignId IN (@scope,'') AND {granted}
                  {(curation ? "AND e.RetiredAtUtc IS NULL" : "")}
                  {CleanRecallPredicate("e")}
                  AND NOT (e.ScopeCampaignId='' AND @scope<>'' AND EXISTS(
                    SELECT 1 FROM lexicon_entries shadow WHERE shadow.ScopeCampaignId=@scope
                      AND shadow.NameNormalized=e.NameNormalized AND {shadowGranted}
                      {(curation ? "AND shadow.RetiredAtUtc IS NULL" : "")}
                      {CleanRecallPredicate("shadow")}))
                  AND (arcanum_ordinal_contains(e.Name,@query) OR arcanum_ordinal_contains(e.Type,@query)
                    OR EXISTS(SELECT 1 FROM json_each(e.FactsJson) fact WHERE arcanum_ordinal_contains(fact.value,@query)))
                  {seek}
                ORDER BY e.NameNormalized,e.ScopeCampaignId,e.Id LIMIT @limit
                """;

            AddParameter(command, "@scope", scope.Key);

            AddParameter(command, "@query", query);

            AddParameter(command, "@limit", limit);

            List<LexiconSearchRow> results = [];

            await using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                LexiconEntryDto entry = ReadEntry(reader);

                results.Add(new(entry, new(reader.GetString(reader.FieldCount - 1),
                    reader.GetString(5), entry.Id.ToString("N"))));
            }

            return Result<LexiconSearchRow[]>.Success([.. results]);
        }, cancellationToken);
}
