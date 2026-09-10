using System.Diagnostics;

using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Middleware;

/// <summary>
/// The one answer a protected request gets while maintenance owns the database.
/// </summary>
/// <remarks>
/// Two surfaces produce it and they must be indistinguishable. Admission refuses a request that
/// arrived after the transition began; the exception handler answers one that was already in flight
/// when admission closed under it. An operator who saw two different bodies for the same window would
/// have to work out which half of the pipeline they were looking at, and the second body is exactly
/// the one that would be missing when it mattered.
///
/// <para>The wording is the sentence the refusal already carries inside the process, unchanged. It
/// names no path, owner, operation id, phase, generation, or native detail — a maintenance window is
/// expected, and a caller learns only that it should ask again.</para>
///
/// <para>The header tuple is applied here rather than left to the Covenant pre-binding hook, because
/// that hook is installed one stage later than this refusal is written. A protected route refused by
/// admission would otherwise ship with no <c>no-store</c> at all, and it is applied on every route
/// rather than only the protected ones because a cached maintenance answer outlives the window that
/// produced it.</para>
/// </remarks>
internal static class GrimoireMaintenanceRefusal
{

    /// <summary>The fixed, sanitized public wording, identical on both surfaces.</summary>
    internal const string Message =
        "The Grimoire is temporarily unavailable while maintenance owns connection admission.";

    /// <summary>
    /// The OpenAI error type the parent design fixes for a maintenance window.
    /// </summary>
    private const string OpenAiType = "service_unavailable";

    /// <summary>
    /// Names which unavailability this is, the way every sibling <c>/v1</c> refusal does.
    /// </summary>
    private const string OpenAiCode = "grimoire_maintenance";

    /// <summary>
    /// Writes the refusal, reporting whether it was written.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> for a response whose first byte has left. Headers cannot be
    /// corrected after that point and a half-written stream cannot become an envelope, so the honest
    /// answer is to leave the response alone and let the caller's own teardown end it.
    /// </remarks>
    internal static async ValueTask<bool> TryWriteAsync(HttpContext context)
    {

        ArgumentNullException.ThrowIfNull(context);

        if (context.Response.HasStarted)
        {

            return false;

        }

        CovenantProtectedResponseHeaders.Apply(context.Response);

        IResult refusal = context.Request.Path.StartsWithSegments(
            "/v1",
            StringComparison.OrdinalIgnoreCase)
            ? Results.Json(
                new OpenAiErrorResponse(
                    new OpenAiErrorDetail(
                        Message,
                        OpenAiType,
                        Param: null,
                        OpenAiCode)),
                ArcanumJsonContext.Default.OpenAiErrorResponse,
                statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Json(
                ApiResponse<string>.FromResult(
                    Result<string>.Failure(
                        new Error(ErrorCodes.Grimoire.MaintenanceUnavailable, Message)),
                    Activity.Current?.Id ?? context.TraceIdentifier),
                ArcanumJsonContext.Default.ApiResponseString,
                statusCode: StatusCodes.Status503ServiceUnavailable);

        await refusal.ExecuteAsync(context).ConfigureAwait(false);

        return true;

    }

}
