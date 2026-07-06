using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Api;

/// <summary>
/// OpenAI-compatible <c>POST /v1/moderations</c> (DESIGN.md §11.18). Phase 1 pass-through: when
/// enabled, every input is reported unflagged with zeroed category scores — Arcanum runs no local
/// or remote moderation model yet. Disabled by default (<see cref="ModerationsSettings.Enabled"/>),
/// returning <c>404</c> so probing clients get an explicit "not configured" signal rather than a
/// silently-useless "always safe" verdict.
/// (<see cref="ExcludeFromCodeCoverageAttribute"/> is applied once on the primary
/// <c>OpenAiV1Endpoints.cs</c> partial declaration and covers this file too.)
/// </summary>
internal static partial class OpenAiV1Endpoints
{

    private const string DefaultModerationModel = "omni-moderation-latest";

    internal static void MapOpenAiV1Moderations(this RouteGroupBuilder v1)
    {
        _ = v1.MapPost("/moderations", HandleModerationsAsync)
            .WithName("PostOpenAiModerations")
            .WithLargeRequestBody();
    }

    private static IResult HandleModerationsAsync(
        OpenAiModerationRequest? body,
        IOptionsSnapshot<ArcanumSettings> settings)
    {

        if (!settings.Value.Moderations.Enabled)
        {

            return JsonError(
                "Moderations are not enabled on this server (Arcanum:Moderations:Enabled).",
                "invalid_request_error",
                "feature_disabled",
                param: null,
                StatusCodes.Status404NotFound);

        }

        if (body is null || body.Input is null || body.Input.Values.Length == 0)
        {

            return JsonError(
                "Missing required parameter: 'input'.",
                "invalid_request_error",
                "missing_required_parameter",
                "input",
                StatusCodes.Status400BadRequest);

        }

        List<OpenAiModerationResult> results = new(body.Input.Values.Length);

        for (int i = 0; i < body.Input.Values.Length; i++)
        {

            results.Add(OpenAiModerationResult.CreateUnflagged());

        }

        string model = string.IsNullOrWhiteSpace(body.Model) ? DefaultModerationModel : body.Model;

        OpenAiModerationResponse response = new(
            Id: $"modr-{Guid.NewGuid():N}",
            Model: model,
            Results: results);

        return Results.Json(response, ArcanumJsonContext.Default.OpenAiModerationResponse);

    }

}
