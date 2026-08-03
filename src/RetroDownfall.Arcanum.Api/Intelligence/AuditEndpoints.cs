using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// <c>GET /api/audit</c> — read-only query surface over the persisted inference audit log (§8.26).
/// Returns an empty list (not an error) when <c>Arcanum:Host:AuditLog:Enabled</c> is
/// <see langword="false"/>, matching every other disabled-feature convention in Arcanum (never a
/// functional regression, just no data).
/// </summary>
[ExcludeFromCodeCoverage] // Reason: thin query-parameter parsing over IInferenceAuditLogger.QueryAsync; logic covered by InferenceAuditLoggerTests.
internal static class AuditEndpoints
{

    private const int DefaultPageSize = 100;

    private const int MaxPageSize = 1_000;

    internal static void MapAuditEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapGet("/audit", HandleGetAuditAsync).WithName("GetInferenceAudit");

    }

    private static async Task<IResult> HandleGetAuditAsync(
        string? from,
        string? to,
        string? model,
        string? sessionId,
        int? limit,
        string? cursor,
        IInferenceAuditLogger auditLogger,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        DateTimeOffset? parsedFrom = null;

        if (!string.IsNullOrWhiteSpace(from))
        {

            if (!DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
            {

                return ValidationError(traceId, $"'from' is not a valid date/time: '{from}'.");

            }

            parsedFrom = parsed;

        }

        DateTimeOffset? parsedTo = null;

        if (!string.IsNullOrWhiteSpace(to))
        {

            if (!DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
            {

                return ValidationError(traceId, $"'to' is not a valid date/time: '{to}'.");

            }

            parsedTo = parsed;

        }

        if (parsedFrom.HasValue && parsedTo.HasValue && parsedFrom.Value > parsedTo.Value)
        {

            return ValidationError(traceId, "'from' must not be after 'to'.");

        }

        int effectiveLimit = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        Result<AuditQueryPage<InferenceAuditRecord>> page = await auditLogger
            .QueryPageAsync(
                parsedFrom,
                parsedTo,
                model,
                sessionId,
                effectiveLimit,
                cursor,
                cancellationToken)
            .ConfigureAwait(false);

        if (page.IsFailure)
        {

            return ValidationError(traceId, page.Error.Message, page.Error.Code);

        }

        if (page.Value.NextCursor is not null)
        {

            httpContext.Response.Headers[ArcanumApiHeaders.AuditNextCursor] = page.Value.NextCursor;

        }

        IReadOnlyList<InferenceAuditRecord> records = page.Value.Records;

        Result<InferenceAuditRecord[]> result = Result<InferenceAuditRecord[]>.Success([.. records]);

        return Results.Ok(ApiResponse<InferenceAuditRecord[]>.FromResult(result, traceId));

    }

    private static IResult ValidationError(
        string traceId,
        string message,
        string errorCode = ErrorCodes.Validation.InvalidBody)
    {

        Result<InferenceAuditRecord[]> invalid = Result<InferenceAuditRecord[]>.Failure(
            new Error(errorCode, message));

        return Results.Json(
            ApiResponse<InferenceAuditRecord[]>.FromResult(invalid, traceId),
            ArcanumJsonContext.Default.ApiResponseInferenceAuditRecordArray,
            statusCode: StatusCodes.Status400BadRequest);

    }

}
