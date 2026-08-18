using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace RetroDownfall.Arcanum.Api;

/// <summary>
/// OpenAI-compatible <c>POST /v1/moderations</c>. Arcanum does not implement moderation; the route
/// remains for client compatibility and always returns <c>501 not_supported</c>. No configuration
/// setting enables it.
/// </summary>
internal static partial class OpenAiV1Endpoints
{

    internal static void MapOpenAiV1Moderations(this RouteGroupBuilder v1)
    {
        _ = v1.MapPost("/moderations", HandleModerationsAsync)
            .WithName("PostOpenAiModerations")
            .WithLargeRequestBody();
    }

    /// <summary>
    /// Parameterless on purpose, matching <c>HandleNotSupportedAsync</c> in
    /// <c>OpenAiV1UnsupportedStubs</c>. Binding a body the handler discards let a malformed payload or
    /// a non-JSON <c>Content-Type</c> answer the framework's 400/415 instead of this route's 501, which
    /// made "not supported" look conditional on a request nothing here reads.
    /// </summary>
    private static IResult HandleModerationsAsync()
    {
        return JsonError(
            "Moderation is not supported by this Arcanum server.",
            "invalid_request_error",
            "not_supported",
            param: null,
            StatusCodes.Status501NotImplemented);
    }

}
