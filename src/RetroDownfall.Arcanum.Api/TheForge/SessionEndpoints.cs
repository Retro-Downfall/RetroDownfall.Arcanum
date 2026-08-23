using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class SessionEndpoints
{

    private static readonly byte[] SseDone = "data: [DONE]\n\n"u8.ToArray();

    private static readonly byte[] SseLiveSentinel = "data: {\"type\":\"live\"}\n\n"u8.ToArray();

    private static readonly byte[] SseDataPrefix = "data: "u8.ToArray();

    private static readonly byte[] SseLineBreak = "\n\n"u8.ToArray();

    private const long AttachmentMultipartEnvelopeAllowanceBytes = 64L * 1024L;

    public static RouteGroupBuilder MapSessionEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapPost(
            "/sessions",
            async (CreateSessionRequest? request, ISessionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null)
                {

                    return Results.BadRequest(
                        ApiResponse<SessionDetailDto>.FromResult(
                            Result<SessionDetailDto>.Failure(
                                new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.DefaultInvalidBodyMessage)),
                            traceId));

                }

                Session session = await repo
                    .CreateAsync(request.CampaignId, request.Title, ctx.RequestAborted)
                    .ConfigureAwait(false);

                int entryCount = 0;

                SessionDetailDto dto = SessionMapping.ToDetailDto(session, entryCount);

                return Results.Created(
                    $"/api/sessions/{session.Id:D}",
                    ApiResponse<SessionDetailDto>.FromResult(Result<SessionDetailDto>.Success(dto), traceId));
            })
        .WithName("CreateSession");

        apiGroup.MapGet(
            "/sessions/analytics",
            async (ISessionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                SessionAnalytics analytics = await repo.GetAnalyticsAsync(ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<SessionAnalytics>.FromResult(Result<SessionAnalytics>.Success(analytics), traceId));
            })
        .WithName("GetSessionAnalytics");

        apiGroup.MapGet(
            "/sessions",
            async (
                Guid? campaignId,
                string? status,
                string? search,
                string? title,
                MessageRole? role,
                string? model,
                DateTimeOffset? from,
                DateTimeOffset? to,
                int? limit,
                DateTimeOffset? beforeUpdatedAt,
                ISessionRepository repo,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                SessionQueryRequest request = new(
                    campaignId,
                    status,
                    search,
                    title,
                    role,
                    model,
                    from,
                    to,
                    limit,
                    beforeUpdatedAt);

                SessionQueryResult result = await repo.QueryAsync(request, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<SessionQueryResult>.FromResult(Result<SessionQueryResult>.Success(result), traceId));
            })
        .WithName("QuerySessions");

        apiGroup.MapGet(
            "/sessions/{id:guid}",
            async (Guid id, ISessionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Session? session = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (session is null)
                {
                    return Results.Json(
                        ApiResponse<SessionDetailDto>.FromResult(
                            Result<SessionDetailDto>.Failure(new Error(ErrorCodes.Session.NotFound, "Session was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSessionDetailDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                int entryCount = await repo.GetEntryCountAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<SessionDetailDto>.FromResult(
                        Result<SessionDetailDto>.Success(SessionMapping.ToDetailDto(session, entryCount)),
                        traceId));
            })
        .WithName("GetSession");

        apiGroup.MapGet(
            "/sessions/{id:guid}/entries",
            async (
                Guid id,
                int? offset,
                int? limit,
                DateTimeOffset? beforeCreatedAt,
                Guid? beforeId,
                bool? countOnly,
                ISessionRepository repo,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Session? session = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (session is null)
                {
                    return Results.Json(
                        ApiResponse<EntryDto[]>.FromResult(
                            Result<EntryDto[]>.Failure(new Error(ErrorCodes.Session.NotFound, "Session was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseEntryDtoArray,
                        statusCode: StatusCodes.Status404NotFound);
                }

                if (countOnly is true)
                {
                    int count = await repo.GetEntryCountAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                    return Results.Ok(
                        ApiResponse<SessionEntryCountDto>.FromResult(
                            Result<SessionEntryCountDto>.Success(new SessionEntryCountDto(count)),
                            traceId));
                }

                List<Entry> entries = await repo
                    .GetEntriesAsync(
                        id,
                        offset ?? 0,
                        limit ?? 100,
                        beforeCreatedAt,
                        beforeId,
                        ct: ctx.RequestAborted)
                    .ConfigureAwait(false);

                EntryDto[] dtos = entries.Select(SessionMapping.ToEntryDto).ToArray();

                return Results.Ok(
                    ApiResponse<EntryDto[]>.FromResult(Result<EntryDto[]>.Success(dtos), traceId));
            })
        .WithName("GetSessionEntries");

        apiGroup.MapGet(
            "/sessions/{id:guid}/attachments",
            async (
                Guid id,
                ISessionRepository repo,
                ISessionAttachmentStore store,
                ISessionAttachmentRetrievalService attachmentRetrieval,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Session? session = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (session is null)
                {
                    return Results.Json(
                        ApiResponse<SessionAttachmentDto[]>.FromResult(
                            Result<SessionAttachmentDto[]>.Failure(
                                new Error(ErrorCodes.Session.NotFound, "Session was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray,
                        statusCode: StatusCodes.Status404NotFound);
                }

                IReadOnlyList<SessionAttachmentRecord> bound = await store
                    .RevalidateBoundSourcesAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus> statuses = await attachmentRetrieval
                    .GetStatusesAsync(
                        [.. bound.Select(static attachment => attachment.Id)],
                        ctx.RequestAborted)
                    .ConfigureAwait(false);

                SessionAttachmentDto[] dtos = bound
                    .Select(attachment => SessionMapping.ToAttachmentDto(
                        attachment,
                        statuses.GetValueOrDefault(
                            attachment.Id,
                            SessionAttachmentIndexStatus.NotEligible)))
                    .ToArray();

                return Results.Ok(
                    ApiResponse<SessionAttachmentDto[]>.FromResult(
                        Result<SessionAttachmentDto[]>.Success(dtos),
                        traceId));
            })
        .WithName("GetSessionAttachments");

        apiGroup.MapPost(

            "/sessions/{id:guid}/attachments",

            async (

                Guid id,

                ISessionRepository repo,

                ISessionAttachmentStore store,

                ISessionAttachmentRetrievalService attachmentRetrieval,

                IOptionsMonitor<ArcanumSettings> options,

                HttpContext ctx) =>

            {

                ArcanumSettings settings = options.CurrentValue;

                if (!settings.ResolveAttachments().Enabled)

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.Disabled,

                        "Session attachments are disabled.");

                }

                Session? session = await repo

                    .GetByIdAsync(id, ctx.RequestAborted)

                    .ConfigureAwait(false);

                if (session is null)

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Session.NotFound,

                        "Session was not found.");

                }

                if (!ctx.Request.HasFormContentType)

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.InvalidRequest,

                        "A multipart form with a 'file' field is required.");

                }

                long maximumReadBytes = SessionAttachmentContentPolicy

                    .ResolveMaximumReadBytes(settings);

                long maximumRequestBytes = ResolveAttachmentMultipartRequestLimit(

                    maximumReadBytes);

                if (ctx.Request.ContentLength is { } contentLength

                    && contentLength > maximumRequestBytes)

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.TooLarge,

                        $"The multipart attachment request exceeds the {maximumRequestBytes}-byte aggregate limit.");

                }

                IHttpMaxRequestBodySizeFeature? requestBodySize = ctx.Features

                    .Get<IHttpMaxRequestBodySizeFeature>();

                if (requestBodySize is { IsReadOnly: false })

                {

                    requestBodySize.MaxRequestBodySize =

                        ResolveAttachmentMultipartTransportLimit(maximumRequestBytes);

                }

                IFormCollection form;

                Stream originalBody = ctx.Request.Body;

                using AttachmentMultipartAggregateReadStream aggregateBody = new(

                    originalBody,

                    maximumRequestBytes);

                try

                {

                    ctx.Request.Body = aggregateBody;

                    form = await ctx.Request

                        .ReadFormAsync(

                            CreateAttachmentFormOptions(maximumReadBytes),

                            ctx.RequestAborted)

                        .ConfigureAwait(false);

                }
                catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException)

                {

                    string errorCode = ResolveAttachmentFormErrorCode(exception);

                    return AttachmentFailure(

                        ctx,

                        errorCode,

                        errorCode == ErrorCodes.Attachment.TooLarge

                            ? "The attachment exceeds the multipart read limit."

                            : "The multipart attachment request could not be read.");

                }
                finally

                {

                    ctx.Request.Body = originalBody;

                }

                IFormFile? file = form.Files.GetFile("file");

                if (file is null)

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.InvalidRequest,

                        "A multipart 'file' field is required.");

                }

                string submittedFileName = GetSubmittedFileName(file.FileName);

                if (!SessionAttachmentPathSanitizer.TrySanitize(

                        submittedFileName,

                        out string safeFileName,

                        out _))

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.InvalidRequest,

                        "The attachment filename is invalid.");

                }

                string logicalNameHint = form["logicalName"].ToString();

                if (string.IsNullOrWhiteSpace(logicalNameHint))

                {

                    logicalNameHint = safeFileName;

                }

                if (!SessionAttachmentPathSanitizer.TrySanitize(

                        logicalNameHint,

                        out string safeLogicalName,

                        out _))

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.InvalidRequest,

                        "The attachment logical name is invalid.");

                }

                string declaredMimeType = NormalizeMimeType(file.ContentType);

                if (UploadedFileMimeValidator.IsExtensionMimeMismatch(

                        safeFileName,

                        declaredMimeType))

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.InvalidContent,

                        "The attachment filename extension does not match its declared content type.");

                }

                if (file.Length > maximumReadBytes || file.Length > int.MaxValue)

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.TooLarge,

                        $"The attachment exceeds the {maximumReadBytes}-byte read limit.");

                }

                byte[] bytes;

                await using (Stream source = file.OpenReadStream())

                {

                    using MemoryStream buffer = new((int)file.Length);

                    await source

                        .CopyToAsync(buffer, ctx.RequestAborted)

                        .ConfigureAwait(false);

                    if (buffer.Length > maximumReadBytes)

                    {

                        return AttachmentFailure(

                            ctx,

                            ErrorCodes.Attachment.TooLarge,

                            $"The attachment exceeds the {maximumReadBytes}-byte read limit.");

                    }

                    bytes = buffer.ToArray();

                }

                string detectedMimeType = AttachmentMimeDetector.Detect(bytes, safeFileName);

                string mimeType = ResolveSnapshotMimeType(

                    declaredMimeType,

                    detectedMimeType);

                SessionAttachmentKind kind = SessionAttachmentContentPolicy.Classify(mimeType);

                string? validationError = SessionAttachmentContentPolicy.Validate(

                    kind,

                    bytes,

                    mimeType,

                    settings);

                if (validationError is not null)

                {

                    string errorCode = validationError.Contains(

                        "byte limit",

                        StringComparison.OrdinalIgnoreCase)

                            ? ErrorCodes.Attachment.TooLarge

                            : ErrorCodes.Attachment.InvalidContent;

                    return AttachmentFailure(ctx, errorCode, validationError);

                }

                SessionAttachmentRecord attachment;

                try

                {

                    attachment = await store

                        .PersistNewAsync(

                            id,

                            pendingTurnId: null,

                            entryId: null,

                            safeLogicalName,

                            safeFileName,

                            bytes,

                            mimeType,

                            kind,

                            ctx.RequestAborted)

                        .ConfigureAwait(false);

                }
                catch (InvalidOperationException)

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.LimitExceeded,

                        "The session attachment storage limit would be exceeded.");

                }

                SessionAttachmentDto dto = await ToAttachmentDtoAsync(

                    attachment,

                    attachmentRetrieval,

                    ctx.RequestAborted)

                    .ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                return Results.Created(

                    $"/api/sessions/{id:D}/attachments/{attachment.Id:D}/content",

                    ApiResponse<SessionAttachmentDto>.FromResult(

                        Result<SessionAttachmentDto>.Success(dto),

                        traceId));

            })

        .WithName("CreateSessionAttachmentSnapshot");

        apiGroup.MapPost(

            "/sessions/{id:guid}/attachments/reference",

            async (

                Guid id,

                ISessionRepository repo,

                ISessionAttachmentStore store,

                IAttachmentSourceResolver sourceResolver,

                ISessionAttachmentRetrievalService attachmentRetrieval,

                ISanctumGuard sanctumGuard,

                IWorkspaceRegistry workspaceRegistry,

                IOptionsMonitor<ArcanumSettings> options,

                HttpContext ctx) =>

            {

                ArcanumSettings settings = options.CurrentValue;

                if (!settings.ResolveAttachments().Enabled)

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.Disabled,

                        "Session attachments are disabled.");

                }

                Session? session = await repo

                    .GetByIdAsync(id, ctx.RequestAborted)

                    .ConfigureAwait(false);

                if (session is null)

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Session.NotFound,

                        "Session was not found.");

                }

                CreateSessionAttachmentReferenceRequest? request;

                IResult? jsonError;

                (request, jsonError) = await ApiRequestJson.ReadAsync(

                    ctx,

                    ArcanumJsonContext.Default.CreateSessionAttachmentReferenceRequest,

                    static httpContext => ApiRequestJson.InvalidBodyResult<SessionAttachmentDto>(

                        httpContext,

                        ApiRequestJson.MalformedJsonMessage,

                        ArcanumJsonContext.Default.ApiResponseSessionAttachmentDto),

                    ctx.RequestAborted)

                    .ConfigureAwait(false);

                if (jsonError is not null)

                {

                    return jsonError;

                }

                if (request is null || string.IsNullOrWhiteSpace(request.WorkspacePath))

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.InvalidRequest,

                        "A workspace path is required.");

                }

                string? workspaceRoot;

                bool explicitRegisteredWorkspace = false;

                if (!string.IsNullOrWhiteSpace(request.WorkspaceId))

                {

                    WorkspaceInfo? workspace = await workspaceRegistry

                        .GetAsync(request.WorkspaceId.Trim(), ctx.RequestAborted)

                        .ConfigureAwait(false);

                    if (workspace is null)

                    {

                        return AttachmentFailure(

                            ctx,

                            ErrorCodes.Workspace.NotFound,

                            "No workspace exists with that id.");

                    }

                    workspaceRoot = workspace.Path;

                    explicitRegisteredWorkspace = true;

                }
                else

                {

                    workspaceRoot = settings.ResolveDefaultWorkspace();

                }

                if (string.IsNullOrWhiteSpace(workspaceRoot))

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.SourceUnavailable,

                        "No server workspace is available for attachment references.");

                }

                string normalizedRoot;

                string candidate;

                try

                {

                    normalizedRoot = Path.GetFullPath(workspaceRoot);

                    candidate = Path.IsPathRooted(request.WorkspacePath)

                        ? Path.GetFullPath(request.WorkspacePath)

                        : Path.GetFullPath(Path.Combine(normalizedRoot, request.WorkspacePath));

                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.InvalidReference,

                        "The workspace attachment path is invalid.");

                }

                string submittedFileName = GetSubmittedFileName(request.WorkspacePath);

                if (!SessionAttachmentPathSanitizer.TrySanitize(

                        submittedFileName,

                        out string safeFileName,

                        out _))

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.InvalidRequest,

                        "The attachment filename is invalid.");

                }

                string logicalNameHint = string.IsNullOrWhiteSpace(request.LogicalName)

                    ? safeFileName

                    : request.LogicalName;

                if (!SessionAttachmentPathSanitizer.TrySanitize(

                        logicalNameHint,

                        out string safeLogicalName,

                        out _))

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.InvalidRequest,

                        "The attachment logical name is invalid.");

                }

                async Task<bool> AuthorizeCanonicalPathAsync(

                    string canonicalPath,

                    CancellationToken cancellationToken)

                {

                    if (session.CampaignId is not { } campaignId)

                    {

                        return true;

                    }

                    SanctumResult result = await sanctumGuard

                        .ValidatePathAsync(

                            campaignId.ToString("D"),

                            canonicalPath,

                            "attachment source read",

                            "attachment_reference",

                            cancellationToken)

                        .ConfigureAwait(false);

                    return result.Allowed;

                }

                long maximumReadBytes = SessionAttachmentContentPolicy

                    .ResolveMaximumReadBytes(settings);

                AttachmentSourceResolution resolution = await sourceResolver

                    .ResolveForReferenceAsync(

                        new AttachmentSourceClaim(

                            candidate,

                            explicitRegisteredWorkspace ? normalizedRoot : null),

                        maximumReadBytes,

                        AuthorizeCanonicalPathAsync,

                        ctx.RequestAborted)

                    .ConfigureAwait(false);

                IResult? resolutionError = ResolveAttachmentReferenceError(

                    ctx,

                    resolution,

                    maximumReadBytes);

                if (resolutionError is not null)

                {

                    return resolutionError;

                }

                string mimeType = resolution.DetectedMimeType!;

                SessionAttachmentKind kind = SessionAttachmentContentPolicy.Classify(mimeType);

                string? validationError = SessionAttachmentContentPolicy.Validate(

                    kind,

                    resolution.VerifiedBytes,

                    mimeType,

                    settings);

                if (validationError is not null)

                {

                    string errorCode = validationError.Contains(

                        "byte limit",

                        StringComparison.OrdinalIgnoreCase)

                            ? ErrorCodes.Attachment.TooLarge

                            : ErrorCodes.Attachment.InvalidContent;

                    return AttachmentFailure(ctx, errorCode, validationError);

                }

                SessionAttachmentRecord attachment;

                try

                {

                    attachment = await store

                        .PersistNewResolvedSourceAsync(

                            id,

                            pendingTurnId: null,

                            entryId: null,

                            safeLogicalName,

                            safeFileName,

                            kind,

                            resolution,

                            ctx.RequestAborted)

                        .ConfigureAwait(false);

                }
                catch (InvalidOperationException)

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.LimitExceeded,

                        "The session attachment storage limit would be exceeded.");

                }

                SessionAttachmentDto dto = await ToAttachmentDtoAsync(

                    attachment,

                    attachmentRetrieval,

                    ctx.RequestAborted)

                    .ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                return Results.Created(

                    $"/api/sessions/{id:D}/attachments/{attachment.Id:D}/content",

                    ApiResponse<SessionAttachmentDto>.FromResult(

                        Result<SessionAttachmentDto>.Success(dto),

                        traceId));

            })

        .WithName("CreateSessionAttachmentReference");

        apiGroup.MapGet(

            "/sessions/{id:guid}/attachments/{attachmentId:guid}/content",

            async (

                Guid id,

                Guid attachmentId,

                ISessionRepository repo,

                ISessionAttachmentStore store,

                IOptionsMonitor<ArcanumSettings> options,

                HttpContext ctx) =>

            {

                if (!options.CurrentValue.ResolveAttachments().Enabled)

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.Disabled,

                        "Session attachments are disabled.");

                }

                Session? session = await repo

                    .GetByIdAsync(id, ctx.RequestAborted)

                    .ConfigureAwait(false);

                if (session is null)

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Session.NotFound,

                        "Session was not found.");

                }

                SessionAttachmentRecord? attachment = await store

                    .GetByIdAsync(attachmentId, ctx.RequestAborted)

                    .ConfigureAwait(false);

                if (attachment is null

                    || attachment.State != SessionAttachmentState.Bound

                    || attachment.SessionId != id)

                {

                    return AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.NotFound,

                        "Attachment was not found in this session.");

                }

                Stream plaintext = await store

                    .OpenReadAsync(attachment, ctx.RequestAborted)

                    .ConfigureAwait(false);

                string downloadName = SessionAttachmentPathSanitizer.TrySanitize(

                    attachment.OriginalFileName,

                    out string safeFileName,

                    out _)

                        ? safeFileName

                        : "attachment";

                string mimeType = string.IsNullOrWhiteSpace(attachment.MimeType)

                    ? "application/octet-stream"

                    : attachment.MimeType;

                ctx.Response.Headers.CacheControl = "no-store";

                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

                return Results.Stream(

                    plaintext,

                    mimeType,

                    fileDownloadName: downloadName,

                    enableRangeProcessing: false);

            })

        .WithName("DownloadSessionAttachment");

        apiGroup.MapPost(

            "/sessions/{id:guid}/attachments/{attachmentId:guid}/refresh",

            async (

                Guid id,

                Guid attachmentId,

                ISessionRepository repo,

                ToolExecutionPipeline refreshPipeline,

                HttpContext ctx) =>

            {

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Session? session = await repo

                    .GetByIdAsync(id, ctx.RequestAborted)

                    .ConfigureAwait(false);

                if (session is null)

                {

                    return Results.Json(

                        ApiResponse<AttachmentRefreshEvent>.FromResult(

                            Result<AttachmentRefreshEvent>.Failure(

                                new Error(ErrorCodes.Session.NotFound, "Session was not found.")),

                            traceId),

                        ArcanumJsonContext.Default.ApiResponseAttachmentRefreshEvent,

                        statusCode: StatusCodes.Status404NotFound);

                }

                Result<AttachmentRefreshEvent> result = await refreshPipeline

                    .RefreshSessionAttachmentAsync(

                        id,

                        attachmentId,

                        session.CampaignId,

                        ctx.RequestAborted)

                    .ConfigureAwait(false);

                if (result.IsFailure)

                {

                    return Results.Json(

                        ApiResponse<AttachmentRefreshEvent>.FromResult(result, traceId),

                        ArcanumJsonContext.Default.ApiResponseAttachmentRefreshEvent,

                        statusCode: ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));

                }

                return Results.Ok(

                    ApiResponse<AttachmentRefreshEvent>.FromResult(result, traceId));

            })

        .WithName("RefreshSessionAttachment");

        apiGroup.MapGet(
            "/sessions/{id:guid}/context-pins",
            async (Guid id, ISessionRepository repo, ISessionContextPinStore store, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;
                if (await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false) is null)
                {
                    return Results.Json(
                        ApiResponse<SessionContextPinDto[]>.FromResult(
                            Result<SessionContextPinDto[]>.Failure(
                                new Error(ErrorCodes.Session.NotFound, "Session was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSessionContextPinDtoArray,
                        statusCode: StatusCodes.Status404NotFound);
                }

                IReadOnlyList<SessionContextPinRecord> pins =
                    await store.ListAsync(id, ctx.RequestAborted).ConfigureAwait(false);
                return Results.Ok(ApiResponse<SessionContextPinDto[]>.FromResult(
                    Result<SessionContextPinDto[]>.Success(pins.Select(ToContextPinDto).ToArray()),
                    traceId));
            })
        .WithName("GetSessionContextPins");

        apiGroup.MapPost(
            "/sessions/{id:guid}/context-pins",
            async (
                Guid id,
                CreateSessionContextPinRequest? request,
                ISessionRepository repo,
                ISessionContextPinStore store,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;
                if (request is null || string.IsNullOrWhiteSpace(request.TargetIdentifier))
                {
                    return Results.BadRequest(ApiResponse<SessionContextPinDto>.FromResult(
                        Result<SessionContextPinDto>.Failure(
                            new Error(ErrorCodes.Validation.InvalidBody, "A context pin target is required.")),
                        traceId));
                }

                if (await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false) is null)
                {
                    return Results.Json(
                        ApiResponse<SessionContextPinDto>.FromResult(
                            Result<SessionContextPinDto>.Failure(
                                new Error(ErrorCodes.Session.NotFound, "Session was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSessionContextPinDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                string target = request.TargetIdentifier.Trim();
                if (target.Length > 16_384)
                {
                    return Results.BadRequest(ApiResponse<SessionContextPinDto>.FromResult(
                        Result<SessionContextPinDto>.Failure(
                            new Error(ErrorCodes.Validation.InvalidBody, "Context pin target is too long.")),
                        traceId));
                }

                string label = string.IsNullOrWhiteSpace(request.DisplayLabel)
                    ? target.Length <= 160 ? target : target[..157] + "..."
                    : request.DisplayLabel.Trim();
                if (label.Length > 256)
                {
                    return Results.BadRequest(ApiResponse<SessionContextPinDto>.FromResult(
                        Result<SessionContextPinDto>.Failure(
                            new Error(ErrorCodes.Validation.InvalidBody, "Context pin label is too long.")),
                        traceId));
                }

                SessionContextPinRecord pin = await store.UpsertAsync(
                    id, request.Kind, target, label, request.ContentVersion, ctx.RequestAborted)
                    .ConfigureAwait(false);
                return Results.Ok(ApiResponse<SessionContextPinDto>.FromResult(
                    Result<SessionContextPinDto>.Success(ToContextPinDto(pin)), traceId));
            })
        .WithName("CreateSessionContextPin");

        apiGroup.MapDelete(
            "/sessions/{id:guid}/context-pins/{pinId:guid}",
            async (Guid id, Guid pinId, ISessionContextPinStore store, HttpContext ctx) =>
                await store.DeleteAsync(id, pinId, ctx.RequestAborted).ConfigureAwait(false)
                    ? Results.NoContent()
                    : Results.NotFound())
        .WithName("DeleteSessionContextPin");

        apiGroup.MapPost(
            "/sessions/{id:guid}/entries",
            async (Guid id, AppendEntryRequest? request, ISessionRepository repo, SessionEventHub eventHub, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null)
                {

                    return Results.BadRequest(
                        ApiResponse<EntryDto>.FromResult(
                            Result<EntryDto>.Failure(
                                new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.DefaultInvalidBodyMessage)),
                            traceId));

                }

                if (string.IsNullOrWhiteSpace(request.Content))
                {
                    return Results.BadRequest(
                        ApiResponse<EntryDto>.FromResult(
                            Result<EntryDto>.Failure(
                                new Error(ErrorCodes.Session.EmptyContent, "Entry content is required.")),
                            traceId));
                }

                Entry entry = new()
                {
                    Id = Guid.NewGuid(),
                    Role = request.Role,
                    Content = request.Content.Trim(),
                    ModelUsed = request.ModelUsed ?? string.Empty,
                };

                Result<Entry> addResult = await repo.AddEntryAsync(id, entry, ctx.RequestAborted).ConfigureAwait(false);

                if (addResult.IsFailure)
                {

                    return Results.Json(
                        ApiResponse<EntryDto>.FromResult(
                            Result<EntryDto>.Failure(addResult.Error),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseEntryDto,
                        statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(addResult.Error.Code));

                }

                eventHub.Publish(id, addResult.Value);

                return Results.Ok(
                    ApiResponse<EntryDto>.FromResult(
                        Result<EntryDto>.Success(SessionMapping.ToEntryDto(addResult.Value)),
                        traceId));
            })
        .WithName("AppendSessionEntry");

        apiGroup.MapPatch(
            "/sessions/{id:guid}",
            async (Guid id, UpdateSessionRequest? request, ISessionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (request is null)
                {

                    return Results.BadRequest(
                        ApiResponse<SessionDetailDto>.FromResult(
                            Result<SessionDetailDto>.Failure(
                                new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.DefaultInvalidBodyMessage)),
                            traceId));

                }

                Session? session = await GetSessionForUpdateAsync(repo, id, ctx.RequestAborted).ConfigureAwait(false);

                if (session is null)
                {
                    return Results.Json(
                        ApiResponse<SessionDetailDto>.FromResult(
                            Result<SessionDetailDto>.Failure(new Error(ErrorCodes.Session.NotFound, "Session was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSessionDetailDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                if (request.Title is not null)
                {
                    session.Title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
                }

                if (request.Status is not null)
                {
                    string status = request.Status.Trim();

                    if (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(status, "archived", StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.BadRequest(
                            ApiResponse<SessionDetailDto>.FromResult(
                                Result<SessionDetailDto>.Failure(
                                    new Error(ErrorCodes.Session.InvalidStatus, "Status must be 'active' or 'archived'.")),
                                traceId));
                    }

                    session.Status = string.Equals(status, "archived", StringComparison.OrdinalIgnoreCase)
                        ? "archived"
                        : "active";
                }

                await repo.UpdateSessionAsync(session, ctx.RequestAborted).ConfigureAwait(false);

                int entryCount = await repo.GetEntryCountAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<SessionDetailDto>.FromResult(
                        Result<SessionDetailDto>.Success(SessionMapping.ToDetailDto(session, entryCount)),
                        traceId));
            })
        .WithName("UpdateSession");

        apiGroup.MapDelete(
            "/sessions/{id:guid}",
            async (Guid id, ISessionRepository repo, HttpContext ctx) =>
            {
                Session? session = await repo.GetByIdAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                if (session is null)
                {
                    string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                    return Results.Json(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error(ErrorCodes.Session.NotFound, "No session exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                await repo.ArchiveAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                return Results.NoContent();
            })
        .WithName("ArchiveSession");

        apiGroup.MapGet(
            "/sessions/{id:guid}/export",
            async (Guid id, string? format, ISessionRepository repo, HttpContext ctx) =>
                await ExportSessionAsync(id, format, repo, ctx).ConfigureAwait(false))
        .WithName("ExportSession")
        .RequireConditionalCovenantReadAuthority();

        apiGroup.MapPost(
            "/sessions/{id:guid}/fork",
            async (Guid id, ForkSessionRequest? request, ISessionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<Session> result = await repo
                    .ForkAsync(id, request ?? new ForkSessionRequest(), ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return Results.Json(
                        ApiResponse<SessionDetailDto>.FromResult(Result<SessionDetailDto>.Failure(result.Error), traceId),
                        ArcanumJsonContext.Default.ApiResponseSessionDetailDto,
                        statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));
                }

                Session fork = result.Value;

                int entryCount = await repo.GetEntryCountAsync(fork.Id, ctx.RequestAborted).ConfigureAwait(false);

                SessionDetailDto dto = SessionMapping.ToDetailDto(fork, entryCount);

                return Results.Created(
                    $"/api/sessions/{fork.Id:D}",
                    ApiResponse<SessionDetailDto>.FromResult(Result<SessionDetailDto>.Success(dto), traceId));
            })
        .WithName("ForkSession")
        .WithLargeRequestBody();

        apiGroup.MapPost(
            "/sessions/{id:guid}/rest",
            async (Guid id, IGrimoireRepository grimoire, ICampaignLoggerQueue queue, HttpContext ctx) =>
            {
                if (!await grimoire.SessionExistsAsync(id, ctx.RequestAborted).ConfigureAwait(false))
                {
                    string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                    Result<bool> notFound = Result<bool>.Failure(
                        new Error(ErrorCodes.Session.NotFound, "No session exists with that id."));

                    return Results.NotFound(ApiResponse<bool>.FromResult(notFound, traceId));
                }

                string acceptedTraceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                if (!queue.TryQueue(id))
                {
                    Result<bool> rejected = Result<bool>.Failure(
                        new Error(
                            ErrorCodes.Session.RestQueueFull,
                            "Campaign Log consolidation could not be queued; the queue is full. Try again shortly."));

                    return Results.Json(
                        ApiResponse<bool>.FromResult(rejected, acceptedTraceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                Result<bool> queued = Result<bool>.Success(true);

                return Results.Json(
                    ApiResponse<bool>.FromResult(queued, acceptedTraceId),
                    ArcanumJsonContext.Default.ApiResponseBoolean,
                    statusCode: StatusCodes.Status202Accepted);
            })
        .WithName("PostSessionRest");

        apiGroup.MapGet(
            "/sessions/{id:guid}/stream",
            async (
                Guid id,
                Guid? since,
                ISessionRepository repo,
                SessionEventHub eventHub,
                SseConnectionGate sseGate,
                IOptionsMonitor<ArcanumSettings> options,
                HttpContext httpContext) =>
            {
                Session? session = await repo.GetByIdAsync(id, httpContext.RequestAborted).ConfigureAwait(false);

                if (session is null)
                {
                    string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    return Results.Json(
                        ApiResponse<SessionDetailDto>.FromResult(
                            Result<SessionDetailDto>.Failure(new Error(ErrorCodes.Session.NotFound, "No session exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSessionDetailDto,
                        statusCode: StatusCodes.Status404NotFound);
                }

                Entry? sinceEntry = null;

                if (since is Guid sinceEntryId)
                {
                    sinceEntry = await repo
                        .GetEntryAsync(id, sinceEntryId, httpContext.RequestAborted)
                        .ConfigureAwait(false);

                    if (sinceEntry is null)
                    {
                        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                        return Results.Json(
                            ApiResponse<EntryDto>.FromResult(
                                Result<EntryDto>.Failure(new Error(ErrorCodes.Session.EntryNotFound, "No entry exists with that id in this session.")),
                                traceId),
                            ArcanumJsonContext.Default.ApiResponseEntryDto,
                            statusCode: StatusCodes.Status404NotFound);
                    }
                }

                if (!sseGate.TryAcquire(SseEventTypes.Session, out SseConnectionLease? sseLease, out SseConnectionDenial denial))
                {

                    return SseConnectionResults.FromDenial(httpContext, denial);

                }

                using (sseLease)
                {

                SseStreamWriter.PrepareResponse(httpContext);

                int channelCapacity = ArcanumSettingClamps.EventBusChannelCapacity(
                    options.CurrentValue.ResolveEventBus().ChannelCapacity);

                Channel<Entry> liveBuffer = Channel.CreateBounded<Entry>(
                    new BoundedChannelOptions(channelCapacity)
                    {
                        SingleReader = true,
                        SingleWriter = true,
                        FullMode = BoundedChannelFullMode.DropOldest,
                    });

                using CancellationTokenSource pumpCts = CancellationTokenSource.CreateLinkedTokenSource(
                    httpContext.RequestAborted);

                Task pumpTask = PumpSessionLiveAsync(id, eventHub, liveBuffer.Writer, pumpCts.Token);

                // The pump owns a SessionEventHub subscription that only its own cancellation
                // releases, so everything from here on must sit inside the try whose finally cancels
                // pumpCts. Leaving the Grimoire replay outside it let a fault there unwind through
                // `using pumpCts` — disposing the linked source without cancelling it, which unlinks
                // it from RequestAborted for good and strands the pump, its subscriber channel, and
                // the per-session hub entry.
                try
                {

                    HashSet<Guid> replayIds = [];

                    SessionSettings sessionSettings = options.CurrentValue.ResolveSessions();

                    int replayLimit = ArcanumSettingClamps.SessionStreamReplayLimit(
                        sessionSettings.MaxStreamReplayEntries);

                    if (sinceEntry is not null)
                    {
                        List<Entry> catchUp = await repo
                            .GetEntriesAfterAsync(id, sinceEntry.Sequence, replayLimit, httpContext.RequestAborted)
                            .ConfigureAwait(false);

                        foreach (Entry entry in catchUp)
                        {
                            replayIds.Add(entry.Id);

                            await WriteEntrySseAsync(httpContext, entry, httpContext.RequestAborted).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        List<Entry> replay = await repo
                            .GetEntriesAscendingAsync(id, replayLimit, httpContext.RequestAborted)
                            .ConfigureAwait(false);

                        replayIds = replay.Select(e => e.Id).ToHashSet();

                        foreach (Entry entry in replay)
                        {
                            await WriteEntrySseAsync(httpContext, entry, httpContext.RequestAborted).ConfigureAwait(false);
                        }
                    }

                    await httpContext.Response.Body.WriteAsync(SseLiveSentinel, httpContext.RequestAborted).ConfigureAwait(false);

                    await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);

                    TimeSpan heartbeatInterval = TimeSpan.FromSeconds(
                        ArcanumSettingClamps.EventBusHeartbeatSeconds(
                            options.CurrentValue.ResolveEventBus().HeartbeatSeconds));

                    while (liveBuffer.Reader.TryRead(out Entry? buffered) && buffered is not null)
                    {

                        if (!replayIds.Contains(buffered.Id))
                        {

                            await WriteEntrySseAsync(httpContext, buffered, httpContext.RequestAborted).ConfigureAwait(false);

                        }

                    }

                    await SseStreamWriter.StreamAsync(
                        httpContext,
                        liveBuffer.Reader.ReadAllAsync(httpContext.RequestAborted),
                        (entry, ct) => WriteEntrySseAsync(httpContext, entry, ct),
                        heartbeatInterval,
                        httpContext.RequestAborted).ConfigureAwait(false);

                }
                catch (Exception ex) when (ClientDisconnect.IsClientDisconnect(ex, httpContext))
                {

                    // The client went away during replay, the buffered drain, or the live stream.
                    // Break silently — no DONE frame to a dead socket. Mirrors the Chronicle stream
                    // in ApprenticeEndpoints; the finally still cancels the pump CTS.

                }
                catch (OperationCanceledException)
                {

                    await SseStreamWriter.WriteDoneAsync(httpContext).ConfigureAwait(false);

                }
                finally
                {
                    await pumpCts.CancelAsync().ConfigureAwait(false);

                    try
                    {
                        await pumpTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                return Results.Empty;

                }

            })
        .WithName("StreamSession");

        apiGroup.MapDelete(
            "/sessions/{id:guid}/entries/{entryId:guid}",
            async (
                Guid id,
                Guid entryId,
                IGrimoireRepository grimoire,
                ICovenantSensitiveArtifactPurger purger,
                IOptionsMonitor<ArcanumSettings> options,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                SessionSettings sessionSettings = options.CurrentValue.ResolveSessions();

                if (!sessionSettings.AllowMemoryManagement)
                {
                    return Results.BadRequest(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(
                                new Error(ErrorCodes.Session.MemoryManagementDisabled, "Memory management is disabled.")),
                            traceId));
                }

                GrimoireEntryDto? entry = await grimoire
                    .GetEntryByIdAsync(id, entryId, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (entry is null)
                {
                    return Results.Json(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error(ErrorCodes.Session.EntryNotFound, "Entry was not found in this session.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                // Dispatched before the ordinary delete, never after. A labelled assistant Entry has to
                // leave through the shared kernel so its erasure receipt is appended and its
                // finalization guard and turn claim survive; deleting the row here first would strand a
                // guard pointing at nothing, which is the one integrity state indistinguishable from
                // data loss (§10.20.2).
                Result<CovenantSensitivePurgeOutcome> purged = await CovenantSensitiveDeletion
                    .DispatchAsync(purger, SensitiveArtifactKind.AssistantEntry, entryId, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (purged.IsFailure)
                {
                    return Results.Json(
                        ApiResponse<bool>.FromResult(Result<bool>.Failure(purged.Error), traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(purged.Error.Code));
                }

                CovenantSensitiveDeletion.MarkProtectedWhenPurged(ctx, purged.Value);

                if (purged.Value.IsBlocked)
                {
                    Error blocked = CovenantSensitiveDeletion.BlockedError(purged.Value);

                    return Results.Json(
                        ApiResponse<bool>.FromResult(Result<bool>.Failure(blocked), traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: ArcanumErrorMapper.ResolveStatusCode(blocked.Code));
                }

                if (purged.Value.RequiresOrdinaryDelete(entryId))
                {
                    await grimoire.DeleteEntryAsync(id, entryId, ctx.RequestAborted).ConfigureAwait(false);
                }

                return Results.NoContent();
            })
        .RequireConditionalSensitivityRetentionPurge()
        .WithName("DeleteSessionEntry");

        apiGroup.MapPost(
            "/sessions/{id:guid}/entries/{entryId:guid}/pin",
            async (Guid id, Guid entryId, IGrimoireRepository grimoire, IOptionsMonitor<ArcanumSettings> options, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                SessionSettings sessionSettings = options.CurrentValue.ResolveSessions();

                if (!sessionSettings.AllowMemoryManagement)
                {
                    return Results.BadRequest(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(
                                new Error(ErrorCodes.Session.MemoryManagementDisabled, "Memory management is disabled.")),
                            traceId));
                }

                GrimoireEntryDto? entry = await grimoire
                    .GetEntryByIdAsync(id, entryId, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (entry is null)
                {
                    return Results.Json(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error(ErrorCodes.Session.EntryNotFound, "Entry was not found in this session.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                if (!entry.IsPinned)
                {
                    int maxPinned = ArcanumSettingClamps.SessionMaxPinnedEntries(sessionSettings.MaxPinnedEntries);

                    int currentPinned = await grimoire.GetPinnedEntryCountAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                    if (currentPinned >= maxPinned)
                    {
                        return Results.Json(
                            ApiResponse<bool>.FromResult(
                                Result<bool>.Failure(
                                    new Error(ErrorCodes.Session.TooManyPinned, $"Cannot pin more than {maxPinned} entries in this session.")),
                                traceId),
                            ArcanumJsonContext.Default.ApiResponseBoolean,
                            statusCode: StatusCodes.Status409Conflict);
                    }
                }

                await grimoire.SetEntryPinnedAsync(id, entryId, true, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId));
            })
        .WithName("PinSessionEntry");

        apiGroup.MapPost(
            "/sessions/{id:guid}/compact",
            async (Guid id, IContextCompressionService compression, IGrimoireRepository grimoire, IOptionsMonitor<ArcanumSettings> options, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                SessionSettings sessionSettings = options.CurrentValue.ResolveSessions();

                if (!sessionSettings.AllowMemoryManagement)
                {
                    return Results.BadRequest(
                        ApiResponse<CompactResult>.FromResult(
                            Result<CompactResult>.Failure(
                                new Error(ErrorCodes.Session.MemoryManagementDisabled, "Memory management is disabled.")),
                            traceId));
                }

                if (!await grimoire.SessionExistsAsync(id, ctx.RequestAborted).ConfigureAwait(false))
                {
                    return Results.Json(
                        ApiResponse<CompactResult>.FromResult(
                            Result<CompactResult>.Failure(new Error(ErrorCodes.Session.NotFound, "Session was not found.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseCompactResult,
                        statusCode: StatusCodes.Status404NotFound);
                }

                ArcanumSettings settings = options.CurrentValue;

                int contextWindowLimit = 8192; // Matches ContextCompressionService.DefaultContextWindowLimit.

                if (ProviderResolver.TryResolveProviderForModel(settings, settings.DefaultModel, out ProviderSettings? provider, out _)
                    && provider is not null)
                {
                    contextWindowLimit = provider.ContextWindowLimit;
                }

                CompactResult result = await compression
                    .CompressSessionAsync(id, contextWindowLimit, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<CompactResult>.FromResult(Result<CompactResult>.Success(result), traceId));
            })
        .RequireConditionalSensitivityRetentionPurge()
        .WithName("CompactSession");

        apiGroup.MapDelete(
            "/sessions/{id:guid}/entries/{entryId:guid}/pin",
            async (Guid id, Guid entryId, IGrimoireRepository grimoire, IOptionsMonitor<ArcanumSettings> options, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                SessionSettings sessionSettings = options.CurrentValue.ResolveSessions();

                if (!sessionSettings.AllowMemoryManagement)
                {
                    return Results.BadRequest(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(
                                new Error(ErrorCodes.Session.MemoryManagementDisabled, "Memory management is disabled.")),
                            traceId));
                }

                GrimoireEntryDto? entry = await grimoire
                    .GetEntryByIdAsync(id, entryId, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (entry is null)
                {
                    return Results.Json(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error(ErrorCodes.Session.EntryNotFound, "Entry was not found in this session.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                await grimoire.SetEntryPinnedAsync(id, entryId, false, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId));
            })
        .WithName("UnpinSessionEntry");

        return apiGroup;
    }

    /// <summary>
    /// Exports one Session as plaintext, or refuses before a single content byte.
    /// </summary>
    /// <remarks>
    /// The order here is the whole contract. The conditional read lease is taken <em>before</em> the
    /// export graph, the sensitivity decision is read under that same lease, and only a clean answer
    /// reaches <see cref="ISessionRepository.ExportAsync"/> at all. A refusal that ran afterwards
    /// would already have pulled the Covenant-derived transcript into the process it is refusing to
    /// send it out of (§10.19.11).
    ///
    /// <para>The lease is then transferred to the response, which revalidates it before the first
    /// byte and releases it after the last. A handler that disposed it on return would still be
    /// serializing protected content while a reset was draining.</para>
    /// </remarks>
    private static async Task<IResult> ExportSessionAsync(
        Guid id,
        string? requestedFormat,
        ISessionRepository repo,
        HttpContext ctx)
    {

        string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

        if (!TryParseExportFormat(requestedFormat, out SessionExportFormat format))
        {

            return SessionExportResponse(
                Result<SessionExportResult>.Failure(
                    new Error(
                        ErrorCodes.Session.InvalidFormat,
                        "format must be 'json' or 'markdown'.")),
                traceId);

        }

        ICovenantExportPolicy? policy = ctx.RequestServices.GetService<ICovenantExportPolicy>();

        if (policy is null)
        {

            return SessionExportResponse(
                await repo.ExportAsync(id, format, ctx.RequestAborted).ConfigureAwait(false),
                traceId);

        }

        // A Session's labels may name any Campaign, or none, so the arm asks for an installation read.
        Result<CovenantExportAdmission> admission = await policy
            .AcquireConditionalReadAsync(scope: null, ctx.RequestAborted)
            .ConfigureAwait(false);

        if (admission.IsFailure)
        {

            return SessionExportResponse(
                Result<SessionExportResult>.Failure(admission.Error),
                traceId);

        }

        ICovenantSnapshotReadLease? owned = admission.Value.ReadLease;

        if (owned is null)
        {

            return SessionExportResponse(
                await repo.ExportAsync(id, format, ctx.RequestAborted).ConfigureAwait(false),
                traceId);

        }

        try
        {

            Result<CovenantSessionExportSensitivity> sensitivity = await policy
                .InspectSessionAsync(id, owned, ctx.RequestAborted)
                .ConfigureAwait(false);

            Result<SessionExportResult> result;

            if (sensitivity.IsFailure)
            {

                result = Result<SessionExportResult>.Failure(sensitivity.Error);

            }
            else if (sensitivity.Value.IsRefused)
            {

                result = Result<SessionExportResult>.Failure(
                    new Error(
                        ErrorCodes.Covenant.PlaintextExportRefused,
                        "This session carries Covenant-derived content, so it cannot be exported as plaintext."));

            }
            else
            {

                result = await repo.ExportAsync(id, format, ctx.RequestAborted).ConfigureAwait(false);

            }

            // Ownership moves to the result, which revalidates before the first byte and disposes in
            // its own finally. Clearing the local is what keeps the guard below from double-releasing.
            IResult response = new CovenantProtectedJsonResult<SessionExportResult>(
                owned,
                result,
                ArcanumJsonContext.Default.ApiResponseSessionExportResult);

            owned = null;

            return response;

        }
        finally
        {

            if (owned is not null)
            {

                await owned.DisposeAsync().ConfigureAwait(false);

            }

        }

    }

    /// <summary>
    /// Parses the documented <c>format</c> vocabulary rather than the CLR enum spelling.
    /// </summary>
    /// <remarks>
    /// Bound as a string on purpose. Minimal-API enum binding is case-sensitive, so a route typed as
    /// <see cref="SessionExportFormat"/> accepted only <c>Json</c> and <c>Markdown</c> — while the
    /// enum's own wire names, the published contract, and <c>arcanum session export</c> all say
    /// <c>json</c> and <c>markdown</c>. Every request the shipped CLI sent was refused with an
    /// untyped framework 400.
    /// </remarks>
    private static bool TryParseExportFormat(string? requested, out SessionExportFormat format)
    {

        format = SessionExportFormat.Json;

        if (string.IsNullOrWhiteSpace(requested))
        {

            return false;

        }

        if (string.Equals(requested, "json", StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        if (string.Equals(requested, "markdown", StringComparison.OrdinalIgnoreCase))
        {

            format = SessionExportFormat.Markdown;

            return true;

        }

        return false;

    }

    private static IResult SessionExportResponse(Result<SessionExportResult> result, string traceId) =>
        result.IsSuccess
            ? Results.Ok(ApiResponse<SessionExportResult>.FromResult(result, traceId))
            : Results.Json(
                ApiResponse<SessionExportResult>.FromResult(result, traceId),
                ArcanumJsonContext.Default.ApiResponseSessionExportResult,
                statusCode: ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(result.Error.Code));

    private static IResult AttachmentFailure(

        HttpContext ctx,

        string errorCode,

        string message)

    {

        string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

        return Results.Json(

            ApiResponse<SessionAttachmentDto>.FromResult(

                Result<SessionAttachmentDto>.Failure(new Error(errorCode, message)),

                traceId),

            ArcanumJsonContext.Default.ApiResponseSessionAttachmentDto,

            statusCode: ArcanumErrorMapper.ResolveStatusCode(errorCode));

    }

    private static string GetSubmittedFileName(string? input)

    {

        if (string.IsNullOrWhiteSpace(input))

        {

            return string.Empty;

        }

        string normalized = input.Replace('\\', '/');

        int finalSeparator = normalized.LastIndexOf('/');

        return finalSeparator >= 0

            ? normalized[(finalSeparator + 1)..]

            : normalized;

    }

    private static string NormalizeMimeType(string? mimeType)

    {

        if (string.IsNullOrWhiteSpace(mimeType))

        {

            return "application/octet-stream";

        }

        int parameterSeparator = mimeType.IndexOf(';');

        return (parameterSeparator >= 0

                ? mimeType[..parameterSeparator]

                : mimeType)

            .Trim()

            .ToLowerInvariant();

    }

    internal static long ResolveAttachmentMultipartRequestLimit(long maximumReadBytes)

    {

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReadBytes);

        return checked(

            maximumReadBytes + AttachmentMultipartEnvelopeAllowanceBytes);

    }

    internal static FormOptions CreateAttachmentFormOptions(long maximumReadBytes)

    {

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReadBytes);

        return new FormOptions

        {

            MultipartBodyLengthLimit = maximumReadBytes,

        };

    }

    internal static long ResolveAttachmentMultipartTransportLimit(

        long aggregateRequestLimit)

    {

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(aggregateRequestLimit);

        return checked(aggregateRequestLimit + 1L);

    }

    internal static string ResolveAttachmentFormErrorCode(Exception exception)

    {

        ArgumentNullException.ThrowIfNull(exception);

        bool payloadTooLarge = exception is BadHttpRequestException

            {

                StatusCode: StatusCodes.Status413PayloadTooLarge,

            }

            || exception is InvalidDataException

            && exception.Message.Contains(

                "length limit",

                StringComparison.OrdinalIgnoreCase);

        return payloadTooLarge

            ? ErrorCodes.Attachment.TooLarge

            : ErrorCodes.Attachment.InvalidRequest;

    }

    private sealed class AttachmentMultipartAggregateReadStream : Stream

    {

        private readonly Stream _inner;

        private readonly long _maximumBytes;

        private long _bytesRead;

        public AttachmentMultipartAggregateReadStream(

            Stream inner,

            long maximumBytes)

        {

            ArgumentNullException.ThrowIfNull(inner);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

            _inner = inner;

            _maximumBytes = maximumBytes;

        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position

        {

            get => throw new NotSupportedException();

            set => throw new NotSupportedException();

        }

        public override void Flush()

        {

        }

        public override int Read(

            byte[] buffer,

            int offset,

            int count)

        {

            int read = _inner.Read(

                buffer,

                offset,

                ResolveReadCount(count));

            return RecordRead(read);

        }

        public override int Read(Span<byte> buffer)

        {

            int read = _inner.Read(

                buffer[..ResolveReadCount(buffer.Length)]);

            return RecordRead(read);

        }

        public override async Task<int> ReadAsync(

            byte[] buffer,

            int offset,

            int count,

            CancellationToken cancellationToken)

        {

            int read = await _inner

                .ReadAsync(

                    buffer,

                    offset,

                    ResolveReadCount(count),

                    cancellationToken)

                .ConfigureAwait(false);

            return RecordRead(read);

        }

        public override async ValueTask<int> ReadAsync(

            Memory<byte> buffer,

            CancellationToken cancellationToken = default)

        {

            int read = await _inner

                .ReadAsync(

                    buffer[..ResolveReadCount(buffer.Length)],

                    cancellationToken)

                .ConfigureAwait(false);

            return RecordRead(read);

        }

        public override long Seek(long offset, SeekOrigin origin) =>

            throw new NotSupportedException();

        public override void SetLength(long value) =>

            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>

            throw new NotSupportedException();

        protected override void Dispose(bool disposing)

        {

            base.Dispose(disposing);

        }

        private int ResolveReadCount(int requestedCount)

        {

            if (requestedCount <= 0)

            {

                return requestedCount;

            }

            long remainingWithSentinel = checked(

                _maximumBytes - _bytesRead + 1L);

            return checked((int)Math.Min(

                requestedCount,

                remainingWithSentinel));

        }

        private int RecordRead(int read)

        {

            _bytesRead = checked(_bytesRead + read);

            if (_bytesRead > _maximumBytes)

            {

                throw new InvalidDataException(

                    $"Multipart aggregate length limit {_maximumBytes} exceeded.");

            }

            return read;

        }

    }

    private static string ResolveSnapshotMimeType(

        string declaredMimeType,

        string detectedMimeType)

    {

        if (!string.Equals(

                detectedMimeType,

                "application/octet-stream",

                StringComparison.OrdinalIgnoreCase))

        {

            return detectedMimeType;

        }

        return declaredMimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)

            || declaredMimeType is "application/json" or "application/xml" or "application/yaml" or "application/toml"

                ? declaredMimeType

                : detectedMimeType;

    }

    private static IResult? ResolveAttachmentReferenceError(

        HttpContext ctx,

        AttachmentSourceResolution resolution,

        long maximumReadBytes)

    {

        if (resolution.Metadata.Kind == AttachmentSourceKind.WorkspaceFile

            && resolution.Metadata.Status == AttachmentSourceStatus.Refreshable

            && !string.IsNullOrWhiteSpace(resolution.DetectedMimeType))

        {

            return null;

        }

        return resolution.Metadata.Status switch

        {

            AttachmentSourceStatus.Missing or AttachmentSourceStatus.Moved => AttachmentFailure(

                ctx,

                ErrorCodes.Attachment.SourceNotFound,

                "The workspace attachment source was not found."),

            AttachmentSourceStatus.Inaccessible

                when resolution.Metadata.DiagnosticReason?.Contains(

                    "exceeds",

                    StringComparison.OrdinalIgnoreCase) is true => AttachmentFailure(

                        ctx,

                        ErrorCodes.Attachment.TooLarge,

                        $"The workspace attachment exceeds the {maximumReadBytes}-byte read limit."),

            AttachmentSourceStatus.Inaccessible

                or AttachmentSourceStatus.WorkspaceUnavailable => AttachmentFailure(

                    ctx,

                    ErrorCodes.Attachment.SourceUnavailable,

                    "The workspace attachment source could not be read."),

            _ => AttachmentFailure(

                ctx,

                ErrorCodes.Attachment.InvalidReference,

                "The workspace attachment reference is unsafe or no longer valid."),

        };

    }

    private static async Task<SessionAttachmentDto> ToAttachmentDtoAsync(

        SessionAttachmentRecord attachment,

        ISessionAttachmentRetrievalService attachmentRetrieval,

        CancellationToken cancellationToken)

    {

        IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus> statuses = await attachmentRetrieval

            .GetStatusesAsync([attachment.Id], cancellationToken)

            .ConfigureAwait(false);

        return SessionMapping.ToAttachmentDto(

            attachment,

            statuses.GetValueOrDefault(

                attachment.Id,

                SessionAttachmentIndexStatus.NotEligible));

    }

    private static SessionContextPinDto ToContextPinDto(SessionContextPinRecord pin) => new(
        pin.Id, pin.SessionId, pin.Kind, pin.TargetIdentifier, pin.DisplayLabel,
        pin.ContentVersion, pin.CreatedAt, pin.UpdatedAt);

    private static async Task PumpSessionLiveAsync(
        Guid sessionId,
        SessionEventHub eventHub,
        ChannelWriter<Entry> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (Entry entry in eventHub.SubscribeAsync(sessionId, cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task<Session?> GetSessionForUpdateAsync(ISessionRepository repo, Guid id, CancellationToken ct)
    {
        Session? session = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);

        return session?.CloneHeader();
    }

    private static async Task WriteEntrySseAsync(HttpContext httpContext, Entry entry, CancellationToken cancellationToken)
    {

        EntryDto dto = SessionMapping.ToEntryDto(entry);

        ArrayBufferWriter<byte> buffer = new(SseDataPrefix.Length + 512 + SseLineBreak.Length);

        buffer.Write(SseDataPrefix);

        Utf8JsonWriter jsonWriter = new(buffer);

        try
        {

            JsonSerializer.Serialize(jsonWriter, dto, ArcanumJsonContext.Default.EntryDto);

            jsonWriter.Flush();

            buffer.Write(SseLineBreak);

            await httpContext.Response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

            await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            jsonWriter.Dispose();

        }

    }

}
