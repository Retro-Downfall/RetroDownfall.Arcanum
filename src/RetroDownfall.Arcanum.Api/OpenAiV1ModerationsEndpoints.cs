using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

namespace RetroDownfall.Arcanum.Api;

/// <summary>
/// OpenAI-compatible <c>POST /v1/moderations</c>. Arcanum does not implement moderation; the route
/// remains for client compatibility and always returns <c>501 not_supported</c>. There is no
/// <c>Arcanum:Moderations</c> setting — that block is rejected as obsolete at startup.
/// </summary>
internal static partial class OpenAiV1Endpoints
{

    internal static void MapOpenAiV1Moderations(this RouteGroupBuilder v1)
    {
        _ = v1.MapPost("/moderations", HandleModerationsAsync)
            .WithName("PostOpenAiModerations")
            .WithLargeRequestBody();
    }

    private static IResult HandleModerationsAsync(OpenAiModerationRequest? body)
    {
        _ = body;

        return JsonError(
            "Moderation is not supported by this Arcanum server.",
            "invalid_request_error",
            "not_supported",
            param: null,
            StatusCodes.Status501NotImplemented);
    }

}
