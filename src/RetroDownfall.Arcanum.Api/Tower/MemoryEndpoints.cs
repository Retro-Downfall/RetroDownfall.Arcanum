using System.Globalization;
using System.Data;

using System.Data.Common;

using System.Diagnostics;
using System.Text.Json.Serialization.Metadata;

using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using Microsoft.EntityFrameworkCore;

using Microsoft.Data.Sqlite;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Core.Weave.Tapestry;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Api.Tower;

internal static class MemoryEndpoints
{
    /// <summary>
    /// Ceiling on the number of rows <c>POST /api/memory/search</c> materializes and returns, shared
    /// across every scope of one request rather than granted per scope.
    /// </summary>
    /// <remarks>
    /// A one-character query matches essentially every Grimoire entry, attachment chunk and workspace
    /// chunk on a mature install, and every matched row carries its full text. Without this bound the
    /// handler reads the whole corpus into one list — the same failure <c>GrimoireRepository</c>
    /// windows long threads to avoid and <c>SagaEndpoints</c> clamps to <c>MaxListLimit</c> to avoid.
    /// Set to <see cref="ArcanumSettingClamps.ListQueryLimit"/>'s upper bound so this surface is
    /// bounded exactly like its neighbours.
    /// </remarks>
    internal const int SearchResultLimit = 10_000;

    /// <summary>
    /// Ceiling, in UTF-8 bytes of returned content, on what <c>POST /api/memory/search</c> reads and writes,
    /// shared across every scope of one request as <see cref="SearchResultLimit"/> is.
    /// </summary>
    internal const long SearchResultByteBudget = 8L * 1024L * 1024L;

    /// <summary>
    /// The UTF-8 bytes of content one search has already taken, shared across its scopes.
    /// </summary>
    /// <remarks>
    /// Checked after each row rather than before, so a search overshoots by at most the one row that crossed
    /// the budget. A scope that stops reading because the budget is spent cannot know whether it left rows
    /// behind without reading another one, so it reports <c>hasMore</c>: the answer is never "complete" while
    /// a row may have been left unread.
    /// </remarks>
    private sealed class ResultByteBudget
    {
        private long _used;

        internal bool Exhausted => _used >= SearchResultByteBudget;

        /// <summary>Charges <paramref name="content"/>; true means the budget is now spent and the scope must stop.</summary>
        internal bool Charge(string content)
        {
            _used += System.Text.Encoding.UTF8.GetByteCount(content);

            if (!Exhausted)
            {
                return false;
            }

            return true;
        }
    }

    private const string CampaignSummaryRetention = "Campaign lifetime; archive preserves continuity, source deletion invalidates it, and Campaign-summary reset or enabled retention removes derived summaries.";

    private const string SessionRetention = "Session lifetime; archiving retains it and session purge removes it.";

    private const string PinRetention = "Until explicitly unpinned or the owning session is purged.";

    private const string AttachmentRetention = "Bound to the session; deleting an attachment does not delete Saga or Lexicon facts derived from it.";

    private const string AttachmentIndexRetention = "Derived and rebuildable from bound attachment versions; removed with the attachment or index reset.";

    private const string LexiconRetention =
        "Durable until explicitly deleted, erased, or reset, or pruned by an enabled `lexicon-entries` "
            + "retention rule; pinned entries are exempt from pruning, and retirement keeps an entry "
            + "inspectable but out of matching.";

    private const string SagaRetention =
        "Durable until explicitly deleted, erased, or reset, or pruned by an enabled `saga-memories` "
            + "retention rule; pinned memories are exempt from pruning, and retirement keeps a memory "
            + "inspectable but out of retrieval.";

    private const string WorkspaceRetention = "Derived and rebuildable from registered workspace files; not session memory.";

    private const string TapestryRetention = "Derived and fully rebuildable from the corpora it summarizes; dropped by the tapestry embeddings reset and rebuilt on the next sweep.";

    public static RouteGroupBuilder MapMemoryEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapGet(
            "/memory/status",
            (ArcanumDbContext db, IOptionsMonitor<ArcanumSettings> options, HttpContext context) =>
                HandleStatusAsync(null, db, options, context))
        .WithName("GetMemoryStatus");

        apiGroup.MapGet(
            "/memory/status/{sessionId:guid}",
            (Guid sessionId, ArcanumDbContext db, IOptionsMonitor<ArcanumSettings> options, HttpContext context) =>
                HandleStatusAsync(sessionId, db, options, context))
        .WithName("GetSessionMemoryStatus");

        apiGroup.MapGet(
            "/memory/sources",
            (ArcanumDbContext db, IOptionsMonitor<ArcanumSettings> options, HttpContext context) =>
                HandleSourcesAsync(null, db, options, context))
        .RequireConditionalCovenantReadAuthority()
        .WithName("GetMemorySources");

        apiGroup.MapGet(
            "/memory/sources/{sessionId:guid}",
            (Guid sessionId, ArcanumDbContext db, IOptionsMonitor<ArcanumSettings> options, HttpContext context) =>
                HandleSourcesAsync(sessionId, db, options, context))
        .RequireConditionalCovenantReadAuthority()
        .WithName("GetSessionMemorySources");

        apiGroup.MapPost(
            "/memory/search",
            HandleSearchAsync)
        .RequireConditionalCovenantReadAuthority()
        .WithName("SearchMemory");

        apiGroup.MapGet(
            "/memory/explain",
            (ArcanumDbContext db, IOptionsMonitor<ArcanumSettings> options, HttpContext context) =>
                HandleExplainAsync(null, db, options, context))
        .RequireConditionalCovenantReadAuthority()
        .WithName("ExplainMemory");

        apiGroup.MapGet(
            "/memory/explain/{sessionId:guid}",
            (Guid sessionId, ArcanumDbContext db, IOptionsMonitor<ArcanumSettings> options, HttpContext context) =>
                HandleExplainAsync(sessionId, db, options, context))
        .RequireConditionalCovenantReadAuthority()
        .WithName("ExplainSessionMemory");

        apiGroup.MapGet(
            "/memory/lexicon",
            HandleLexiconListAsync)
        .RequireConditionalCovenantReadAuthority()
        .WithName("ListLexiconEntries");

        apiGroup.MapGet(
            "/memory/lexicon/{**name}",
            HandleLexiconShowAsync)
        .RequireConditionalCovenantReadAuthority()
        .WithName("GetLexiconEntry");

        apiGroup.MapDelete(
            "/memory/lexicon/{**name}",
            HandleLexiconDeleteAsync)
        .RequireConditionalSensitivityRetentionPurge()
        .WithName("DeleteLexiconEntry");

        apiGroup.MapMemoryReviewEndpoints();

