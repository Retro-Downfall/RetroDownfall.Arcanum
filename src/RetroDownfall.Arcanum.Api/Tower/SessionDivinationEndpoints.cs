using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Api.Tower;

/// <summary>
/// Session Divination via <c>POST /api/sessions/divine</c>: semantic search over Grimoire entries embedded by
/// <c>EntryWeavingService</c>. Every step degrades gracefully when The Weave is disabled or unavailable
/// — see the graceful-degradation matrix in <c>docs/Arcanum.DESIGN.md</c> §21.4.
/// </summary>
internal static class SessionDivinationEndpoints
{
    private const string DefaultStatusFilter = "active";

    private const int ContentPreviewChars = 200;

    // How many ranked candidates the first search looks at per requested result, and the most any search
    // looks at. A first window too few of whose hits survive the filters is followed by exactly one more
    // search, at the cap.
    private const int InitialWindowMultiplier = 4;

    private const int MaxCandidateWindow = 500;

    // Mirrors Sessions.Status's DB constraint (ArcanumDbContext: HasMaxLength(32)) and the only two
    // values ever written to that column (SessionRepository, SessionEndpoints). Allow-listing here
    // means an unrecognized/garbage status fails fast with a clear 400 instead of silently matching
    // zero rows.
    private static readonly string[] AllowedStatuses = ["active", "archived"];

