using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal static class IntelligenceEndpoints
{

    public static RouteGroupBuilder MapIntelligenceEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapPost("/intelligence/ping", async (PingRequest? body, IArcanumIntelligenceProvider intelligence, ICampaignRepository campaignRepository, IOptionsSnapshot<ArcanumSettings> settings, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            if (body is null
                || (string.IsNullOrWhiteSpace(body.Prompt) && body.StatelessMessages is not { Count: > 0 }))
            {
                Result<PromptResponseDto> invalid = Result<PromptResponseDto>.Failure(new Error(ErrorCodes.Validation.InvalidPrompt, "Prompt is required unless StatelessMessages is provided."));

                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                return Results.Json(
                    ApiResponse<PromptResponseDto>.FromResult(invalid, badTraceId),
                    ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            Result pingBounds = PingRequestBoundsValidator.Validate(body, settings.Value);

            if (pingBounds.IsFailure)
            {
                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                return Results.Json(
                    ApiResponse<PromptResponseDto>.FromResult(
                        Result<PromptResponseDto>.Failure(pingBounds.Error),
                        badTraceId),
                    ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            Result<PingRequest> resolvedRequest = await PingRequestResolver
                .ResolveCampaignAsync(body, campaignRepository, cancellationToken)
                .ConfigureAwait(false);

            if (resolvedRequest.IsFailure)
            {
                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                // W3.5: map the error code to a status (Campaign.NotFound -> 404) instead of a flat
                // 400, matching /prompts/{id}/execute.
                return Results.Json(
                    ApiResponse<PromptResponseDto>.FromResult(
                        Result<PromptResponseDto>.Failure(resolvedRequest.Error),
                        badTraceId),
                    ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                    statusCode: ArcanumErrorMapper.ResolveStatusCode(resolvedRequest.Error.Code));
            }

            InferenceAuditContext pingAuditContext = new()
            {
                RequestType = "ping",
                ClientIp = httpContext.Connection.RemoteIpAddress?.ToString(),
            };

            Result<PromptTurnResult> turn = await intelligence.ExecutePromptAsync(resolvedRequest.Value, cancellationToken, pingAuditContext).ConfigureAwait(false);

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result<PromptResponseDto> envelopeResult = turn.IsFailure
                ? Result<PromptResponseDto>.Failure(turn.Error)
                : Result<PromptResponseDto>.Success(PromptResponseDto.From(turn.Value));

            ApiResponse<PromptResponseDto> response = ApiResponse<PromptResponseDto>.FromResult(envelopeResult, traceId);

            // W3.5: route inference failures through the shared mapper (404/403/400/503/500) rather
            // than a hardcoded 500, matching /prompts/{id}/execute.
            return turn.IsSuccess
                ? Results.Ok(response)
                : Results.Json(response, ArcanumJsonContext.Default.ApiResponsePromptResponseDto, statusCode: ArcanumErrorMapper.ResolveStatusCode(turn.Error.Code));
        })
        .WithName("PostIntelligencePing")
        .WithLargeRequestBody()
        .AddEndpointFilter(IdempotencyEndpointFilters.ForBoundArgument(0, ArcanumJsonContext.Default.PingRequest));

        apiGroup.MapPost(
            "/intelligence/human-response",
            async (
                HttpContext httpContext,
                IHumanPromptRegistry registry,
                IOptionsSnapshot<ArcanumSettings> settings,
                CancellationToken cancellationToken) =>
            {
                SubmitHumanResponseRequest? body;

                IResult? jsonError;

                (body, jsonError) = await ApiRequestJson.ReadAsync(
                    httpContext,
                    ArcanumJsonContext.Default.SubmitHumanResponseRequest,
                    static ctx => ApiRequestJson.InvalidBodyResult(ctx, ApiRequestJson.MalformedJsonMessage),
                    cancellationToken).ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (body is null)
                {

                    Result<bool> invalid = Result<bool>.Failure(
                        new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.DefaultInvalidBodyMessage));

                    return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));

                }

                if (string.IsNullOrWhiteSpace(body.PromptId)
                    || string.IsNullOrWhiteSpace(body.Answer))
                {
                    Result<bool> invalid = Result<bool>.Failure(
                        new Error("Validation.InvalidHumanResponse", "promptId and answer are required."));

                    return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
                }

                int maxAnswerBytes = ArcanumSettingClamps.MaxEntryContentBytes(
                    settings.Value.ResolveSessions().MaxEntryContentBytes);

                if (System.Text.Encoding.UTF8.GetByteCount(body.Answer) > maxAnswerBytes)
                {
                    Result<bool> tooLarge = Result<bool>.Failure(
                        new Error(
                            ErrorCodes.Validation.InvalidBody,
                            $"answer exceeds maximum size of {maxAnswerBytes} UTF-8 bytes."));

                    return Results.BadRequest(ApiResponse<bool>.FromResult(tooLarge, traceId));
                }

                bool accepted = registry.TrySubmitResponse(body.PromptId.Trim(), body.Answer);

                if (!accepted)
                {
                    Result<bool> notFound = Result<bool>.Failure(
                        new Error(
                            ErrorCodes.Intelligence.HumanPromptNotFound,
                            "No active ask_human prompt matches that promptId (unknown, expired, or already answered)."));

                    return Results.NotFound(ApiResponse<bool>.FromResult(notFound, traceId));
                }

                Result<bool> ok = Result<bool>.Success(true);

                return Results.Ok(ApiResponse<bool>.FromResult(ok, traceId));
            })
        .WithName("PostIntelligenceHumanResponse")
        .WithLargeRequestBody();

        apiGroup.MapPost("/intelligence/ping-stream", async (HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            using CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(
                httpContext.RequestAborted,
                cancellationToken);

            CancellationToken ct = streamCts.Token;

            PingRequest? body;

            try
            {

                body = await httpContext.Request
                    .ReadFromJsonAsync(ArcanumJsonContext.Default.PingRequest, ct)
                    .ConfigureAwait(false);

            }
            catch (JsonException)
            {

                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

                await httpContext.Response.WriteAsJsonAsync(
                    ApiResponse<string>.FromResult(
                        Result<string>.Failure(new Error(ErrorCodes.Validation.InvalidBody, ApiRequestJson.MalformedJsonMessage)),
                        badTraceId),
                    ArcanumJsonContext.Default.ApiResponseString,
                    cancellationToken: ct).ConfigureAwait(false);

                return;

            }

            if (body is null
                || (string.IsNullOrWhiteSpace(body.Prompt) && body.StatelessMessages is not { Count: > 0 }))
            {
                Result<string> invalid = Result<string>.Failure(new Error(ErrorCodes.Validation.InvalidPrompt, "Prompt is required unless StatelessMessages is provided."));

                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

                await httpContext.Response.WriteAsJsonAsync(ApiResponse<string>.FromResult(invalid, badTraceId), ArcanumJsonContext.Default.ApiResponseString, cancellationToken: ct).ConfigureAwait(false);

                return;
            }

            ArcanumSettings arcSettings = httpContext.RequestServices.GetRequiredService<IOptionsSnapshot<ArcanumSettings>>().Value;

            Result streamPingBounds = PingRequestBoundsValidator.Validate(body, arcSettings);

            if (streamPingBounds.IsFailure)
            {
                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

                await httpContext.Response.WriteAsJsonAsync(
                    ApiResponse<string>.FromResult(Result<string>.Failure(streamPingBounds.Error), badTraceId),
                    ArcanumJsonContext.Default.ApiResponseString,
                    cancellationToken: ct).ConfigureAwait(false);

                return;
            }

            ICampaignRepository campaignRepository = httpContext.RequestServices.GetRequiredService<ICampaignRepository>();

            Result<PingRequest> resolvedRequest = await PingRequestResolver
                .ResolveCampaignAsync(body, campaignRepository, ct)
                .ConfigureAwait(false);

            if (resolvedRequest.IsFailure)
            {
                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                // W6.3: map the resolve failure through the shared mapper (Campaign.NotFound -> 404)
                // so ping-stream matches the buffered /intelligence/ping status semantics instead of
                // a flat 400.
                httpContext.Response.StatusCode = ArcanumErrorMapper.ResolveStatusCode(resolvedRequest.Error.Code);

                await httpContext.Response.WriteAsJsonAsync(
                    ApiResponse<string>.FromResult(Result<string>.Failure(resolvedRequest.Error), badTraceId),
                    ArcanumJsonContext.Default.ApiResponseString,
                    cancellationToken: ct).ConfigureAwait(false);

                return;
            }

            IArcanumIntelligenceProvider intelligence = httpContext.RequestServices.GetRequiredService<IArcanumIntelligenceProvider>();

            InferenceAuditContext pingStreamAuditContext = new()
            {
                RequestType = "ping-stream",
                ClientIp = httpContext.Connection.RemoteIpAddress?.ToString(),
            };

            await InferenceExecuteWriter
                .WriteStreamAsync(httpContext, intelligence, resolvedRequest.Value, ct, pingStreamAuditContext)
                .ConfigureAwait(false);

        })
        .WithName("PostIntelligencePingStream")
        .WithLargeRequestBody()
        .AddEndpointFilter(IdempotencyEndpointFilters.ForRawBody);

        apiGroup.MapPost("/intelligence/arsenal", async (OptionalWorkspaceRequest? body, IMcpConnectionManager mcp, IBuiltInToolRegistry builtInTools, IOptionsSnapshot<ArcanumSettings> settings, IWorkspaceCheckCapabilityReporter workspaceCheckCapability, HttpContext httpContext, CancellationToken ct) =>
        {
            string workingDirectory = body?.WorkingDirectory ?? string.Empty;

            string? spellRoot = WorkspacePathPolicy.TryNormalizeWorkspace(workingDirectory, out string? root, out _)
                ? root
                : null;

            long maxSpellFileSizeBytes = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes(settings.Value);

            int metadataScanCacheTtlSeconds = ArcanumSettingClamps.MetadataScanCacheTtlSeconds(
                ArcanumRuntimeDefaults.Spells.MetadataScanCacheTtlSeconds);

            IReadOnlyList<Core.Intelligence.Spells.SpellSummary> spellSummaries = await SpellScanner
                .ScanSummariesAsync(spellRoot, ct, maxSpellFileSizeBytes, metadataScanCacheTtlSeconds)
                .ConfigureAwait(false);

            List<string> spellNames = spellSummaries.Select(static s => s.Name).ToList();

            List<string> nativeTools = builtInTools.GetToolNames().ToList();

            List<McpServerStatusDto> servers = await mcp.GetServerStatusesAsync(workingDirectory, ct).ConfigureAwait(false);

            WorkspaceCheckCapabilityStatus checkStatus =
                await workspaceCheckCapability
                    .GetStatusAsync(
                        spellRoot,
                        ct)
                    .ConfigureAwait(false);
            WorkspaceArsenalDto dto = new(
                spellNames,
                nativeTools,
                servers,
                spellSummaries.ToList(),
                new WorkspaceCheckCapabilityDto(
                    checkStatus.IsAvailable,
                    checkStatus.Reason));

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result<WorkspaceArsenalDto> arsenalOk = Result<WorkspaceArsenalDto>.Success(dto);

            return Results.Ok(ApiResponse<WorkspaceArsenalDto>.FromResult(arsenalOk, traceId));
        })
        .WithName("PostIntelligenceArsenal");

        apiGroup.MapPost("/intelligence/mana", HandleCountManaAsync)
            .WithName("PostIntelligenceMana");

        apiGroup.MapPost(

            "/intelligence/context/inspect",

            async (

                ContextPreviewRequest? body,

                IContextPreviewService previewService,

                IOptionsSnapshot<ArcanumSettings> settings,

                ICampaignRepository campaignRepository,

                HttpContext httpContext,

                CancellationToken cancellationToken) =>

            {

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                if (body is null)

                {

                    Result<ContextPreviewResult> invalid = Result<ContextPreviewResult>.Failure(

                        new Error(ErrorCodes.Validation.InvalidBody, "A context preview request is required."));

                    return Results.Json(

                        ApiResponse<ContextPreviewResult>.FromResult(invalid, traceId),

                        ArcanumJsonContext.Default.ApiResponseContextPreviewResult,

                        statusCode: StatusCodes.Status400BadRequest);

                }

                PingRequest previewTurn = new(

                    body.Prompt ?? string.Empty,

                    body.Model,

                    body.WorkingDirectory ?? string.Empty,

                    SessionId: body.SessionId,

                    AttachedFiles: body.AttachedFiles,

                    OverrideSpellName: body.OverrideSpellName,

                    Temperature: body.Temperature,

                    TopP: body.TopP,

                    MaxOutputTokens: body.MaxOutputTokens,

                    Stop: body.Stop,

                    Seed: body.Seed,

                    ResponseFormat: body.ResponseFormat,

                    PresencePenalty: body.PresencePenalty,

                    FrequencyPenalty: body.FrequencyPenalty,

                    CampaignId: body.CampaignId,

                    AdditionalSystemPrompt: body.AdditionalSystemPrompt,

                    ScryingFoci: body.ScryingFoci,

                    DisableAllTools: body.DisableAllTools,

                    UnattendedMode: body.UnattendedMode);

                Result previewBounds = PingRequestBoundsValidator.Validate(

                    previewTurn,

                    settings.Value);

                if (previewBounds.IsFailure)

                {

                    Result<ContextPreviewResult> invalid = Result<ContextPreviewResult>.Failure(

                        previewBounds.Error);

                    return Results.Json(

                        ApiResponse<ContextPreviewResult>.FromResult(invalid, traceId),

                        ArcanumJsonContext.Default.ApiResponseContextPreviewResult,

                        statusCode: StatusCodes.Status400BadRequest);

                }

                Result<PingRequest> resolvedTurn = await PingRequestResolver

                    .ResolveCampaignAsync(previewTurn, campaignRepository, cancellationToken)

                    .ConfigureAwait(false);

                if (resolvedTurn.IsFailure)

                {

                    Result<ContextPreviewResult> unresolved = Result<ContextPreviewResult>.Failure(

                        resolvedTurn.Error);

                    return Results.Json(

                        ApiResponse<ContextPreviewResult>.FromResult(unresolved, traceId),

                        ArcanumJsonContext.Default.ApiResponseContextPreviewResult,

                        statusCode: ArcanumErrorMapper.ResolveStatusCode(resolvedTurn.Error.Code));

                }

                ContextPreviewRequest effectiveRequest = body with

                {

                    WorkingDirectory = resolvedTurn.Value.WorkingDirectory,

                };

                Result<ContextPreviewResult> preview = await previewService

                    .PreviewContextAsync(effectiveRequest, cancellationToken)

                    .ConfigureAwait(false);

                ApiResponse<ContextPreviewResult> response =

                    ApiResponse<ContextPreviewResult>.FromResult(preview, traceId);

                return preview.IsSuccess

                    ? Results.Ok(response)

                    : Results.Json(

                        response,

                        ArcanumJsonContext.Default.ApiResponseContextPreviewResult,

                        statusCode: ArcanumErrorMapper.ResolveStatusCode(preview.Error.Code));

            })

            .WithName("PostIntelligenceContextInspect");

        return apiGroup;
    }

    /// <summary>
    /// Read-only diagnostic Mana (token) counter. Reuses the model-aware
    /// <see cref="IModelTokenEstimator"/> used at the provider-call boundary. Non-billable
    /// (ADR 0002): performs no Grimoire writes and no inference.
    /// </summary>
    private static async Task<IResult> HandleCountManaAsync(
        ManaCountRequest? body,
        IModelTokenEstimator modelTokenEstimator,
        IMcpConnectionManager mcp,
        IOptionsSnapshot<ArcanumSettings> settings,
        IWebResearchProviderCatalog webResearchProviders,
        ILogger<ArcanumWebSearchTool> webSearchLogger,
        ILogger<ArcanumReadUrlTool> readUrlLogger,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        bool hasMessages = body?.Messages is { Count: > 0 };

        bool hasPrompt = !string.IsNullOrEmpty(body?.Prompt);

        if (body is null || (!hasMessages && !hasPrompt))
        {

            Result<ManaCountResult> invalid = Result<ManaCountResult>.Failure(
                new Error(ErrorCodes.Validation.InvalidBody, "Either 'messages' or 'prompt' is required."));

            return Results.Json(
                ApiResponse<ManaCountResult>.FromResult(invalid, traceId),
                ArcanumJsonContext.Default.ApiResponseManaCountResult,
                statusCode: StatusCodes.Status400BadRequest);

        }

        ArcanumSettings current = settings.Value;
        string requestedModel = string.IsNullOrWhiteSpace(body!.Model)
            ? current.DefaultModel ?? string.Empty
            : body.Model.Trim();
        ProviderSettings provider;
        string canonicalModel;

        if (ProviderResolver.TryResolveProviderForModel(
                current,
                requestedModel,
                out ProviderSettings? configuredProvider,
                out string configuredModel)
            && configuredProvider is not null)
        {
            provider = configuredProvider;
            canonicalModel = configuredModel;
        }
        else
        {
            canonicalModel = string.IsNullOrWhiteSpace(requestedModel) ? "unknown" : requestedModel;
            provider = new ProviderSettings
            {
                Name = "unconfigured",
                Type = AiProviderKind.OpenAICompatible,
                Models = [new ModelEntry(canonicalModel)],
                ContextWindowLimit = (current.Providers ?? []).FirstOrDefault()?.ContextWindowLimit ?? 8192,
            };
        }

        List<MeAiChatMessage> chatMessages = hasMessages
            ? InferenceContextBuilder.MapToAiChatMessages(body.Messages!)
            : [new MeAiChatMessage(ChatRole.User, body.Prompt!)];
        ChatOptions chatOptions = new();

        if (body.Tools)
        {
            bool webBrowsingEnabled = settings.Value.ResolveWebBrowsing().Enabled;
            ArcanumWebSearchTool? webSearchTool = webBrowsingEnabled
                ? new ArcanumWebSearchTool(
                    webResearchProviders,
                    settings,
                    webSearchLogger)
                : null;
            ArcanumReadUrlTool? readUrlTool = webBrowsingEnabled
                ? new ArcanumReadUrlTool(
                    webResearchProviders,
                    settings,
                    readUrlLogger)
                : null;
            chatOptions.Tools = await ManaToolCatalog
                .CollectAsync(
                    mcp,
                    workingDirectory: null,
                    webSearchTool,
                    readUrlTool,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        ContextTokenBreakdown breakdown = modelTokenEstimator.EstimateContext(
            new ModelTokenizationRequest(
                provider,
                canonicalModel,
                chatMessages,
                chatOptions,
                ReservedAnswerTokens: 0,
                ReservedReasoningTokens: 0));
        int? toolManaEstimate = body.Tools
            ? breakdown.Source(ContextTokenSource.Tools).TokenCount
            : null;
        List<int>? perMessage = hasMessages
            ? [.. breakdown.MessageTokenCounts]
            : null;

        ManaCountResult result = new(
            breakdown.InputTokens,
            breakdown.Profile.TokenizerId,
            perMessage,
            toolManaEstimate,
            breakdown.OverallClassification,
            breakdown.Profile.ProfileId,
            breakdown.SafetyMarginTokens,
            breakdown);

        Result<ManaCountResult> ok = Result<ManaCountResult>.Success(result);

        return Results.Ok(ApiResponse<ManaCountResult>.FromResult(ok, traceId));

    }

}