        return apiGroup;
    }

    /// <summary>
    /// The scope a turn on this surface would draw from, resolved through the same seam retrieval uses.
    /// </summary>
    /// <remarks>
    /// Resolved from the Session the route names, because a Session's Campaign binding is the canonical
    /// statement of its authority. A route naming no Session describes the installation-wide picture,
    /// which is what an operator inspecting memory without a turn in mind is asking for.
    /// </remarks>
    private static async Task<MemoryScope> ResolveScopeAsync(Guid? sessionId, HttpContext context)
    {
        IMemoryScopeResolver? resolver = context.RequestServices.GetService<IMemoryScopeResolver>();

        return resolver is null
            ? MemoryScope.Installation
            : await resolver.ResolveForSessionAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleStatusAsync(
        Guid? sessionId,
        ArcanumDbContext db,
        IOptionsMonitor<ArcanumSettings> options,
        HttpContext context)
    {
        MemoryScope scope = await ResolveScopeAsync(sessionId, context).ConfigureAwait(false);

        IGrimoireOrdinaryConnectionFactory connections = context.RequestServices
            .GetRequiredService<IGrimoireOrdinaryConnectionFactory>();

        GrimoireSchemaTransitionStatusDto schemaTransitions;

        await using (IGrimoireOrdinaryConnectionLease lease = await OpenConnectionAsync(
            db, connections, context.RequestAborted).ConfigureAwait(false))
        {
            schemaTransitions = await RetroDownfall.Arcanum.Infrastructure.Diagnostics.GrimoireSchemaTransitionDiagnostics
                .ReadAsync(lease.Connection, context.RequestAborted).ConfigureAwait(false);
        }

        // Later stores may not exist while Core is evolving. Their absent tables are not empty
        // stores, and diagnostics must remain readable before their schemas become available.
        bool coreTransition = schemaTransitions.Runs.Any(static run => run.Tier == "Core");

        Result<MemoryStatusDto> result = coreTransition
            ? Result<MemoryStatusDto>.Success(new MemoryStatusDto(
                sessionId, null, [], SchemaTransitions: schemaTransitions, StoreCensusAvailable: false))
            : await BuildStatusAsync(
                sessionId,
                db,
                options.CurrentValue,
                context.RequestServices.GetService<ICovenantAvailability>(),
                context.RequestServices.GetService<ICovenantManagementService>(),
                connections,
                context.RequestAborted).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            result = Result<MemoryStatusDto>.Success(
                result.Value with
                {
                    CampaignScope = MemoryCampaignScopeReport.Describe(scope),
                    SagaRanking = result.Value.StoreCensusAvailable
                        ? await context.RequestServices.GetRequiredService<ISagaMemoryStore>()
                            .ReadRankingStatusAsync(scope, sessionId, context.RequestAborted).ConfigureAwait(false)
                        : null,
                });
        }

        string traceId = TraceId(context);

        return Results.Json(
            ApiResponse<MemoryStatusDto>.FromResult(result, traceId),
            ArcanumJsonContext.Default.ApiResponseMemoryStatusDto,
            statusCode: result.IsSuccess
                ? StatusCodes.Status200OK
                : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));
    }

    private static Task<IResult> HandleSourcesAsync(
        Guid? sessionId,
        ArcanumDbContext db,
        IOptionsMonitor<ArcanumSettings> options,
        HttpContext context) =>
        RespondToLexiconCountsAsync(
            context.RequestServices.GetRequiredService<ILexiconCurationService>(),
            context,
            counts => BuildSourcesAsync(sessionId, db, options, context, counts.Retained),
            ArcanumJsonContext.Default.ApiResponseMemorySourcesDto);

    private static async Task<Result<MemorySourcesDto>> BuildSourcesAsync(
        Guid? sessionId,
        ArcanumDbContext db,
        IOptionsMonitor<ArcanumSettings> options,
        HttpContext context,
        int lexiconCount)
    {
        Result<MemoryStatusDto> status = await BuildStatusAsync(
            sessionId,
            db,
            options.CurrentValue,
            context.RequestServices.GetService<ICovenantAvailability>(),
            context.RequestServices.GetService<ICovenantManagementService>(),
            context.RequestServices.GetRequiredService<IGrimoireOrdinaryConnectionFactory>(),
            context.RequestAborted,
            lexiconCount).ConfigureAwait(false);

        Result<MemorySourcesDto> result;

        if (status.IsFailure)
        {
            result = Result<MemorySourcesDto>.Failure(status.Error);
        }
        else
        {
            MemorySourceDto[] sources = status.Value.Stores
                .Select(static store => new MemorySourceDto(
                    store.Name,
                    store.Scope,
                    ProvenanceFor(store.Name),
                    store.Retention,
                    store.Enabled,
                    store.Count))
                .ToArray();

            result = Result<MemorySourcesDto>.Success(
                new MemorySourcesDto(status.Value.SessionId, sources, status.Value.CampaignSummary));
        }

        return result;
    }

    private static Task<IResult> HandleExplainAsync(
        Guid? sessionId,
        ArcanumDbContext db,
        IOptionsMonitor<ArcanumSettings> options,
        HttpContext context)
    {
        string? similarityText = context.Request.Query["similarity"].FirstOrDefault();
        string? sagaId = context.Request.Query["sagaId"].FirstOrDefault();
        bool invalid = context.Request.Query["similarity"].Count > 1 || context.Request.Query["sagaId"].Count > 1
            || (similarityText is not null && (!float.TryParse(similarityText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float similarity) || !float.IsFinite(similarity) || similarity is < -1 or > 1))
            || (sagaId is not null && (string.IsNullOrWhiteSpace(sagaId) || System.Text.Encoding.UTF8.GetByteCount(sagaId) > 1024 || sagaId.Any(char.IsControl)));
        if (invalid)
        {
            Result<MemoryExplainDto> result = Result<MemoryExplainDto>.Failure(new Error(ErrorCodes.Validation.InvalidBody,
                "Use one optional Saga identity and a finite reference similarity between -1 and 1."));
            return Task.FromResult<IResult>(Results.Json(ApiResponse<MemoryExplainDto>.FromResult(result, TraceId(context)),
                ArcanumJsonContext.Default.ApiResponseMemoryExplainDto, statusCode: StatusCodes.Status400BadRequest));
        }

        return RespondToLexiconCountsAsync(
            context.RequestServices.GetRequiredService<ILexiconCurationService>(),
            context,
            counts => BuildExplainAsync(sessionId, db, options, context, counts.Eligible),
            ArcanumJsonContext.Default.ApiResponseMemoryExplainDto);
    }

    private static async Task<Result<MemoryExplainDto>> BuildExplainAsync(
        Guid? sessionId,
        ArcanumDbContext db,
        IOptionsMonitor<ArcanumSettings> options,
        HttpContext context,
        int lexiconCount)
    {
        MemoryScope scope = await ResolveScopeAsync(sessionId, context).ConfigureAwait(false);

        Result<MemoryStatusDto> status = await BuildStatusAsync(
            sessionId,
            db,
            options.CurrentValue,
            context.RequestServices.GetService<ICovenantAvailability>(),
            context.RequestServices.GetService<ICovenantManagementService>(),
            context.RequestServices.GetRequiredService<IGrimoireOrdinaryConnectionFactory>(),
            context.RequestAborted,
            lexiconCount).ConfigureAwait(false);

        Result<MemoryExplainDto> result;

        if (status.IsFailure)
        {
            result = Result<MemoryExplainDto>.Failure(status.Error);
        }
        else
        {
            Dictionary<string, MemoryStoreStatusDto> stores = status.Value.Stores
                .ToDictionary(static store => store.Name, StringComparer.Ordinal);

            bool hasSession = status.Value.SessionId is not null;

            // What a turn in this scope can reach, not what is stored: status counts the rows, and a
            // retired, unembedded, or other-Campaign memory is a row no turn can recall.
            bool sagaRetrievable = stores["Saga"].Enabled
                && await context.RequestServices.GetRequiredService<ISagaMemoryStore>()
                    .AnyRetrievableAsync(scope, context.RequestAborted).ConfigureAwait(false);

            MemoryEligibilityDto[] eligibility =
            [
                Explain(
                    stores["Session Entries"],
                    hasSession && stores["Session Entries"].Count > 0,
                    hasSession
                        ? "The active session transcript is considered in sequence and may be compressed when a Session Summary watermark applies."
                        : "Select a session to make a transcript eligible for a turn."),
                Explain(
                    stores["Pinned Entries"],
                    hasSession && stores["Pinned Entries"].Count > 0,
                    hasSession
                        ? "Pinned entries remain eligible even when older unpinned transcript entries are compressed."
                        : "Pins belong to a specific session; select one to inspect eligibility."),
                Explain(
                    stores["Session Summary"],
                    hasSession && stores["Session Summary"].Count > 0,
                    hasSession
                        ? "The summary becomes compressed context when the session watermark and context-pressure policy require it."
                        : "Session summaries are stored on sessions; select one to inspect eligibility."),
                Explain(
                    stores["Campaign Summary"],
                    hasSession && stores["Campaign Summary"].Enabled
                        && status.Value.CampaignSummary is { CurrentArtifactId: not null, RefoldRequired: false },
                    !stores["Campaign Summary"].Enabled
                        ? "Campaign continuity is disabled; retained summaries remain inspectable and resettable."
                        : status.Value.CampaignSummary is { RefoldRequired: true }
                            ? "The Campaign source evidence changed; a fresh refold is required before continuity is eligible."
                            : status.Value.CampaignSummary is { CurrentArtifactId: not null }
                                ? "A published Campaign summary provides cross-session continuity independently of Session compression."
                                : "No current Campaign summary is available for the selected Session."),
                Explain(
                    stores["Attachments"],
                    hasSession && stores["Attachments"].Enabled && stores["Attachments"].Count > 0,
                    "Attachment metadata is eligible for its owning session; bytes are materialized only by explicit attachment or retrieval rules."),
                Explain(
                    stores["Indexed Attachment Chunks"],
                    hasSession && stores["Indexed Attachment Chunks"].Enabled && stores["Indexed Attachment Chunks"].Count > 0,
                    "Indexed attachment chunks are candidates only for the owning session and only when the current prompt retrieves them."),
                Explain(
                    stores["Lexicon"],
                    stores["Lexicon"].Enabled && stores["Lexicon"].Count > 0,
                    "Lexicon entities are candidates when the current prompt matches their names or facts; inspection does not promote new facts."),
                Explain(
                    stores["Saga"],
                    sagaRetrievable,
                    "Saga memories are candidates when semantic retrieval for the current prompt selects them; they remain distinct from attachments and Lexicon. "
                    + MemoryCampaignScopeReport.Describe(scope).Detail),
                Explain(
                    stores["Workspace Index"],
                    stores["Workspace Index"].Enabled && stores["Workspace Index"].Count > 0,
                    "Workspace chunks are candidates when a bound workspace and the current prompt retrieve them; they are not copied into durable memory."),
                Explain(
                    stores["Tapestry"],
                    stores["Tapestry"].Enabled && stores["Tapestry"].Count > 0,
                    "Tapestry nodes are candidates when the current prompt retrieves them from a published generation; a summary that overlaps an exact excerpt already selected is suppressed."),
            ];

            result = Result<MemoryExplainDto>.Success(
                new MemoryExplainDto(
                    status.Value.SessionId,
                    status.Value.SessionTitle,
                    eligibility,
                    MemoryCampaignScopeReport.Describe(scope),
                    status.Value.CampaignSummary,
                    await context.RequestServices.GetRequiredService<ISagaMemoryStore>().ExplainRankingAsync(
                        scope,
                        context.Request.Query["sagaId"].FirstOrDefault(),
                        context.Request.Query["similarity"].FirstOrDefault() is { } reference
                            ? float.Parse(reference, System.Globalization.CultureInfo.InvariantCulture) : 0,
                        context.RequestAborted).ConfigureAwait(false)));
        }

        return result;
    }

    private static async Task<IResult> HandleSearchAsync(
        MemorySearchRequest? request,
        ArcanumDbContext db,
        ILexiconCurationService lexicon,
        ISagaMemoryStore sagaStore,
        IGrimoireOrdinaryConnectionFactory connections,
        HttpContext context)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Query))
        {
            Result<MemorySearchResponse> invalid = Result<MemorySearchResponse>.Failure(
                new Error(
                    ErrorCodes.Validation.InvalidBody,
                    "A memory search query is required."));

            return Results.Json(
                ApiResponse<MemorySearchResponse>.FromResult(invalid, TraceId(context)),
                ArcanumJsonContext.Default.ApiResponseMemorySearchResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!IsKnownScope(request.Scope))
        {
            Result<MemorySearchResponse> invalid = Result<MemorySearchResponse>.Failure(
                new Error(
                    ErrorCodes.Validation.InvalidBody,
                    "Memory search scope must be session, attachments, workspace, saga, lexicon, or all."));

            return Results.Json(
                ApiResponse<MemorySearchResponse>.FromResult(invalid, TraceId(context)),
                ArcanumJsonContext.Default.ApiResponseMemorySearchResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Refused rather than clamped. A caller that asked for 50,000, silently got the budget, and
        // read hasMore:false would take a truncated page for the whole answer.
        if (request.Limit is { } requestedLimit
            && (requestedLimit < 1 || requestedLimit > SearchResultLimit))
        {
            Result<MemorySearchResponse> invalid = Result<MemorySearchResponse>.Failure(
                new Error(
                    ErrorCodes.Validation.InvalidBody,
                    $"Memory search limit must be between 1 and {SearchResultLimit}."));

            return Results.Json(
                ApiResponse<MemorySearchResponse>.FromResult(invalid, TraceId(context)),
                ArcanumJsonContext.Default.ApiResponseMemorySearchResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        await using IGrimoireOrdinaryConnectionLease lease = await OpenConnectionAsync(
            db,
            connections,
            context.RequestAborted).ConfigureAwait(false);

        SqliteConnection connection = lease.Connection;

        if (request.SessionId is { } sessionId
            && !await SessionExistsAsync(
                connection,
                sessionId,
                context.RequestAborted).ConfigureAwait(false))
        {
            Result<MemorySearchResponse> missing = Result<MemorySearchResponse>.Failure(
                new Error(ErrorCodes.Session.NotFound, "Session was not found."));

            return Results.Json(
                ApiResponse<MemorySearchResponse>.FromResult(missing, TraceId(context)),
                ArcanumJsonContext.Default.ApiResponseMemorySearchResponse,
                statusCode: StatusCodes.Status404NotFound);
        }

        (string Path, string Label)? workspace = null;

        if (Includes(request.Scope, MemorySearchScope.Workspace)
            && !string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            workspace = await ResolveSearchWorkspaceAsync(
                connection,
                request.WorkspaceId,
                context.RequestAborted).ConfigureAwait(false);

            if (workspace is null)
            {
                Result<MemorySearchResponse> missing = Result<MemorySearchResponse>.Failure(
                    new Error(ErrorCodes.Workspace.NotFound, "The workspace was not found."));

                return Results.Json(
                    ApiResponse<MemorySearchResponse>.FromResult(missing, TraceId(context)),
                    ArcanumJsonContext.Default.ApiResponseMemorySearchResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }
        }

        string query = request.Query.Trim();

        MemoryScope effectiveScope = await ResolveScopeAsync(request.SessionId, context).ConfigureAwait(false);

        string binding = MemorySearchContinuationCodec.Binding(query, request, workspace?.Path, effectiveScope);

        MemorySearchPosition position = new(0);

        if (request.Continuation is not null
            && (!MemorySearchContinuationCodec.TryRead(request.Continuation, binding, out position)
                || !MemorySearchContinuationCodec.AppliesTo(request.Scope, position.Phase)))
        {
            return Results.Json(ApiResponse<MemorySearchResponse>.FromResult(
                Result<MemorySearchResponse>.Failure(new Error(ErrorCodes.Validation.InvalidBody, "The memory search continuation is invalid for this request.")), TraceId(context)),
                ArcanumJsonContext.Default.ApiResponseMemorySearchResponse, statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Scope == MemorySearchScope.Saga && position.Phase < 4) position = new(4);

        if (request.Scope == MemorySearchScope.Lexicon && position.Phase < 5) position = new(5);

        int budget = request.Limit ?? SearchResultLimit;

        List<MemorySearchResultDto> results = [];

        ResultByteBudget bytes = new();

        MemorySearchTruncationReason? reason = null;

        if (position.Phase < 4)
        {
            Result<NativeMemorySearchPage> read = await NativeMemorySearchReader.ReadAsync(connection, query, request.Scope,
                request.SessionId, workspace, position, budget, context.RequestAborted).ConfigureAwait(false);

            if (read.IsFailure)
            {
                return Results.Json(ApiResponse<MemorySearchResponse>.FromResult(Result<MemorySearchResponse>.Failure(read.Error), TraceId(context)),
                    ArcanumJsonContext.Default.ApiResponseMemorySearchResponse, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            NativeMemorySearchPage page = read.Value;

            foreach (NativeMemorySearchHit hit in page.Hits)
            {
                results.Add(hit.Result);

                _ = bytes.Charge(hit.Result.Content);
            }

            if (Includes(request.Scope, MemorySearchScope.Session) && position.Phase < 2)
            {
                await GrimoireScopedConsumerTestSeam.PauseAsync("MemoryEndpoints.SearchSessionAsync",
                    GrimoireScopedConsumerFinalUseKind.ReaderMaterialized, results.Count, context.RequestAborted).ConfigureAwait(false);
            }

            position = page.Position;

            reason = page.Reason;
        }

        if (reason is null && position.Phase <= 4)
        {
            if (Includes(request.Scope, MemorySearchScope.Saga))
            {
                int before = results.Count;

                if (before >= budget || bytes.Exhausted)
                {
                    reason = bytes.Exhausted ? MemorySearchTruncationReason.ContentBytes : MemorySearchTruncationReason.ResultLimit;
                }
                else
                {
                    List<SagaMemoryPosition> positions = [];

                    await SearchSagaAsync(sagaStore, query, request.SessionId, effectiveScope, results,
                        budget - before + 1, bytes, context.RequestAborted,
                        position.Id is null ? null : new(position.Time!, position.Id), positions.Add).ConfigureAwait(false);

                    if (results.Count > budget)
                    {
                        results.RemoveRange(budget, results.Count - budget);

                        reason = MemorySearchTruncationReason.ResultLimit;
                    }
                    else if (bytes.Exhausted) reason = MemorySearchTruncationReason.ContentBytes;

                    int kept = results.Count - before;

                    if (kept > 0)
                    {
                        SagaMemoryPosition last = positions[kept - 1];

                        position = new(4, last.CreatedAt, Id: last.Id);
                    }
                }
            }

            if (reason is null) position = new(5);
        }

        MemorySearchResponse Payload()
        {
            List<MemorySearchScopeStatusDto> statuses = [];

            foreach (MemorySearchScope candidate in new[] { MemorySearchScope.Session, MemorySearchScope.Attachments, MemorySearchScope.Workspace, MemorySearchScope.Saga, MemorySearchScope.Lexicon })
            {
                if (!Includes(request.Scope, candidate)) continue;

                int phase = candidate switch { MemorySearchScope.Session => 1, MemorySearchScope.Attachments => 2, MemorySearchScope.Workspace => 3, MemorySearchScope.Saga => 4, _ => 5 };

                statuses.Add(new(candidate, results.Count(hit => hit.Scope == candidate), reason is not null && phase >= position.Phase));
            }

            return new(query, request.Scope, [.. results], [.. statuses], reason is not null,
                reason is null ? null : MemorySearchContinuationCodec.Write(binding, position), reason);
        }

        if (reason is null && position.Phase <= 5 && Includes(request.Scope, MemorySearchScope.Lexicon))
        {
            int left = budget - results.Count;

            if (left <= 0 || bytes.Exhausted)
            {
                reason = bytes.Exhausted ? MemorySearchTruncationReason.ContentBytes : MemorySearchTruncationReason.ResultLimit;
            }
            else
            {
                LexiconSearchPosition? after = position.Id is null ? null : new("", position.Tier!, position.Id);

                return await RespondToLexiconReadAsync(context, null,
                    lease => lexicon.SearchInspectionPageAsync(query, left + 1, after, lease, context.RequestAborted),
                    rows =>
                    {
                        int taken = 0;

                        foreach (LexiconSearchRow row in rows)
                        {
                            if (taken >= left)
                            {
                                reason = MemorySearchTruncationReason.ResultLimit;

                                break;
                            }

                            int before = results.Count;

                            AddLexiconMatches([row.Entry], query, results, 1, bytes);

                            taken += results.Count - before;

                            position = new(5, Id: row.Position.Id, Tier: row.Position.ScopeKey);

                            if (bytes.Exhausted)
                            {
                                reason = MemorySearchTruncationReason.ContentBytes;

                                break;
                            }
                        }

                        if (reason is null) position = new(6);

                        return Task.FromResult(Result<MemorySearchResponse>.Success(Payload()));
                    }, ArcanumJsonContext.Default.ApiResponseMemorySearchResponse).ConfigureAwait(false);
            }
        }

        return Results.Json(ApiResponse<MemorySearchResponse>.FromResult(Result<MemorySearchResponse>.Success(Payload()), TraceId(context)),
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);
    }

    private static async Task<IResult> HandleLexiconListAsync(
        string? q,
        ILexiconCurationService lexicon,
        HttpContext context)
    {
        return await RespondToLexiconInspectionAsync(lexicon, context,
            listed => new LexiconListDto(listed.ToArray()),
            ArcanumJsonContext.Default.ApiResponseLexiconListDto, q).ConfigureAwait(false);
    }

    private static Task<IResult> RespondToLexiconInspectionAsync<T>(
        ILexiconCurationService lexicon,
        HttpContext context,
        Func<IReadOnlyList<LexiconEntryDto>, T> project,
        JsonTypeInfo<ApiResponse<T>> typeInfo, string? query = null, int? limit = null) =>
        RespondToLexiconInspectionAsync(lexicon, context,
            entries => Task.FromResult(Result<T>.Success(project(entries))), typeInfo, query, limit);

    private static Task<IResult> RespondToLexiconInspectionAsync<T>(
        ILexiconCurationService lexicon,
        HttpContext context,
        Func<IReadOnlyList<LexiconEntryDto>, Task<Result<T>>> project,
        JsonTypeInfo<ApiResponse<T>> typeInfo, string? query = null, int? limit = null) =>
        RespondToLexiconReadAsync(context, null,
            lease => string.IsNullOrWhiteSpace(query) && limit is null
                ? lexicon.ListInspectionAsync(lease, context.RequestAborted)
                : lexicon.SearchInspectionAsync(query, limit, lease, context.RequestAborted), project, typeInfo);

    private static Task<IResult> RespondToLexiconCountsAsync<T>(
        ILexiconCurationService lexicon, HttpContext context,
        Func<LexiconInspectionCounts, Task<Result<T>>> project, JsonTypeInfo<ApiResponse<T>> typeInfo) =>
        RespondToLexiconReadAsync(context, null,
            lease => lexicon.CountInspectionAsync(lease, context.RequestAborted), project, typeInfo);

    private static async Task<IResult> RespondToLexiconReadAsync<TRead, T>(
        HttpContext context,
        CovenantOperationScope? scope,
        Func<ICovenantSnapshotReadLease?, Task<Result<LexiconInspectionResult<TRead>>>> inspect,
        Func<TRead, Task<Result<T>>> project,
        JsonTypeInfo<ApiResponse<T>> typeInfo)
    {
        ICovenantSnapshotReadLease? owned = null;

        try
        {
            Result<CovenantExportAdmission> acquired = await context.RequestServices
                .GetRequiredService<ICovenantExportPolicy>()
                .AcquireConditionalReadAsync(scope, context.RequestAborted).ConfigureAwait(false);

            if (acquired.IsFailure)
            {
                return Results.Json(ApiResponse<T>.FromResult(Result<T>.Failure(acquired.Error), TraceId(context)),
                    typeInfo, statusCode: ArcanumErrorMapper.ResolveStatusCode(acquired.Error.Code));
            }

            owned = acquired.Value.ReadLease;

            Result<LexiconInspectionResult<TRead>> inspected = await inspect(owned).ConfigureAwait(false);

            Result<T> result = inspected.IsFailure
                ? Result<T>.Failure(inspected.Error)
                : await project(inspected.Value.Value).ConfigureAwait(false);

            if (result.IsSuccess && inspected.Value.ContainsProtectedContent)
            {
                if (owned is null)
                {
                    result = Result<T>.Failure(new Error(ErrorCodes.Covenant.ForbiddenAuthority,
                        "Protected Lexicon content requires read authority."));
                }
                else
                {
                    IResult response = new CovenantProtectedJsonResult<T>(owned, result, typeInfo);

                    owned = null;

                    return response;
                }
            }

            return Results.Json(ApiResponse<T>.FromResult(result, TraceId(context)), typeInfo,
                statusCode: result.IsSuccess ? StatusCodes.Status200OK : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <remarks>
    /// <paramref name="campaignId"/> selects the tier the operator is asking about and defaults to the
    /// global one, so a request that names no Campaign sees exactly what it saw before scopes existed.
    /// It resolves the way a turn in that Campaign would - that Campaign first, then global - so what an
    /// operator inspects is what the model would be shown.
    /// </remarks>
    private static Task<IResult> HandleLexiconShowAsync(
        string name,
        Guid? campaignId,
        ILexiconCurationService lexicon,
        HttpContext context)
    {
        LexiconCurationScope scope = campaignId is { } campaign
            ? new(LexiconScopeKind.Campaign, campaign)
            : new(LexiconScopeKind.Global, null);

        return RespondToLexiconReadAsync(context, campaignId is null ? CovenantOperationScope.Global : null,
            lease => lexicon.ShowEffectiveAsync(scope, name, lease, context.RequestAborted),
            detail => Task.FromResult(Result<LexiconEntryDto>.Success(detail.Entry)),
            ArcanumJsonContext.Default.ApiResponseLexiconEntryDto);
    }

    /// <remarks>
    /// Deletion targets exactly the tier <paramref name="campaignId"/> names, with no fallback: a
    /// request aimed at a Campaign that holds no such entity deletes nothing rather than reaching past
    /// it and removing the installation's.
    /// </remarks>
    private static async Task<IResult> HandleLexiconDeleteAsync(
        string name,
        Guid? campaignId,
        ILexiconService lexicon,
        ICovenantSensitiveArtifactPurger purger,
        HttpContext context)
    {
        LexiconScope scope = LexiconScope.ForResolvedCampaign(campaignId);

        // Resolved to an identity first, because the purge boundary keys on the artifact's own id and
        // the route names an entity. A name lookup that finds nothing simply leaves the ordinary path to
        // produce its existing 404.
        Result<Guid?> existing = await lexicon
            .FindAllLifecycleIdentityForDeletionAsync(name, scope, context.RequestAborted)
            .ConfigureAwait(false);

        if (existing.IsFailure)
        {
            return Results.Json(
                ApiResponse<string>.FromResult(Result<string>.Failure(existing.Error), TraceId(context)),
                ArcanumJsonContext.Default.ApiResponseString,
                statusCode: ArcanumErrorMapper.ResolveStatusCode(existing.Error.Code));
        }

        if (existing.Value is { } identity)
        {
            Result<CovenantSensitivePurgeOutcome> purged = await CovenantSensitiveDeletion
                .DispatchAsync(purger, SensitiveArtifactKind.Lexicon, identity, context.RequestAborted)
                .ConfigureAwait(false);

            if (purged.IsFailure)
            {
                return Results.Json(
                    ApiResponse<string>.FromResult(
                        Result<string>.Failure(purged.Error),
                        TraceId(context)),
                    ArcanumJsonContext.Default.ApiResponseString,
                    statusCode: ArcanumErrorMapper.ResolveStatusCode(purged.Error.Code));
            }

            CovenantSensitiveDeletion.MarkProtectedWhenPurged(context, purged.Value);

            if (purged.Value.IsBlocked)
            {
                Error blocked = CovenantSensitiveDeletion.BlockedError(purged.Value);

                return Results.Json(
                    ApiResponse<string>.FromResult(Result<string>.Failure(blocked), TraceId(context)),
                    ArcanumJsonContext.Default.ApiResponseString,
                    statusCode: ArcanumErrorMapper.ResolveStatusCode(blocked.Code));
            }

            if (purged.Value.WasPurged(identity))
            {
                return Results.NoContent();
            }
        }

        Result<bool> deleted = await lexicon
            .DeleteByNameAsync(name, scope, context.RequestAborted)
            .ConfigureAwait(false);

        if (deleted.IsFailure)
        {
            return Results.Json(
                ApiResponse<string>.FromResult(
                    Result<string>.Failure(deleted.Error),
                    TraceId(context)),
                ArcanumJsonContext.Default.ApiResponseString,
                statusCode: ArcanumErrorMapper.ResolveStatusCode(deleted.Error.Code));
        }

        if (!deleted.Value)
        {
            Error missing = new(
                ErrorCodes.Lexicon.NotFound,
                "Lexicon entity was not found.");

            return Results.Json(
                ApiResponse<string>.FromResult(
                    Result<string>.Failure(missing),
                    TraceId(context)),
                ArcanumJsonContext.Default.ApiResponseString,
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Reports the Covenant block exactly as the management port composed it, or nothing at all.
    /// </summary>
    /// <remarks>
    /// Transferred, never rebuilt. <c>CovenantStatusDto</c> is one frozen contract with several
    /// producers reading it, and this surface once composed its own copy from the availability
    /// snapshot: four of its fields — search state, execution mode, rebuild guidance, and the
    /// degradation code — then meant different things depending on which producer had answered. The
    /// port reads the same published snapshot and owns the one content-free census, so it is the only
    /// place any of them is decided (§10.18).
    ///
    /// <para>A host with no Covenant arm, and an installation whose census could not be read, both
    /// report absence rather than a block of zeroes. A zero is a measurement, and the honest rendering
    /// of something never measured is nothing: "you have no Covenant entries" and "your Covenant could
    /// not be read" are different sentences, and only one of them is an emergency.</para>
    /// </remarks>
    private static async Task<CovenantStatusDto?> BuildCovenantStatusAsync(
        ICovenantAvailability? availability,
        ICovenantManagementService? management,
        CancellationToken cancellationToken)
    {
        if (availability is null || management is null)
        {
            return null;
        }

        Result<CovenantStatusDto> status = await management
            .StatusAsync(cancellationToken)
            .ConfigureAwait(false);

        return status.IsSuccess ? status.Value : null;
    }

    private static async Task<Result<MemoryStatusDto>> BuildStatusAsync(
        Guid? sessionId,
        ArcanumDbContext db,
        ArcanumSettings settings,
        ICovenantAvailability? availability,
        ICovenantManagementService? management,
        IGrimoireOrdinaryConnectionFactory connections,
        CancellationToken cancellationToken,
        int? verifiedLexiconCount = null)
    {
        await using IGrimoireOrdinaryConnectionLease lease = await OpenConnectionAsync(
            db,
            connections,
            cancellationToken).ConfigureAwait(false);

        SqliteConnection connection = lease.Connection;

        Session? session = null;

        if (sessionId is { } id)
        {
            session = await ReadSessionAsync(connection, id, cancellationToken).ConfigureAwait(false);

            if (session is null)
            {
                return new Error(
                    ErrorCodes.Session.NotFound,
                    "Session was not found.");
            }
        }

        int entries = await CountAsync(
            connection,
            sessionId is null
                ? "SELECT COUNT(*) FROM Entries"
                : "SELECT COUNT(*) FROM Entries WHERE SessionId = @canonicalSessionId",
            sessionId,
            cancellationToken).ConfigureAwait(false);

        int pinned = await CountAsync(
            connection,
            sessionId is null
                ? "SELECT COUNT(*) FROM Entries WHERE IsPinned = 1"
                : "SELECT COUNT(*) FROM Entries WHERE SessionId = @canonicalSessionId AND IsPinned = 1",
            sessionId,
            cancellationToken).ConfigureAwait(false);

        int summaries = await CountAsync(
            connection,
            sessionId is null
                ? "SELECT COUNT(*) FROM Sessions WHERE Summary IS NOT NULL AND trim(Summary) <> ''"
                : "SELECT COUNT(*) FROM Sessions WHERE Id = @canonicalSessionId AND Summary IS NOT NULL AND trim(Summary) <> ''",
            sessionId,
            cancellationToken).ConfigureAwait(false);

        int campaignSummaries = sessionId is not null && session?.CampaignId is null
            ? 0
            : await CountCampaignSummariesAsync(connection, session?.CampaignId, cancellationToken).ConfigureAwait(false);

        MemoryCampaignSummaryDto? campaignSummary = session?.CampaignId is { } continuityCampaign
            ? await ReadCampaignSummaryMetadataAsync(connection, continuityCampaign, cancellationToken).ConfigureAwait(false)
            : null;

        int attachments = await CountAsync(
            connection,
            sessionId is null
                ? "SELECT COUNT(*) FROM (SELECT SessionId, LogicalKey FROM SessionAttachments WHERE State = 'Bound' GROUP BY SessionId, LogicalKey)"
                : "SELECT COUNT(DISTINCT LogicalKey) FROM SessionAttachments WHERE SessionId = @canonicalSessionId AND State = 'Bound'",
            sessionId,
            cancellationToken).ConfigureAwait(false);

        int attachmentChunks = await CountAsync(
            connection,
            sessionId is null
                ? "SELECT COUNT(*) FROM session_attachment_chunks"
                : "SELECT COUNT(*) FROM session_attachment_chunks WHERE SessionId = @sessionId",
            sessionId,
            cancellationToken).ConfigureAwait(false);

        int lexicon = verifiedLexiconCount ?? await CountAsync(
            connection,
            "SELECT COUNT(*) FROM lexicon_entries",
            null,
            cancellationToken).ConfigureAwait(false);

        int saga = await CountAsync(
            connection,
            sessionId is null
                ? "SELECT COUNT(*) FROM saga_memories"
                : "SELECT COUNT(*) FROM saga_memories WHERE SessionId = @sessionId",
            sessionId,
            cancellationToken).ConfigureAwait(false);

        // Only published nodes are counted: a staging generation is invisible to retrieval, so
        // reporting it here would overstate what a turn could actually draw on.
        int tapestry = await CountAsync(
            connection,
            sessionId is null
                ? """
                  SELECT COUNT(*) FROM tapestry_nodes n
                  INNER JOIN tapestry_generations g ON g.GenerationId = n.GenerationId
                  WHERE g.Status = 'Complete'
                  """
                : """
                  SELECT COUNT(*) FROM tapestry_nodes n
                  INNER JOIN tapestry_generations g ON g.GenerationId = n.GenerationId
                  WHERE g.Status = 'Complete'
                    AND ((g.ScopeKind = 'Session' AND g.ScopeId = @tapestrySessionScopeId)
                      OR (g.ScopeKind = 'SessionAttachment' AND g.ScopeId = @tapestryAttachmentScopeId))
                  """,
            sessionId,
            cancellationToken).ConfigureAwait(false);

        string? workspacePath = session?.CampaignId is { } campaignId
            ? await ReadCampaignPathAsync(connection, campaignId, cancellationToken).ConfigureAwait(false)
            : null;

        int workspace = sessionId is not null && workspacePath is null
            ? 0
            : await CountWorkspaceChunksAsync(
                connection,
                workspacePath,
                cancellationToken).ConfigureAwait(false);

        FeatureSettings features = settings.Features ?? new FeatureSettings();

        MemoryStoreStatusDto[] stores =
        [
            new("Session Entries", true, entries, "session", SessionRetention),
            new("Pinned Entries", features.MemoryManagement, pinned, "session", PinRetention),
            new("Session Summary", true, summaries, "session", SessionRetention),
            new("Campaign Summary", features.CampaignRollups, campaignSummaries, "campaign-summary", CampaignSummaryRetention),
            new("Attachments", features.Attachments, attachments, "attachments", AttachmentRetention),
            new("Indexed Attachment Chunks", features.AttachmentRetrieval, attachmentChunks, "attachments", AttachmentIndexRetention),
            new("Lexicon", features.Lexicon, lexicon, "lexicon", LexiconRetention),
            new("Saga", features.Saga || features.SagaExtraction, saga, "saga", SagaRetention),
            new("Workspace Index", features.CodebaseRetrieval, workspace, "workspace", WorkspaceRetention),
            new("Tapestry", features.Tapestry, tapestry, "tapestry", TapestryRetention),
        ];

        return Result<MemoryStatusDto>.Success(
            new MemoryStatusDto(
                sessionId,
                session?.Title,
                stores,
                await BuildCovenantStatusAsync(availability, management, cancellationToken)
                    .ConfigureAwait(false),
                CampaignSummary: campaignSummary,
                SchemaTransitions: await RetroDownfall.Arcanum.Infrastructure.Diagnostics.GrimoireSchemaTransitionDiagnostics
                    .ReadAsync(connection, cancellationToken).ConfigureAwait(false)));
    }

    private static async Task<MemoryCampaignSummaryDto?> ReadCampaignSummaryMetadataAsync(
        SqliteConnection connection,
        Guid campaignId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT s.CurrentArtifactId, s.Revision, s.SourceGeneration,
                COALESCE(a.SourceCount, 0), s.RefoldRequired, COALESCE(a.SensitivityCode, 0), s.UpdatedAtUtc
            FROM campaign_rollup_state s
            LEFT JOIN campaign_rollup_artifacts a ON a.ArtifactId = s.CurrentArtifactId
                AND a.CampaignId = s.CampaignId AND a.Revision = s.Revision
            WHERE s.CampaignId = $campaignId;
            """;

        AddParameter(command, "$campaignId", campaignId.ToString("D").ToUpperInvariant());

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new MemoryCampaignSummaryDto(
                campaignId,
                reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt32(3),
                reader.GetInt64(4) != 0,
                (ContentSensitivity)reader.GetInt64(5),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))
            : new MemoryCampaignSummaryDto(campaignId, null, 0, 0, 0, false, ContentSensitivity.None, null);
    }

    private static async Task<int> CountCampaignSummariesAsync(
        SqliteConnection connection,
        Guid? campaignId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = campaignId is null
            ? "SELECT COUNT(*) FROM campaign_rollup_artifacts;"
            : "SELECT COUNT(*) FROM campaign_rollup_artifacts WHERE CampaignId = $campaignId;";

        if (campaignId is { } campaign)
        {
            AddParameter(command, "$campaignId", campaign.ToString("D").ToUpperInvariant());
        }

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static MemoryEligibilityDto Explain(
        MemoryStoreStatusDto status,
        bool eligible,
        string reason) =>
        new(status.Name, eligible, reason, status.Retention);

    private static string ProvenanceFor(string name) => name switch
    {
        "Session Entries" => "Grimoire Entries rows for the selected session, or all sessions when none is selected.",
        "Pinned Entries" => "The IsPinned flag on an exact Grimoire Entry row.",
        "Session Summary" => "The selected Session Summary and its summarization watermark.",
        "Campaign Summary" => "Immutable Campaign summary revisions folded from exact native Session contribution revisions and source manifests.",
        "Attachments" => "Encrypted session-bound attachment metadata; no bytes or host paths are returned.",
        "Indexed Attachment Chunks" => "Derived text chunks carrying session, attachment, logical key, version, and content-hash provenance.",
        "Lexicon" => "Structured Lexicon entity name, type, facts, and any typed attachment fact provenance.",
        "Saga" => "Saga memory id, originating session when present, source label, and typed attachment provenance when present.",
        "Workspace Index" => "Derived workspace chunk id and relative file location; host absolute paths are not returned.",
        "Tapestry" => "Derived hierarchical node id, layer, corpus scope kind, and content hash from the current published generation; host absolute paths are not returned.",
        _ => "Unknown memory source.",
    };

    private static bool Includes(
        MemorySearchScope requested,
        MemorySearchScope candidate) =>
        requested == MemorySearchScope.All || requested == candidate;

    private static bool IsKnownScope(MemorySearchScope scope) =>
        scope is MemorySearchScope.Session
            or MemorySearchScope.Attachments
            or MemorySearchScope.Workspace
            or MemorySearchScope.Saga
            or MemorySearchScope.Lexicon
            or MemorySearchScope.All;

    private static async Task<(string Path, string Label)?> ResolveSearchWorkspaceAsync(
        DbConnection connection,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        string selector = workspaceId.Trim();

        bool isId = Guid.TryParse(selector, out Guid id);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = isId
            ? "SELECT Path, Name FROM Campaigns WHERE Id = @id LIMIT 1"
            : "SELECT Path, Name FROM Campaigns WHERE NameLower = lower(@id) LIMIT 1";

        AddParameter(command, "@id", isId ? GrimoireEntitySql.Format(id) : selector);

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async Task SearchSagaAsync(
        ISagaMemoryStore store,
        string query,
        Guid? sessionId,
        MemoryScope scope,
        List<MemorySearchResultDto> results,
        int limit,
        ResultByteBudget bytes,
        CancellationToken cancellationToken,
        SagaMemoryPosition? after = null,
        Action<SagaMemoryPosition>? remember = null)
    {
        MemoryCampaignScopeDto reported = MemoryCampaignScopeReport.Describe(scope);

        // The listing's own rows, so a retired memory is a hit exactly as a live one is; each carries
        // its lifecycle and whether an embedding survives, read in the same statement, so the
        // eligibility reported beside it describes the instant the row was read.
        SagaMemoryCurationRow[] rows = await store
            .ListCurationRowsAsync(
                query,
                sessionId,
                scope,
                limit,
                after,
                cancellationToken)
            .ConfigureAwait(false);

        foreach (SagaMemoryCurationRow row in rows)
        {
            SagaMemoryDto memory = row.Memory;

            string provenance = memory.SessionId is null
                ? "Saga memory with no originating session"
                : $"Saga memory from session {memory.SessionId.Value:D}";

            if (!string.IsNullOrWhiteSpace(memory.Source))
            {
                provenance += $", source {memory.Source}";
            }

            if (memory.AttachmentProvenance is { } attachmentSource)
            {
                provenance += $"; {FormatAttachmentProvenance(attachmentSource)}";
            }

            // Per memory, so a result set spanning scopes is readable: the row says which Campaign owns
            // it, and the block on it says which scope this search drew from. An unresolved row is listed
            // only while Campaign scoping is off -- the listing narrows by the ownership a turn ranks by
            // whenever scoping is on -- and a turn then ranks it like any other, so the text says when it
            // would be withheld rather than claiming it is withheld now.
            provenance += memory.ScopeKind switch
            {
                SagaMemoryScopeKind.Campaign => $"; campaign {memory.ScopeCampaignId:D}",

                SagaMemoryScopeKind.Global => "; installation-scoped",

                SagaMemoryScopeKind.LegacyUnresolved => "; ownership unresolved, withheld from turns while Campaign scoping is on",

                _ => "; ownership not yet classified",
            };

            if (memory.ConsolidatedIntoMemoryId is { } survivorMemoryId
                && memory.ConsolidatedIntoVersionId is { } survivorVersionId
                && memory.LongRestReceiptId is { } receiptId)
            {
                provenance += $"; consolidated into memory {survivorMemoryId}, version {survivorVersionId}, Long Rest receipt {receiptId}";
            }

            // Last, so it reads as the verdict on everything before it: the row is still here, and no
            // turn can recall it.
            if (row.Lifecycle.RetiredAtUtc is not null)
            {
                provenance += "; retired";
            }

            results.Add(new MemorySearchResultDto(
                MemorySearchScope.Saga,
                "Saga memory",
                memory.Content,
                provenance,
                SagaRetention,
                memory.Id,
                reported,
                Action: new MemorySearchActionDto(
                    MemorySearchActionKind.ShowSagaMemory,
                    Saga: new MemorySagaTargetDto(memory.Id)),
                SagaLifecycle: row.Lifecycle,
                SagaEligibility: SagaRetrievalEligibilityClassifier.Classify(row, scope.IsEnforced),
                        SagaRanking: row.Memory.Ranking));

            remember?.Invoke(row.SearchPosition ?? new SagaMemoryPosition(UtcInstantText.Format(memory.CreatedAt), memory.Id));

            if (bytes.Charge(memory.Content))
            {
                break;
            }
        }
    }

    private static void AddLexiconMatches(
        IReadOnlyList<LexiconEntryDto> entries,
        string query,
        List<MemorySearchResultDto> results,
        int limit,
        ResultByteBudget bytes)
    {
        int taken = 0;

        foreach (LexiconEntryDto entry in entries)
        {
            if (taken >= limit)
            {
                break;
            }

            if (!LexiconMatches(entry, query))
            {
                continue;
            }

            // The tier is part of the entity's identity now: two rows may share a name, and a listing that
            // did not say which scope each belongs to would show them as duplicates.
            string provenance = entry.ScopeCampaignId is { } entryCampaign
                ? $"Lexicon entity: {entry.Name}; campaign {entryCampaign:D}"
                : $"Lexicon entity: {entry.Name}; installation-scoped";

            if (entry.FactProvenance is { Length: > 0 } factSources)
            {
                string sources = string.Join(
                    "; ",
                    factSources
                        .Select(static fact => FormatAttachmentProvenance(fact.Source))
                        .Distinct(StringComparer.Ordinal));

                provenance += $"; {sources}";
            }

            string facts = string.Join("; ", entry.Facts);

            results.Add(new MemorySearchResultDto(
                MemorySearchScope.Lexicon,
                $"{entry.Name} [{entry.Type}]",
                facts,
                provenance,
                LexiconRetention,
                entry.Id.ToString("D"),
                LexiconLifecycle: new LexiconEntryLifecycle(entry.RetiredAtUtc, entry.PinnedAtUtc),
                LexiconEligibility: entry.Eligibility,
                Action: new MemorySearchActionDto(
                    MemorySearchActionKind.ShowLexiconEntry,
                    Lexicon: new MemoryLexiconTargetDto(
                        entry.Name,
                        entry.ScopeCampaignId is { } campaignId
                            ? new LexiconCurationScope(LexiconScopeKind.Campaign, campaignId)
                            : new LexiconCurationScope(LexiconScopeKind.Global, null)))));

            taken++;

            if (bytes.Charge(facts))
            {
                break;
            }
        }
    }

    private static bool LexiconMatches(
        LexiconEntryDto entry,
        string query) =>
        entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.Type.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.Facts.Any(fact => fact.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static string FormatAttachmentProvenance(
        RetroDownfall.Arcanum.Core.Intelligence.AttachmentMemoryProvenance source) =>
        $"attachment source session {source.SessionId:D}, attachment {source.AttachmentId:D}, logical key {source.LogicalKey}, version {source.Version}, content hash {source.ContentHash}, materialized {source.MaterializedAt:u}, type {source.SourceType}, availability {source.Availability}";

    private static async Task<int> CountWorkspaceChunksAsync(
        DbConnection connection,
        string? workspacePath,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = workspacePath is null
            ? "SELECT COUNT(*) FROM workspace_file_chunks"
            : "SELECT COUNT(*) FROM workspace_file_chunks WHERE WorkspacePath = @workspacePath";

        if (workspacePath is not null)
        {
            AddParameter(command, "@workspacePath", workspacePath);
        }

        object? value = await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);

        int count = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);

        await GrimoireScopedConsumerTestSeam
            .PauseAsync(
                "MemoryEndpoints.CountWorkspaceChunksAsync",
                GrimoireScopedConsumerFinalUseKind.ScalarConverted,
                count,
                cancellationToken)
            .ConfigureAwait(false);

        return count;
    }

    private static async Task<int> CountAsync(
        DbConnection connection,
        string sql,
        Guid? sessionId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        AddSessionParameter(command, sessionId);

        object? value = await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);

        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> SessionExistsAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT EXISTS(SELECT 1 FROM \"Sessions\" WHERE \"Id\" = $sessionId);";

        GrimoireEntitySql.AddParameter(command, "$sessionId", sessionId);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is long exists && exists == 1;
    }

    private static async Task<Session?> ReadSessionAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT {GrimoireEntitySql.SessionColumns} FROM \"Sessions\" WHERE \"Id\" = $sessionId LIMIT 1;";

        GrimoireEntitySql.AddParameter(command, "$sessionId", sessionId);

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? GrimoireEntitySql.ReadSession(reader)
            : null;
    }

    private static async Task<string?> ReadCampaignPathAsync(
        SqliteConnection connection,
        Guid campaignId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT \"Path\" FROM \"Campaigns\" WHERE \"Id\" = $campaignId LIMIT 1;";

        GrimoireEntitySql.AddParameter(command, "$campaignId", campaignId);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is string path ? path : null;
    }

    [GrimoireConnectionAcquisitionRoute]
    private static async Task<IGrimoireOrdinaryConnectionLease> OpenConnectionAsync(
        ArcanumDbContext db,
        IGrimoireOrdinaryConnectionFactory connections,
        CancellationToken cancellationToken)
    {
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
            throw GrimoireConnectionUnavailableException.FromError(acquired.Error);
        }

        return acquired.Value;
    }

    /// <summary>
    /// Binds one Session identity under two names, because the columns these queries compare it against
    /// do not agree on one spelling.
    /// </summary>
    /// <remarks>
    /// <c>@canonicalSessionId</c> is for a column that holds the uppercase dashed form the
    /// object-relational writer renders. <c>@sessionId</c> is for the columns that deliberately hold the
    /// lowercase one: <c>session_attachment_chunks.SessionId</c>, which the tapestry reads as its live
    /// scope-id set, and <c>saga_memories.SessionId</c>. One parameter served both groups until the
    /// attachment family moved, and the bound-attachment count then compared a lowercase value against a
    /// canonical column and reported zero.
    ///
    /// <para><c>tapestry_generations.ScopeId</c> belongs to neither group, because it is filled from the
    /// corpus each scope kind was woven from: a <c>Session</c> tree reads <c>Entries.SessionId</c>, so its
    /// scope id is canonical, and a <c>SessionAttachment</c> tree reads the chunk column, so its scope id
    /// is lowercase. Its predicate therefore binds each kind to its own parameter,
    /// <c>@tapestrySessionScopeId</c> and <c>@tapestryAttachmentScopeId</c>, and takes the value from
    /// <see cref="TapestryScope.ForSession"/> and <see cref="TapestryScope.ForSessionAttachment"/>, the one
    /// place each spelling is written, rather than spelling it a third time here.</para>
    ///
    /// <para>Every predicate that reads a canonical column now binds <c>@canonicalSessionId</c>: the
    /// entry, pinned-entry and campaign-summary counts, and both session-scoped search predicates, join
    /// the bound-attachment count in doing so. Only the two columns named above stay bound to the
    /// deliberately-lowercase group.</para>
    ///
    /// <para>Both names are always added. A named parameter a statement never mentions is simply not
    /// bound, so a query that wants only one of them is unaffected by the other's presence.</para>
    /// </remarks>
    private static void AddSessionParameter(
        DbCommand command,
        Guid? sessionId)
    {
        if (sessionId is not null)
        {
            AddParameter(command, "@sessionId", sessionId.Value.ToString("D"));

            AddParameter(
                command,
                "@canonicalSessionId",
                sessionId.Value.ToString("D").ToUpperInvariant());

            AddParameter(command, "@tapestrySessionScopeId", TapestryScope.ForSession(sessionId.Value).Id);

            AddParameter(
                command,
                "@tapestryAttachmentScopeId",
                TapestryScope.ForSessionAttachment(sessionId.Value).Id);
        }
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object value)
    {
        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        _ = command.Parameters.Add(parameter);
    }

    private static string TraceId(HttpContext context) =>
        Activity.Current?.Id ?? context.TraceIdentifier;
}
