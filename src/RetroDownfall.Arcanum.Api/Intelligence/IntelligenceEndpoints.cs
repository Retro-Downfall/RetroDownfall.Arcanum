using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

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
                Result<PromptResponseDto> invalid = Result<PromptResponseDto>.Failure(new Error("Validation.InvalidPrompt", "Prompt is required unless StatelessMessages is provided."));

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

                return Results.Json(
                    ApiResponse<PromptResponseDto>.FromResult(
                        Result<PromptResponseDto>.Failure(resolvedRequest.Error),
                        badTraceId),
                    ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            Result<PromptTurnResult> turn = await intelligence.ExecutePromptAsync(resolvedRequest.Value, cancellationToken).ConfigureAwait(false);

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result<PromptResponseDto> envelopeResult = turn.IsFailure
                ? Result<PromptResponseDto>.Failure(turn.Error)
                : Result<PromptResponseDto>.Success(new PromptResponseDto(
                    turn.Value.Text,
                    turn.Value.Usage,
                    turn.Value.ToolCalls,
                    turn.Value.FinishReason));

            ApiResponse<PromptResponseDto> response = ApiResponse<PromptResponseDto>.FromResult(envelopeResult, traceId);

            return turn.IsSuccess
                ? Results.Ok(response)
                : Results.Json(response, ArcanumJsonContext.Default.ApiResponsePromptResponseDto, statusCode: StatusCodes.Status500InternalServerError);
        })
        .WithName("PostIntelligencePing")
        .WithLargeRequestBody();

        apiGroup.MapPost(
            "/intelligence/human-response",
            async (HttpContext httpContext, IHumanPromptRegistry registry, CancellationToken cancellationToken) =>
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
                        new Error("Validation.InvalidBody", ApiRequestJson.DefaultInvalidBodyMessage));

                    return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));

                }

                if (string.IsNullOrWhiteSpace(body.PromptId)
                    || string.IsNullOrWhiteSpace(body.Answer))
                {
                    Result<bool> invalid = Result<bool>.Failure(
                        new Error("Validation.InvalidHumanResponse", "promptId and answer are required."));

                    return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
                }

                bool accepted = registry.TrySubmitResponse(body.PromptId.Trim(), body.Answer);

                if (!accepted)
                {
                    Result<bool> notFound = Result<bool>.Failure(
                        new Error(
                            "Intelligence.HumanPromptNotFound",
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
                        Result<string>.Failure(new Error("Validation.InvalidBody", ApiRequestJson.MalformedJsonMessage)),
                        badTraceId),
                    ArcanumJsonContext.Default.ApiResponseString,
                    cancellationToken: ct).ConfigureAwait(false);

                return;

            }

            if (body is null
                || (string.IsNullOrWhiteSpace(body.Prompt) && body.StatelessMessages is not { Count: > 0 }))
            {
                Result<string> invalid = Result<string>.Failure(new Error("Validation.InvalidPrompt", "Prompt is required unless StatelessMessages is provided."));

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

                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

                await httpContext.Response.WriteAsJsonAsync(
                    ApiResponse<string>.FromResult(Result<string>.Failure(resolvedRequest.Error), badTraceId),
                    ArcanumJsonContext.Default.ApiResponseString,
                    cancellationToken: ct).ConfigureAwait(false);

                return;
            }

            IArcanumIntelligenceProvider intelligence = httpContext.RequestServices.GetRequiredService<IArcanumIntelligenceProvider>();

            await InferenceExecuteWriter
                .WriteStreamAsync(httpContext, intelligence, resolvedRequest.Value, ct)
                .ConfigureAwait(false);

        })
        .WithName("PostIntelligencePingStream")
        .WithLargeRequestBody();

        apiGroup.MapPost("/intelligence/arsenal", async (OptionalWorkspaceRequest? body, IMcpConnectionManager mcp, IOptionsSnapshot<ArcanumSettings> settings, HttpContext httpContext, CancellationToken ct) =>
        {
            string workingDirectory = body?.WorkingDirectory ?? string.Empty;

            string? spellRoot = ToolHelpers.TryNormalizeWorkspace(workingDirectory, out string? root, out _)
                ? root
                : null;

            long maxSpellFileSizeBytes = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes(settings.Value);

            IReadOnlyList<Core.Intelligence.Spells.SpellSummary> spellSummaries = await SpellScanner
                .ScanSummariesAsync(spellRoot, ct, maxSpellFileSizeBytes)
                .ConfigureAwait(false);

            List<string> spellNames = spellSummaries.Select(static s => s.Name).ToList();

            List<string> nativeTools = [ArcanumLocalTimeTool.ToolName, ArcanumSystemInfoTool.ToolName];

            List<McpServerStatusDto> servers = await mcp.GetServerStatusesAsync(workingDirectory, ct).ConfigureAwait(false);

            WorkspaceArsenalDto dto = new(spellNames, nativeTools, servers, spellSummaries.ToList());

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result<WorkspaceArsenalDto> arsenalOk = Result<WorkspaceArsenalDto>.Success(dto);

            return Results.Ok(ApiResponse<WorkspaceArsenalDto>.FromResult(arsenalOk, traceId));
        })
        .WithName("PostIntelligenceArsenal");

        return apiGroup;
    }

}
