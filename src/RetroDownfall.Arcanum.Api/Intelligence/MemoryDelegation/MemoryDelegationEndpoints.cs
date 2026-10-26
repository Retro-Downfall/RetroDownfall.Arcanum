using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;

internal static class MemoryDelegationEndpoints
{
    internal static void MapMemoryDelegationEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/intelligence/memory-delegation", (Delegate)RunAsync)
            .WithName("PostIntelligenceMemoryDelegation")
            .WithMetadata(MemoryDelegationRouteMetadata.Instance)
            .AllowCovenantContext()
            .RequireCovenantReadAuthority()
            .WithLargeRequestBody();
    }

    private static async Task<IResult> RunAsync(HttpContext http)
    {
        var read = await MemoryDelegationRequestJson.ReadAsync(http).ConfigureAwait(false);

        if (read.Error is not null) return read.Error;

        var opened = await MemoryDelegationRequestScope.OpenAsync(http, read.Body!, http.RequestAborted).ConfigureAwait(false);

        if (opened.IsFailure) return Failure(opened.Error, opened.Diagnostic);

        var scope = opened.Value;

        try
        {
            using var binding = MemoryDelegationRootBinding.Enter(scope.Binding);

            ArcanumInvocationContext invocationContext = scope.Invocation;

            var turn = await http.RequestServices.GetRequiredService<IArcanumIntelligenceProvider>()
                .ExecutePromptAsync(scope.Request, invocationContext, http.RequestAborted,
                    new InferenceAuditContext { RequestType = "memory-delegation", ClientIp = http.Connection.RemoteIpAddress?.ToString() }).ConfigureAwait(false);

            Result<PromptResponseDto> payload = turn.IsFailure ? turn.Error : PromptResponseDto.From(turn.Value);

            return new MemoryDelegationJsonResult(scope, payload);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    internal static IResult Failure(Error error) => Failure(error, null);

    internal static IResult Failure(Error error, MemoryDelegationCallerDiagnostic? diagnostic) =>
        new MemoryDelegationJsonResult(null, Result<PromptResponseDto>.Failure(error), diagnostic);
}

internal sealed class MemoryDelegationJsonResult(MemoryDelegationRequestScope? scope, Result<PromptResponseDto> payload, MemoryDelegationCallerDiagnostic? callerDiagnostic) : IResult
{
    internal MemoryDelegationJsonResult(MemoryDelegationRequestScope? scope, Result<PromptResponseDto> payload)
        : this(scope, payload, null) { }

    private int _executed;

    public async Task ExecuteAsync(HttpContext http)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0) throw new InvalidOperationException("A memory delegation result is single use.");

        MemoryDelegationEffect? effect = null;

        try
        {
            CovenantProtectedResponseHeaders.Apply(http.Response);

            Result valid = scope is null
                ? callerDiagnostic?.Origin.Revalidate(http) ?? Result.Success()
                : await scope.RevalidateAsync(http.RequestAborted).ConfigureAwait(false);

            if (valid.IsSuccess && scope?.Root.IsConsumed == true)
            {
                var admitted = await scope.Coordinator.AdmitEffectAsync(scope.Root, http.RequestAborted).ConfigureAwait(false);

                if (admitted.IsFailure) valid = admitted.Error;
                else effect = admitted.Value;
            }

            // An awaited source validation cannot replace the original authenticated feature.
            if (valid.IsSuccess && scope is not null) valid = await scope.RevalidateAsync(http.RequestAborted).ConfigureAwait(false);

            Result<PromptResponseDto> effective = valid.IsFailure ? valid.Error : payload;

            string? callerMessage = effective.IsFailure && valid.IsSuccess
                ? callerDiagnostic?.PublicMessage(effective.Error.Code) : null;

            if (effective.IsFailure)
            {
                effective = new Error(effective.Error.Code, callerMessage ?? MemoryDelegationCallerDiagnostic.ProtectedMessage);
            }

            http.Response.StatusCode = effective.IsSuccess ? StatusCodes.Status200OK
                : callerMessage is not null && callerDiagnostic!.IsLocalRequestBoundFailure ? StatusCodes.Status400BadRequest
                : ArcanumErrorMapper.ResolveStatusCode(effective.Error.Code);

            http.Response.ContentType = "application/json; charset=utf-8";

            using CancellationTokenSource cancellation = effect is null
                ? CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted)
                : CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, effect.Revocation);

            // Failed admission emits only a constant refusal. Admitted plaintext remains fenced until every body write drains.
            await JsonSerializer.SerializeAsync(http.Response.Body, ApiResponse<PromptResponseDto>.FromResult(effective, http.TraceIdentifier),
                ArcanumJsonContext.Default.ApiResponsePromptResponseDto, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            if (effect is not null) await effect.DisposeAsync().ConfigureAwait(false);

            if (scope is not null) await scope.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class MemoryDelegationRouteMetadata
{
    internal static readonly MemoryDelegationRouteMetadata Instance = new();

    private MemoryDelegationRouteMetadata() { }
}