    public static RouteGroupBuilder MapSessionDivinationEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapPost(
            "/sessions/divine",
            async (
                SemanticSearchRequest? request,
                IWeaveService weaveService,
                IDivinationService divinationService,
                ArcanumDbContext db,
                IGrimoireOrdinaryConnectionFactory connections,
                IOptionsMonitor<ArcanumSettings> options,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                EmbeddingSettings embeddings = options.CurrentValue.ResolveEmbeddings();

                if (!embeddings.Enabled || !embeddings.SessionSearchEnabled)
                {
                    return FailureResult(
                        traceId,
                        new Error(
                            ErrorCodes.Embeddings.FeatureDisabled,
                            "Session semantic search is disabled (Arcanum:Features:Embeddings and Arcanum:Features:SessionSearch must both be true)."));
                }

                if (request is null || string.IsNullOrWhiteSpace(request.Query))
                {
                    return FailureResult(
                        traceId,
                        new Error(ErrorCodes.Validation.InvalidBody, "Query is required."));
                }

                if (request.Status is { } status
                    && !AllowedStatuses.Contains(status.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    return FailureResult(
                        traceId,
                        new Error(
                            ErrorCodes.Validation.InvalidBody,
                            $"Status must be one of: {string.Join(", ", AllowedStatuses)}."));
                }

                if (!weaveService.IsAvailable)
                {
                    return FailureResult(
                        traceId,
                        new Error(ErrorCodes.Embeddings.ProviderUnavailable, "The embedding provider is unavailable."));
                }

                Result<Embedding<float>> embedResult = await weaveService
                    .EmbedAsync(request.Query.Trim(), ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (embedResult.IsFailure)
                {
                    return FailureResult(traceId, embedResult.Error);
                }

                int limit = ArcanumSettingClamps.EmbeddingsMaxResults(request.Limit ?? embeddings.MaxResults);

                float similarityThreshold = ArcanumSettingClamps.EmbeddingsSimilarityThreshold(embeddings.SimilarityThreshold);

                // The vector search ranks every embedded entry in the installation, and the Campaign and
                // status filters live on the Session the entry belongs to. Cutting the ranking at `limit`
                // first and filtering afterwards returned nothing for a Campaign whose closest matches sat
                // just below the cut. A first window of 4 x `limit` candidates that leaves too few
                // survivors (one more than `limit` is wanted, so hasMore can be answered) and did not exhaust
                // the ranking is followed by one search at the cap. Doubling in between repeated the whole
                // search for a few more candidates each time: eight searches for limit 1, each a scan of
                // every embedding on the managed path. The window only changes how many candidates are
                // looked at: the similarity threshold is applied inside every search and is never relaxed.
                int window = Math.Min(MaxCandidateWindow, limit * InitialWindowMultiplier);

                List<SemanticSessionSearchResult> survivors = [];

                // The wider search returns the narrower one's hits again. Only the ones not yet joined are
                // read, so the second pass never re-reads an Entry the first already filtered.
                HashSet<string> joined = new(StringComparer.Ordinal);

                bool rankingExhausted;

                while (true)
                {
                    Result<DivinationResult[]> searchResult = await divinationService
                        .SearchAsync(
                            "entry_embeddings_vec",
                            "EntryId",
                            "Embedding",
                            embedResult.Value,
                            window,
                            similarityThreshold,
                            ctx.RequestAborted)
                        .ConfigureAwait(false);

                    if (searchResult.IsFailure)
                    {
                        return FailureResult(traceId, searchResult.Error);
                    }

                    DivinationResult[] unjoined = [.. searchResult.Value.Where(hit => joined.Add(hit.Id))];

                    survivors.AddRange(await JoinSessionMetadataAsync(
                        db,
                        connections,
                        unjoined,
                        request.CampaignId,
                        request.Status,
                        ctx.RequestAborted).ConfigureAwait(false));

                    // A search that returned fewer rows than it was asked for has already ranked everything
                    // above the threshold, so widening the window cannot find another candidate.
                    rankingExhausted = searchResult.Value.Length < window;

                    if (survivors.Count > limit || rankingExhausted || window >= MaxCandidateWindow)
                    {
                        break;
                    }

                    window = MaxCandidateWindow;
                }

                // Each join ranks only its own hits, so the two passes are merged here, closest first.
                SemanticSessionSearchResult[] ranked = [.. survivors.OrderByDescending(static r => r.Similarity)];

                // Phase 2 does not implement cursor pagination over Divination hits, so hasMore says only
                // that the answer was cut: more matching hits were found than `limit` returned, or the
                // candidate window filled before the search could rule that out. It is never false while
                // another matching hit could exist.
                SemanticSearchResult payload = new(
                    ranked.Take(limit).ToArray(),
                    HasMore: ranked.Length > limit || !rankingExhausted,
                    NextCursor: null);

                return Results.Ok(
                    ApiResponse<SemanticSearchResult>.FromResult(Result<SemanticSearchResult>.Success(payload), traceId));
            })
        .WithName("SessionDivination");

        return apiGroup;
    }

    private static IResult FailureResult(string traceId, Error error) =>
        Results.Json(
            ApiResponse<SemanticSearchResult>.FromResult(Result<SemanticSearchResult>.Failure(error), traceId),
            ArcanumJsonContext.Default.ApiResponseSemanticSearchResult,
            statusCode: ArcanumErrorMapper.ResolveStatusCode(error.Code));

    /// <summary>
    /// Joins Divination hits (EntryId text + similarity) against <c>Entries</c>/<c>Sessions</c> to
    /// populate display metadata, applying the optional campaign/status filters and re-ranking by
    /// similarity (the SQL join does not preserve Divination's ordering). Returns every hit that survives
    /// the filters; cutting to the caller's limit is the caller's job.
    /// </summary>
    private static async Task<SemanticSessionSearchResult[]> JoinSessionMetadataAsync(
        ArcanumDbContext db,
        IGrimoireOrdinaryConnectionFactory connections,
        DivinationResult[] hits,
        Guid? campaignIdFilter,
        string? statusFilter,
        CancellationToken cancellationToken)
    {
        if (hits.Length == 0)
        {
            return [];
        }

        Dictionary<string, float> similarityByEntryId = new(StringComparer.Ordinal);

        foreach (DivinationResult hit in hits)
        {
            similarityByEntryId[hit.Id] = hit.Similarity;
        }

        if (db.Database.GetDbConnection() is not SqliteConnection scopedConnection)
        {
            throw new InvalidOperationException("The Grimoire requires a SQLCipher connection.");
        }

        Result<IGrimoireOrdinaryConnectionLease> acquired = await connections
            .AcquireScopedAsync(
                scopedConnection,
                CovenantSqliteConnectionMode.ReadOnly,
                cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            throw new GrimoireMaintenanceUnavailableException();
        }

        await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

        DbConnection connection = lease.Connection;

        await using DbCommand cmd = connection.CreateCommand();

        StringBuilder sql = new(
            """
            SELECT e."Id", e."SessionId", substr(e."Content", 1, @previewChars), e."Role", e."CreatedAt", s."Title"
            FROM "Entries" e
            INNER JOIN "Sessions" s ON s."Id" = e."SessionId"
            WHERE e."Id" IN (
            """);

        for (int i = 0; i < hits.Length; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            string paramName = $"@id{i.ToString(CultureInfo.InvariantCulture)}";

            sql.Append(paramName);

            AddParameter(cmd, paramName, hits[i].Id);
        }

        sql.Append(')');

        // Only the preview is returned, so only the preview is read: an Entry may hold up to
        // MaxEntryContentBytes, and the cap window joins up to 500 of them. SQLite's substr counts
        // characters (code points), and each is one or two UTF-16 units, so the first ContentPreviewChars
        // of them always cover the ContentPreviewChars units the preview is cut from below.
        AddParameter(cmd, "@previewChars", ContentPreviewChars);

        if (campaignIdFilter is { } campaignId)
        {
            sql.Append(" AND s.\"CampaignId\" = @campaignId");

            AddParameter(cmd, "@campaignId", NormalizeGuidText(campaignId));
        }

        string effectiveStatus = string.IsNullOrWhiteSpace(statusFilter) ? DefaultStatusFilter : statusFilter.Trim();

        sql.Append(" AND s.\"Status\" = @status");

        AddParameter(cmd, "@status", effectiveStatus);

        cmd.CommandText = sql.ToString();

        List<SemanticSessionSearchResult> results = [];

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string entryIdText = reader.GetString(0);

            Guid entryId = Guid.Parse(entryIdText);

            Guid sessionId = Guid.Parse(reader.GetString(1));

            string contentHead = reader.GetString(2);

            int roleValue = reader.GetInt32(3);

            DateTimeOffset createdAt = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture);

            string? title = reader.IsDBNull(5) ? null : reader.GetString(5);

            float similarity = similarityByEntryId.TryGetValue(entryIdText, out float sim) ? sim : 0f;

            string preview = contentHead[..Utf8Truncation.SafeCharSliceLength(contentHead, ContentPreviewChars)];

            results.Add(new SemanticSessionSearchResult(
                sessionId,
                title,
                entryId,
                ((MessageRole)roleValue).ToString().ToLowerInvariant(),
                preview,
                similarity,
                createdAt));
        }

        await GrimoireScopedConsumerTestSeam
            .PauseAsync(
                "SessionDivinationEndpoints.JoinSessionMetadataAsync",
                GrimoireScopedConsumerFinalUseKind.ReaderMaterialized,
                results.Count,
                cancellationToken)
            .ConfigureAwait(false);

        return results
            .OrderByDescending(static r => r.Similarity)
            .ToArray();
    }

    /// <summary>
    /// EF's SQLite provider stores Guid columns (e.g. <c>Sessions.CampaignId</c>) as uppercase
    /// "D"-format text — see the identical normalization in <c>SanctumBreachRepository</c>.
    /// </summary>
    private static string NormalizeGuidText(Guid id) => id.ToString().ToUpperInvariant();

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);
    }
}
