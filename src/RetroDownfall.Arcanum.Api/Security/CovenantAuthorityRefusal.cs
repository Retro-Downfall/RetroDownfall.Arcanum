using System.Text.Json;

using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// The typed refusals the Covenant boundary emits before a handler ever runs.
/// </summary>
/// <remarks>
/// Written here rather than at each call site so every pre-handler refusal is shaped identically: the
/// same envelope, the same mapped status, and the same protected header tuple. A refusal that omitted
/// the tuple would be a cacheable "no" — and an intermediary replaying a stale 403 to a caller who now
/// has authority is a bug an operator cannot diagnose from their side (§10.18).
/// </remarks>
internal static class CovenantAuthorityRefusal
{

    internal static IResult Forbidden(HttpContext context) =>
        Refuse(
            context,
            new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This request carries no Covenant authority issued for this route's requirement."));

    internal static IResult Stale(HttpContext context) =>
        Refuse(
            context,
            new Error(
                ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                "The Covenant authority issued for this request is no longer current."));

    internal static IResult InvalidContextPolicy(HttpContext context, Error error) =>
        Refuse(context, error);

    private static IResult Refuse(HttpContext context, Error error)
    {

        ArgumentNullException.ThrowIfNull(context);

        CovenantRequestFeatures.MarkProtectedResponse(context);

        return new CovenantRefusalResult(error);

    }

    private sealed class CovenantRefusalResult(Error error) : IResult
    {

        public async Task ExecuteAsync(HttpContext httpContext)
        {

            ArgumentNullException.ThrowIfNull(httpContext);

            CovenantProtectedResponseHeaders.Apply(httpContext.Response);

            httpContext.Response.StatusCode = ArcanumErrorMapper.ResolveStatusCode(error.Code);

            httpContext.Response.ContentType = "application/json; charset=utf-8";

            await JsonSerializer
                .SerializeAsync(
                    httpContext.Response.Body,
                    ApiResponse<bool>.FromResult(
                        Result<bool>.Failure(error),
                        httpContext.TraceIdentifier),
                    ArcanumJsonContext.Default.ApiResponseBoolean,
                    httpContext.RequestAborted)
                .ConfigureAwait(false);

        }

    }

}
