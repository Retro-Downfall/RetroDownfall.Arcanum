using System.Data;

using System.Data.Common;

using System.Diagnostics;

using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using Microsoft.EntityFrameworkCore;

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

using RetroDownfall.Arcanum.Infrastructure.Data;

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

    private const string SessionRetention = "Session lifetime; archiving retains it and session purge removes it.";

    private const string PinRetention = "Until explicitly unpinned or the owning session is purged.";

    private const string AttachmentRetention = "Bound to the session; deleting an attachment does not delete Saga or Lexicon facts derived from it.";

    private const string AttachmentIndexRetention = "Derived and rebuildable from bound attachment versions; removed with the attachment or index reset.";

    private const string CovenantRetention =
        "Durable immutable versions until an operator retires the entry or a Covenant reset, family "
            + "reinitialize, or factory erasure removes it. Content already sent to a provider is "
            + "outside every local erasure path.";

    private const string LexiconRetention = "Durable until that explicitly named Lexicon entity is deleted.";

    private const string SagaRetention = "Durable associative memory until that Saga memory is explicitly deleted.";

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
        .WithName("GetMemorySources");

        apiGroup.MapGet(
            "/memory/sources/{sessionId:guid}",
            (Guid sessionId, ArcanumDbContext db, IOptionsMonitor<ArcanumSettings> options, HttpContext context) =>
                HandleSourcesAsync(sessionId, db, options, context))
        .WithName("GetSessionMemorySources");

        apiGroup.MapPost(
            "/memory/search",
            HandleSearchAsync)
        .WithName("SearchMemory");

        apiGroup.MapGet(
            "/memory/explain",
            (ArcanumDbContext db, IOptionsMonitor<ArcanumSettings> options, HttpContext context) =>
                HandleExplainAsync(null, db, options, context))
        .WithName("ExplainMemory");

        apiGroup.MapGet(
            "/memory/explain/{sessionId:guid}",
            (Guid sessionId, ArcanumDbContext db, IOptionsMonitor<ArcanumSettings> options, HttpContext context) =>
                HandleExplainAsync(sessionId, db, options, context))
        .WithName("ExplainSessionMemory");

        apiGroup.MapGet(
            "/memory/lexicon",
            HandleLexiconListAsync)
        .WithName("ListLexiconEntries");

        apiGroup.MapGet(
            "/memory/lexicon/{**name}",
            HandleLexiconShowAsync)
        .WithName("GetLexiconEntry");

        apiGroup.MapDelete(
            "/memory/lexicon/{**name}",
            HandleLexiconDeleteAsync)
        .RequireConditionalSensitivityRetentionPurge()
        .WithName("DeleteLexiconEntry");

        return apiGroup;

    }

    private static async Task<IResult> HandleStatusAsync(
        Guid? sessionId,
        ArcanumDbContext db,
        IOptionsMonitor<ArcanumSettings> options,
        HttpContext context)
    {

        Result<MemoryStatusDto> result = await BuildStatusAsync(
            sessionId,
            db,
            options.CurrentValue,
            context.RequestServices.GetService<ICovenantAvailability>(),
            context.RequestServices.GetService<ICovenantManagementService>(),
            context.RequestAborted).ConfigureAwait(false);

        string traceId = TraceId(context);

        return Results.Json(
            ApiResponse<MemoryStatusDto>.FromResult(result, traceId),
            ArcanumJsonContext.Default.ApiResponseMemoryStatusDto,
            statusCode: result.IsSuccess
                ? StatusCodes.Status200OK
                : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));

    }

    private static async Task<IResult> HandleSourcesAsync(
        Guid? sessionId,
        ArcanumDbContext db,
        IOptionsMonitor<ArcanumSettings> options,
        HttpContext context)
    {

        Result<MemoryStatusDto> status = await BuildStatusAsync(
            sessionId,
            db,
            options.CurrentValue,
            context.RequestServices.GetService<ICovenantAvailability>(),
            context.RequestServices.GetService<ICovenantManagementService>(),
            context.RequestAborted).ConfigureAwait(false);

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
                new MemorySourcesDto(status.Value.SessionId, sources));

        }

        return Results.Json(
            ApiResponse<MemorySourcesDto>.FromResult(result, TraceId(context)),
            ArcanumJsonContext.Default.ApiResponseMemorySourcesDto,
            statusCode: result.IsSuccess
                ? StatusCodes.Status200OK
                : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));

    }

    private static async Task<IResult> HandleExplainAsync(
        Guid? sessionId,
        ArcanumDbContext db,
        IOptionsMonitor<ArcanumSettings> options,
        HttpContext context)
    {

        Result<MemoryStatusDto> status = await BuildStatusAsync(
            sessionId,
            db,
            options.CurrentValue,
            context.RequestServices.GetService<ICovenantAvailability>(),
            context.RequestServices.GetService<ICovenantManagementService>(),
            context.RequestAborted).ConfigureAwait(false);

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

            MemoryEligibilityDto[] eligibility =
            [
                Explain(
                    stores["Session Entries"],
                    hasSession && stores["Session Entries"].Count > 0,
                    hasSession
                        ? "The active session transcript is considered in sequence and may be compressed when a Campaign Summary watermark applies."
                        : "Select a session to make a transcript eligible for a turn."),
                Explain(
                    stores["Pinned Entries"],
                    hasSession && stores["Pinned Entries"].Count > 0,
                    hasSession
                        ? "Pinned entries remain eligible even when older unpinned transcript entries are compressed."
                        : "Pins belong to a specific session; select one to inspect eligibility."),
                Explain(
                    stores["Campaign Summary"],
                    hasSession && stores["Campaign Summary"].Count > 0,
                    hasSession
                        ? "The summary becomes compressed context when the session watermark and context-pressure policy require it."
                        : "Campaign summaries are stored on sessions; select one to inspect eligibility."),
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
                    stores["Saga"].Enabled && stores["Saga"].Count > 0,
                    "Saga memories are candidates when semantic retrieval for the current prompt selects them; they remain distinct from attachments and Lexicon."),
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
                    eligibility));

        }

        return Results.Json(
            ApiResponse<MemoryExplainDto>.FromResult(result, TraceId(context)),
            ArcanumJsonContext.Default.ApiResponseMemoryExplainDto,
            statusCode: result.IsSuccess
                ? StatusCodes.Status200OK
                : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));

    }

    private static async Task<IResult> HandleSearchAsync(
        MemorySearchRequest? request,
        ArcanumDbContext db,
        ILexiconService lexicon,
        ISagaMemoryStore sagaStore,
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

        if (request.SessionId is { } sessionId
            && !await db.Sessions.AsNoTracking().AnyAsync(
                session => session.Id == sessionId,
                context.RequestAborted).ConfigureAwait(false))
        {

            Result<MemorySearchResponse> missing = Result<MemorySearchResponse>.Failure(
                new Error(ErrorCodes.Session.NotFound, "Session was not found."));

            return Results.Json(
                ApiResponse<MemorySearchResponse>.FromResult(missing, TraceId(context)),
                ArcanumJsonContext.Default.ApiResponseMemorySearchResponse,
                statusCode: StatusCodes.Status404NotFound);

        }

        DbConnection connection = await OpenConnectionAsync(
            db,
            context.RequestAborted).ConfigureAwait(false);

        string query = request.Query.Trim();

        // One budget for the whole request, not one per scope: a per-scope ceiling would still let
        // scope=all return five times the bound. Each scope is asked only for what the budget has
        // left, so a one-character query can never materialize more than the budget's worth of rows.
        // A caller-supplied limit narrows that budget; it can never widen it.
        int budget = request.Limit ?? SearchResultLimit;

        List<MemorySearchResultDto> results = [];

        List<MemorySearchScopeStatusDto> scopes = [];

        if (Includes(request.Scope, MemorySearchScope.Session))
        {

            int slice = OpenSlice(MemorySearchScope.Session, results, budget, scopes);

            if (slice > 0)
            {

                await SearchSessionAsync(
                    connection,
                    query,
                    request.SessionId,
                    results,
                    slice + 1,
                    context.RequestAborted).ConfigureAwait(false);

                CloseSlice(MemorySearchScope.Session, results, budget, slice, scopes);

            }

        }

        if (Includes(request.Scope, MemorySearchScope.Attachments))
        {

            int slice = OpenSlice(MemorySearchScope.Attachments, results, budget, scopes);

            if (slice > 0)
            {

                await SearchAttachmentsAsync(
                    connection,
                    query,
                    request.SessionId,
                    results,
                    slice + 1,
                    context.RequestAborted).ConfigureAwait(false);

                CloseSlice(MemorySearchScope.Attachments, results, budget, slice, scopes);

            }

        }

        if (Includes(request.Scope, MemorySearchScope.Workspace))
        {

            int slice = OpenSlice(MemorySearchScope.Workspace, results, budget, scopes);

            if (slice > 0)
            {

                await SearchWorkspaceAsync(
                    connection,
                    query,
                    request.WorkspaceId,
                    results,
                    slice + 1,
                    context.RequestAborted).ConfigureAwait(false);

                CloseSlice(MemorySearchScope.Workspace, results, budget, slice, scopes);

            }

        }

        if (Includes(request.Scope, MemorySearchScope.Saga))
        {

            int slice = OpenSlice(MemorySearchScope.Saga, results, budget, scopes);

            if (slice > 0)
            {

                await SearchSagaAsync(
                    sagaStore,
                    query,
                    request.SessionId,
                    results,
                    slice + 1,
                    context.RequestAborted).ConfigureAwait(false);

                CloseSlice(MemorySearchScope.Saga, results, budget, slice, scopes);

            }

        }

        if (Includes(request.Scope, MemorySearchScope.Lexicon))
        {

            int slice = OpenSlice(MemorySearchScope.Lexicon, results, budget, scopes);

            if (slice > 0)
            {

                Result<IReadOnlyList<LexiconEntryDto>> entries = await lexicon
                    .ListAsync(context.RequestAborted)
                    .ConfigureAwait(false);

                if (entries.IsFailure)
                {

                    Result<MemorySearchResponse> failed = Result<MemorySearchResponse>.Failure(
                        entries.Error);

                    return Results.Json(
                        ApiResponse<MemorySearchResponse>.FromResult(failed, TraceId(context)),
                        ArcanumJsonContext.Default.ApiResponseMemorySearchResponse,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(entries.Error.Code));

                }

                AddLexiconMatches(entries.Value, query, results, slice + 1);

                CloseSlice(MemorySearchScope.Lexicon, results, budget, slice, scopes);

            }

        }

        MemorySearchResponse payload = new(
            query,
            request.Scope,
            [.. results],
            [.. scopes],
            scopes.Exists(static scope => scope.HasMore));

        return Results.Json(
            ApiResponse<MemorySearchResponse>.FromResult(
                Result<MemorySearchResponse>.Success(payload),
                TraceId(context)),
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse);

    }

    /// <summary>
    /// How many rows this scope may still contribute. Zero means an earlier scope consumed the whole
    /// budget: the scope is recorded as starved with <c>hasMore</c>, because "not consulted" and
    /// "consulted and empty" are different answers and a caller raising <c>limit</c> needs to tell them
    /// apart.
    /// </summary>
    private static int OpenSlice(
        MemorySearchScope scope,
        List<MemorySearchResultDto> results,
        int budget,
        List<MemorySearchScopeStatusDto> scopes)
    {

        int slice = budget - results.Count;

        if (slice <= 0)
        {

            scopes.Add(new MemorySearchScopeStatusDto(scope, 0, HasMore: true));

        }

        return slice;

    }

    /// <summary>
    /// Each scope is asked for one row beyond its slice, so an overflowing scope is distinguishable
    /// from one that happened to fill its slice exactly. The extra row is trimmed here and never
    /// reaches the caller.
    /// </summary>
    private static void CloseSlice(
        MemorySearchScope scope,
        List<MemorySearchResultDto> results,
        int budget,
        int slice,
        List<MemorySearchScopeStatusDto> scopes)
    {

        int produced = results.Count - (budget - slice);

        bool hasMore = produced > slice;

        if (hasMore)
        {

            results.RemoveRange(budget, results.Count - budget);

            produced = slice;

        }

        scopes.Add(new MemorySearchScopeStatusDto(scope, produced, hasMore));

    }

    private static async Task<IResult> HandleLexiconListAsync(
        string? q,
        ILexiconService lexicon,
        HttpContext context)
    {

        Result<IReadOnlyList<LexiconEntryDto>> listed = await lexicon
            .ListAsync(context.RequestAborted)
            .ConfigureAwait(false);

        Result<LexiconListDto> result;

        if (listed.IsFailure)
        {

            result = Result<LexiconListDto>.Failure(listed.Error);

        }
        else
        {

            IEnumerable<LexiconEntryDto> entries = listed.Value;

            if (!string.IsNullOrWhiteSpace(q))
            {

                string query = q.Trim();

                entries = entries.Where(entry => LexiconMatches(entry, query));

            }

            result = Result<LexiconListDto>.Success(
                new LexiconListDto(entries.ToArray()));

        }

        return Results.Json(
            ApiResponse<LexiconListDto>.FromResult(result, TraceId(context)),
            ArcanumJsonContext.Default.ApiResponseLexiconListDto,
            statusCode: result.IsSuccess
                ? StatusCodes.Status200OK
                : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));

    }

    private static async Task<IResult> HandleLexiconShowAsync(
        string name,
        ILexiconService lexicon,
        HttpContext context)
    {

        Result<LexiconEntryDto?> lookup = await lexicon
            .GetByNameAsync(name, context.RequestAborted)
            .ConfigureAwait(false);

        Result<LexiconEntryDto> result = lookup.IsFailure
            ? Result<LexiconEntryDto>.Failure(lookup.Error)
            : lookup.Value is null
                ? Result<LexiconEntryDto>.Failure(
                    new Error(
                        ErrorCodes.Lexicon.NotFound,
                        "Lexicon entity was not found."))
                : Result<LexiconEntryDto>.Success(lookup.Value);

        return Results.Json(
            ApiResponse<LexiconEntryDto>.FromResult(result, TraceId(context)),
            ArcanumJsonContext.Default.ApiResponseLexiconEntryDto,
            statusCode: result.IsSuccess
                ? StatusCodes.Status200OK
                : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));

    }

    private static async Task<IResult> HandleLexiconDeleteAsync(
        string name,
        ILexiconService lexicon,
        ICovenantSensitiveArtifactPurger purger,
        HttpContext context)
    {

        // Resolved to an identity first, because the purge boundary keys on the artifact's own id and
        // the route names an entity. A name lookup that finds nothing simply leaves the ordinary path to
        // produce its existing 404.
        Result<LexiconEntryDto?> existing = await lexicon
            .GetByNameAsync(name, context.RequestAborted)
            .ConfigureAwait(false);

        if (existing.IsSuccess && existing.Value is { } entity)
        {

            Result<CovenantSensitivePurgeOutcome> purged = await CovenantSensitiveDeletion
                .DispatchAsync(purger, SensitiveArtifactKind.Lexicon, entity.Id, context.RequestAborted)
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

            if (purged.Value.WasPurged(entity.Id))
            {

                return Results.NoContent();

            }

        }

        Result<bool> deleted = await lexicon
            .DeleteByNameAsync(name, context.RequestAborted)
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
        CancellationToken cancellationToken)
    {

        Session? session = null;

        if (sessionId is { } id)
        {

            session = await db.Sessions
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
                .ConfigureAwait(false);

            if (session is null)
            {

                return new Error(
                    ErrorCodes.Session.NotFound,
                    "Session was not found.");

            }

        }

        DbConnection connection = await OpenConnectionAsync(
            db,
            cancellationToken).ConfigureAwait(false);

        int entries = await CountAsync(
            connection,
            sessionId is null
                ? "SELECT COUNT(*) FROM Entries"
                : "SELECT COUNT(*) FROM Entries WHERE SessionId = @sessionId",
            sessionId,
            cancellationToken).ConfigureAwait(false);

        int pinned = await CountAsync(
            connection,
            sessionId is null
                ? "SELECT COUNT(*) FROM Entries WHERE IsPinned = 1"
                : "SELECT COUNT(*) FROM Entries WHERE SessionId = @sessionId AND IsPinned = 1",
            sessionId,
            cancellationToken).ConfigureAwait(false);

        int summaries = await CountAsync(
            connection,
            sessionId is null
                ? "SELECT COUNT(*) FROM Sessions WHERE Summary IS NOT NULL AND trim(Summary) <> ''"
                : "SELECT COUNT(*) FROM Sessions WHERE Id = @sessionId AND Summary IS NOT NULL AND trim(Summary) <> ''",
            sessionId,
            cancellationToken).ConfigureAwait(false);

        int attachments = await CountAsync(
            connection,
            sessionId is null
                ? "SELECT COUNT(*) FROM (SELECT SessionId, LogicalKey FROM SessionAttachments WHERE State = 'Bound' GROUP BY SessionId, LogicalKey)"
                : "SELECT COUNT(DISTINCT LogicalKey) FROM SessionAttachments WHERE SessionId = @sessionId AND State = 'Bound'",
            sessionId,
            cancellationToken).ConfigureAwait(false);

        int attachmentChunks = await CountAsync(
            connection,
            sessionId is null
                ? "SELECT COUNT(*) FROM session_attachment_chunks"
                : "SELECT COUNT(*) FROM session_attachment_chunks WHERE SessionId = @sessionId",
            sessionId,
            cancellationToken).ConfigureAwait(false);

        int lexicon = await CountAsync(
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
                    AND g.ScopeKind <> 'Workspace'
                    AND g.ScopeId = @sessionId
                  """,
            sessionId,
            cancellationToken).ConfigureAwait(false);

        string? workspacePath = session?.CampaignId is { } campaignId
            ? await db.Campaigns
                .AsNoTracking()
                .Where(campaign => campaign.Id == campaignId)
                .Select(static campaign => campaign.Path)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
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
            new("Campaign Summary", true, summaries, "session", SessionRetention),
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
                    .ConfigureAwait(false)));

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
        "Campaign Summary" => "The selected Session Summary and its summarization watermark.",
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

    private static async Task SearchSessionAsync(
        DbConnection connection,
        string query,
        Guid? sessionId,
        List<MemorySearchResultDto> results,
        int limit,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT e.Id, e.SessionId, e.Content, e.Role, COALESCE(s.Title, '')
            FROM Entries e
            INNER JOIN Sessions s ON s.Id = e.SessionId
            WHERE instr(lower(e.Content), lower(@query)) > 0
            {(sessionId is null ? string.Empty : "AND e.SessionId = @sessionId")}
            ORDER BY e.CreatedAt DESC, e.Sequence DESC
            LIMIT @limit
            """;

        AddParameter(command, "@query", query);

        AddParameter(command, "@limit", limit);

        AddSessionParameter(command, sessionId);

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        int taken = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            string title = reader.GetString(4);

            results.Add(new MemorySearchResultDto(
                MemorySearchScope.Session,
                $"Session entry ({(MessageRole)reader.GetInt32(3)})",
                reader.GetString(2),
                $"Session {(title.Length == 0 ? reader.GetString(1) : title)}, entry {reader.GetString(0)}",
                SessionRetention,
                reader.GetString(0)));

            taken++;

        }

        await reader.DisposeAsync().ConfigureAwait(false);

        // The entry scan and the summary scan share this scope's slice of the budget; entries are the
        // narrower, more specific hit, so they are read first and the summaries take what is left.
        int summaryLimit = limit - taken;

        if (summaryLimit <= 0)
        {

            return;

        }

        await using DbCommand summaries = connection.CreateCommand();

        summaries.CommandText =
            $"""
            SELECT Id, COALESCE(Title, ''), Summary
            FROM Sessions
            WHERE Summary IS NOT NULL
              AND instr(lower(Summary), lower(@query)) > 0
              {(sessionId is null ? string.Empty : "AND Id = @sessionId")}
            ORDER BY UpdatedAt DESC
            LIMIT @limit
            """;

        AddParameter(summaries, "@query", query);

        AddParameter(summaries, "@limit", summaryLimit);

        AddSessionParameter(summaries, sessionId);

        await using DbDataReader summaryReader = await summaries
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await summaryReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            string id = summaryReader.GetString(0);

            string title = summaryReader.GetString(1);

            results.Add(new MemorySearchResultDto(
                MemorySearchScope.Session,
                "Campaign Summary",
                summaryReader.GetString(2),
                $"Session {(title.Length == 0 ? id : title)}, Summary field",
                SessionRetention,
                id));

        }

    }

    private static async Task SearchAttachmentsAsync(
        DbConnection connection,
        string query,
        Guid? sessionId,
        List<MemorySearchResultDto> results,
        int limit,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT ChunkId, SessionId, AttachmentId, LogicalKey, Version, OriginalFileName,
                   ChunkIndex, Content, ContentSha256
            FROM session_attachment_chunks
            WHERE (instr(lower(Content), lower(@query)) > 0
                   OR instr(lower(OriginalFileName), lower(@query)) > 0
                   OR instr(lower(LogicalKey), lower(@query)) > 0)
              {(sessionId is null ? string.Empty : "AND SessionId = @sessionId")}
            ORDER BY IndexedAt DESC, ChunkIndex
            LIMIT @limit
            """;

        AddParameter(command, "@query", query);

        AddParameter(command, "@limit", limit);

        AddSessionParameter(command, sessionId);

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            results.Add(new MemorySearchResultDto(
                MemorySearchScope.Attachments,
                $"{reader.GetString(5)} chunk {reader.GetInt32(6)}",
                reader.GetString(7),
                $"Session {reader.GetString(1)}, attachment {reader.GetString(2)}, logical key {reader.GetString(3)}, version {reader.GetInt32(4)}, content hash {reader.GetString(8)}",
                AttachmentIndexRetention,
                reader.GetString(0)));

        }

    }

    private static async Task SearchWorkspaceAsync(
        DbConnection connection,
        string query,
        string? workspaceId,
        List<MemorySearchResultDto> results,
        int limit,
        CancellationToken cancellationToken)
    {

        string? workspacePath = null;

        string? workspaceLabel = null;

        if (!string.IsNullOrWhiteSpace(workspaceId))
        {

            await using DbCommand resolve = connection.CreateCommand();

            resolve.CommandText =
                "SELECT Path, Name FROM Campaigns WHERE Id = @id OR NameLower = lower(@id)";

            AddParameter(resolve, "@id", workspaceId.Trim());

            await using DbDataReader resolver = await resolve
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (await resolver.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                workspacePath = resolver.GetString(0);

                workspaceLabel = resolver.GetString(1);

            }

        }

        if (!string.IsNullOrWhiteSpace(workspaceId) && workspacePath is null)
        {

            return;

        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, StartLine, EndLine
            FROM workspace_file_chunks
            WHERE (instr(lower(Content), lower(@query)) > 0
                   OR instr(lower(RelativePath), lower(@query)) > 0)
              {(workspacePath is null ? string.Empty : "AND WorkspacePath = @workspacePath")}
            ORDER BY IndexedAt DESC, RelativePath, ChunkIndex
            LIMIT @limit
            """;

        AddParameter(command, "@query", query);

        AddParameter(command, "@limit", limit);

        if (workspacePath is not null)
        {

            AddParameter(command, "@workspacePath", workspacePath);

        }

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            string label = workspaceLabel ?? "indexed workspace";

            results.Add(new MemorySearchResultDto(
                MemorySearchScope.Workspace,
                $"{reader.GetString(2)} lines {reader.GetInt32(5)}-{reader.GetInt32(6)}",
                reader.GetString(4),
                $"Workspace {label}, relative file {reader.GetString(2)}, chunk {reader.GetInt32(3)}",
                WorkspaceRetention,
                reader.GetString(0)));

        }

    }

    private static async Task SearchSagaAsync(
        ISagaMemoryStore store,
        string query,
        Guid? sessionId,
        List<MemorySearchResultDto> results,
        int limit,
        CancellationToken cancellationToken)
    {

        SagaMemoryDto[] memories = await store
            .ListAsync(
                query,
                sessionId,
                limit,
                0,
                cancellationToken)
            .ConfigureAwait(false);

        foreach (SagaMemoryDto memory in memories)
        {

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

            results.Add(new MemorySearchResultDto(
                MemorySearchScope.Saga,
                "Saga memory",
                memory.Content,
                provenance,
                SagaRetention,
                memory.Id));

        }

    }

    private static void AddLexiconMatches(
        IReadOnlyList<LexiconEntryDto> entries,
        string query,
        List<MemorySearchResultDto> results,
        int limit)
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

            string provenance = $"Lexicon entity: {entry.Name}";

            if (entry.FactProvenance is { Length: > 0 } factSources)
            {

                string sources = string.Join(
                    "; ",
                    factSources
                        .Select(static fact => FormatAttachmentProvenance(fact.Source))
                        .Distinct(StringComparer.Ordinal));

                provenance += $"; {sources}";

            }

            results.Add(new MemorySearchResultDto(
                MemorySearchScope.Lexicon,
                $"{entry.Name} [{entry.Type}]",
                string.Join("; ", entry.Facts),
                provenance,
                LexiconRetention,
                entry.Id.ToString("D")));

            taken++;

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

        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);

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

    private static async Task<DbConnection> OpenConnectionAsync(
        ArcanumDbContext db,
        CancellationToken cancellationToken)
    {

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        }

        return connection;

    }

    private static void AddSessionParameter(
        DbCommand command,
        Guid? sessionId)
    {

        if (sessionId is not null)
        {

            AddParameter(command, "@sessionId", sessionId.Value.ToString("D"));

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
