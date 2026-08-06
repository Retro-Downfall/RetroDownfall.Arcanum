using System.Diagnostics;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Api.A2A;

/// <summary>
/// Operator-facing Conclave surface: A2A status and outbound Sending dispatch.
/// </summary>
/// <remarks>
/// Issue #12 requires the Mage to discover capability, dispatch work, observe progress, cancel, and receive
/// a terminal result <em>without raw HTTP calls</em>. Before this, <c>dispatch_sending</c> was reachable
/// only from inside an Apprentice's tool loop, so an operator had no supported way to drive A2A at all.
/// These routes are the API half; <c>arcanum conclave</c> is the CLI half.
/// </remarks>
internal static class ConclaveEndpoints
{

    public static RouteGroupBuilder MapConclaveEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapGet("/conclave/status", (
            IOptionsSnapshot<ArcanumSettings> settings,
            HttpContext httpContext) =>
        {

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            ConclaveA2AStatus status = ConclaveA2AStatus.Resolve(settings.Value);

            ConclaveStatusDto dto = new(
                State: status.StateName,
                ConclaveEnabled: status.ConclaveEnabled,
                A2AServerEnabled: status.ServerEnabled,
                A2AClientEnabled: status.ClientEnabled,
                ServerPath: status.ServerPath,
                AgentCardPath: status.AgentCardPath,
                AllowedRemoteAgentCount: status.AllowedRemoteAgentCount,
                Detail: status.Detail);

            return Results.Json(
                ApiResponse<ConclaveStatusDto>.FromResult(Result<ConclaveStatusDto>.Success(dto), traceId),
                ArcanumJsonContext.Default.ApiResponseConclaveStatusDto);

        })
        .WithName("GetConclaveStatus");

        apiGroup.MapPost("/conclave/sendings", async (
            IA2AClientService clientService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            (DispatchSendingRequest? body, IResult? bodyError) = await ApiRequestJson
                .ReadAsync(
                    httpContext,
                    ArcanumJsonContext.Default.DispatchSendingRequest,
                    static ctx => ApiRequestJson.InvalidBodyResult(
                        ctx,
                        ApiRequestJson.MalformedJsonMessage,
                        ArcanumJsonContext.Default.ApiResponseSendingDispatchDto),
                    cancellationToken)
                .ConfigureAwait(false);

            if (bodyError is not null)
            {

                return bodyError;

            }

            if (body is null || string.IsNullOrWhiteSpace(body.AgentUrl) || string.IsNullOrWhiteSpace(body.Goal))
            {

                return ApiRequestJson.InvalidBodyResult(
                    httpContext,
                    "'agentUrl' and 'goal' are required.",
                    ArcanumJsonContext.Default.ApiResponseSendingDispatchDto);

            }

            Result<A2ADispatchResult> dispatch = await clientService
                .DispatchSendingAsync(
                    body.Goal,
                    body.Name,
                    body.AgentUrl,
                    cancellationToken: cancellationToken,
                    mode: ResolveMode(body.Continuable))
                .ConfigureAwait(false);

            Result<SendingDispatchDto> result = ToDto(body.AgentUrl.Trim(), dispatch);

            return Results.Json(
                ApiResponse<SendingDispatchDto>.FromResult(result, traceId),
                ArcanumJsonContext.Default.ApiResponseSendingDispatchDto,
                statusCode: result.IsSuccess
                    ? StatusCodes.Status200OK
                    : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));

        })
        .WithName("DispatchSending");

        // Answers a Sending the remote parked at input-required/auth-required, on the same remote task,
        // instead of cancelling it and re-running the whole goal to add one sentence of detail (issue #64).
        apiGroup.MapPost("/conclave/sendings/{taskId}/continue", async (
            string taskId,
            IA2AClientService clientService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            (ContinueSendingRequest? body, IResult? bodyError) = await ApiRequestJson
                .ReadAsync(
                    httpContext,
                    ArcanumJsonContext.Default.ContinueSendingRequest,
                    static ctx => ApiRequestJson.InvalidBodyResult(
                        ctx,
                        ApiRequestJson.MalformedJsonMessage,
                        ArcanumJsonContext.Default.ApiResponseSendingDispatchDto),
                    cancellationToken)
                .ConfigureAwait(false);

            if (bodyError is not null)
            {

                return bodyError;

            }

            if (body is null
                || string.IsNullOrWhiteSpace(body.AgentUrl)
                || string.IsNullOrWhiteSpace(body.Message)
                || string.IsNullOrWhiteSpace(taskId))
            {

                return ApiRequestJson.InvalidBodyResult(
                    httpContext,
                    "'agentUrl' and 'message' are required, along with a non-empty task id.",
                    ArcanumJsonContext.Default.ApiResponseSendingDispatchDto);

            }

            Result<A2ADispatchResult> dispatch = await clientService
                .ContinueSendingAsync(
                    body.AgentUrl,
                    taskId,
                    body.Message,
                    cancellationToken: cancellationToken,
                    mode: ResolveMode(body.Continuable))
                .ConfigureAwait(false);

            Result<SendingDispatchDto> result = ToDto(body.AgentUrl.Trim(), dispatch);

            return Results.Json(
                ApiResponse<SendingDispatchDto>.FromResult(result, traceId),
                ArcanumJsonContext.Default.ApiResponseSendingDispatchDto,
                statusCode: result.IsSuccess
                    ? StatusCodes.Status200OK
                    : ArcanumErrorMapper.ResolveStatusCode(result.Error.Code));

        })
        .WithName("ContinueSending");

        return apiGroup;

    }

    private static A2ADispatchMode ResolveMode(bool? continuable) =>
        continuable == true ? A2ADispatchMode.Continuable : A2ADispatchMode.Blocking;

    private static Result<SendingDispatchDto> ToDto(string agentUrl, Result<A2ADispatchResult> dispatch) =>
        dispatch.IsSuccess
            ? Result<SendingDispatchDto>.Success(new SendingDispatchDto(
                agentUrl,
                dispatch.Value.TaskId,
                dispatch.Value.ResponseText,
                dispatch.Value.RemoteCost.IsKnown,
                dispatch.Value.RemoteCost.TotalTokens,
                dispatch.Value.RemoteCost.CostUsd,
                Stamp(dispatch.Value.DispatchedAt),
                Stamp(dispatch.Value.SettledAt),
                dispatch.Value.Continuation?.Need switch
                {
                    A2AContinuationNeed.Input => "input",
                    A2AContinuationNeed.Authentication => "auth",
                    _ => null,
                }))
            : Result<SendingDispatchDto>.Failure(dispatch.Error);

    private static DateTimeOffset? Stamp(DateTimeOffset value) => value == default ? null : value;

}
