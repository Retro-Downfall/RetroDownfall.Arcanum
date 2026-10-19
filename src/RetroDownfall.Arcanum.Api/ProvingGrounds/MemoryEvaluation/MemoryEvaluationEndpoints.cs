using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;

internal static class MemoryEvaluationEndpoints
{
    internal static RouteGroupBuilder MapMemoryEvaluationEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapPost("/proving-grounds/memory-evaluations/run", RunAsync)
            .WithName("RunMemoryEvaluation").WithMetadata(MemoryEvaluationRouteMetadata.Instance).RequireCovenantReadAuthority();

        apiGroup.MapPost("/proving-grounds/memory-evaluations/get", GetAsync)
            .WithName("GetMemoryEvaluation").WithMetadata(MemoryEvaluationRouteMetadata.Instance).RequireCovenantReadAuthority();

        apiGroup.MapPost("/proving-grounds/memory-evaluations/delete", DeleteAsync)
            .WithName("DeleteMemoryEvaluation").WithMetadata(MemoryEvaluationRouteMetadata.Instance).RequireCovenantReadAuthority();

        return apiGroup;
    }

    private static async Task<IResult> RunAsync(HttpContext http, MemoryEvaluationRequestScopeFactory scopes, MemoryEvaluationRunner runner)
    {
        var read = await MemoryEvaluationRequestJson.ReadAsync(http, MemoryEvaluationRequestJson.RunTypeInfo);

        if (read.Error is not null) return read.Error;

        var opened = await scopes.OpenAsync(http, read.Body!.Scope, http.RequestAborted);

        if (opened.IsFailure) return Failure(opened.Error, ArcanumJsonContext.Default.ApiResponseMemoryEvaluationResponse);

        var result = await runner.RunAsync(read.Body, opened.Value, http.RequestAborted);

        return Respond(opened.Value, result);
    }

    private static async Task<IResult> GetAsync(HttpContext http, MemoryEvaluationRequestScopeFactory scopes, MemoryEvaluationRunner runner)
    {
        var read = await MemoryEvaluationRequestJson.ReadAsync(http, MemoryEvaluationRequestJson.GetTypeInfo);

        if (read.Error is not null) return read.Error;

        var (opened, absence) = await OpenOwnedAsync(http, read.Body!.EvaluationId, scopes, runner);

        if (opened.IsFailure) return Failure(opened.Error, ArcanumJsonContext.Default.ApiResponseMemoryEvaluationResponse);

        return Respond(opened.Value, absence is { } error ? Result<MemoryEvaluationResponse>.Failure(error)
            : await runner.GetAsync(read.Body, opened.Value, http.RequestAborted));
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, MemoryEvaluationRequestScopeFactory scopes, MemoryEvaluationRunner runner)
    {
        var read = await MemoryEvaluationRequestJson.ReadAsync(http, MemoryEvaluationRequestJson.DeleteTypeInfo);

        if (read.Error is not null) return read.Error;

        if (read.Body!.EvaluationId == Guid.Empty || !read.Body.Confirm || !read.Body.ExpectedReportDigest.IsValid)
            return Failure(new(ErrorCodes.MemoryEvaluation.InvalidRequest, "Confirmed exact report-only cleanup is required."), ArcanumJsonContext.Default.ApiResponseMemoryEvaluationDeleteResult);

        var (opened, absence) = await OpenOwnedAsync(http, read.Body.EvaluationId, scopes, runner);

        if (opened.IsFailure) return Failure(opened.Error, ArcanumJsonContext.Default.ApiResponseMemoryEvaluationDeleteResult);

        var result = absence is { Code: ErrorCodes.MemoryEvaluation.NotFound } error
            ? Result<MemoryEvaluationDeleteResult>.Failure(error)
            : await runner.DeleteAsync(read.Body, opened.Value, http.RequestAborted);

        return new MemoryEvaluationJsonResult<MemoryEvaluationDeleteResult>(opened.Value, result, ArcanumJsonContext.Default.ApiResponseMemoryEvaluationDeleteResult);
    }

    private static async Task<(Result<MemoryEvaluationRequestScope> Opened, Error? Absence)> OpenOwnedAsync(HttpContext http, Guid id,
        MemoryEvaluationRequestScopeFactory scopes, MemoryEvaluationRunner runner)
    {
        if (id == Guid.Empty) return (new Error(ErrorCodes.MemoryEvaluation.InvalidRequest, "An evaluation identity is required."), null);

        var owner = await runner.ReadOwnershipAsync(id, http.RequestAborted);

        if (owner.IsFailure && owner.Error.Code is not (ErrorCodes.MemoryEvaluation.NotFound or ErrorCodes.MemoryEvaluation.Gone)) return (owner.Error, null);

        // Retain a coherent absence result while obtaining genuine current Global authority.
        // A concurrent creation must not turn that earlier absence into a fabricated scope error.
        MemoryEvaluationScope scope = owner.IsSuccess ? new(owner.Value.Scope, owner.Value.CampaignId) : new(MemoryEvaluationScopeKind.Global);

        return (await scopes.OpenAsync(http, scope, http.RequestAborted), owner.IsFailure ? owner.Error : null);
    }

    private static IResult Respond(MemoryEvaluationRequestScope scope, Result<MemoryEvaluationResponse> result) =>
        new MemoryEvaluationJsonResult<MemoryEvaluationResponse>(scope, result, ArcanumJsonContext.Default.ApiResponseMemoryEvaluationResponse,
            result.IsSuccess && result.Value.Report is null ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);

    internal static IResult Failure<T>(Error error, JsonTypeInfo<ApiResponse<T>> info) =>
        new MemoryEvaluationJsonResult<T>(null, Result<T>.Failure(error), info);
}

/// <summary>Revalidates the entire original request scope immediately before bytes; response completion owns its Turn.</summary>
internal sealed class MemoryEvaluationJsonResult<T>(MemoryEvaluationRequestScope? scope, Result<T> payload,
    JsonTypeInfo<ApiResponse<T>> info, int successStatus = StatusCodes.Status200OK) : IResult
{
    private int _executed;

    public async Task ExecuteAsync(HttpContext http)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0) throw new InvalidOperationException("An evaluation result is single use.");

        CovenantProtectedResponseHeaders.Apply(http.Response);

        Result valid = scope is null ? Result.Success() : await scope.RevalidateAsync(http.RequestAborted);

        Result<T> effective = valid.IsFailure ? Result<T>.Failure(valid.Error) : payload;

        if (effective.IsFailure)
            effective = Result<T>.Failure(new Error(effective.Error.Code, effective.Error.Code == ErrorCodes.MemoryEvaluation.QuotaExceeded
                ? "The evaluation quota requires confirmed report-only cleanup through memory-evaluations/delete."
                : "The memory evaluation request could not be completed."));

        http.Response.StatusCode = effective.IsSuccess ? successStatus : ArcanumErrorMapper.ResolveStatusCode(effective.Error.Code);

        http.Response.ContentType = "application/json; charset=utf-8";

        await JsonSerializer.SerializeAsync(http.Response.Body, ApiResponse<T>.FromResult(effective, http.TraceIdentifier), info, http.RequestAborted);
    }
}

/// <summary>Marks this capability's refusals as protected even when master authentication fails.</summary>
internal sealed class MemoryEvaluationRouteMetadata
{
    internal static readonly MemoryEvaluationRouteMetadata Instance = new();

    private MemoryEvaluationRouteMetadata() { }
}
