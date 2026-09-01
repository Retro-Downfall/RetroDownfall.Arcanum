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

                Result<DivinationResult[]> searchResult = await divinationService
                    .SearchAsync(
                        "entry_embeddings_vec",
                        "EntryId",
                        "Embedding",
                        embedResult.Value,
                        limit,
                        similarityThreshold,
                        ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (searchResult.IsFailure)
                {

                    return FailureResult(traceId, searchResult.Error);

                }

                SemanticSessionSearchResult[] joined = await JoinSessionMetadataAsync(
                    db,
                    connections,
                    searchResult.Value,
                    request.CampaignId,
                    request.Status,
                    limit,
                    ctx.RequestAborted).ConfigureAwait(false);

                // Phase 2 does not implement cursor pagination over Divination hits — the vector
                // search itself is already bounded by `limit`, so there is no further page to fetch.
                SemanticSearchResult payload = new(joined, HasMore: false, NextCursor: null);

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
    /// similarity (the SQL join does not preserve Divination's ordering).
    /// </summary>
    private static async Task<SemanticSessionSearchResult[]> JoinSessionMetadataAsync(
        ArcanumDbContext db,
        IGrimoireOrdinaryConnectionFactory connections,
        DivinationResult[] hits,
        Guid? campaignIdFilter,
        string? statusFilter,
        int limit,
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
            SELECT e."Id", e."SessionId", e."Content", e."Role", e."CreatedAt", s."Title"
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

            string content = reader.GetString(2);

            int roleValue = reader.GetInt32(3);

            DateTimeOffset createdAt = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture);

            string? title = reader.IsDBNull(5) ? null : reader.GetString(5);

            float similarity = similarityByEntryId.TryGetValue(entryIdText, out float sim) ? sim : 0f;

            string preview = content[..Utf8Truncation.SafeCharSliceLength(content, ContentPreviewChars)];

            results.Add(new SemanticSessionSearchResult(
                sessionId,
                title,
                entryId,
                ((MessageRole)roleValue).ToString().ToLowerInvariant(),
                preview,
                similarity,
                createdAt));

        }

        return results
            .OrderByDescending(static r => r.Similarity)
            .Take(limit)
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
