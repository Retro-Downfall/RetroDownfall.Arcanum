using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;

namespace RetroDownfall.Arcanum.Api;

/// <summary>
/// OpenAI-compatible routes Arcanum does not implement yet — images (DALL-E-style generation/
/// edits/variations) and audio (transcription/translation/speech). Every route always returns
/// <c>501 Not Implemented</c> with the standard OpenAI error envelope, unconditionally (no config
/// toggle — unlike <c>/v1/moderations</c>, there is no partial/pass-through behavior to gate; these
/// simply do not exist yet). Toggles are only worth adding once real functionality lands.
/// (<see cref="ExcludeFromCodeCoverageAttribute"/> is applied once on the primary
/// <c>OpenAiV1Endpoints.cs</c> partial declaration and covers this file too.)
/// </summary>
internal static partial class OpenAiV1Endpoints
{

    internal static void MapOpenAiV1ImagesStubs(this RouteGroupBuilder v1)
    {

        RouteGroupBuilder images = v1.MapGroup("/images");

        images.MapPost("/generations", HandleNotSupportedAsync).WithName("PostOpenAiImagesGenerations").WithLargeRequestBody();

        images.MapPost("/edits", HandleNotSupportedAsync).WithName("PostOpenAiImagesEdits").WithLargeRequestBody();

        images.MapPost("/variations", HandleNotSupportedAsync).WithName("PostOpenAiImagesVariations").WithLargeRequestBody();

    }

    internal static void MapOpenAiV1AudioStubs(this RouteGroupBuilder v1)
    {

        RouteGroupBuilder audio = v1.MapGroup("/audio");

        audio.MapPost("/transcriptions", HandleNotSupportedAsync).WithName("PostOpenAiAudioTranscriptions").WithLargeRequestBody();

        audio.MapPost("/translations", HandleNotSupportedAsync).WithName("PostOpenAiAudioTranslations").WithLargeRequestBody();

        audio.MapPost("/speech", HandleNotSupportedAsync).WithName("PostOpenAiAudioSpeech").WithLargeRequestBody();

    }

    private static IResult HandleNotSupportedAsync()
    {

        OpenAiErrorResponse response = new(new OpenAiErrorDetail(
            "This endpoint is not yet implemented by Arcanum.",
            "invalid_request_error",
            Param: null,
            Code: "not_supported"));

        return Results.Json(response, ArcanumJsonContext.Default.OpenAiErrorResponse, statusCode: StatusCodes.Status501NotImplemented);

    }

}
